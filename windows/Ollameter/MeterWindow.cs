using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using Ollameter.Core;

namespace Ollameter;

/// <summary>Measures its child with no limits and reports the size it wants, so the window can take that size.</summary>
internal sealed class MeasureHost : System.Windows.Controls.Decorator
{
    public Size Wanted { get; private set; }
    public event Action<Size>? WantedChanged;

    protected override Size MeasureOverride(Size constraint)
    {
        if (Child is null) return default;
        Child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = Child.DesiredSize;
        if (size != Wanted)
        {
            Wanted = size;
            // Resizing the window from inside a layout pass would start another, so it waits for this one to end.
            Dispatcher.BeginInvoke(() => WantedChanged?.Invoke(size));
        }
        return size;
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        Child?.Arrange(new Rect(Child.DesiredSize));
        return arrangeSize;
    }
}

/// <summary>
/// The always-on-top window. It never takes focus from the app being used, stays out of the taskbar and
/// Alt+Tab, and moves by dragging anywhere that isn't a button.
/// </summary>
internal sealed class MeterWindow : Window
{
    private const int ScreenMargin = 12;
    private const int TopStrip = 24;

    private readonly MeterHub hub;
    private readonly Settings settings = Settings.Current;
    private readonly MeasureHost host = new();
    private readonly MeterBoard board;
    private Theme theme = new(Theme.AppsUseDark);
    private IntPtr hwnd;
    private bool compact;
    private Native.POINT? dragStart;
    private Native.RECT dragOrigin;

    /// <summary>Where the window goes the first time, near the tray icon.</summary>
    public Func<Native.RECT?>? PlacementHint { get; set; }

