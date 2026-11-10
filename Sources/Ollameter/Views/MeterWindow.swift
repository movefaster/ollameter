import MeterCore
import SwiftUI

struct MeterWindowRoot: View {
    @ObservedObject var monitor: Monitor
    @AppStorage("compact") private var compact = false
    let onClose: () -> Void
    let onHeightChange: (CGFloat) -> Void

    var body: some View {
        MeterContent(
            model: monitor.model, compact: compact, onToggleCompact: { compact.toggle() }, onClose: onClose,
            onUnload: { monitor.pressUnload(model: $0) }
        )
        .fixedSize(horizontal: false, vertical: true)
        .onGeometryChange(for: CGFloat.self) { $0.size.height } action: { onHeightChange($0) }
        .frame(maxHeight: .infinity, alignment: .top)
        .ignoresSafeArea()
    }
}

/// The whole window as a pure function of the view model, so it can also be rendered offscreen.
struct MeterContent: View {
    let model: MeterViewModel
    let compact: Bool
    var onToggleCompact: () -> Void = {}
    var onClose: () -> Void = {}
    var onUnload: (String) -> Void = { _ in }

    @Environment(\.colorScheme) private var colorScheme

    var body: some View {
        let theme = Theme(colorScheme)
        VStack(alignment: .leading, spacing: 12) {
            HeaderSection(
                model: model, compact: compact, onToggleCompact: onToggleCompact, onClose: onClose, onUnload: onUnload)
            HeroSection(model: model)
            if model.display.displayPhase == .offline {
                EmptyView()
            } else if compact {
                CompactRows(model: model)
            } else {
                Hairline()
                SpeedSection(stats: model.stats)
                Hairline()
                EfficiencySection(stats: model.stats)
                if let used = model.snapshot.contextUsed, let size = model.snapshot.contextSize, size > 0 {
                    ContextSection(used: used, size: size)
                }
                if let cache = model.promptCache, !model.models.isEmpty {
                    PromptCacheSection(cache: cache)
                }
                Hairline()
                HardwareSection(model: model)
                if model.models.count > 1 {
                    Hairline()
                    ModelsSection(models: model.models, now: model.now)
                }
                if !model.visibleNotices.isEmpty {
                    Hairline()
                    NoticesSection(notices: model.visibleNotices)
                }
                if !model.requests.isEmpty {
                    Hairline()
                    RequestsSection(requests: Array(model.requests.prefix(6)), now: model.now)
                }
                FooterSection(stats: model.stats)
            }
        }
        .padding(.horizontal, MeterLayout.gutter)
        .padding(.top, 10)
        .padding(.bottom, 14)
        .frame(width: MeterLayout.width, alignment: .leading)
        .background(theme.surface)
        .environment(\.theme, theme)
    }
}

private struct HeaderSection: View {
    let model: MeterViewModel
    let compact: Bool
    let onToggleCompact: () -> Void
    let onClose: () -> Void
    let onUnload: (String) -> Void

    @Environment(\.theme) private var theme

    private var phaseName: String {
        switch model.display.displayPhase {
        case .offline: return "Offline"
        case .idle: return "Idle"
        case .loading: return "Loading model"
        case .prefill: return "Reading prompt"
        case .generating: return "Generating"
        }
    }

    private var phaseColor: Color {
        switch model.display.displayPhase {
        case .offline: return theme.critical
        case .idle: return theme.muted
        case .loading, .prefill: return theme.prompt
        case .generating: return theme.generation
        }
    }

