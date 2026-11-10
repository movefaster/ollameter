import MeterCore
import SwiftUI

enum MeterLayout {
    static let width: CGFloat = 400
    static let gutter: CGFloat = 16
}

extension Color {
    init(hex: UInt32) {
        self.init(
            .sRGB, red: Double((hex >> 16) & 0xFF) / 255, green: Double((hex >> 8) & 0xFF) / 255,
            blue: Double(hex & 0xFF) / 255)
    }
}

/// Colors are chosen per color scheme from the validated data-viz palette. Text only uses the ink
/// tokens. Series colors appear on marks, and status colors always sit beside an icon and words.
struct Theme {
    let surface, ink, ink2, muted, hairline: Color
    let prompt, generation, gpu: Color
    let warning, serious, critical: Color

    init(_ scheme: ColorScheme) {
        let dark = scheme == .dark
        surface = Color(hex: dark ? 0x1A1A19 : 0xFCFCFB)
        ink = Color(hex: dark ? 0xF0EFEC : 0x0B0B0B)
        ink2 = Color(hex: dark ? 0xC3C2B7 : 0x52514E)
        muted = Color(hex: 0x898781)
        hairline = Color(hex: dark ? 0x2C2C2A : 0xE1E0D9)
        generation = Color(hex: dark ? 0x3987E5 : 0x2A78D6)
        prompt = Color(hex: dark ? 0xD95926 : 0xEB6834)
        gpu = Color(hex: dark ? 0x199E70 : 0x1BAF7A)
        warning = Color(hex: 0xFAB219)
        serious = Color(hex: 0xEC835A)
        critical = Color(hex: 0xD03B3B)
    }
}

private struct ThemeKey: EnvironmentKey {
    static let defaultValue = Theme(.dark)
}

extension EnvironmentValues {
    var theme: Theme {
        get { self[ThemeKey.self] }
        set { self[ThemeKey.self] = newValue }
    }
}

struct Hairline: View {
    @Environment(\.theme) private var theme

    var body: some View {
        Rectangle().fill(theme.hairline).frame(height: 1)
    }
}

struct Sparkline: View {
    let values: [Double]
    /// With a slot count, values fill in from the right so the time scale stays fixed.
    var slots: Int?
    var maximum: Double?
    let color: Color

    @Environment(\.theme) private var theme

    var body: some View {
        Canvas { context, size in
            let baseline = size.height - 1
            guard values.count >= 2 else { return }
            let top = maximum ?? max((values.max() ?? 1) * 1.1, 0.001)
            let count = max(slots ?? values.count, values.count)
            let step = size.width / CGFloat(count - 1)
            let firstX = size.width - step * CGFloat(values.count - 1)
            let points = values.enumerated().map { index, value in
                CGPoint(
                    x: firstX + step * CGFloat(index),
                    y: baseline - CGFloat(min(value / top, 1)) * (baseline - 2))
            }

            var line = Path()
            line.addLines(points)
            var area = line
            area.addLine(to: CGPoint(x: size.width, y: baseline))
            area.addLine(to: CGPoint(x: firstX, y: baseline))
            area.closeSubpath()
            context.fill(area, with: .color(color.opacity(0.10)))
            context.stroke(
                line, with: .color(color), style: StrokeStyle(lineWidth: 2, lineCap: .round, lineJoin: .round))
        }
        .overlay(alignment: .bottom) { Hairline() }
    }
}

/// A rounded bar whose unfilled track is a lighter step of its own color.
struct Meter: View {
    let fraction: Double
    let color: Color
    var height: CGFloat = 8

    var body: some View {
        GeometryReader { proxy in
            ZStack(alignment: .leading) {
                Capsule().fill(color.opacity(0.18))
                Capsule()
                    .fill(color)
                    .frame(width: fraction > 0 ? max(height, proxy.size.width * min(fraction, 1)) : 0)
            }
        }
        .frame(height: height)
    }
}

struct SegmentedBar: View {
    let leadingFraction: Double
    let leadingColor: Color
    let trailingColor: Color

    private static let height: CGFloat = 8
    private static let gap: CGFloat = 2

    var body: some View {
        GeometryReader { proxy in
            let usable = max(proxy.size.width - Self.gap, 0)
            HStack(spacing: Self.gap) {
                Capsule().fill(leadingColor).frame(width: max(usable * leadingFraction, Self.height))
                Capsule().fill(trailingColor)
            }
        }
        .frame(height: Self.height)
    }
}

struct LegendRow: View {
    let color: Color
    let text: String

    @Environment(\.theme) private var theme

    var body: some View {
        HStack(spacing: 5) {
            Circle().fill(color).frame(width: 8, height: 8)
            Text(text).font(.system(size: 11)).foregroundStyle(theme.ink2).lineLimit(1)
        }
    }
}

struct StatTile: View {
    let label: String
    let value: String
    var note: String?

    @Environment(\.theme) private var theme

    var body: some View {
        VStack(alignment: .leading, spacing: 2) {
            Text(label).font(.system(size: 11)).foregroundStyle(theme.ink2).lineLimit(1)
            Text(value).font(.system(size: 15, weight: .semibold)).foregroundStyle(theme.ink).lineLimit(1)
            if let note {
                Text(note).font(.system(size: 11)).foregroundStyle(theme.ink2).lineLimit(1)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }
}

struct IconButton: View {
    let symbol: String
    let help: String
    let action: () -> Void

    @Environment(\.theme) private var theme

    var body: some View {
        Button(action: action) {
            Image(systemName: symbol)
                .font(.system(size: 11, weight: .semibold))
                .foregroundStyle(theme.ink2)
                .frame(width: 24, height: 24)
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .help(help)
        .accessibilityLabel(help)
    }
}
