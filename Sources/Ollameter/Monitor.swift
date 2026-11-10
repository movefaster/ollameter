import Foundation
import MeterCore

struct MeterConfiguration {
    var logPath = NSString(string: "~/.ollama/logs/server.log").expandingTildeInPath
    var baseURL = URL(string: "http://127.0.0.1:11434")!
}

final class OllamaClient {
    private let base: URL
    private let session: URLSession

    init(base: URL) {
        self.base = base
        let configuration = URLSessionConfiguration.ephemeral
        configuration.timeoutIntervalForRequest = 2
        configuration.connectionProxyDictionary = [:]
        configuration.requestCachePolicy = .reloadIgnoringLocalCacheData
        session = URLSession(configuration: configuration)
    }

    func version() async -> String? {
        await fetch(base.appendingPathComponent("api/version")).flatMap(OllamaJSON.version)
    }

    func loadedModels() async -> [LoadedModel]? {
        await fetch(base.appendingPathComponent("api/ps")).flatMap(OllamaJSON.models)
    }

    func slots(port: Int) async -> [SlotReading]? {
        guard let url = URL(string: "http://127.0.0.1:\(port)/slots") else { return nil }
        return await fetch(url).flatMap(OllamaJSON.slots)
    }

    func unload(model: String) async -> Bool {
        var request = URLRequest(url: base.appendingPathComponent("api/generate"), timeoutInterval: 30)
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.httpBody = OllamaJSON.unloadBody(model: model)
        guard let (_, response) = try? await session.data(for: request) else { return false }
        return (response as? HTTPURLResponse)?.statusCode == 200
    }

    private func fetch(_ url: URL) async -> Data? {
        guard let (data, response) = try? await session.data(from: url),
            (response as? HTTPURLResponse)?.statusCode == 200
        else { return nil }
        return data
    }
}

/// Runs the sampling loop and publishes one view model for the menu bar and the window.
@MainActor
final class Monitor: ObservableObject {
    @Published private(set) var model = MeterViewModel()

    /// Runs on every publish, whether or not the window is open.
    var onUpdate: ((MeterViewModel) -> Void)?
    /// Power, thermal and process readings are skipped while the window is hidden.
    var wantsDetail = true

    private static let busyInterval: Duration = .milliseconds(250)
    private static let idleInterval: Duration = .seconds(1)
    private static let scanInterval: TimeInterval = 2
    private static let hardwareInterval: TimeInterval = 1
    // Every /api/ps call adds a line to Ollama's own log, so it is polled sparingly.
    // /slots and the process scan leave no trace there and carry the live state instead.
    private static let modelRefreshFloor: TimeInterval = 15
    private static let modelRefreshCeiling: TimeInterval = 30
    // Enough to reach back to the model's load line, so cache and session figures are complete at launch.
    private static let backlogBytes = 1 << 23
    private static let unloadConfirmWindow: Duration = .seconds(4)
    private static let unloadTimeout: Duration = .seconds(30)
    private static let unloadFailureShown: Duration = .seconds(6)

    private let engine = MeterEngine()
    private var presenter = DisplayPresenter()
    private let tailer: LogTailer
    private let client: OllamaClient
    private let sampler = HardwareSampler()
    private var loop: Task<Void, Never>?

    private var scan = ProcessScan()
    private var models: [LoadedModel] = []
    private var version: String?
    private var apiReachable = true
    private var modelsStale = true
    private var lastScan = Date.distantPast
    private var lastModelRefresh = Date.distantPast
    private var lastHardware = Date.distantPast
    private var hardware = HardwareSample()
    private var runnerStats: RunnerStats?
    private var tpsHistory: [Double] = []
    private var gpuHistory: [Double] = []
    // Set straight on the model too, so a press shows at once instead of on the next tick.
    private var unloadState = UnloadState.ready {
        didSet { model.unload = unloadState }
    }
    private var unloadExpiry: Task<Void, Never>?

    init(configuration: MeterConfiguration = MeterConfiguration()) {
        tailer = LogTailer(path: configuration.logPath)
        client = OllamaClient(base: configuration.baseURL)
    }

    func start() {
        for line in tailer.openAtEnd(backlogBytes: Self.backlogBytes) {
            engine.ingest(line: line, at: nil)
        }
        loop = Task { [weak self] in
            while !Task.isCancelled {
                guard let self else { return }
                let interval = await self.tick()
                try? await Task.sleep(for: interval)
            }
        }
    }

    func pressUnload(model name: String) {
        let press = unloadState.pressed(name)
        if press.sendsUnload {
            Task { await unload(name) }
        } else if press.next != unloadState {
            setUnloadState(press.next, expiringAfter: Self.unloadConfirmWindow)
        }
    }