    private var details: String {
        guard let loaded = model.models.first else {
            return model.version.map { "Ollama \($0)" } ?? "Ollama"
        }
        var parts = [loaded.parameterSize, loaded.quantization].compactMap { $0 }
        if let context = loaded.contextLength { parts.append("\(Format.compactTokens(context)) ctx") }
        if let expires = loaded.expiresAt, expires > model.now {
            parts.append("unloads in \(Format.countdown(expires.timeIntervalSince(model.now)))")
        }
        return parts.joined(separator: " · ")
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 2) {
            HStack(spacing: 7) {
                Circle().fill(phaseColor).frame(width: 8, height: 8)
                Text(phaseName).font(.system(size: 12, weight: .semibold)).foregroundStyle(theme.ink)
                Spacer(minLength: 8)
                if model.display.displayPhase != .offline {
                    IconButton(
                        symbol: compact ? "chevron.down" : "chevron.up",
                        help: compact ? "Show all metrics" : "Show less", action: onToggleCompact)
                }
                IconButton(symbol: "xmark", help: "Hide window", action: onClose)
            }
            HStack(spacing: 8) {
                Text(model.models.first?.name ?? "No model loaded")
                    .font(.system(size: 13, weight: .medium))
                    .foregroundStyle(theme.ink)
                    .lineLimit(1)
                    .truncationMode(.middle)
                Spacer(minLength: 0)
                if let name = model.models.first?.name {
                    UnloadButton(model: model, name: name, onPress: onUnload)
                }
            }
            // Keeps the window from changing height when the button comes and goes.
            .frame(minHeight: UnloadButton.height)
            if !compact, model.display.displayPhase != .offline {
                Text(details).font(.system(size: 11)).foregroundStyle(theme.ink2).lineLimit(1)
            }
        }
    }
}

private struct UnloadButton: View {
    let model: MeterViewModel
    let name: String
    let onPress: (String) -> Void

    @Environment(\.theme) private var theme

    static let height: CGFloat = 20

    private enum Look { case ready, working, confirming, unloading, failed }

    private var look: Look {
        switch model.unload {
        case .unloading(let target) where target == name: return .unloading
        case .confirming(let target) where target == name && !model.isWorking: return .confirming
        case .failed(let target) where target == name: return .failed
        default: return model.isWorking ? .working : .ready
        }
    }

    private var title: String {
        switch look {
        case .ready, .working: return "Unload"
        case .confirming: return "Click again to unload"
        case .unloading: return "Unloading…"
        case .failed: return "Couldn't unload"
        }
    }

    private var help: String {
        switch look {
        case .ready, .confirming:
            return "Unload \(name) from memory now. Its saved prompts go too, so the next request reads its whole prompt again."
        case .working: return "Available when the model is idle."
        case .unloading: return "Waiting for Ollama to free the model's memory."
        case .failed: return "Ollama didn't unload the model. Click to try again."
        }
    }

    private var isWarning: Bool { look == .confirming || look == .failed }

    var body: some View {
        Button { onPress(name) } label: {
            HStack(spacing: 4) {
                Image(systemName: isWarning ? "exclamationmark.triangle.fill" : "eject")
                    .font(.system(size: 11, weight: .semibold))
                    .foregroundStyle(isWarning ? theme.warning : theme.ink2)
                Text(title)
                    .font(.system(size: 11, weight: .medium))
                    .foregroundStyle(isWarning ? theme.ink : theme.ink2)
                    .lineLimit(1)
            }
            .padding(.horizontal, 6)
            .frame(height: Self.height)
            .background(RoundedRectangle(cornerRadius: 5).fill(theme.hairline))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .disabled(look == .working || look == .unloading)
        .opacity(look == .working ? 0.5 : 1)
        .fixedSize()
        .help(help)
        .accessibilityLabel(title)
        .accessibilityHint(help)
    }
}

private struct HeroSection: View {
    let model: MeterViewModel

    @Environment(\.theme) private var theme

    private var phase: Phase { model.display.displayPhase }

    /// True when the number shown is a remembered speed rather than one being measured now.
    private var isHistoric: Bool {
        phase == .idle || (phase == .generating && model.snapshot.phase != .generating)
    }

    private var value: String {
        let snapshot = model.snapshot
        switch phase {
        case .offline: return "–"
        case .idle: return model.requests.first.map { Format.tps($0.generationTPS) } ?? "–"
        case .loading: return "…"
        case .prefill: return snapshot.promptFraction.map(Format.percent) ?? "…"
        case .generating: return (snapshot.liveTPS ?? model.display.heldTPS).map(Format.tps) ?? "…"
        }
    }

    private var unit: String? {
        switch phase {
        case .prefill: return "of prompt"
        case .generating: return "t/s"
        case .idle: return model.requests.isEmpty ? nil : "t/s"
        case .offline, .loading: return nil
        }
    }

