import Foundation

public struct ActiveRequest: Equatable, Sendable {
    public var task: Int
    public var contextSize: Int
    public var promptTokens: Int
    /// New tokens read so far. The log's count leaves out the prefix that came from the cache.
    public var promptTokensRead: Int?
    /// The log's own progress figure, which does cover the whole prompt.
    public var promptProgress: Double?
    public var promptTPS: Double?
    public var generatedTokens: Int?
    public var recentTPS: Double?
    public var lastUpdate: Date?
}

/// Turns the log event stream into in-flight requests, finished requests and session statistics.
/// A nil date means the event came from the backlog read at launch, not from a live tail.
public struct RequestTracker: Sendable {
    public static let recentLimit = 40
    public static let loadingTimeout: TimeInterval = 600

    public private(set) var active: [Int: ActiveRequest] = [:]
    public private(set) var recent: [CompletedRequest] = []
    public private(set) var stats = SessionStats()
    public private(set) var notices: [Notice] = []
    public private(set) var loadingSince: Date?
    public private(set) var promptCache: PromptCacheState?

    private struct Timing: Sendable {
        var promptMilliseconds: Double?
        var promptTokens = 0
        var promptTPS = 0.0
        var generationMilliseconds: Double?
        var generatedTokens = 0
        var generationTPS = 0.0
    }

    private var timings: [Int: Timing] = [:]
    private var nextSequence = 0

    public init() {}

    public mutating func apply(_ event: LogEvent, at date: Date?) {
        switch event {
        case let .newPrompt(task, contextSize, promptTokens):
            active[task] = ActiveRequest(
                task: task, contextSize: contextSize, promptTokens: promptTokens, lastUpdate: date)
            timings[task] = nil
            loadingSince = nil
        case let .promptProgress(task, tokens, progress, tps):
            active[task]?.promptTokensRead = tokens
            active[task]?.promptProgress = progress
            active[task]?.promptTPS = tps
            active[task]?.lastUpdate = date
        case let .generationProgress(task, tokens, _, recentTPS):
            active[task]?.generatedTokens = tokens
            active[task]?.recentTPS = recentTPS
            active[task]?.lastUpdate = date
        case let .promptEval(task, milliseconds, tokens, tps):
            var timing = timings[task] ?? Timing()
            timing.promptMilliseconds = milliseconds
            timing.promptTokens = tokens
            timing.promptTPS = tps
            timings[task] = timing
        case let .eval(task, milliseconds, tokens, tps):
            var timing = timings[task] ?? Timing()
            timing.generationMilliseconds = milliseconds
            timing.generatedTokens = tokens
            timing.generationTPS = tps
            timings[task] = timing
        case let .total(task, _, _):
            finish(task: task, at: date)
        case let .released(task), let .cancelled(task):
            active[task] = nil
            timings[task] = nil
        case .allIdle:
            active.removeAll()
        case .fullReprocess:
            addNotice(
                id: "full-reprocess", severity: .warning,
                text: "The prompt cache missed, so the whole prompt was read again.", at: date)
        case .contextShift:
            addNotice(
                id: "context-shift", severity: .warning,
                text: "The context filled up, so older tokens were dropped.", at: date)
        case let .inputTruncated(limit, promptTokens):
            addNotice(
                id: "truncated", severity: .serious,
                text: "A \(Format.integer(promptTokens))-token prompt was cut to \(Format.integer(limit)) tokens. "
                    + "Raise the model's context length to avoid this.",
                at: date)
        case .modelLoading:
            if let date { loadingSince = date }
        case .modelLoaded:
            // Speeds from one model mean nothing for the next, so statistics follow the loaded model.
            loadingSince = nil
            active.removeAll()
            recent.removeAll()
            timings.removeAll()
            stats = SessionStats()
        case let .promptCacheEnabled(limit):
            // Logged once per load, before the model is ready, and the old cache died with the old runner.
            promptCache = PromptCacheState(limitBytes: limit.map(Self.bytes))
        case .promptCacheDisabled:
            promptCache = nil
        case let .promptCacheState(savedPrompts, used, limit):
            var cache = promptCache ?? PromptCacheState()
            cache.usedBytes = Self.bytes(used)
            cache.limitBytes = limit > 0 ? Self.bytes(limit) : nil
            cache.savedPrompts = savedPrompts
            cache.savedTokens = []
            promptCache = cache
        case let .promptCacheEntry(tokens):
            // The log lists these oldest first.
            promptCache?.savedTokens.insert(tokens, at: 0)
        case let .promptCacheEvicted(size):
            var cache = promptCache ?? PromptCacheState()
            cache.evictions += 1
            promptCache = cache
            addNotice(
                id: "cache-evicted", severity: .warning,
                text: "The prompt cache was full, so the oldest saved prompt (\(Format.bytes(Self.bytes(size)))) was dropped.",
                at: date)
        }
    }

    private static func bytes(_ mebibytes: Double) -> UInt64 {
        UInt64(mebibytes * 1_048_576)
    }

    public func isLoadingModel(at now: Date) -> Bool {
        guard let loadingSince else { return false }
        return now.timeIntervalSince(loadingSince) < Self.loadingTimeout
    }

    private mutating func finish(task: Int, at date: Date?) {
        guard let timing = timings.removeValue(forKey: task),
            let promptMilliseconds = timing.promptMilliseconds,
            let generationMilliseconds = timing.generationMilliseconds
        else { return }
        let request = CompletedRequest(
            sequence: nextSequence,
            task: task,
            finishedAt: date,
            promptTokens: active[task]?.promptTokens ?? timing.promptTokens,
            evaluatedTokens: timing.promptTokens,
            promptMilliseconds: promptMilliseconds,
            promptTPS: timing.promptTPS,
            generatedTokens: timing.generatedTokens,
            generationMilliseconds: generationMilliseconds,
            generationTPS: timing.generationTPS)
        nextSequence += 1
        recent.insert(request, at: 0)
        if recent.count > Self.recentLimit { recent.removeLast(recent.count - Self.recentLimit) }
        stats.record(request)
    }

    private mutating func addNotice(id: String, severity: Notice.Severity, text: String, at date: Date?) {
        guard let date else { return }
        notices.removeAll { $0.id == id }
        notices.insert(Notice(id: id, severity: severity, text: text, at: date), at: 0)
    }
}

/// Tokens per second over a sliding window of cumulative token counts.
public struct SlidingRate: Sendable {
    public let window: TimeInterval
    private var points: [(time: Date, count: Int)] = []

    public static let minimumSpan: TimeInterval = 0.4

    public init(window: TimeInterval) {
        self.window = window
    }

    public mutating func add(_ count: Int, at time: Date) {
        if let last = points.last, count < last.count { points.removeAll() }
        points.append((time, count))
        let cutoff = time.addingTimeInterval(-window)
        while points.count > 2, points[1].time <= cutoff { points.removeFirst() }
    }

    public mutating func reset() {
        points.removeAll()
    }

    public var rate: Double? {
        guard let first = points.first, let last = points.last else { return nil }
        let span = last.time.timeIntervalSince(first.time)
        guard span >= Self.minimumSpan else { return nil }
        return Double(last.count - first.count) / span
    }
}
