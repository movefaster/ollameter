using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Ollameter.Core;

namespace Ollameter;

internal static class Program
{
    private const string InstanceName = @"Local\Ollameter.Instance";
    private const string ShowSignalName = @"Local\Ollameter.Show";

    [STAThread]
    private static int Main(string[] arguments)
    {
        string? Value(string flag)
        {
            var index = Array.IndexOf(arguments, flag);
            return index >= 0 && index + 1 < arguments.Length ? arguments[index + 1] : null;
        }

        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        // GPU rendering loads the graphics driver's Direct3D stack, about 50 MB with NVIDIA's, for a window that
        // redraws a few small shapes four times a second. Software rendering measured the same CPU time.
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        // Everything starts before the dispatcher runs, and its awaits must come back to this thread, which only
        // happens once the dispatcher's context is in place.
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        Native.AllowDarkMenus();

        if (Value("--render") is { } output)
        {
            Settings.ReadOnly = true;
            return Renderer.Run(Path.GetFullPath(output), arguments.Contains("--live"));
        }

        var options = new AppController.Options
        {
            SelfTestDirectory = Value("--self-test") is { } directory ? Path.GetFullPath(directory) : null,
            SelfTestSeconds = double.TryParse(Value("--seconds"), out var seconds) ? seconds : 8,
        };
        // Each --connect adds a computer: local, service:NAME or an address such as studio.local:11480.
        for (var index = 0; index < arguments.Length - 1; index++)
        {
            if (arguments[index] != "--connect") continue;
            var text = arguments[index + 1];
            if ((MeterSource.FromStorageValue(text) ?? MeterSource.ParseAddress(text)) is not { } source)
            {
                Console.Error.WriteLine($"Can't read --connect {text}");
                return 64;
            }
            (options.Sources ??= []).Add(source);
        }
        if (arguments.Contains("--share")) options.Sharing = true;
        if (arguments.Contains("--no-share")) options.Sharing = false;
        if (options.SelfTestDirectory is not null || options.Sources is not null || options.Sharing is not null)
            Settings.ReadOnly = true;

        // One copy at a time. Starting another shows the running one's window instead.
        using var instance = new Mutex(true, InstanceName, out var first);
        using var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignalName);
        if (!first && options.SelfTestDirectory is null)
        {
            showSignal.Set();
            return 0;
        }

        var controller = new AppController(options);
        controller.Start();
        if (first) WatchForShowRequests(showSignal, controller);
        application.Run();
        return 0;
    }

    private static void WatchForShowRequests(EventWaitHandle signal, AppController controller)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var thread = new Thread(() =>
        {
            while (signal.WaitOne()) dispatcher.BeginInvoke(controller.ShowWindow);
        })
        { IsBackground = true, Name = "Show requests" };
        thread.Start();
    }
}

internal sealed class AppController
{
    public sealed class Options
    {
        public string? SelfTestDirectory { get; init; }
        public double SelfTestSeconds { get; init; } = 8;
        /// <summary>For this run only: whose numbers to show, and whether to share.</summary>
        public List<MeterSource>? Sources { get; set; }
        public bool? Sharing { get; set; }
    }

    private readonly Options options;
    private readonly MeterHub hub;
    private readonly MeterWindow window;
    private readonly TrayIcon tray = new();

    public AppController(Options options)
    {
        this.options = options;
        hub = new MeterHub(new Monitor(), options.Sources, options.Sharing);
        window = new MeterWindow(hub) { PlacementHint = () => tray.Bounds };
    }

    public void Start()
    {
        tray.Clicked += () => window.Toggle();
        tray.MenuRequested += ShowMenu;
        tray.ThemeChanged += () =>
        {
            window.ApplyTheme();
            UpdateTray();
        };
        hub.Updated += () =>
        {
            window.Refresh();
            UpdateTray();
        };
        UpdateTray();
        window.RestoreVisibility();
        hub.Start();
        if (options.SelfTestDirectory is { } directory)
            new SelfTest(directory, options.SelfTestSeconds, hub, window, BuildMenu, Quit).Start();
    }

    public void ShowWindow()
    {
        if (!window.IsVisible) window.SetVisible(true);
    }

    /// <summary>The icon follows the busiest computer. The tooltip has a line for each one.</summary>
    private void UpdateTray()
    {
        var models = hub.Models;
        List<string> lines;
        if (models.Count == 1 && models[0].Remote is null)
        {
            lines = [models[0].Display.Summary];
        }
        else
        {
            // A connection problem already names the computer.
            lines = models.Select(model => model.Remote?.Problem ?? $"{model.Remote?.Host ?? Sharing.HostName}: {model.Display.Summary}").ToList();
        }
        if (lines.Count == 0 || lines.All(string.IsNullOrEmpty)) lines = ["Ollameter"];
        tray.Update(hub.Primary.Display, lines);
    }