    private var detail: String {
        let snapshot = model.snapshot
        switch phase {
        case .offline:
            return "Ollama isn't running on this Mac."
        case .loading:
            return "Moving the model into memory."
        case .idle:
            guard let last = model.requests.first else { return "Waiting for a request." }
            return "Last request · \(Format.integer(last.generatedTokens)) tokens"
        case .prefill:
            var parts: [String] = []
            if let done = snapshot.promptDone, let total = snapshot.promptToProcess {
                parts.append("\(Format.integer(done)) of \(Format.integer(total)) new tokens")
            }
            if let tps = snapshot.promptTPS { parts.append("\(Format.tps(tps)) t/s") }
            if let eta = snapshot.etaSeconds { parts.append("\(Format.duration(eta)) left") }
            return parts.joined(separator: " · ")
        case .generating:
            guard snapshot.phase == .generating else { return "Between requests" }
            var parts: [String] = []
            if let tokens = snapshot.generatedTokens { parts.append("\(Format.integer(tokens)) tokens") }
            if let elapsed = snapshot.phaseElapsed { parts.append(Format.duration(elapsed)) }
            return parts.joined(separator: " · ")
        }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack(alignment: .bottom, spacing: 16) {
                HStack(alignment: .firstTextBaseline, spacing: 5) {
                    Text(value)
                        .font(.system(size: 52, weight: .semibold))
                        .foregroundStyle(isHistoric ? theme.ink2 : theme.ink)
                        .lineLimit(1)
                    if let unit {
                        Text(unit).font(.system(size: 15)).foregroundStyle(theme.ink2).lineLimit(1)
                    }
                }
                .layoutPriority(1)
                Spacer(minLength: 0)
                if phase != .offline {
                    VStack(alignment: .trailing, spacing: 3) {
                        Sparkline(values: model.tpsHistory, slots: MeterViewModel.historyLimit, color: theme.generation)
                            .frame(width: 130, height: 40)
                            .help("Generation speed over the last 60 seconds")
                        Text("t/s, last 60 s").font(.system(size: 11)).foregroundStyle(theme.ink2)
                    }
                }
            }
            Text(detail).font(.system(size: 12)).foregroundStyle(theme.ink2).lineLimit(1)
            // The bar's space stays reserved so the window doesn't resize when a prompt starts.
            if phase != .offline {
                Meter(fraction: model.snapshot.promptFraction ?? 0, color: theme.prompt)
                    .opacity(phase == .prefill ? 1 : 0)
                    .accessibilityHidden(phase != .prefill)
            }
        }
    }
}

private struct CompactRows: View {
    let model: MeterViewModel

    @Environment(\.theme) private var theme

    var body: some View {
        Grid(alignment: .leading, horizontalSpacing: 10, verticalSpacing: 8) {
            GridRow {
                label("GPU")
                Meter(fraction: (model.hardware.gpuUtilization ?? 0) / 100, color: theme.gpu)
                value(gpuSummary)
            }
            if let cache = model.promptCache, !model.models.isEmpty {
                let severity = (cache.fraction ?? 0) >= PromptCacheSection.warningFraction ? theme.warning : nil
                GridRow {
                    HStack(spacing: 4) {
                        if let severity {
                            Image(systemName: "exclamationmark.triangle.fill")
                                .font(.system(size: 11))
                                .foregroundStyle(severity)
                        }
                        label("Cache")
                    }
                    Meter(fraction: cache.fraction ?? 0, color: severity ?? theme.ink2)
                        .help(PromptCacheSection.help)
                    value(cache.usageText)
                }
            }
        }
    }

    private func label(_ text: String) -> some View {
        Text(text).font(.system(size: 11)).foregroundStyle(theme.ink2)
    }

    private func value(_ text: String) -> some View {
        Text(text)
            .font(.system(size: 12, weight: .medium).monospacedDigit())
            .foregroundStyle(theme.ink)
            .lineLimit(1)
            .fixedSize()
            .gridColumnAlignment(.trailing)
    }

    private var gpuSummary: String {
        var parts = [model.hardware.gpuUtilization.map { "\(Int($0))%" } ?? "–"]
        if let watts = model.hardware.gpuPowerWatts { parts.append(String(format: "%.0f W", watts)) }
        if let temperature = model.hardware.dieTemperature { parts.append(String(format: "%.0f °C", temperature)) }
        return parts.joined(separator: " · ")
    }
}

