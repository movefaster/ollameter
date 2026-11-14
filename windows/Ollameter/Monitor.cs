using System.IO;
using System.Net.Http;
using Ollameter.Core;

namespace Ollameter;

internal sealed class MeterConfiguration
{
    public string LogPath { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ollama", "server.log");
    public Uri BaseUri { get; init; } = new("http://127.0.0.1:11434/");
}

internal sealed class OllamaClient
{
    private readonly Uri baseUri;
    private readonly HttpClient client;

    public OllamaClient(Uri baseUri)
    {
        this.baseUri = baseUri;
        // A system proxy, such as one a VPN app sets, mustn't see requests to this PC's own Ollama.
        client = new HttpClient(new SocketsHttpHandler { UseProxy = false, UseCookies = false })
        {
            Timeout = TimeSpan.FromSeconds(2),
        };
    }

    public async Task<string?> Version() => await Fetch(new Uri(baseUri, "api/version")) is { } data ? OllamaJson.Version(data) : null;

    public async Task<List<LoadedModel>?> LoadedModels() =>
        await Fetch(new Uri(baseUri, "api/ps")) is { } data ? OllamaJson.Models(data) : null;

    public async Task<List<SlotReading>?> Slots(int port) =>
        await Fetch(new Uri($"http://127.0.0.1:{port}/slots")) is { } data ? OllamaJson.Slots(data) : null;

    public async Task<bool> Unload(string model)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, "api/generate"))
        {
            Content = new ByteArrayContent(OllamaJson.UnloadBody(model)) { Headers = { { "Content-Type", "application/json" } } },
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            return response.IsSuccessStatusCode;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    private async Task<byte[]?> Fetch(Uri uri)
    {
        try
        {
            using var response = await client.GetAsync(uri);
            return response.IsSuccessStatusCode ? await response.Content.ReadAsByteArrayAsync() : null;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return null;
        }
    }
}

/// <summary>Runs the sampling loop and publishes one view model for the tray icon and the window.</summary>
/// <remarks>Everything here runs on the UI thread. Probes and file reads go to the thread pool.</remarks>
internal sealed class Monitor
{
    private static readonly TimeSpan BusyInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan IdleInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan HardwareInterval = TimeSpan.FromSeconds(1);
    // Every /api/ps call adds a line to Ollama's own log, so it is polled sparingly.
    // /slots and the process scan leave no trace there and carry the live state instead.
    private static readonly TimeSpan ModelRefreshFloor = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ModelRefreshCeiling = TimeSpan.FromSeconds(30);
    // Enough to reach back to the model's load line, so cache and session figures are complete at launch.
    private const int BacklogBytes = 1 << 23;
    private static readonly TimeSpan UnloadConfirmWindow = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan UnloadTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan UnloadFailureShown = TimeSpan.FromSeconds(6);

    private readonly MeterEngine engine = new();
    private readonly DisplayPresenter presenter = new();
    private readonly LogTailer tailer;
    private readonly OllamaClient client;
    private readonly HardwareSampler sampler = new();
    private readonly CancellationTokenSource stopping = new();

    private ProcessScan scan = ProcessScan.None;
    private List<LoadedModel> models = [];
    private string? version;
    private bool apiReachable = true;
    private bool modelsStale = true;
    private DateTime lastScan = DateTime.MinValue;
    private DateTime lastModelRefresh = DateTime.MinValue;
    private DateTime lastHardware = DateTime.MinValue;
    private HardwareSample hardware = new();
    private RunnerStats? runnerStats;
    private readonly List<double> tpsHistory = [];
    private readonly List<double> gpuHistory = [];
    private UnloadState unloadState = UnloadState.Ready;
    private CancellationTokenSource? unloadExpiry;

    public MeterViewModel Model { get; private set; } = new();

    /// <summary>Runs on every publish, whether or not the window is open.</summary>
    public event Action<MeterViewModel>? Updated;

    /// <summary>Power, thermal and process readings are skipped while nobody is looking at them.</summary>
    public bool WantsDetail { get; set; } = true;

