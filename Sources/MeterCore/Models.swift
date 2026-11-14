import Foundation

public enum Phase: String, Equatable, Sendable, Codable {
    case offline, idle, loading, prefill, generating

    public var isBusy: Bool { self == .prefill || self == .generating || self == .loading }
}

/// One entry from llama-server's `/slots` endpoint, reduced to the numeric fields the meter uses.
public struct SlotReading: Equatable, Sendable {
    public var isProcessing: Bool
    public var task: Int
    public var contextSize: Int
    /// Tokens in the slot so far. This grows batch by batch while the prompt is read and then with each
    /// generated token, so it isn't the prompt's size until the last batch. Only the log has that.
    public var promptTokens: Int
    public var processedPromptTokens: Int
    public var cachedPromptTokens: Int
    public var decodedTokens: Int

    public init(
        isProcessing: Bool, task: Int, contextSize: Int, promptTokens: Int,
        processedPromptTokens: Int, cachedPromptTokens: Int, decodedTokens: Int
    ) {
        self.isProcessing = isProcessing
        self.task = task
        self.contextSize = contextSize
        self.promptTokens = promptTokens
        self.processedPromptTokens = processedPromptTokens
        self.cachedPromptTokens = cachedPromptTokens
        self.decodedTokens = decodedTokens
    }
}

public struct LiveSnapshot: Equatable, Sendable, Codable {
    public var phase: Phase = .idle
    public var task: Int?
    public var contextSize: Int?
    public var contextUsed: Int?

    public var promptTokens: Int?
    public var promptCached: Int?
    public var promptDone: Int?
    public var promptToProcess: Int?
    public var promptFraction: Double?
    public var promptTPS: Double?
    public var etaSeconds: Double?

    public var generatedTokens: Int?
    public var liveTPS: Double?
    public var phaseElapsed: Double?

    public init() {}
}

public struct CompletedRequest: Equatable, Sendable, Identifiable, Codable {
    public var id: Int { sequence }
    public var sequence: Int
    public var task: Int
    /// Nil for requests recovered from the existing log, which has no timestamps on these lines.
    public var finishedAt: Date?
    public var promptTokens: Int
    public var evaluatedTokens: Int
    public var promptMilliseconds: Double
    public var promptTPS: Double
    public var generatedTokens: Int
    public var generationMilliseconds: Double
    public var generationTPS: Double

    public var cachedTokens: Int { max(0, promptTokens - evaluatedTokens) }
    public var cacheFraction: Double {
        promptTokens > 0 ? Double(cachedTokens) / Double(promptTokens) : 0
    }
    public var totalMilliseconds: Double { promptMilliseconds + generationMilliseconds }

    public init(
        sequence: Int, task: Int, finishedAt: Date?, promptTokens: Int, evaluatedTokens: Int,
        promptMilliseconds: Double, promptTPS: Double, generatedTokens: Int,
        generationMilliseconds: Double, generationTPS: Double
    ) {
        self.sequence = sequence
        self.task = task
        self.finishedAt = finishedAt
        self.promptTokens = promptTokens
        self.evaluatedTokens = evaluatedTokens
        self.promptMilliseconds = promptMilliseconds
        self.promptTPS = promptTPS
        self.generatedTokens = generatedTokens
        self.generationMilliseconds = generationMilliseconds
        self.generationTPS = generationTPS
    }
}

/// Peak, low and weighted average speed across requests large enough to be meaningful.
public struct SpeedStat: Equatable, Sendable, Codable {
    public private(set) var peak: Double?
    public private(set) var low: Double?
    public private(set) var history: [Double] = []
    private var tokens = 0
    private var milliseconds = 0.0

    public static let historyLimit = 30

    public var average: Double? {
        milliseconds > 0 ? Double(tokens) * 1000 / milliseconds : nil
    }

    public init() {}

    public mutating func record(tokens newTokens: Int, milliseconds newMilliseconds: Double, tps: Double) {
        peak = max(peak ?? tps, tps)
        low = min(low ?? tps, tps)
        tokens += newTokens
        milliseconds += newMilliseconds
        history.append(tps)
        if history.count > Self.historyLimit { history.removeFirst(history.count - Self.historyLimit) }
    }
}