private struct SpeedSection: View {
    let stats: SessionStats

    @Environment(\.theme) private var theme

    var body: some View {
        HStack(alignment: .top, spacing: 20) {
            SpeedColumn(title: "Prompt speed", stat: stats.prompt, color: theme.prompt)
            SpeedColumn(title: "Generation speed", stat: stats.generation, color: theme.generation)
        }
    }
}

private struct SpeedColumn: View {
    let title: String
    let stat: SpeedStat
    let color: Color

    @Environment(\.theme) private var theme

    private var range: String {
        guard let peak = stat.peak, let low = stat.low else { return "No requests yet" }
        return "Peak \(Format.tps(peak)) · Low \(Format.tps(low))"
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(title).font(.system(size: 11)).foregroundStyle(theme.ink2)
            HStack(alignment: .firstTextBaseline, spacing: 4) {
                Text(stat.average.map(Format.tps) ?? "–")
                    .font(.system(size: 22, weight: .semibold))
                    .foregroundStyle(theme.ink)
                Text("t/s average").font(.system(size: 11)).foregroundStyle(theme.ink2)
            }
            Sparkline(values: stat.history, color: color)
                .frame(height: 28)
                .help("Speed of each of the last \(SpeedStat.historyLimit) requests")
            Text(range).font(.system(size: 11)).foregroundStyle(theme.ink2).lineLimit(1)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }
}

private struct EfficiencySection: View {
    let stats: SessionStats

    @Environment(\.theme) private var theme

    private var firstTokenRange: String? {
        guard let low = stats.firstTokenMin, let high = stats.firstTokenMax else { return nil }
        return "\(Format.milliseconds(low)) to \(Format.milliseconds(high))"
    }

    var body: some View {
        HStack(alignment: .top, spacing: 12) {
            StatTile(
                label: "First token, average",
                value: stats.firstTokenAverage.map(Format.milliseconds) ?? "–",
                note: firstTokenRange)
            StatTile(
                label: "Cache reuse",
                value: stats.cacheFraction.map(Format.percentOneDecimal) ?? "–",
                note: stats.cachedTokens > 0 ? "\(Format.compactTokens(stats.cachedTokens)) tokens saved" : nil)
            VStack(alignment: .leading, spacing: 4) {
                Text("Time spent").font(.system(size: 11)).foregroundStyle(theme.ink2)
                if let share = stats.promptTimeFraction {
                    let promptPercent = Int((share * 100).rounded())
                    SegmentedBar(leadingFraction: share, leadingColor: theme.prompt, trailingColor: theme.generation)
                        .padding(.vertical, 2)
                    LegendRow(color: theme.prompt, text: "Prompt \(promptPercent)%")
                    LegendRow(color: theme.generation, text: "Generate \(100 - promptPercent)%")
                } else {
                    Text("–").font(.system(size: 15, weight: .semibold)).foregroundStyle(theme.ink)
                }
            }
            .frame(maxWidth: .infinity, alignment: .leading)
        }
    }
}

private struct ContextSection: View {
    let used: Int
    let size: Int

    @Environment(\.theme) private var theme

    private var fraction: Double { Double(used) / Double(size) }

    private var severityColor: Color? {
        if fraction >= 0.95 { return theme.critical }
        return fraction >= 0.8 ? theme.warning : nil
    }

    var body: some View {
        UsageBar(
            label: "Context used",
            value: "\(Format.integer(used)) of \(Format.integer(size)) · \(Format.percent(fraction))",
            fraction: fraction, severity: severityColor)
    }
}

private struct PromptCacheSection: View {
    let cache: PromptCacheState

    @Environment(\.theme) private var theme

    static let help =
        "Prompts llama-server keeps in RAM so they don't have to be read again. The prompt loaded right now isn't counted."
    // Filling up is normal for a cache, so this stops at a warning. Dropped prompts get a notice.
    static let warningFraction = 0.8

    private var fraction: Double { cache.fraction ?? 0 }