    public string GpuSource => sampler.GpuSource;

    public Monitor(MeterConfiguration? configuration = null)
    {
        configuration ??= new MeterConfiguration();
        tailer = new LogTailer(configuration.LogPath);
        client = new OllamaClient(configuration.BaseUri);
    }

    public async void Start()
    {
        Marks.Add("backlog start");
        var backlog = await Task.Run(() => tailer.OpenAtEnd(BacklogBytes));
        Marks.Add($"backlog read, {backlog.Count} lines");
        foreach (var line in backlog) engine.Ingest(line, null);
        while (!stopping.IsCancellationRequested)
        {
            TimeSpan interval;
            try
            {
                interval = await Tick();
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // One bad sample shouldn't end the meter.
                Marks.Add("tick failed: " + exception);
                interval = IdleInterval;
            }
            try { await Task.Delay(interval, stopping.Token); }
            catch (TaskCanceledException) { return; }
        }
    }

    public void Stop() => stopping.Cancel();

    private void SetUnloadState(UnloadState state, TimeSpan? lifetime = null)
    {
        unloadExpiry?.Cancel();
        unloadExpiry = null;
        unloadState = state;
        // Published straight away too, so a press shows at once instead of on the next tick.
        var shown = Model.Copy();
        shown.Unload = state;
        Model = shown;
        Updated?.Invoke(shown);
        if (lifetime is not { } after) return;
        var expiry = new CancellationTokenSource();
        unloadExpiry = expiry;
        _ = ExpireUnloadState(after, expiry.Token);
    }

    private async Task ExpireUnloadState(TimeSpan after, CancellationToken token)
    {
        try { await Task.Delay(after, token); }
        catch (TaskCanceledException) { return; }
        if (unloadState.Kind == UnloadKind.Unloading) ShowUnloadFailure(unloadState.Model!);
        else SetUnloadState(UnloadState.Ready);
    }

    public void PressUnload(string name)
    {
        var press = unloadState.Pressed(name);
        if (press.SendsUnload) _ = Unload(name);
        else if (press.Next != unloadState) SetUnloadState(press.Next, UnloadConfirmWindow);
    }

    private async Task Unload(string name)
    {
        SetUnloadState(UnloadState.Unloading(name), UnloadTimeout);
        // What the window showed can be a moment old, so both are checked again right before acting.
        await PollSlots();
        if (engine.Snapshot(DateTimeOffset.UtcNow, online: true).Phase.IsBusy())
        {
            SetUnloadState(UnloadState.Ready);
            return;
        }
        if (await client.LoadedModels() is not { } loaded)
        {
            ShowUnloadFailure(name);
            return;
        }
        if (!loaded.Any(model => model.Name == name))
        {
            SetUnloadState(UnloadState.Ready);
            return;
        }
        // On success the state stays at unloading until /api/ps stops listing the model.
        if (!await client.Unload(name)) ShowUnloadFailure(name);
        else modelsStale = true;
    }

    private void ShowUnloadFailure(string name)
    {
        if (unloadState != UnloadState.Unloading(name)) return;
        SetUnloadState(UnloadState.Failed(name), UnloadFailureShown);
    }

    private int ticks;

    private async Task<TimeSpan> Tick()
    {
        var trace = ticks++ < 3;
        if (trace) Marks.Add("tick");
        var lines = await Task.Run(tailer.ReadNew);
        if (trace) Marks.Add("log read");
        var now = DateTimeOffset.UtcNow;
        foreach (var line in lines)
        {
            var e = engine.Ingest(line, now);
            if (e?.Kind is LogEventKind.AllIdle or LogEventKind.ModelLoaded) modelsStale = true;
        }
        await RefreshProcesses();
        if (trace) Marks.Add("processes");
        await RefreshModelsIfNeeded();
        if (trace) Marks.Add("models");
        await PollSlots();
        if (trace) Marks.Add("slots");

        var online = scan.OllamaRunning && apiReachable;
        var snapshot = engine.Snapshot(DateTimeOffset.UtcNow, online);
        var hardwareRefreshed = await RefreshHardwareIfDue();
        if (trace) Marks.Add("hardware");
        Publish(snapshot, hardwareRefreshed);
        return snapshot.Phase.IsBusy() ? BusyInterval : IdleInterval;
    }

