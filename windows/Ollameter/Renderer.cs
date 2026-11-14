using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Ollameter.Core;

namespace Ollameter;

/// <summary>Sample states for rendering the UI without needing a matching request in flight.</summary>
internal static class Fixtures
{
    public static MeterViewModel Base()
    {
        var now = DateTimeOffset.UtcNow;
        var model = new MeterViewModel { Now = now, Version = "0.40.16", Platform = SharedFrame.WindowsPlatform };
        model.Models =
        [
            new LoadedModel
            {
                Name = "qwen3-next:80b", Size = 48_210_330_812, SizeVram = 48_210_330_812, ContextLength = 262_144,
                ExpiresAt = now.AddSeconds(272), ParameterSize = "79.7B", Quantization = "Q4_K_M",
            },
        ];
        (int, int, double, double, int, double, double)[] requests =
        [
            (57_451, 25, 265, 94.3, 321, 11_745, 27.2),
            (54_312, 641, 2_128, 301.2, 425, 15_449, 27.5),
            (49_291, 412, 1_498, 275.0, 188, 7_102, 26.5),
            (48_837, 1_880, 5_560, 338.1, 96, 3_711, 25.9),
            (25_600, 25_600, 76_190, 336.0, 512, 18_660, 27.4),
            (10_837, 10_837, 32_390, 334.6, 244, 8_911, 27.4),
            (10_329, 402, 1_702, 236.2, 67, 2_490, 26.9),
        ];
        var stats = new SessionStats();
        for (var index = requests.Length - 1; index >= 0; index--)
        {
            var entry = requests[index];
            var request = new CompletedRequest
            {
                Sequence = index, TaskId = 18_000 - index, FinishedAt = now.AddSeconds((index + 1) * -37),
                PromptTokens = entry.Item1, EvaluatedTokens = entry.Item2, PromptMilliseconds = entry.Item3, PromptTps = entry.Item4,
                GeneratedTokens = entry.Item5, GenerationMilliseconds = entry.Item6, GenerationTps = entry.Item7,
            };
            stats.Record(request);
            model.Requests.Insert(0, request);
        }
        model.Stats = stats;
        model.Hardware = new HardwareSample
        {
            GpuUtilization = 97, GpuMemoryInUse = 21_474_836_480, GpuMemoryTotal = 25_769_803_776, GpuPowerWatts = 312.4,
            DieTemperature = 68, MemoryPressure = MemoryPressure.Normal,
        };
        model.Runner = new RunnerStats(18, 3_221_225_472);
        model.TpsHistory = Enumerable.Range(0, 60).Select(index => index < 12 ? 0 : 26 + 2 * Math.Sin(index / 3.0)).ToList();
        model.GpuHistory = Enumerable.Range(0, 60).Select(index => index < 12 ? 3 : 92 + 5 * Math.Sin(index / 4.0)).ToList();
        model.Snapshot.ContextSize = 262_144;
        model.Snapshot.ContextUsed = 57_771;
        model.PromptCache = new PromptCacheState
        {
            LimitBytes = 25_769_803_776, UsedBytes = 11_955_210_240, SavedPrompts = 3, SavedTokens = [90_120, 40_738, 15_202],
        };
        return model;
    }

    private static MeterViewModel Presented(MeterViewModel model)
    {
        model.Display = new DisplayPresenter().Present(model);
        return model;
    }

    public static MeterViewModel Generating()
    {
        var model = Base();
        model.Snapshot.Phase = Phase.Generating;
        model.Snapshot.TaskId = 18_484;
        model.Snapshot.LiveTps = 27.4;
        model.Snapshot.GeneratedTokens = 321;
        model.Snapshot.PhaseElapsed = 11.7;
        return Presented(model);
    }

    public static MeterViewModel Prefill()
    {
        var model = Base();
        model.Snapshot.Phase = Phase.Prefill;
        model.Snapshot.TaskId = 18_485;
        model.Snapshot.PromptTokens = 54_287;
        model.Snapshot.PromptCached = 39_381;
        model.Snapshot.PromptDone = 6_144;
        model.Snapshot.PromptToProcess = 14_906;
        model.Snapshot.PromptFraction = (39_381.0 + 6_144) / 54_287;
        model.Snapshot.PromptTps = 301;
        model.Snapshot.EtaSeconds = 29;
        return Presented(model);
    }

