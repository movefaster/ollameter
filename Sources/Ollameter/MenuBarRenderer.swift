import AppKit
import MeterCore

/// Draws the gauge and the speed readout into one image, so its size and colors are exact.
enum MenuBarRenderer {
    static let height: CGFloat = 22
    private static let gaugeDiameter: CGFloat = 16
    private static let strokeWidth: CGFloat = 2.5
    private static let gaugeTextGap: CGFloat = 4
    private static let widestText = "999 t/s"
    /// The ring has a gap at the bottom, so it runs from lower left, clockwise, to lower right.
    private static let startAngle: CGFloat = 225
    private static let sweep: CGFloat = 270

    private static let font = NSFont.monospacedDigitSystemFont(ofSize: 12, weight: .medium)

    private static let reservedTextWidth: CGFloat = ceil(
        (widestText as NSString).size(withAttributes: [.font: font]).width)

    static func size(for presentation: DisplayState) -> NSSize {
        let textWidth = presentation.text == nil ? 0 : gaugeTextGap + reservedTextWidth
        return NSSize(width: gaugeDiameter + textWidth, height: height)
    }

    static func image(for presentation: DisplayState, appearance: NSAppearance) -> NSImage {
        NSImage(size: size(for: presentation), flipped: false) { rect in
            appearance.performAsCurrentDrawingAppearance {
                draw(presentation, in: rect, appearance: appearance)
            }
            return true
        }
    }

    private static func draw(_ presentation: DisplayState, in rect: NSRect, appearance: NSAppearance) {
        let isDark = appearance.bestMatch(from: [.darkAqua, .aqua]) == .darkAqua
        let center = NSPoint(x: gaugeDiameter / 2, y: rect.midY)
        let radius = gaugeDiameter / 2 - strokeWidth / 2

        NSColor.labelColor.withAlphaComponent(presentation.tint == .none ? 0.18 : 0.28).setStroke()
        strokeArc(center: center, radius: radius, fraction: 1)

        if presentation.fraction > 0.005, let color = arcColor(for: presentation.tint, isDark: isDark) {
            color.setStroke()
            strokeArc(center: center, radius: radius, fraction: presentation.fraction)
        }

        if presentation.modelLoaded {
            NSColor.labelColor.withAlphaComponent(0.85).setFill()
            NSBezierPath(ovalIn: NSRect(x: center.x - 1.75, y: center.y - 1.75, width: 3.5, height: 3.5)).fill()
        }

        if let text = presentation.text {
            let color = NSColor.labelColor.withAlphaComponent(presentation.textDimmed ? 0.55 : 1)
            let attributes: [NSAttributedString.Key: Any] = [.font: font, .foregroundColor: color]
            let textSize = (text as NSString).size(withAttributes: attributes)
            (text as NSString).draw(
                at: NSPoint(x: gaugeDiameter + gaugeTextGap, y: (rect.height - textSize.height) / 2),
                withAttributes: attributes)
        }
    }

    private static func strokeArc(center: NSPoint, radius: CGFloat, fraction: Double) {
        let path = NSBezierPath()
        path.appendArc(
            withCenter: center, radius: radius, startAngle: startAngle,
            endAngle: startAngle - sweep * CGFloat(min(max(fraction, 0), 1)), clockwise: true)
        path.lineWidth = strokeWidth
        path.lineCapStyle = .round
        path.stroke()
    }

    private static func arcColor(for tint: DisplayState.Tint, isDark: Bool) -> NSColor? {
        switch tint {
        case .none: return nil
        case .muted: return NSColor.labelColor.withAlphaComponent(0.6)
        case .gpu: return hex(isDark ? 0x199E70 : 0x1BAF7A)
        case .prompt: return hex(isDark ? 0xD95926 : 0xEB6834)
        }
    }

    private static func hex(_ value: UInt32) -> NSColor {
        NSColor(
            srgbRed: CGFloat((value >> 16) & 0xFF) / 255, green: CGFloat((value >> 8) & 0xFF) / 255,
            blue: CGFloat(value & 0xFF) / 255, alpha: 1)
    }
}
