import Foundation
import MeterCore

/// Chooses whose numbers the window and menu bar show, one Mac or several, and shares this Mac's
/// when that's turned on. This Mac keeps being measured while others are shown, so sharing carries on.
@MainActor
final class MeterHub: ObservableObject {
    static let sourcesKey = "sources"
    /// Version 0.2 saved a single Mac here.
    static let legacySourceKey = "source"
    static let sharingKey = "sharing"

    /// One model per watched Mac, in the order of `sources`.
    @Published private(set) var models: [MeterViewModel] = []
    private(set) var sources: [MeterSource]
    private(set) var isSharing: Bool
    /// The Mac the menu bar icon follows: the busiest one.
    private var primarySource: MeterSource?

    /// Runs on every update shown, whether or not the window is open.
    var onUpdate: (() -> Void)?
    /// Runs when the Macs found nearby or the state of sharing change.
    var onNetworkChange: (() -> Void)?
    var isPanelVisible = true {
        didSet { updateDetail() }
    }

    let monitor: Monitor
    let server = ShareServer()
    let browser = PeerBrowser()
    private var clients: [MeterSource: RemoteClient] = [:]

    /// Overrides apply to this run only and aren't saved.
    init(monitor: Monitor, sourcesOverride: [MeterSource]? = nil, sharingOverride: Bool? = nil) {
        self.monitor = monitor
        sources = MeterSource.ordered(sourcesOverride ?? Self.savedSources())
        isSharing = sharingOverride ?? UserDefaults.standard.bool(forKey: Self.sharingKey)
    }

    private static func savedSources() -> [MeterSource] {
        let defaults = UserDefaults.standard
        let saved = defaults.stringArray(forKey: sourcesKey)?.compactMap(MeterSource.init(storageValue:)) ?? []
        if !saved.isEmpty { return saved }
        if let legacy = defaults.string(forKey: legacySourceKey).flatMap(MeterSource.init(storageValue:)) {
            return [legacy]
        }
        return [.local]
    }

    func start() {
        monitor.onUpdate = { [weak self] model in self?.localUpdated(model) }
        server.onChange = { [weak self] in
            self?.updateDetail()
            self?.onNetworkChange?()
        }
        browser.onChange = { [weak self] in self?.onNetworkChange?() }
        monitor.start()
        if isSharing { server.start() }
        // Browsing asks for permission, so it waits until someone uses a network feature.
        if isSharing || sources.contains(where: { !$0.isLocal }) { browser.start() }
        apply(sources)
    }

    /// What the menu bar icon shows.
    var primary: MeterViewModel {
        guard let primarySource, let index = sources.firstIndex(of: primarySource), models.indices.contains(index)
        else { return models.first ?? monitor.model }
        return models[index]
    }

    /// Other Macs found sharing, without this one. Its own name also lingers for a moment after
    /// sharing stops, so that's left out too.
    var peers: [String] {
        let own = Sharing.hostName
        return browser.names.filter { $0 != server.advertisedName && $0 != own }
    }

    /// A line for the menu about how other Macs reach this one, when sharing is on.
    var sharingStatus: String? {
        guard isSharing else { return nil }
        guard server.isRunning else { return "Couldn't start sharing" }
        guard let port = server.port else { return "Starting…" }
        return "Others see this Mac as \(server.advertisedName ?? Sharing.hostName), port \(port)"
    }

    /// Adds a Mac, or removes it. The last one stays, so the window always shows something.
    func toggle(_ source: MeterSource) {
        if sources.contains(source) {
            guard sources.count > 1 else { return }
            save(sources.filter { $0 != source })
        } else {
            save(sources + [source])
        }
    }

    func add(_ source: MeterSource) {
        guard !sources.contains(source) else { return }
        save(sources + [source])
    }

    func setSharing(_ on: Bool, persist: Bool = true) {
        if persist { UserDefaults.standard.set(on, forKey: Self.sharingKey) }
        guard on != isSharing else { return }
        isSharing = on
        if on {
            server.publish(monitor.model)
            server.start()
            browser.start()
        } else {
            server.stop()
        }
        updateDetail()
        onNetworkChange?()
    }

    /// Only this Mac's models can be unloaded from here. Sharing is read-only.
    func pressUnload(model name: String) {
        guard sources.contains(.local) else { return }
        monitor.pressUnload(model: name)
    }

    private func save(_ list: [MeterSource]) {
        let ordered = MeterSource.ordered(list)
        UserDefaults.standard.set(ordered.map(\.storageValue), forKey: Self.sourcesKey)
        UserDefaults.standard.removeObject(forKey: Self.legacySourceKey)
        apply(ordered)
        if ordered.contains(where: { !$0.isLocal }) { browser.start() }
    }

    /// Starts following new Macs and stops following removed ones. Macs that stay keep their numbers.
    private func apply(_ list: [MeterSource]) {
        let kept = Dictionary(uniqueKeysWithValues: zip(sources, models))
        for (source, client) in clients where !list.contains(source) {
            client.stop()
            clients[source] = nil
        }
        sources = list
        models = list.map { source in
            if let model = kept[source] { return model }
            if source.isLocal { return monitor.model }
            return .placeholder(for: RemoteSource(host: source.displayName, status: .connecting))
        }
        // Clients report as soon as they start, so the models above have to be in place first.
        for source in list where !source.isLocal && clients[source] == nil {
            let client = RemoteClient(source: source)
            client.onModel = { [weak self, weak client] model in
                guard let self, let client, self.clients[source] === client else { return }
                self.show(model, for: source)
            }
            clients[source] = client
            client.start()
        }
        choosePrimary()
        updateDetail()
        onUpdate?()
    }

    private func localUpdated(_ model: MeterViewModel) {
        if isSharing { server.publish(model) }
        if sources.contains(.local) { show(model, for: .local) }
    }

    private func show(_ model: MeterViewModel, for source: MeterSource) {
        guard let index = sources.firstIndex(of: source) else { return }
        models[index] = model
        choosePrimary()
        onUpdate?()
    }

    private func choosePrimary() {
        let current = primarySource.flatMap { sources.firstIndex(of: $0) }
        primarySource = MeterViewModel.busiestIndex(of: models, keeping: current).map { sources[$0] }
    }

    /// Power, thermal and process readings are worth taking while someone can see them.
    private func updateDetail() {
        monitor.wantsDetail = (isPanelVisible && sources.contains(.local)) || server.hasViewers
    }
}