    public static MeterViewModel Idle(Func<string, UnloadState>? unload = null)
    {
        var model = Base();
        model.Snapshot.Phase = Phase.Idle;
        model.Hardware.GpuUtilization = 4;
        if (unload is not null) model.Unload = unload(model.Models[0].Name);
        return Presented(model);
    }

    public static MeterViewModel Offline()
    {
        var model = new MeterViewModel();
        model.Snapshot.Phase = Phase.Offline;
        return Presented(model);
    }

    /// <summary>Another computer's numbers, as they arrive in its frames.</summary>
    public static MeterViewModel Remote(MeterViewModel model, string host, bool mac, string? modelName = null)
    {
        var copy = model.Copy();
        copy.Remote = new RemoteSource(host, RemoteStatus.Connected);
        copy.Platform = mac ? null : SharedFrame.WindowsPlatform;
        if (mac)
        {
            copy.Hardware = model.Hardware with { GpuPowerWatts = 16.9, GpuMemoryTotal = null, SwapUsed = 0, GpuMemoryInUse = 76_281_577_472 };
            copy.Runner = new RunnerStats(18, 87_936_303_104);
        }
        if (modelName is not null && copy.Models.Count > 0) copy.Models = [copy.Models[0] with { Name = modelName }];
        return copy;
    }

    public static MeterViewModel Remote(RemoteStatus status, string host) =>
        MeterViewModels.Placeholder(new RemoteSource(host, status));

    /// <summary>Every optional section at once, to expose layout collisions.</summary>
    public static MeterViewModel Crowded()
    {
        var model = Generating();
        model.Hardware.MemoryPressure = MemoryPressure.Warning;
        model.PromptCache = new PromptCacheState
        {
            LimitBytes = 25_769_803_776, UsedBytes = 23_622_320_128, SavedPrompts = 9,
            SavedTokens = [152_004, 90_120, 74_880, 61_233, 48_109, 40_738, 22_581, 15_202, 9_197], Evictions = 12,
        };
        model.Models.Add(new LoadedModel
        {
            Name = "nomic-embed-text:latest", Size = 274_000_000, SizeVram = 200_000_000, ContextLength = 8192,
            ExpiresAt = model.Now.AddSeconds(95), ParameterSize = "137M", Quantization = "F16",
        });
        model.Notices =
        [
            new Notice("truncated", NoticeSeverity.Serious,
                "A 23,010-token prompt was cut to 4,098 tokens. Raise the model's context length to avoid this.", model.Now),
            new Notice("context-shift", NoticeSeverity.Warning, "The context filled up, so older tokens were dropped.", model.Now),
        ];
        return model;
    }

    public static readonly DisplayState[] TrayStates =
    [
        new() { Fraction = 0.97, Tint = Tint.Gpu, ModelLoaded = true, Text = "27 t/s", DisplayPhase = Phase.Generating },
        new() { Fraction = 0.41, Tint = Tint.Prompt, ModelLoaded = true, Text = "41%", DisplayPhase = Phase.Prefill },
        new() { Fraction = 0.2, Tint = Tint.Muted, ModelLoaded = true, Text = "27 t/s", TextDimmed = true, DisplayPhase = Phase.Generating },
        new() { Fraction = 0.65, Tint = Tint.Gpu, ModelLoaded = false, Text = "load", DisplayPhase = Phase.Loading },
        new() { Fraction = 0.04, Tint = Tint.Muted, ModelLoaded = true, DisplayPhase = Phase.Idle },
        new() { Fraction = 0, Tint = Tint.None, ModelLoaded = false, DisplayPhase = Phase.Offline },
    ];
}

/// <summary>Writes PNGs of every window state from sample data, in light and dark mode.</summary>
internal static class Renderer
{
    private const double Scale = 1.5;