    private var note: String {
        guard cache.savedPrompts > 0 else { return "Nothing saved yet" }
        var parts = ["\(cache.savedPrompts) saved \(cache.savedPrompts == 1 ? "prompt" : "prompts")"]
        if cache.evictions > 0 { parts.append("\(cache.evictions) dropped") }
        if !cache.savedTokens.isEmpty {
            parts.append(cache.savedTokens.map(Format.compactTokens).joined(separator: ", ") + " tokens")
        }
        return parts.joined(separator: " · ")
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            UsageBar(
                label: "Prompt cache", value: cache.usageText, fraction: fraction,
                severity: fraction >= Self.warningFraction ? theme.warning : nil)
            Text(note).font(.system(size: 11)).foregroundStyle(theme.ink2).lineLimit(1)
        }
        .help(Self.help)
    }
}

extension PromptCacheState {
    fileprivate var usageText: String {
        let used = Format.bytes(usedBytes)
        guard let limitBytes, let fraction else { return "\(used) · no limit" }
        return "\(used) of \(Format.bytes(limitBytes)) · \(Format.percent(fraction))"
    }
}

/// A labeled bar for something that fills up. It stays neutral until it nears its limit.
private struct UsageBar: View {
    let label: String
    let value: String
    let fraction: Double
    let severity: Color?

    @Environment(\.theme) private var theme

    var body: some View {
        VStack(alignment: .leading, spacing: 5) {
            HStack(spacing: 4) {
                if let severity {
                    Image(systemName: "exclamationmark.triangle.fill")
                        .font(.system(size: 11))
                        .foregroundStyle(severity)
                }
                Text(label).font(.system(size: 11)).foregroundStyle(theme.ink2)
                Spacer()
                Text(value)
                    .font(.system(size: 11).monospacedDigit())
                    .foregroundStyle(theme.ink2)
            }
            Meter(fraction: fraction, color: severity ?? theme.ink2)
        }
    }
}

private struct HardwareSection: View {
    let model: MeterViewModel

    @Environment(\.theme) private var theme

    private var hardware: HardwareSample { model.hardware }

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack(alignment: .center, spacing: 16) {
                VStack(alignment: .leading, spacing: 0) {
                    Text("GPU").font(.system(size: 11)).foregroundStyle(theme.ink2)
                    Text(hardware.gpuUtilization.map { "\(Int($0))%" } ?? "–")
                        .font(.system(size: 22, weight: .semibold))
                        .foregroundStyle(theme.ink)
                }
                .frame(width: 62, alignment: .leading)
                Sparkline(values: model.gpuHistory, slots: MeterViewModel.historyLimit, maximum: 100, color: theme.gpu)
                    .frame(height: 36)
                    .help("GPU load over the last 60 seconds, 0 to 100%")
            }
            HStack(alignment: .top, spacing: 8) {
                StatTile(label: "GPU power", value: hardware.gpuPowerWatts.map { String(format: "%.1f W", $0) } ?? "–")
                StatTile(label: "SoC die", value: hardware.dieTemperature.map { String(format: "%.0f °C", $0) } ?? "–")
                StatTile(label: "Energy", value: model.joulesPerToken.map { String(format: "%.2f J/t", $0) } ?? "–")
                PressureTile(pressure: hardware.memoryPressure)
            }
            HStack(alignment: .top, spacing: 8) {
                StatTile(label: "GPU memory", value: hardware.gpuMemoryInUse.map(Format.bytes) ?? "–")
                StatTile(label: "Runner RAM", value: model.runner?.residentBytes.map(Format.bytes) ?? "–")
                StatTile(label: "Runner CPU", value: model.runner?.cpuPercent.map { "\(Int($0))%" } ?? "–")
                StatTile(label: "Swap used", value: hardware.swapUsed.map(Format.bytes) ?? "–")
            }
        }
    }
}

private struct PressureTile: View {
    let pressure: MemoryPressure?

    @Environment(\.theme) private var theme

