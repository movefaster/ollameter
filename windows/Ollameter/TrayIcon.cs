using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Ollameter.Core;

namespace Ollameter;

/// <summary>Draws the gauge for the notification area: GPU load, or prompt progress while a long prompt is read.</summary>
/// <remarks>
/// The Mac's menu bar has room for the speed beside the ring. A tray icon is a small square, so the speed goes in
/// the tooltip and the window instead.
/// </remarks>
internal static class TrayIconRenderer
{
    /// <summary>The ring has a gap at the bottom, so it runs from lower left, clockwise, to lower right.</summary>
    private const double StartAngle = 225;
    private const double Sweep = 270;

    public static BitmapSource Render(DisplayState presentation, int size, bool darkTaskbar)
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            Draw(context, presentation, size, darkTaskbar);
        }
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static void Draw(DrawingContext context, DisplayState presentation, int size, bool darkTaskbar)
    {
        var ink = darkTaskbar ? Colors.White : Colors.Black;
        // The Mac draws a 2.5-point stroke on a 16-point ring. Small icons keep at least 2 pixels of it.
        var stroke = Math.Max(2.0, size * 2.5 / 16);
        var radius = size / 2.0 - stroke / 2;
        var center = new Point(size / 2.0, size / 2.0);

        var track = new Pen(new SolidColorBrush(WithAlpha(ink, presentation.Tint == Tint.None ? 0.25 : 0.35)), stroke)
        {
            StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round,
        };
        context.DrawGeometry(null, track, Arc(center, radius, 1));

        if (presentation.Fraction > 0.005 && ArcColor(presentation.Tint, darkTaskbar, ink) is { } color)
        {
            var pen = new Pen(new SolidColorBrush(color), stroke) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            context.DrawGeometry(null, pen, Arc(center, radius, presentation.Fraction));
        }

        if (presentation.ModelLoaded)
        {
            var dot = Math.Max(1.5, size * 1.75 / 16);
            context.DrawEllipse(new SolidColorBrush(WithAlpha(ink, 0.9)), null, center, dot, dot);
        }
    }

    private static Geometry Arc(Point center, double radius, double fraction)
    {
        fraction = Math.Clamp(fraction, 0, 1);
        Point At(double degrees)
        {
            var radians = degrees * Math.PI / 180;
            // Angles are measured counterclockwise with y pointing up, as on the Mac.
            return new Point(center.X + radius * Math.Cos(radians), center.Y - radius * Math.Sin(radians));
        }
        var end = StartAngle - Sweep * fraction;
        var figure = new PathFigure { StartPoint = At(StartAngle), IsClosed = false };
        figure.Segments.Add(new ArcSegment(At(end), new Size(radius, radius), 0, Sweep * fraction > 180, SweepDirection.Clockwise, true));
        var geometry = new PathGeometry([figure]);
        geometry.Freeze();
        return geometry;
    }

    private static Color? ArcColor(Tint tint, bool dark, Color ink) => tint switch
    {
        Tint.Muted => WithAlpha(ink, 0.65),
        Tint.Gpu => Theme.Hex(dark ? 0x22C08A : 0x1BAF7A),
        Tint.Prompt => Theme.Hex(dark ? 0xF07038 : 0xEB6834),
        _ => null,
    };

    private static Color WithAlpha(Color color, double alpha) => Color.FromArgb((byte)(alpha * 255), color.R, color.G, color.B);

    /// <summary>Makes a Win32 icon from a rendered bitmap. The caller destroys it.</summary>
    public static IntPtr ToIcon(BitmapSource source)
    {
        // Icons take straight alpha, and WPF renders premultiplied.
        var bitmap = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var width = bitmap.PixelWidth;
        var height = bitmap.PixelHeight;
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);

        var header = new Native.BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
            biWidth = width,
            biHeight = -height,
            biPlanes = 1,
            biBitCount = 32,
        };
        var color = Native.CreateDIBSection(IntPtr.Zero, ref header, 0, out var bits, IntPtr.Zero, 0);
        if (color == IntPtr.Zero) return IntPtr.Zero;
        Marshal.Copy(pixels, 0, bits, pixels.Length);
        var mask = Native.CreateBitmap(width, height, 1, 1, IntPtr.Zero);
        var info = new Native.ICONINFO { fIcon = true, hbmColor = color, hbmMask = mask };
        var icon = Native.CreateIconIndirect(ref info);
        Native.DeleteObject(color);
        Native.DeleteObject(mask);
        return icon;
    }
}

