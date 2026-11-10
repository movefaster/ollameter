import Foundation

public enum Format {
    public static func integer(_ value: Int) -> String {
        value.formatted(.number.grouping(.automatic).locale(Locale(identifier: "en_US")))
    }

    /// Speed for the window: one decimal below 100, grouped integer above.
    public static func tps(_ value: Double) -> String {
        value < 100 ? String(format: "%.1f", value) : integer(Int(value.rounded()))
    }

    /// Speed for the menu bar, where a changing decimal is distracting.
    public static func menuTPS(_ value: Double) -> String {
        if value >= 1000 { return String(format: "%.1fk t/s", value / 1000) }
        if value < 10 { return String(format: "%.1f t/s", value) }
        return "\(Int(value.rounded())) t/s"
    }

    /// Whole percent from a 0...1 fraction, rounded down so 100% only appears when complete.
    public static func percent(_ fraction: Double) -> String {
        "\(Int((min(max(fraction, 0), 1) * 100).rounded(.down)))%"
    }

    public static func percentOneDecimal(_ fraction: Double) -> String {
        String(format: "%.1f%%", fraction * 100)
    }

    public static func milliseconds(_ value: Double) -> String {
        value < 1000 ? "\(Int(value.rounded())) ms" : duration(value / 1000)
    }

    public static func duration(_ seconds: Double) -> String {
        if seconds < 60 { return String(format: "%.1f s", seconds) }
        let whole = Int(seconds.rounded())
        return "\(whole / 60)m \(String(format: "%02d", whole % 60))s"
    }

    public static func bytes(_ value: UInt64) -> String {
        let gigabytes = Double(value) / 1_073_741_824
        if gigabytes >= 1 { return String(format: "%.1f GB", gigabytes) }
        return "\(Int((Double(value) / 1_048_576).rounded())) MB"
    }

    public static func compactTokens(_ value: Int) -> String {
        if value >= 1_000_000 { return String(format: "%.1fM", Double(value) / 1_000_000) }
        return value >= 1000 ? "\(Int((Double(value) / 1000).rounded()))k" : "\(value)"
    }

    public static func countdown(_ seconds: TimeInterval) -> String {
        let whole = max(Int(seconds.rounded()), 0)
        if whole >= 3600 { return "\(whole / 3600)h \(String(format: "%02d", (whole % 3600) / 60))m" }
        return "\(whole / 60):\(String(format: "%02d", whole % 60))"
    }

    public static func age(_ seconds: TimeInterval) -> String {
        let whole = max(Int(seconds), 0)
        if whole < 60 { return "\(whole)s" }
        if whole < 3600 { return "\(whole / 60)m" }
        return "\(whole / 3600)h"
    }
}
