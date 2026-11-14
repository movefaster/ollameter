using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace Ollameter;

/// <summary>
/// The same validated palette as the Mac app, per color scheme. Text only uses the ink tokens. Series colors
/// appear on marks, and status colors always sit beside an icon and words.
/// </summary>
internal sealed class Theme
{
    public bool IsDark { get; }
    public Brush Surface { get; }
    public Brush Ink { get; }
    public Brush Ink2 { get; }
    public Brush Muted { get; }
    public Brush Hairline { get; }
    public Brush HoverFill { get; }
    public Color PromptColor { get; }
    public Color GenerationColor { get; }
    public Color GpuColor { get; }
    public Color Ink2Color { get; }
    public Brush Prompt { get; }
    public Brush Generation { get; }
    public Brush Gpu { get; }
    public Brush Warning { get; }
    public Brush Serious { get; }
    public Brush Critical { get; }
    public Color HairlineColor { get; }

    public static readonly FontFamily Font = new("Segoe UI Variable Text, Segoe UI");
    public static readonly FontFamily DisplayFont = new("Segoe UI Variable Display, Segoe UI");

    public Theme(bool dark)
    {
        IsDark = dark;
        Surface = Brush(dark ? 0x1A1A19 : 0xFCFCFB);
        Ink = Brush(dark ? 0xF0EFEC : 0x0B0B0B);
        Ink2Color = Hex(dark ? 0xC3C2B7 : 0x52514E);
        Ink2 = Freeze(new SolidColorBrush(Ink2Color));
        Muted = Brush(0x898781);
        HairlineColor = Hex(dark ? 0x2C2C2A : 0xE1E0D9);
        Hairline = Freeze(new SolidColorBrush(HairlineColor));
        HoverFill = Brush(dark ? 0x262625 : 0xEDECE7);
        GenerationColor = Hex(dark ? 0x3987E5 : 0x2A78D6);
        PromptColor = Hex(dark ? 0xD95926 : 0xEB6834);
        GpuColor = Hex(dark ? 0x199E70 : 0x1BAF7A);
        Generation = Freeze(new SolidColorBrush(GenerationColor));
        Prompt = Freeze(new SolidColorBrush(PromptColor));
        Gpu = Freeze(new SolidColorBrush(GpuColor));
        Warning = Brush(0xFAB219);
        Serious = Brush(0xEC835A);
        Critical = Brush(0xD03B3B);
    }

    public static Color Hex(int value) =>
        Color.FromRgb((byte)((value >> 16) & 0xFF), (byte)((value >> 8) & 0xFF), (byte)(value & 0xFF));

    private static SolidColorBrush Brush(int value) => Freeze(new SolidColorBrush(Hex(value)));

    private static T Freeze<T>(T brush) where T : Freezable
    {
        brush.Freeze();
        return brush;
    }

    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    /// <summary>Whether apps use dark mode, from Settings → Personalization → Colors.</summary>
    public static bool AppsUseDark => ReadLight("AppsUseLightTheme") == 0;

    /// <summary>Whether the taskbar, where the tray icon sits, is dark. It can differ from apps.</summary>
    public static bool TaskbarIsDark => ReadLight("SystemUsesLightTheme") == 0;

    private static int ReadLight(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
        return key?.GetValue(name) is int value ? value : 1;
    }
}