public struct SessionStats: Equatable, Sendable, Codable {
    public static let promptMinTokens = 64
    public static let generationMinTokens = 16

    public var prompt = SpeedStat()
    public var generation = SpeedStat()
    public var requests = 0
    public var generatedTokens = 0
    public var promptTokens = 0
    public var cachedTokens = 0
    public var promptMilliseconds = 0.0
    public var generationMilliseconds = 0.0
    public var firstTokenMin: Double?
    public var firstTokenMax: Double?
    private var firstTokenSum = 0.0

    public var firstTokenAverage: Double? {
        requests > 0 ? firstTokenSum / Double(requests) : nil
    }
    public var cacheFraction: Double? {
        promptTokens > 0 ? Double(cachedTokens) / Double(promptTokens) : nil
    }
    /// Share of inference time spent reading prompts rather than generating.
    public var promptTimeFraction: Double? {
        let total = promptMilliseconds + generationMilliseconds
        return total > 0 ? promptMilliseconds / total : nil
    }

    public init() {}

    public mutating func record(_ request: CompletedRequest) {
        requests += 1
        generatedTokens += request.generatedTokens
        promptTokens += request.promptTokens
        cachedTokens += request.cachedTokens
        promptMilliseconds += request.promptMilliseconds
        generationMilliseconds += request.generationMilliseconds
        firstTokenSum += request.promptMilliseconds
        firstTokenMin = min(firstTokenMin ?? request.promptMilliseconds, request.promptMilliseconds)
        firstTokenMax = max(firstTokenMax ?? request.promptMilliseconds, request.promptMilliseconds)
        if request.evaluatedTokens >= Self.promptMinTokens {
            prompt.record(
                tokens: request.evaluatedTokens, milliseconds: request.promptMilliseconds, tps: request.promptTPS)
        }
        if request.generatedTokens >= Self.generationMinTokens {
            generation.record(
                tokens: request.generatedTokens, milliseconds: request.generationMilliseconds,
                tps: request.generationTPS)
        }
    }
}

/// What llama-server last logged about the prompts it keeps in RAM. It only writes this when the cache
/// changes, so the last report stays accurate until the next one. The prompt in the slot isn't included.
public struct PromptCacheState: Equatable, Sendable, Codable {
    /// Nil when llama-server runs with no limit.
    public var limitBytes: UInt64?
    public var usedBytes: UInt64
    public var savedPrompts: Int
    /// Size of each saved prompt, newest first.
    public var savedTokens: [Int]
    /// Prompts dropped to make room since this model loaded.
    public var evictions: Int

    public var fraction: Double? {
        guard let limitBytes, limitBytes > 0 else { return nil }
        return Double(usedBytes) / Double(limitBytes)
    }

    public init(
        limitBytes: UInt64? = nil, usedBytes: UInt64 = 0, savedPrompts: Int = 0, savedTokens: [Int] = [],
        evictions: Int = 0
    ) {
        self.limitBytes = limitBytes
        self.usedBytes = usedBytes
        self.savedPrompts = savedPrompts
        self.savedTokens = savedTokens
        self.evictions = evictions
    }
}

/// The window's unload button. A stray click would cost a long reload and every saved prompt, so the
/// first press only arms it and the next one sends the unload.
public enum UnloadState: Equatable, Sendable {
    case ready
    case confirming(model: String)
    case unloading(model: String)
    case failed(model: String)

    public var isUnloading: Bool {
        if case .unloading = self { return true }
        return false
    }

    /// The state after a press on the button for `name`, and whether that press sends the unload.
    public func pressed(_ name: String) -> (next: UnloadState, sendsUnload: Bool) {
        switch self {
        case .unloading:
            return (self, false)
        case .confirming(let armed) where armed == name:
            return (.unloading(model: name), true)
        default:
            return (.confirming(model: name), false)
        }
    }
}

public struct Notice: Equatable, Sendable, Identifiable, Codable {
    public enum Severity: String, Sendable, Codable { case warning, serious }