    public static int Run(string directory, bool live)
    {
        Directory.CreateDirectory(directory);
        var states = new List<(string Name, MeterViewModel[] Models, bool Compact)>
        {
            ("generating", [Fixtures.Generating()], false),
            ("prefill", [Fixtures.Prefill()], false),
            ("idle", [Fixtures.Idle()], false),
            ("idle-confirm-unload", [Fixtures.Idle(UnloadState.Confirming)], false),
            ("offline", [Fixtures.Offline()], false),
            ("crowded", [Fixtures.Crowded()], false),
            ("compact-generating", [Fixtures.Generating()], true),
            ("compact-prefill", [Fixtures.Prefill()], true),
            ("compact-offline", [Fixtures.Offline()], true),
            // Several Macs and several PCs at once, each labeled by what it reports.
            ("several-compact",
            [
                Fixtures.Generating(),
                Fixtures.Remote(Fixtures.Prefill(), "studio", mac: true, "llama3.3:70b"),
                Fixtures.Remote(Fixtures.Idle(), "mini", mac: true, "gemma3:27b"),
                Fixtures.Remote(Fixtures.Generating(), "WORKSTATION", mac: false, "gpt-oss:20b"),
                Fixtures.Remote(RemoteStatus.Unreachable, "GAMING-PC"),
            ], true),
            ("several-columns",
            [
                Fixtures.Idle(),
                Fixtures.Remote(Fixtures.Generating(), "studio", mac: true, "llama3.3:70b"),
                Fixtures.Remote(Fixtures.Prefill(), "WORKSTATION", mac: false, "gpt-oss:20b"),
            ], false),
            ("remote-connecting", [Fixtures.Remote(RemoteStatus.Connecting, "studio")], false),
            ("remote-incompatible", [Fixtures.Remote(RemoteStatus.Incompatible("0.9.0"), "WORKSTATION")], true),
        };

        foreach (var dark in new[] { false, true })
        {
            var theme = new Theme(dark);
            var suffix = dark ? "dark" : "light";
            foreach (var (name, models, compact) in states)
            {
                var board = new MeterBoard("MY-PC");
                board.Update(models, compact, theme);
                SavePng(Snapshot(board, Scale), Path.Combine(directory, $"{name}-{suffix}.png"));
            }
            SavePng(TrayStrip(dark), Path.Combine(directory, $"tray-{suffix}.png"));
        }

        if (live)
        {
            var monitor = new Monitor();
            var frame = new DispatcherFrameWaiter(TimeSpan.FromSeconds(6));
            monitor.Start();
            frame.Wait();
            monitor.Stop();
            foreach (var dark in new[] { false, true })
            {
                var board = new MeterBoard(Sharing.HostName);
                board.Update([monitor.Model], false, new Theme(dark));
                SavePng(Snapshot(board, Scale), Path.Combine(directory, $"live-{(dark ? "dark" : "light")}.png"));
            }
        }
        Console.WriteLine($"Rendered to {directory}");
        return 0;
    }

    /// <summary>Lays an element out at the size it wants and draws it at the given scale.</summary>
    public static BitmapSource Snapshot(FrameworkElement element, double scale)
    {
        element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        element.Arrange(new Rect(element.DesiredSize));
        element.UpdateLayout();
        var width = Math.Max(1, (int)Math.Ceiling(element.ActualWidth * scale));
        var height = Math.Max(1, (int)Math.Ceiling(element.ActualHeight * scale));
        var bitmap = new RenderTargetBitmap(width, height, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(element);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Every tray state at the sizes Windows asks for, on a taskbar-colored strip.</summary>
    private static BitmapSource TrayStrip(bool dark)
    {
        int[] sizes = [16, 20, 24, 32];
        const int Pad = 8;
        var width = Fixtures.TrayStates.Length * (32 + Pad) + Pad;
        var height = sizes.Sum(size => size + Pad) + Pad;
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(new SolidColorBrush(dark ? Color.FromRgb(0x20, 0x20, 0x20) : Color.FromRgb(0xEE, 0xEE, 0xEE)), null,
                new Rect(0, 0, width, height));
            var y = Pad;
            foreach (var size in sizes)
            {
                for (var index = 0; index < Fixtures.TrayStates.Length; index++)
                {
                    var icon = TrayIconRenderer.Render(Fixtures.TrayStates[index], size, dark);
                    context.DrawImage(icon, new Rect(Pad + index * (32 + Pad), y, size, size));
                }
                y += size + Pad;
            }
        }
        var bitmap = new RenderTargetBitmap(width * 3, height * 3, 96 * 3, 96 * 3, PixelFormats.Pbgra32);
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.NearestNeighbor);
        bitmap.Render(visual);
        return bitmap;
    }

    public static void SavePng(BitmapSource image, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    /// <summary>Keeps the dispatcher running for a while, so async work can proceed outside Application.Run.</summary>
    private sealed class DispatcherFrameWaiter(TimeSpan duration)
    {
        public void Wait()
        {
            var frame = new System.Windows.Threading.DispatcherFrame();
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = duration };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                frame.Continue = false;
            };
            timer.Start();
            System.Windows.Threading.Dispatcher.PushFrame(frame);
        }
    }
}
