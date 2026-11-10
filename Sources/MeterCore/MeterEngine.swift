import Foundation

/// Combines the log stream and `/slots` polling into one live view of what the runner is doing.
///
/// `/slots` is the primary live source because it updates per token. The log only reports generation
/// speed every few seconds, but it has exact final timings and is the fallback if `/slots` goes away.
public final class MeterEngine {
    public private(set) var tracker = RequestTracker()

    private var slots: [SlotReading] = []
    private var hasSlots = false
    private var generationRate = SlidingRate(window: 3)
    private var trackedTask: Int?
    private var prefillBegan: Date?
    private var generationBegan: Date?

    private static let logOnlyStaleAfter: TimeInterval = 120

    public init() {}

    @discardableResult
    public func ingest(line: String, at date: Date?) -> LogEvent? {
        guard let event = LogParser.parse(line) else { return nil }
        tracker.apply(event, at: date)
        return event
    }

    /// Pass nil when no runner answered, which switches live state to the log.
    public func update(slots newSlots: [SlotReading]?, at now: Date) {
        guard let newSlots else {
            slots = []
            hasSlots = false
            clearTracking()
            return
        }
        slots = newSlots
        hasSlots = true
        guard let current = newSlots.first(where: \.isProcessing) else {
            clearTracking()
            return
        }
        if current.task != trackedTask {
            clearTracking()
            trackedTask = current.task
            prefillBegan = now
        }
        if current.decodedTokens > 0 {
            if generationBegan == nil { generationBegan = now }
            generationRate.add(current.decodedTokens, at: now)
        }
    }

    public func snapshot(at now: Date, online: Bool) -> LiveSnapshot {
        var snapshot = LiveSnapshot()
        guard online else {
            snapshot.phase = .offline
            return snapshot
        }
        if let current = slots.first(where: \.isProcessing) {
            fill(&snapshot, from: current, at: now)
            return snapshot
        }
        if !hasSlots, let request = freshLogRequest(at: now) {
            fill(&snapshot, from: request)
            return snapshot
        }
        snapshot.phase = tracker.isLoadingModel(at: now) ? .loading : .idle
        if let slot = slots.first, slot.promptTokens > 0 {
            snapshot.contextSize = slot.contextSize
            snapshot.contextUsed = slot.promptTokens
        }
        return snapshot
    }

    private func clearTracking() {
        trackedTask = nil
        prefillBegan = nil
        generationBegan = nil
        generationRate.reset()
    }

    private func freshLogRequest(at now: Date) -> ActiveRequest? {
        tracker.active.values
            .filter { request in
                guard let updated = request.lastUpdate else { return false }
                return now.timeIntervalSince(updated) < Self.logOnlyStaleAfter
            }
            .max { ($0.lastUpdate ?? .distantPast) < ($1.lastUpdate ?? .distantPast) }
    }

    private func fill(_ snapshot: inout LiveSnapshot, from slot: SlotReading, at now: Date) {
        let log = tracker.active[slot.task]
        snapshot.task = slot.task
        snapshot.contextSize = slot.contextSize
        snapshot.contextUsed = slot.promptTokens

        if slot.decodedTokens > 0 {
            snapshot.phase = .generating
            snapshot.generatedTokens = slot.decodedTokens
            snapshot.liveTPS = generationRate.rate ?? log?.recentTPS
            snapshot.phaseElapsed = generationBegan.map { now.timeIntervalSince($0) }
            return
        }

        snapshot.phase = .prefill
        let cached = slot.cachedPromptTokens
        // Both counts leave out the cached prefix. /slots updates between the log's progress lines.
        let read = max(slot.processedPromptTokens, log?.promptTokensRead ?? 0)
        let elapsed = prefillBegan.map { now.timeIntervalSince($0) } ?? 0
        let rate = log?.promptTPS ?? (elapsed > 0.5 && read > 0 ? Double(read) / elapsed : nil)

        snapshot.promptCached = cached
        snapshot.promptDone = read
        snapshot.promptTPS = rate
        snapshot.phaseElapsed = elapsed

        // A percentage needs the prompt's full size, which only the log's "new prompt" line has.
        guard let total = log?.promptTokens, total > 0 else { return }
        let toProcess = max(total - cached, 0)
        let done = min(read, toProcess)
        snapshot.promptTokens = total
        snapshot.promptDone = done
        snapshot.promptToProcess = toProcess
        // The same definition as the log's `progress`, so the two can be compared directly.
        snapshot.promptFraction = Double(cached + done) / Double(total)
        if let rate, rate > 0 { snapshot.etaSeconds = Double(toProcess - done) / rate }
    }

    private func fill(_ snapshot: inout LiveSnapshot, from request: ActiveRequest) {
        snapshot.task = request.task
        snapshot.contextSize = request.contextSize
        snapshot.contextUsed = request.promptTokens + (request.generatedTokens ?? 0)

        if let generated = request.generatedTokens {
            snapshot.phase = .generating
            snapshot.generatedTokens = generated
            snapshot.liveTPS = request.recentTPS
            return
        }

        snapshot.phase = .prefill
        snapshot.promptTokens = request.promptTokens
        snapshot.promptDone = request.promptTokensRead
        snapshot.promptTPS = request.promptTPS
        guard let progress = request.promptProgress, let read = request.promptTokensRead else {
            snapshot.promptToProcess = request.promptTokens
            return
        }
        let remaining = max(Int((1 - progress) * Double(request.promptTokens)), 0)
        snapshot.promptToProcess = read + remaining
        snapshot.promptFraction = min(progress, 1)
        if let rate = request.promptTPS, rate > 0 { snapshot.etaSeconds = Double(remaining) / rate }
    }
}