    private func unload(_ name: String) async {
        setUnloadState(.unloading(model: name), expiringAfter: Self.unloadTimeout)
        // What the window showed can be a moment old, so both are checked again right before acting.
        await pollSlots()
        guard !engine.snapshot(at: Date(), online: true).phase.isBusy else {
            setUnloadState(.ready)
            return
        }
        guard let loaded = await client.loadedModels() else {
            showUnloadFailure(name)
            return
        }
        guard loaded.contains(where: { $0.name == name }) else {
            setUnloadState(.ready)
            return
        }
        // On success the state stays at unloading until /api/ps stops listing the model.
        if await !client.unload(model: name) { showUnloadFailure(name) }
    }

    private func showUnloadFailure(_ name: String) {
        guard unloadState == .unloading(model: name) else { return }
        setUnloadState(.failed(model: name), expiringAfter: Self.unloadFailureShown)
    }

    private func setUnloadState(_ state: UnloadState, expiringAfter lifetime: Duration? = nil) {
        unloadExpiry?.cancel()
        unloadExpiry = nil
        unloadState = state
        guard let lifetime else { return }
        unloadExpiry = Task { [weak self] in
            try? await Task.sleep(for: lifetime)
            guard !Task.isCancelled, let self else { return }
            if case .unloading(let name) = self.unloadState {
                self.showUnloadFailure(name)
            } else {
                self.setUnloadState(.ready)
            }
        }
    }

    private func tick() async -> Duration {
        for line in tailer.readNew() {
            let event = engine.ingest(line: line, at: Date())
            if event == .allIdle || event == .modelLoaded { modelsStale = true }
        }
        await refreshProcesses()
        await refreshModelsIfNeeded()
        await pollSlots()

        let online = scan.ollamaRunning && apiReachable
        let snapshot = engine.snapshot(at: Date(), online: online)
        let hardwareRefreshed = await refreshHardwareIfDue()
        publish(snapshot, appendHistory: hardwareRefreshed)
        return snapshot.phase.isBusy ? Self.busyInterval : Self.idleInterval
    }

    private func refreshProcesses() async {
        let now = Date()
        guard now.timeIntervalSince(lastScan) >= Self.scanInterval else { return }
        lastScan = now
        let newScan = await sampler.scan()
        if newScan.runners != scan.runners || newScan.ollamaRunning != scan.ollamaRunning {
            modelsStale = true
            lastModelRefresh = .distantPast
        }
        scan = newScan
    }

    private func refreshModelsIfNeeded() async {
        guard scan.ollamaRunning else {
            models = []
            version = nil
            apiReachable = true
            return
        }
        let age = Date().timeIntervalSince(lastModelRefresh)
        guard unloadState.isUnloading || (modelsStale && age >= Self.modelRefreshFloor) || age >= Self.modelRefreshCeiling
        else { return }
        lastModelRefresh = Date()
        modelsStale = false
        if let loaded = await client.loadedModels() {
            models = loaded
            apiReachable = true
            if case .unloading(let name) = unloadState, !loaded.contains(where: { $0.name == name }) {
                setUnloadState(.ready)
            }
            if version == nil { version = await client.version() }
        } else {
            models = []
            apiReachable = false
        }
    }

    private func pollSlots() async {
        guard !scan.runners.isEmpty else {
            engine.update(slots: nil, at: Date())
            return
        }
        var readings: [SlotReading] = []
        var answered = false
        for runner in scan.runners {
            if let slots = await client.slots(port: runner.port) {
                readings.append(contentsOf: slots)
                answered = true
            }
        }
        engine.update(slots: answered ? readings : nil, at: Date())
    }

    private func refreshHardwareIfDue() async -> Bool {
        let now = Date()
        guard now.timeIntervalSince(lastHardware) >= Self.hardwareInterval else { return false }
        lastHardware = now
        let (sample, stats) = await sampler.sample(detail: wantsDetail, runners: scan.runners)
        hardware = sample
        runnerStats = stats
        return true
    }

    private func append(_ value: Double, to history: inout [Double]) {
        history.append(value)
        if history.count > MeterViewModel.historyLimit {
            history.removeFirst(history.count - MeterViewModel.historyLimit)
        }
    }

    private func publish(_ snapshot: LiveSnapshot, appendHistory: Bool) {
        var next = MeterViewModel()
        next.now = Date()
        next.snapshot = snapshot
        next.stats = engine.tracker.stats
        next.requests = engine.tracker.recent
        next.notices = engine.tracker.notices
        next.models = models
        next.version = version
        next.hardware = hardware
        next.runner = runnerStats
        next.promptCache = engine.tracker.promptCache
        next.display = presenter.present(next)
        // A request that starts while the button is armed shouldn't leave it armed for when it ends.
        if next.isWorking, case .confirming = unloadState { setUnloadState(.ready) }
        next.unload = unloadState
        if appendHistory {
            let shownTPS = next.display.displayPhase == .generating ? snapshot.liveTPS ?? next.display.heldTPS ?? 0 : 0
            append(shownTPS, to: &tpsHistory)
            append(hardware.gpuUtilization ?? 0, to: &gpuHistory)
        }
        next.tpsHistory = tpsHistory
        next.gpuHistory = gpuHistory
        model = next
        onUpdate?(next)
    }
}