    private async Task RefreshProcesses()
    {
        var now = DateTime.UtcNow;
        if (now - lastScan < ScanInterval) return;
        lastScan = now;
        var newScan = await sampler.Scan();
        if (!newScan.SameAs(scan))
        {
            modelsStale = true;
            lastModelRefresh = DateTime.MinValue;
        }
        scan = newScan;
    }

    private async Task RefreshModelsIfNeeded()
    {
        if (!scan.OllamaRunning)
        {
            models = [];
            version = null;
            apiReachable = true;
            return;
        }
        var age = DateTime.UtcNow - lastModelRefresh;
        if (!(unloadState.IsUnloading || (modelsStale && age >= ModelRefreshFloor) || age >= ModelRefreshCeiling)) return;
        lastModelRefresh = DateTime.UtcNow;
        modelsStale = false;
        if (await client.LoadedModels() is { } loaded)
        {
            models = loaded;
            apiReachable = true;
            if (unloadState.Kind == UnloadKind.Unloading && !loaded.Any(model => model.Name == unloadState.Model))
                SetUnloadState(UnloadState.Ready);
            version ??= await client.Version();
        }
        else
        {
            models = [];
            apiReachable = false;
        }
    }

    private async Task PollSlots()
    {
        if (scan.Runners.Count == 0)
        {
            engine.Update(null, DateTimeOffset.UtcNow);
            return;
        }
        var readings = new List<SlotReading>();
        var answered = false;
        foreach (var runner in scan.Runners)
        {
            if (await client.Slots(runner.Port) is { } slots)
            {
                readings.AddRange(slots);
                answered = true;
            }
        }
        engine.Update(answered ? readings : null, DateTimeOffset.UtcNow);
    }

    private async Task<bool> RefreshHardwareIfDue()
    {
        var now = DateTime.UtcNow;
        if (now - lastHardware < HardwareInterval) return false;
        lastHardware = now;
        (hardware, runnerStats) = await sampler.Sample(WantsDetail, scan.Runners);
        return true;
    }

    private static void Append(double value, List<double> history)
    {
        history.Add(value);
        if (history.Count > MeterViewModel.HistoryLimit) history.RemoveRange(0, history.Count - MeterViewModel.HistoryLimit);
    }

    private void Publish(LiveSnapshot snapshot, bool appendHistory)
    {
        var tracker = engine.Tracker;
        var next = new MeterViewModel
        {
            Now = DateTimeOffset.UtcNow,
            Snapshot = snapshot,
            // The tracker keeps changing these, so the published model gets its own copies.
            Stats = tracker.Stats.Clone(),
            Requests = [.. tracker.Recent],
            Notices = [.. tracker.Notices],
            Models = [.. models],
            Version = version,
            Hardware = hardware,
            Runner = runnerStats,
            PromptCache = tracker.PromptCache?.Clone(),
        };
        next.Display = presenter.Present(next);
        // A request that starts while the button is armed shouldn't leave it armed for when it ends.
        if (next.IsWorking && unloadState.Kind == UnloadKind.Confirming) SetUnloadState(UnloadState.Ready);
        next.Unload = unloadState;
        if (appendHistory)
        {
            var shownTps = next.Display.DisplayPhase == Phase.Generating ? snapshot.LiveTps ?? next.Display.HeldTps ?? 0 : 0;
            Append(shownTps, tpsHistory);
            Append(hardware.GpuUtilization ?? 0, gpuHistory);
        }
        next.TpsHistory = [.. tpsHistory];
        next.GpuHistory = [.. gpuHistory];
        Model = next;
        Updated?.Invoke(next);
    }
}
