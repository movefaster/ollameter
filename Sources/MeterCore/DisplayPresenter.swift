import Foundation

public struct DisplayState: Equatable, Sendable, Codable {
    public enum Tint: String, Sendable, Codable { case none, muted, gpu, prompt }

    public var fraction: Double
    public var tint: Tint
    public var modelLoaded: Bool
    public var text: String?
    public var textDimmed: Bool
    public var summary: String
    /// The phase to show. A short gap between requests still counts as generating.
    public var displayPhase: Phase
    /// The last speed, carried across a gap while no tokens are being produced.
    public var heldTPS: Double?

    public static let initial = DisplayState(
        fraction: 0, tint: .none, modelLoaded: false, text: nil, textDimmed: false, summary: "", displayPhase: .idle)

    public init(
        fraction: Double, tint: Tint, modelLoaded: Bool, text: String?, textDimmed: Bool, summary: String,
        displayPhase: Phase, heldTPS: Double? = nil
    ) {
        self.fraction = fraction
        self.tint = tint
        self.modelLoaded = modelLoaded
        self.text = text
        self.textDimmed = textDimmed
        self.summary = summary
        self.displayPhase = displayPhase
        self.heldTPS = heldTPS
    }
}

/// Decides what the menu bar and window show. The ring is GPU load, except while a prompt is being
/// read, when it fills with prompt progress. Timing rules keep agent loops from flickering.
public struct DisplayPresenter: Sendable {
    public static let holdSeconds: TimeInterval = 5
    public static let prefillDelay: TimeInterval = 0.75
    public static let largePrefillTokens = 2048

    private var lastTPS: Double?
    private var lastGeneratingAt: Date?
    private var prefillTask: Int?
    private var prefillSeenAt: Date?

    public init() {}

    public mutating func present(_ model: MeterViewModel) -> DisplayState {
        let snapshot = model.snapshot
        let now = model.now
        let gpu = min(max((model.hardware.gpuUtilization ?? 0) / 100, 0), 1)
        var result = DisplayState(
            fraction: gpu, tint: .muted, modelLoaded: !model.models.isEmpty,
            text: nil, textDimmed: false, summary: "", displayPhase: snapshot.phase)

        switch snapshot.phase {
        case .offline:
            lastTPS = nil
            lastGeneratingAt = nil
            result.fraction = 0
            result.tint = .none
            result.summary = "Ollama is not running"

        case .loading:
            result.tint = .gpu
            result.text = "load"
            result.summary = "Ollama is loading a model"

        case .generating:
            if let tps = snapshot.liveTPS { lastTPS = tps }
            lastGeneratingAt = now
            result.tint = .gpu
            result.text = lastTPS.map(Format.menuTPS) ?? "– t/s"
            result.summary = "Generating at \(result.text ?? "unknown speed"), GPU \(Format.percent(gpu))"

        case .prefill:
            if snapshot.task != prefillTask {
                prefillTask = snapshot.task
                prefillSeenAt = now
            }
            let age = now.timeIntervalSince(prefillSeenAt ?? now)
            let sustained = age >= Self.prefillDelay
                || (snapshot.promptToProcess ?? 0) >= Self.largePrefillTokens
            if sustained, let fraction = snapshot.promptFraction {
                result.fraction = min(max(fraction, 0), 1)
                result.tint = .prompt
                result.text = Format.percent(fraction)
                result.summary = "Reading the prompt, \(Format.percent(fraction)) done"
            } else {
                result.tint = .gpu
                applyHold(to: &result, at: now)
                result.summary = "Reading the prompt"
            }

        case .idle:
            applyHold(to: &result, at: now)
            result.summary = model.models.isEmpty
                ? "Ollama is idle with no model loaded"
                : "Ollama is idle with \(model.models[0].name) loaded"
        }
        return result
    }

    private func applyHold(to result: inout DisplayState, at now: Date) {
        guard let tps = lastTPS, let at = lastGeneratingAt,
            now.timeIntervalSince(at) < Self.holdSeconds
        else { return }
        result.text = Format.menuTPS(tps)
        result.textDimmed = true
        result.displayPhase = .generating
        result.heldTPS = tps
    }
}
