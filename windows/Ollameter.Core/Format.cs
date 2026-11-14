using System.Globalization;

namespace Ollameter.Core;

public static class Format
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    private static long Round(double value) => (long)Math.Round(value, MidpointRounding.AwayFromZero);

    public static string Integer(long value) => value.ToString("#,0", Invariant);

    /// <summary>Speed for the window: one decimal below 100, grouped integer above.</summary>
    public static string Tps(double value) => value < 100 ? value.ToString("F1", Invariant) : Integer(Round(value));

    /// <summary>Speed for the tray tooltip, where a changing decimal is distracting.</summary>
    public static string MenuTps(double value)
    {
        if (value >= 1000) return (value / 1000).ToString("F1", Invariant) + "k t/s";
        if (value < 10) return value.ToString("F1", Invariant) + " t/s";
        return $"{Round(value)} t/s";
    }

    /// <summary>Whole percent from a 0...1 fraction, rounded down so 100% only appears when complete.</summary>
    public static string Percent(double fraction) =>
        $"{(long)Math.Floor(Math.Clamp(fraction, 0, 1) * 100)}%";

    public static string PercentOneDecimal(double fraction) => (fraction * 100).ToString("F1", Invariant) + "%";

    public static string Milliseconds(double value) => value < 1000 ? $"{Round(value)} ms" : Duration(value / 1000);

    public static string Duration(double seconds)
    {
        if (seconds < 60) return seconds.ToString("F1", Invariant) + " s";
        var whole = Round(seconds);
        return $"{whole / 60}m {whole % 60:00}s";
    }

    public static string Bytes(ulong value)
    {
        var gigabytes = value / 1_073_741_824.0;
        if (gigabytes >= 1) return gigabytes.ToString("F1", Invariant) + " GB";
        return $"{Round(value / 1_048_576.0)} MB";
    }

    public static string CompactTokens(long value)
    {
        if (value >= 1_000_000) return (value / 1_000_000.0).ToString("F1", Invariant) + "M";
        return value >= 1000 ? $"{Round(value / 1000.0)}k" : value.ToString(Invariant);
    }

    public static string Countdown(double seconds)
    {
        var whole = Math.Max(Round(seconds), 0);
        if (whole >= 3600) return $"{whole / 3600}h {whole % 3600 / 60:00}m";
        return $"{whole / 60}:{whole % 60:00}";
    }

    public static string Age(double seconds)
    {
        var whole = Math.Max((long)seconds, 0);
        if (whole < 60) return $"{whole}s";
        if (whole < 3600) return $"{whole / 60}m";
        return $"{whole / 3600}h";
    }

    /// <summary>A fixed-point number for the tiles, such as <c>16.9 W</c>.</summary>
    public static string Fixed(double value, int decimals, string unit) =>
        value.ToString("F" + decimals, Invariant) + " " + unit;
}