    private void ShowMenu(Native.POINT at) => BuildMenu().Show(tray.Handle, at);

    public PopupMenu BuildMenu()
    {
        var menu = new PopupMenu();
        menu.Add(window.IsVisible ? "Hide Window" : "Show Window", window.Toggle);
        menu.Add("Keep Window on Top", () => window.SetAlwaysOnTop(!window.IsAlwaysOnTop), window.IsAlwaysOnTop);
        menu.AddSeparator();

        menu.AddDisabled("Show Stats From");
        var peers = hub.Peers;
        var listed = new List<MeterSource> { MeterSource.Local };
        listed.AddRange(peers.Select(MeterSource.Service));
        // Computers being watched stay listed while Bonjour can't see them, or when they were typed in.
        listed.AddRange(hub.Sources.Where(source => !listed.Contains(source)));
        foreach (var source in listed)
        {
            var watched = hub.Sources.Contains(source);
            // The last one watched stays, so the window always shows something. Pick another first.
            menu.Add(source.DisplayName, () => hub.Toggle(source), watched, enabled: !(watched && hub.Sources.Count == 1));
        }
        if (peers.Count == 0) menu.AddDisabled("No other computers found sharing");
        menu.Add("Other Computer…", EnterAddress);
        menu.AddSeparator();

        menu.Add("Share on Local Network", () => hub.SetSharing(!hub.IsSharing), hub.IsSharing);
        if (hub.SharingStatus is { } status) menu.AddDisabled(status);
        menu.AddSeparator();
        var autostart = Autostart.IsEnabled;
        menu.Add("Start with Windows", () => Autostart.Set(!autostart), autostart);
        menu.Add("Quit Ollameter", Quit);
        return menu;
    }

    private void EnterAddress()
    {
        if (AddressDialog.Ask(window.IsAlwaysOnTop) is { } source) hub.Add(source);
    }

    private void Quit()
    {
        window.SavePosition();
        hub.Stop();
        tray.Dispose();
        window.Quitting = true;
        window.Close();
        Application.Current.Shutdown();
    }
}

/// <summary>Asks for a computer that Bonjour can't find, such as one across a VPN.</summary>
internal sealed class AddressDialog : Window
{
    private readonly System.Windows.Controls.TextBox field;
    private readonly System.Windows.Controls.TextBlock heading;
    private MeterSource? result;

    private AddressDialog(Theme theme, bool topmost)
    {
        Title = "Watch Another Computer";
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = topmost;
        Background = theme.Surface;
        heading = Ui.Text(14, theme.Ink, FontWeights.SemiBold, text: "Watch Another Computer");
        var explanation = Ui.Text(12, theme.Ink2,
            text: "Enter the name or IP address of a Mac or PC that has Share on Local Network turned on, such as "
                + $"studio.local or 192.168.1.20. Add a port after a colon if it isn't {MeterSource.DefaultPort}.");
        explanation.TextWrapping = TextWrapping.Wrap;
        explanation.TextTrimming = TextTrimming.None;
        explanation.MaxWidth = 340;
        field = new System.Windows.Controls.TextBox
        {
            FontSize = 13, FontFamily = Theme.Font, Padding = new Thickness(4, 3, 4, 3),
            Background = theme.Surface, Foreground = theme.Ink, BorderBrush = theme.Ink2, CaretBrush = theme.Ink,
        };
        var connect = DialogButton(theme, "Connect", primary: true);
        connect.Click += Submit;
        var cancel = DialogButton(theme, "Cancel", primary: false);
        cancel.Click += () => DialogResult = false;
        var buttons = new Stack { Orientation = System.Windows.Controls.Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right }
            .With(cancel, connect);
        Content = new Stack { Spacing = 12, Margin = new Thickness(20) }.With(heading, explanation, field, buttons);
        field.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter) Submit();
            if (e.Key == System.Windows.Input.Key.Escape) DialogResult = false;
        };
        Loaded += (_, _) =>
        {
            Activate();
            field.Focus();
        };
        SourceInitialized += (_, _) =>
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            var dark = theme.IsDark ? 1 : 0;
            Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        };
    }

    private static FlatButton DialogButton(Theme theme, string text, bool primary)
    {
        var label = Ui.Text(12, primary ? System.Windows.Media.Brushes.White : theme.Ink, FontWeights.Medium, text: text);
        label.Margin = new Thickness(14, 5, 14, 5);
        label.HorizontalAlignment = HorizontalAlignment.Center;
        var rest = primary ? theme.Generation : theme.Hairline;
        return new FlatButton(label, rest, primary ? theme.Generation : theme.HoverFill, new CornerRadius(5)) { MinWidth = 80 };
    }

    private void Submit()
    {
        if (MeterSource.ParseAddress(field.Text) is { } source)
        {
            result = source;
            DialogResult = true;
            return;
        }
        heading.Text = "That isn't a name or address Ollameter can use.";
        field.SelectAll();
        field.Focus();
    }

    public static MeterSource? Ask(bool topmost)
    {
        var dialog = new AddressDialog(new Theme(Theme.AppsUseDark), topmost);
        return dialog.ShowDialog() == true ? dialog.result : null;
    }
}