/// <summary>The notification area icon: a left click toggles the window and a right click opens the menu.</summary>
internal sealed class TrayIcon : IDisposable
{
    private const int CallbackMessage = Native.WM_USER + 0x50;
    private const uint IconId = 1;
    /// <summary>szTip holds 128 characters with the terminator.</summary>
    private const int TipLimit = 127;

    private readonly HwndSource window;
    private readonly int taskbarCreated = Native.RegisterWindowMessage("TaskbarCreated");
    private IntPtr icon;
    private string tip = "Ollameter";
    private bool added;
    private (DisplayState Display, int Size, bool Dark)? lastDrawn;

    public event Action? Clicked;
    /// <summary>Runs with the point the menu should open at, in physical pixels.</summary>
    public event Action<Native.POINT>? MenuRequested;
    /// <summary>Runs when Windows switches between light and dark.</summary>
    public event Action? ThemeChanged;

    public IntPtr Handle => window.Handle;

    public TrayIcon()
    {
        // A hidden top-level window rather than a message-only one, which wouldn't hear TaskbarCreated.
        window = new HwndSource(new HwndSourceParameters("OllameterTray") { Width = 0, Height = 0, WindowStyle = 0 });
        window.AddHook(Hook);
    }

    public void Update(DisplayState display, IReadOnlyList<string> lines)
    {
        var size = Native.GetSystemMetricsForDpi(Native.SM_CXSMICON, Native.GetDpiForWindow(window.Handle));
        if (size <= 0) size = 16;
        var dark = Theme.TaskbarIsDark;
        var nextTip = Truncate(string.Join("\n", lines));
        var redraw = lastDrawn is not { } drawn || drawn.Size != size || drawn.Dark != dark || drawn.Display != display;
        if (!redraw && nextTip == tip && added) return;
        if (redraw)
        {
            var next = TrayIconRenderer.ToIcon(TrayIconRenderer.Render(display, size, dark));
            if (next != IntPtr.Zero)
            {
                if (icon != IntPtr.Zero) Native.DestroyIcon(icon);
                icon = next;
                lastDrawn = (display, size, dark);
            }
        }
        tip = nextTip;
        Notify(added ? Native.NIM_MODIFY : Native.NIM_ADD);
    }

    private static string Truncate(string text) => text.Length <= TipLimit ? text : text[..(TipLimit - 1)] + "…";