    var body: some View {
        VStack(alignment: .leading, spacing: 2) {
            Text("Mem. pressure").font(.system(size: 11)).foregroundStyle(theme.ink2).lineLimit(1)
            HStack(spacing: 4) {
                if let pressure, pressure != .normal {
                    Image(systemName: "exclamationmark.triangle.fill")
                        .font(.system(size: 11))
                        .foregroundStyle(pressure == .critical ? theme.critical : theme.warning)
                }
                Text(pressure?.rawValue ?? "–")
                    .font(.system(size: 15, weight: .semibold))
                    .foregroundStyle(theme.ink)
                    .lineLimit(1)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }
}

private struct ModelsSection: View {
    let models: [LoadedModel]
    let now: Date

    @Environment(\.theme) private var theme

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            Text("Loaded models").font(.system(size: 11, weight: .semibold)).foregroundStyle(theme.ink2)
            ForEach(models) { model in
                HStack {
                    Text(model.name).font(.system(size: 12)).foregroundStyle(theme.ink).lineLimit(1).truncationMode(.middle)
                    Spacer(minLength: 8)
                    Text(summary(model))
                        .font(.system(size: 11).monospacedDigit())
                        .foregroundStyle(theme.ink2)
                        .lineLimit(1)
                        .fixedSize()
                }
            }
        }
    }

    private func summary(_ model: LoadedModel) -> String {
        var text = Format.bytes(model.size)
        if let expires = model.expiresAt, expires > now {
            text += " · \(Format.countdown(expires.timeIntervalSince(now)))"
        }
        return text
    }
}

private struct NoticesSection: View {
    let notices: [Notice]

    @Environment(\.theme) private var theme

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            ForEach(notices) { notice in
                HStack(alignment: .firstTextBaseline, spacing: 6) {
                    Image(systemName: "exclamationmark.triangle.fill")
                        .font(.system(size: 11))
                        .foregroundStyle(notice.severity == .serious ? theme.serious : theme.warning)
                    Text(notice.text)
                        .font(.system(size: 12))
                        .foregroundStyle(theme.ink)
                        .fixedSize(horizontal: false, vertical: true)
                }
            }
        }
    }
}

private struct RequestsSection: View {
    let requests: [CompletedRequest]
    let now: Date

    @Environment(\.theme) private var theme

    private enum Column {
        static let age: CGFloat = 30
        static let prompt: CGFloat = 50
        static let cache: CGFloat = 46
        static let output: CGFloat = 46
        static let promptSpeed: CGFloat = 62
        static let generationSpeed: CGFloat = 50
        static let total: CGFloat = 50
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            Text("Recent requests").font(.system(size: 11, weight: .semibold)).foregroundStyle(theme.ink2)
            row(
                age: "Age", prompt: "Prompt", cache: "Cache", output: "Output",
                promptSpeed: "Prompt t/s", generationSpeed: "Gen t/s", total: "Total", color: theme.ink2)
            Hairline()
            ForEach(requests) { request in
                row(
                    age: request.finishedAt.map { Format.age(now.timeIntervalSince($0)) } ?? "–",
                    prompt: Format.integer(request.promptTokens),
                    cache: Format.percent(request.cacheFraction),
                    output: Format.integer(request.generatedTokens),
                    promptSpeed: Format.tps(request.promptTPS),
                    generationSpeed: Format.tps(request.generationTPS),
                    total: Format.milliseconds(request.totalMilliseconds),
                    color: theme.ink)
            }
        }
    }

    private func row(
        age: String, prompt: String, cache: String, output: String, promptSpeed: String,
        generationSpeed: String, total: String, color: Color
    ) -> some View {
        HStack(spacing: 6) {
            cell(age, width: Column.age, alignment: .leading)
            cell(prompt, width: Column.prompt)
            cell(cache, width: Column.cache)
            cell(output, width: Column.output)
            cell(promptSpeed, width: Column.promptSpeed)
            cell(generationSpeed, width: Column.generationSpeed)
            cell(total, width: Column.total)
        }
        .foregroundStyle(color)
    }

    private func cell(_ text: String, width: CGFloat, alignment: Alignment = .trailing) -> some View {
        Text(text)
            .font(.system(size: 11).monospacedDigit())
            .lineLimit(1)
            .frame(width: width, alignment: alignment)
    }
}

private struct FooterSection: View {
    let stats: SessionStats

    @Environment(\.theme) private var theme

    var body: some View {
        if stats.requests > 0 {
            Text("Since this model loaded: \(Format.integer(stats.requests)) requests, \(Format.integer(stats.generatedTokens)) tokens generated")
                .font(.system(size: 11))
                .foregroundStyle(theme.ink2)
                .lineLimit(1)
        }
    }
}