/// <summary>
/// Runs the real app for a while, records every model it publishes, and writes a report and images of the window,
/// so behavior can be checked without watching it.
/// </summary>
internal sealed class SelfTest(
    string directory, double seconds, MeterHub hub, MeterWindow window, Func<PopupMenu> menu, Action quit)
{
    private readonly List<string> frames = [];
    private readonly HashSet<string> phases = [];
    private DateTime started;

    public void Start()
    {
        Directory.CreateDirectory(directory);
        started = DateTime.UtcNow;
        hub.Updated += Record;
        var half = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds / 2) };
        half.Tick += (_, _) =>
        {
            half.Stop();
            Save(window.Capture(), "window-mid.png");
            if (window.CaptureScreen() is { } screen) Save(screen, "screen-mid.png");
        };
        half.Start();
        var end = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        end.Tick += (_, _) =>
        {
            end.Stop();
            Finish();
        };
        end.Start();
    }

    private void Record()
    {
        var primary = hub.Primary;
        phases.Add(primary.Display.DisplayPhase.ToString());
        frames.Add(JsonSerializer.Serialize(new
        {
            t = Math.Round((DateTime.UtcNow - started).TotalSeconds, 3),
            models = hub.Models.Select(model => new
            {
                host = model.Remote?.Host ?? Sharing.HostName,
                phase = model.Display.DisplayPhase.ToString(),
                snapshotPhase = model.Snapshot.Phase.ToString(),
                text = model.Display.Text,
                summary = model.Display.Summary,
                liveTps = model.Snapshot.LiveTps,
                promptFraction = model.Snapshot.PromptFraction,
                gpu = model.Hardware.GpuUtilization,
                watts = model.Hardware.GpuPowerWatts,
                temperature = model.Hardware.DieTemperature,
                vram = model.Hardware.GpuMemoryInUse,
                vramTotal = model.Hardware.GpuMemoryTotal,
                pressure = model.Hardware.MemoryPressure?.ToString(),
                runnerCpu = model.Runner?.CpuPercent,
                runnerRam = model.Runner?.ResidentBytes,
                loaded = model.Models.Select(loaded => loaded.Name),
                requests = model.Requests.Count,
                platform = model.Platform,
            }),
        }));
    }

    private void Finish()
    {
        Save(window.Capture(), "window-end.png");
        File.WriteAllLines(Path.Combine(directory, "frames.jsonl"), frames);
        var report = new
        {
            seconds,
            frames = frames.Count,
            phases,
            gpuSource = hub.Monitor.GpuSource,
            sources = hub.Sources.Select(source => source.StorageValue),
            sharing = hub.IsSharing,
            sharingStatus = hub.SharingStatus,
            port = hub.Server.Port,
            advertisedName = hub.Server.AdvertisedName,
            peers = hub.Peers,
            viewers = hub.Server.HasViewers,
            windowVisible = window.IsVisible,
            compact = window.IsCompact,
            alwaysOnTop = window.IsAlwaysOnTop,
            menu = menu().Snapshot(),
            marks = Marks.All,
            memory = Memory(),
            lastModel = JsonSerializer.Deserialize<JsonElement>(new SharedFrame(Sharing.HostName, Sharing.AppVersion, hub.Monitor.Model).Encoded()),
        };
        File.WriteAllText(
            Path.Combine(directory, "report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        quit();
    }

    /// <summary>Where the process's memory goes, after a full collection so garbage doesn't count.</summary>
    private static object Memory()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var info = GC.GetGCMemoryInfo();
        const double Megabyte = 1 << 20;
        return new
        {
            workingSetMB = Math.Round(process.WorkingSet64 / Megabyte, 1),
            privateMB = Math.Round(process.PrivateMemorySize64 / Megabyte, 1),
            gcHeapMB = Math.Round(GC.GetTotalMemory(false) / Megabyte, 1),
            gcCommittedMB = Math.Round(info.TotalCommittedBytes / Megabyte, 1),
            threads = process.Threads.Count,
            cpuSeconds = Math.Round(process.TotalProcessorTime.TotalSeconds, 2),
            renderTier = System.Windows.Media.RenderCapability.Tier >> 16,
            modules = process.Modules.Cast<System.Diagnostics.ProcessModule>()
                .OrderByDescending(module => module.ModuleMemorySize)
                .Take(12)
                .Select(module => $"{module.ModuleName} {module.ModuleMemorySize / Megabyte:F1}"),
        };
    }

    private void Save(System.Windows.Media.Imaging.BitmapSource image, string name) =>
        Renderer.SavePng(image, Path.Combine(directory, name));
}