    public var id: String
    public var severity: Severity
    public var text: String
    public var at: Date

    public init(id: String, severity: Severity, text: String, at: Date) {
        self.id = id
        self.severity = severity
        self.text = text
        self.at = at
    }
}

public struct LoadedModel: Equatable, Sendable, Identifiable, Codable {
    public var id: String { name }
    public var name: String
    public var size: UInt64
    public var sizeVRAM: UInt64
    public var contextLength: Int?
    public var expiresAt: Date?
    public var parameterSize: String?
    public var quantization: String?

    public init(
        name: String, size: UInt64, sizeVRAM: UInt64, contextLength: Int?, expiresAt: Date?,
        parameterSize: String?, quantization: String?
    ) {
        self.name = name
        self.size = size
        self.sizeVRAM = sizeVRAM
        self.contextLength = contextLength
        self.expiresAt = expiresAt
        self.parameterSize = parameterSize
        self.quantization = quantization
    }
}

public enum MemoryPressure: String, Equatable, Sendable, Codable {
    case normal = "Normal", warning = "Warning", critical = "Critical"
}

public struct HardwareSample: Equatable, Sendable, Codable {
    public var gpuUtilization: Double?
    public var gpuMemoryInUse: UInt64?
    public var gpuPowerWatts: Double?
    public var dieTemperature: Double?
    public var swapUsed: UInt64?
    public var memoryPressure: MemoryPressure?

    public init() {}
}

public struct RunnerStats: Equatable, Sendable, Codable {
    public var cpuPercent: Double?
    public var residentBytes: UInt64?

    public init(cpuPercent: Double?, residentBytes: UInt64?) {
        self.cpuPercent = cpuPercent
        self.residentBytes = residentBytes
    }
}

public struct MeterViewModel: Equatable, Sendable, Codable {
    public static let historyLimit = 60
    public static let noticeLifetime: TimeInterval = 300

    public var now = Date()
    public var snapshot = LiveSnapshot()
    public var display = DisplayState.initial
    public var stats = SessionStats()
    public var requests: [CompletedRequest] = []
    public var notices: [Notice] = []
    public var models: [LoadedModel] = []
    public var version: String?
    public var hardware = HardwareSample()
    public var runner: RunnerStats?
    public var promptCache: PromptCacheState?
    public var unload = UnloadState.ready
    public var tpsHistory: [Double] = []
    public var gpuHistory: [Double] = []
    /// Set when the numbers come from another Mac rather than this one.
    public var remote: RemoteSource?

    public init() {}

    // The unload button and the remote source belong to the Mac showing the window, so they aren't shared.
    private enum CodingKeys: String, CodingKey {
        case now, snapshot, display, stats, requests, notices, models, version, hardware, runner, promptCache
        case tpsHistory, gpuHistory
    }

    /// True during a request and the short gaps between an agent's requests, when unloading would cut in.
    public var isWorking: Bool { snapshot.phase.isBusy || display.displayPhase.isBusy }

    /// Log-derived notices plus conditions read from the current hardware and model state.
    public var visibleNotices: [Notice] {
        var result = notices.filter { now.timeIntervalSince($0.at) < Self.noticeLifetime }
        if let swap = hardware.swapUsed, swap >= 1 << 30, !models.isEmpty {
            result.append(
                Notice(
                    id: "swap", severity: .serious,
                    text: "\(Format.bytes(swap)) of swap in use, which can slow inference.", at: now))
        }
        for model in models where model.size > 0 && model.sizeVRAM < model.size {
            let share = Double(model.sizeVRAM) / Double(model.size)
            result.append(
                Notice(
                    id: "cpu-\(model.name)", severity: .serious,
                    text: "Only \(Format.percent(share)) of \(model.name) is on the GPU. The rest runs on the CPU.",
                    at: now))
        }
        return result
    }

    /// Generation energy per token, available only during generation and the short gaps between requests.
    public var joulesPerToken: Double? {
        guard display.displayPhase == .generating, let tps = snapshot.liveTPS ?? display.heldTPS, tps > 0,
            let watts = hardware.gpuPowerWatts
        else { return nil }
        return watts / tps
    }
}