    public MeterWindow(MeterHub hub)
    {
        this.hub = hub;
        compact = settings.Compact;
        Title = "Ollameter";
        WindowStyle = WindowStyle.SingleBorderWindow;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        SizeToContent = SizeToContent.Manual;
        Width = MeterLayout.Width;
        Height = 300;
        Topmost = settings.AlwaysOnTop ?? true;
        UseLayoutRounding = true;
        // The frame stays, for the shadow and rounded corners, but the content covers all of it.
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0,
            ResizeBorderThickness = new Thickness(0),
            GlassFrameThickness = new Thickness(0, 0, 0, 1),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
            NonClientFrameEdges = NonClientFrameEdges.None,
        });

        board = new MeterBoard(Sharing.HostName)
        {
            ToggleCompact = ToggleCompact,
            Close = () => SetVisible(false),
            Unload = hub.PressUnload,
        };
        host.Child = board;
        host.WantedChanged += Fit;
        Content = host;
        Background = theme.Surface;

        SourceInitialized += (_, _) => Initialize();
        MouseLeftButtonDown += BeginDrag;
        MouseMove += Drag;
        MouseLeftButtonUp += EndDrag;
        LostMouseCapture += (_, _) => dragStart = null;
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(() => Fit(host.Wanted));
        new WindowInteropHelper(this).EnsureHandle();
    }

    public bool IsAlwaysOnTop => Topmost;

    public bool IsCompact => compact;

    public MeterBoard Board => board;

    private void Initialize()
    {
        hwnd = new WindowInteropHelper(this).Handle;
        var style = (long)Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE);
        Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, (IntPtr)(style | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW));
        var round = Native.DWMWCP_ROUND;
        Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        ApplyFrameColors();
        HwndSource.FromHwnd(hwnd)?.AddHook(Hook);
        Refresh();
        UpdateLayout();
        Place();
    }

    private void ApplyFrameColors()
    {
        if (hwnd == IntPtr.Zero) return;
        var dark = theme.IsDark ? 1 : 0;
        Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        var color = theme.HairlineColor;
        var border = color.R | (color.G << 8) | (color.B << 16);
        Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_BORDER_COLOR, ref border, sizeof(int));
    }

    private IntPtr Hook(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == Native.WM_SETTINGCHANGE && System.Runtime.InteropServices.Marshal.PtrToStringUni(lParam) == "ImmersiveColorSet")
            Dispatcher.BeginInvoke(ApplyTheme);
        return IntPtr.Zero;
    }

    /// <summary>Follows Windows between light and dark.</summary>
    public void ApplyTheme()
    {
        var dark = Theme.AppsUseDark;
        if (dark == theme.IsDark) return;
        theme = new Theme(dark);
        Background = theme.Surface;
        ApplyFrameColors();
        Refresh();
    }

    public void Refresh() => board.Update(hub.Models, compact, theme);

    private void ToggleCompact()
    {
        compact = !compact;
        settings.Compact = compact;
        settings.Save();
        Refresh();
    }

    /// <summary>The saved spot when its top is still on a screen, or else the top right of the main screen.</summary>
    private void Place()
    {
        if (!Native.GetWindowRect(hwnd, out var rect)) return;
        if (settings.WindowLeft is { } left && settings.WindowTop is { } top)
        {
            var strip = new Native.RECT { Left = left, Top = top, Right = left + rect.Width, Bottom = top + TopStrip };
            if (Native.MonitorFromRect(ref strip, Native.MONITOR_DEFAULTTONULL) != IntPtr.Zero)
            {
                Move(left, top);
                return;
            }
        }
        var hint = PlacementHint?.Invoke();
        var monitor = hint is { } icon
            ? Native.MonitorFromRect(ref icon, Native.MONITOR_DEFAULTTOPRIMARY)
            : Native.MonitorFromPoint(default, Native.MONITOR_DEFAULTTOPRIMARY);
        if (Native.WorkArea(monitor) is not { } area) return;
        var margin = (int)Math.Round(ScreenMargin * Scale);
        Move(area.Right - rect.Width - margin, area.Top + margin);
    }

    private double Scale => hwnd == IntPtr.Zero ? 1 : Native.GetDpiForWindow(hwnd) / 96.0;

    private void Move(int x, int y) =>
        Native.SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);

    /// <summary>
    /// Keeps the top edge in place as the content grows or shrinks. Columns for more computers grow away from the
    /// nearer side of the screen, and the window stays on screen.
    /// </summary>
    private void Fit(Size wanted)
    {
        if (wanted.Width <= 0 || wanted.Height <= 0) return;
        if (hwnd == IntPtr.Zero)
        {
            Width = wanted.Width;
            Height = wanted.Height;
            return;
        }
        var scale = Scale;
        var width = (int)Math.Ceiling(wanted.Width * scale);
        var height = (int)Math.Ceiling(wanted.Height * scale);
        if (!Native.GetWindowRect(hwnd, out var rect)) return;
        if (rect.Width == width && rect.Height == height) return;
        var x = rect.Left;
        var strip = new Native.RECT { Left = rect.Left, Top = rect.Top, Right = rect.Right, Bottom = rect.Top + TopStrip };
        if (Native.WorkArea(Native.MonitorFromRect(ref strip, Native.MONITOR_DEFAULTTONEAREST)) is { } area)
        {
            if ((rect.Left + rect.Right) / 2 > (area.Left + area.Right) / 2) x = rect.Right - width;
            x = Math.Min(Math.Max(x, area.Left), Math.Max(area.Right - width, area.Left));
        }
        Native.SetWindowPos(hwnd, IntPtr.Zero, x, rect.Top, width, height, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        SavePosition();
    }

    public void SavePosition()
    {
        if (hwnd == IntPtr.Zero || !Native.GetWindowRect(hwnd, out var rect)) return;
        if (settings.WindowLeft == rect.Left && settings.WindowTop == rect.Top) return;
        settings.WindowLeft = rect.Left;
        settings.WindowTop = rect.Top;
        settings.Save();
    }

    // Dragging by hand rather than with DragMove, which would make the window take focus.

    private void BeginDrag(object sender, MouseButtonEventArgs e)
    {
        if (!Native.GetCursorPos(out var cursor) || !Native.GetWindowRect(hwnd, out dragOrigin)) return;
        dragStart = cursor;
        CaptureMouse();
    }

    private void Drag(object sender, MouseEventArgs e)
    {
        if (dragStart is not { } start || !Native.GetCursorPos(out var cursor)) return;
        Move(dragOrigin.Left + cursor.X - start.X, dragOrigin.Top + cursor.Y - start.Y);
    }

    private void EndDrag(object sender, MouseButtonEventArgs e)
    {
        if (dragStart is null) return;
        dragStart = null;
        ReleaseMouseCapture();
        SavePosition();
    }

    /// <summary>On by default. Off, the window sits among other windows and goes behind them like any other.</summary>
    public void SetAlwaysOnTop(bool onTop, bool persist = true)
    {
        if (persist)
        {
            settings.AlwaysOnTop = onTop;
            settings.Save();
        }
        Topmost = onTop;
    }

    public void RestoreVisibility() => SetVisible(settings.PanelVisible ?? true);

    public void Toggle()
    {
        // When it isn't kept on top and other windows cover it completely, a click brings it forward rather than
        // hiding a window nobody can see.
        if (IsVisible && !Topmost && IsCovered())
        {
            Native.SetWindowPos(hwnd, Native.HWND_TOP, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            return;
        }
        SetVisible(!IsVisible);
    }

    public void SetVisible(bool visible)
    {
        hub.IsPanelVisible = visible;
        settings.PanelVisible = visible;
        settings.Save();
        if (visible)
        {
            Show();
            Native.SetWindowPos(hwnd, Topmost ? Native.HWND_TOPMOST : Native.HWND_TOP, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }
        else
        {
            Hide();
        }
    }

    /// <summary>True when no part of the window can be seen, checked at a grid of points across it.</summary>
    private bool IsCovered()
    {
        if (!Native.GetWindowRect(hwnd, out var rect)) return false;
        const int Steps = 5;
        for (var row = 0; row < Steps; row++)
        {
            for (var column = 0; column < Steps; column++)
            {
                var point = new Native.POINT
                {
                    X = rect.Left + (column * 2 + 1) * rect.Width / (Steps * 2),
                    Y = rect.Top + (row * 2 + 1) * rect.Height / (Steps * 2),
                };
                if (Native.GetAncestor(Native.WindowFromPoint(point), Native.GA_ROOT) == hwnd) return false;
            }
        }
        return true;
    }

    /// <summary>Set when the app quits, so closing really closes.</summary>
    public bool Quitting { get; set; }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (Quitting) return;
        // Alt+F4 hides the window like its close button. Quitting is in the tray menu.
        e.Cancel = true;
        SetVisible(false);
    }

    /// <summary>
    /// The window as it looks on screen, frame and shadow included, for the self-test. Only the window's own
    /// rectangle and a small margin for the shadow are copied.
    /// </summary>
    public System.Windows.Media.Imaging.BitmapSource? CaptureScreen()
    {
        if (!IsVisible || !Native.GetWindowRect(hwnd, out var rect)) return null;
        var margin = (int)Math.Round(16 * Scale);
        int x = rect.Left - margin, y = rect.Top - margin, width = rect.Width + 2 * margin, height = rect.Height + 2 * margin;
        var screen = Native.GetDC(IntPtr.Zero);
        var memory = Native.CreateCompatibleDC(screen);
        var bitmap = Native.CreateCompatibleBitmap(screen, width, height);
        var previous = Native.SelectObject(memory, bitmap);
        try
        {
            const uint SourceCopy = 0x00CC0020, CaptureLayered = 0x40000000;
            Native.BitBlt(memory, 0, 0, width, height, screen, x, y, SourceCopy | CaptureLayered);
            var image = Imaging.CreateBitmapSourceFromHBitmap(
                bitmap, IntPtr.Zero, Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
            image.Freeze();
            return image;
        }
        finally
        {
            Native.SelectObject(memory, previous);
            Native.DeleteObject(bitmap);
            Native.DeleteDC(memory);
            Native.ReleaseDC(IntPtr.Zero, screen);
        }
    }

    /// <summary>The window's content as an image, for the self-test.</summary>
    public System.Windows.Media.Imaging.BitmapSource Capture() => Renderer.Snapshot(board, Scale);
}