    private void Notify(uint message)
    {
        var data = new Native.NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<Native.NOTIFYICONDATA>(),
            hWnd = window.Handle,
            uID = IconId,
            uFlags = Native.NIF_MESSAGE | Native.NIF_ICON | Native.NIF_TIP | Native.NIF_SHOWTIP,
            uCallbackMessage = CallbackMessage,
            hIcon = icon,
            szTip = tip,
            szInfo = "",
            szInfoTitle = "",
        };
        if (!Native.Shell_NotifyIcon(message, ref data))
        {
            // The taskbar isn't there yet, such as early in sign-in. TaskbarCreated brings another try.
            if (message == Native.NIM_ADD) added = false;
            return;
        }
        if (message == Native.NIM_ADD)
        {
            added = true;
            data.uVersion = Native.NOTIFYICON_VERSION_4;
            Native.Shell_NotifyIcon(Native.NIM_SETVERSION, ref data);
        }
    }

    /// <summary>Where the icon is on screen, for placing the window near it the first time.</summary>
    public Native.RECT? Bounds
    {
        get
        {
            var identifier = new Native.NOTIFYICONIDENTIFIER
            {
                cbSize = Marshal.SizeOf<Native.NOTIFYICONIDENTIFIER>(), hWnd = window.Handle, uID = IconId,
            };
            return Native.Shell_NotifyIconGetRect(ref identifier, out var rect) == 0 ? rect : null;
        }
    }

    private IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == CallbackMessage)
        {
            // With version 4, the low word is the event and wParam carries the anchor point.
            var e = (int)(lParam.ToInt64() & 0xFFFF);
            var anchor = new Native.POINT { X = (short)(wParam.ToInt64() & 0xFFFF), Y = (short)((wParam.ToInt64() >> 16) & 0xFFFF) };
            switch (e)
            {
                case Native.NIN_SELECT:
                case Native.NIN_KEYSELECT:
                    Clicked?.Invoke();
                    break;
                case Native.WM_CONTEXTMENU:
                    MenuRequested?.Invoke(anchor);
                    break;
            }
            handled = true;
        }
        else if (message == taskbarCreated)
        {
            // Explorer restarted, and the icon went with it.
            added = false;
            Notify(Native.NIM_ADD);
        }
        else if (message == Native.WM_SETTINGCHANGE && Marshal.PtrToStringUni(lParam) == "ImmersiveColorSet")
        {
            lastDrawn = null;
            ThemeChanged?.Invoke();
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (added) Notify(Native.NIM_DELETE);
        added = false;
        if (icon != IntPtr.Zero) Native.DestroyIcon(icon);
        icon = IntPtr.Zero;
        window.Dispose();
    }
}

/// <summary>A native Win32 popup menu, which Windows 11 draws in its own style and in dark mode.</summary>
internal sealed class PopupMenu
{
    private readonly List<(uint Flags, string? Text, Action? Action)> items = [];

    public void Add(string text, Action action, bool isChecked = false, bool enabled = true) =>
        items.Add((Native.MF_STRING | (isChecked ? Native.MF_CHECKED : 0) | (enabled ? 0 : Native.MF_GRAYED), text, enabled ? action : null));

    public void AddDisabled(string text) => items.Add((Native.MF_STRING | Native.MF_GRAYED, text, null));

    public void AddSeparator() => items.Add((Native.MF_SEPARATOR, null, null));

    public IReadOnlyList<string> Snapshot() => items
        .Select(item => item.Flags == Native.MF_SEPARATOR ? "---"
            : ((item.Flags & Native.MF_GRAYED) != 0 ? "(disabled) " : "") + ((item.Flags & Native.MF_CHECKED) != 0 ? "[x] " : "") + item.Text)
        .ToList();

    public void Show(IntPtr owner, Native.POINT at)
    {
        var menu = Native.CreatePopupMenu();
        try
        {
            for (var index = 0; index < items.Count; index++)
                Native.AppendMenu(menu, items[index].Flags, (UIntPtr)(uint)(index + 1), items[index].Text);
            // Without this the menu doesn't close when the user clicks elsewhere.
            Native.SetForegroundWindow(owner);
            var align = Native.GetSystemMetrics(Native.SM_MENUDROPALIGNMENT) != 0 ? Native.TPM_RIGHTALIGN : 0;
            var chosen = Native.TrackPopupMenuEx(
                menu, Native.TPM_RETURNCMD | Native.TPM_NONOTIFY | Native.TPM_RIGHTBUTTON | Native.TPM_BOTTOMALIGN | align,
                at.X, at.Y, owner, IntPtr.Zero);
            Native.PostMessage(owner, Native.WM_NULL, IntPtr.Zero, IntPtr.Zero);
            if (chosen > 0 && chosen <= items.Count) items[chosen - 1].Action?.Invoke();
        }
        finally
        {
            Native.DestroyMenu(menu);
        }
    }
}
