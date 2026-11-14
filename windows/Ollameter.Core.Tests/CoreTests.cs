using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ollameter.Core.Tests;

// Ported from Tests/MeterCoreTests, so both apps are held to the same behavior.

public class UnloadTests
{
    [Fact]
    public void FirstPressOnlyArms()
    {
        var press = UnloadState.Ready.Pressed("qwen");
        Assert.Equal(UnloadState.Confirming("qwen"), press.Next);
        Assert.False(press.SendsUnload);
    }

    [Fact]
    public void SecondPressSendsTheUnload()
    {
        var press = UnloadState.Confirming("qwen").Pressed("qwen");
        Assert.Equal(UnloadState.Unloading("qwen"), press.Next);
        Assert.True(press.SendsUnload);
    }

    [Fact]
    public void ArmingOneModelDoesNotConfirmAnother()
    {
        var press = UnloadState.Confirming("qwen").Pressed("llama");
        Assert.Equal(UnloadState.Confirming("llama"), press.Next);
        Assert.False(press.SendsUnload);
    }

    [Fact]
    public void PressDuringAnUnloadDoesNothing()
    {
        var press = UnloadState.Unloading("qwen").Pressed("qwen");
        Assert.Equal(UnloadState.Unloading("qwen"), press.Next);
        Assert.False(press.SendsUnload);
    }

    [Fact]
    public void RetryAfterAFailureStillNeedsConfirming()
    {
        var press = UnloadState.Failed("qwen").Pressed("qwen");
        Assert.Equal(UnloadState.Confirming("qwen"), press.Next);
        Assert.False(press.SendsUnload);
    }

    [Fact]
    public void BodyAsksForAnImmediateUnloadAndNoGeneration()
    {
        var body = JsonNode.Parse(OllamaJson.UnloadBody("qwen:latest"))!.AsObject();
        Assert.Equal("qwen:latest", (string?)body["model"]);
        Assert.Equal(0, (int?)body["keep_alive"]);
        Assert.False(body.ContainsKey("prompt"));
    }

    [Fact]
    public void WorkingCoversTheGapsBetweenRequests()
    {
        var model = new MeterViewModel();
        model.Snapshot.Phase = Phase.Idle;
        Assert.False(model.IsWorking);
        model.Display.DisplayPhase = Phase.Generating;
        Assert.True(model.IsWorking);
        model.Display.DisplayPhase = Phase.Idle;
        model.Snapshot.Phase = Phase.Prefill;
        Assert.True(model.IsWorking);
    }
}

/// <summary>Fixture lines are copied from real Ollama 0.40 server.log files on macOS and Windows.</summary>
public class LogParserTests
{
    [Fact]
    public void NewPrompt() => Assert.Equal(
        LogEvent.NewPrompt(18484, 262144, 57451),
        LogParser.Parse("slot   operator(): id  0 | task 18484 | new prompt, n_ctx_slot = 262144, n_keep = 4, task.n_tokens = 57451"));

    [Fact]
    public void PromptProgress() => Assert.Equal(
        LogEvent.PromptProgress(447, 2048, 0.08, 427.01),
        LogParser.Parse("slot print_timing: id  0 | task 447 | prompt processing, n_tokens =   2048, progress = 0.08, t =   4.80 s / 427.01 tokens per second"));

    [Fact]
    public void GenerationProgress() => Assert.Equal(
        LogEvent.GenerationProgress(12050, 100, 27.70, 27.98),
        LogParser.Parse("slot print_timing: id  0 | task 12050 | n_gen =    100, tg =  27.70 t/s, tg_3s =  27.98 t/s"));

    [Fact]
    public void FinalTimings()
    {
        Assert.Equal(
            LogEvent.PromptEval(18484, 265.07, 25, 94.32),
            LogParser.Parse("slot print_timing: id  0 | task 18484 | prompt eval time =     265.07 ms /    25 tokens (   10.60 ms per token,    94.32 tokens per second)"));
        Assert.Equal(
            LogEvent.Eval(18484, 11745.47, 321, 27.24),
            LogParser.Parse("slot print_timing: id  0 | task 18484 |        eval time =   11745.47 ms /   321 tokens (   36.70 ms per token,    27.24 tokens per second)"));
        Assert.Equal(
            LogEvent.Total(18484, 12010.54, 346),
            LogParser.Parse("slot print_timing: id  0 | task 18484 |       total time =   12010.54 ms /   346 tokens"));
    }

    [Fact]
    public void LifecycleLines()
    {
        Assert.Equal(
            LogEvent.Released(18484),
            LogParser.Parse("slot      release: id  0 | task 18484 | stop processing: n_tokens = 57771, truncated = 0"));
        Assert.Equal(LogEvent.AllIdle, LogParser.Parse("srv  update_slots: all slots are idle"));
        Assert.Equal(LogEvent.Cancelled(32026), LogParser.Parse("srv          stop: cancel task, id_task = 32026"));
        Assert.Equal(
            LogEvent.ContextShift(21),
            LogParser.Parse("slot   operator(): id  0 | task 21 | slot context shift, n_keep = 4, n_left = 8187, n_discard = 4093"));
        Assert.Equal(
            LogEvent.FullReprocess(5),
            LogParser.Parse("slot   operator(): id  0 | task 5 | forcing full prompt re-processing due to lack of cache data (likely due to SWA or hybrid/recurrent memory)"));
    }

    [Fact]
    public void RuntimeLines()
    {
        Assert.Equal(
            LogEvent.ModelLoading,
            LogParser.Parse("time=2026-11-07T22:32:39.750+00:00 level=INFO source=llama_server.go:1351 msg=\"waiting for llama-server to start responding\""));
        Assert.Equal(
            LogEvent.ModelLoaded,
            LogParser.Parse("time=2026-11-07T22:32:40.255+00:00 level=INFO source=llama_server.go:1418 msg=\"llama-server started in 0.50 seconds\""));
        Assert.Equal(
            LogEvent.InputTruncated(4098, 23010),
            LogParser.Parse("time=2026-11-07T22:49:31.505+00:00 level=WARN source=llama_server.go:344 msg=\"truncating input prompt\" limit=4098 prompt=23010 keep=4 new=4098"));
    }

    [Fact]
    public void PromptCacheLines()
    {
        Assert.Equal(LogEvent.PromptCacheEnabled(24576), LogParser.Parse("srv    load_model: prompt cache is enabled, size limit: 24576 MiB"));
        Assert.Equal(LogEvent.PromptCacheEnabled(null), LogParser.Parse("srv    load_model: prompt cache is enabled, size limit: no limit"));
        Assert.Equal(
            LogEvent.PromptCacheDisabled,
            LogParser.Parse("srv    load_model: prompt cache is disabled - use `--cache-ram N` to enable it"));
        Assert.Equal(
            LogEvent.PromptCacheState(3, 4171.081, 8192),
            LogParser.Parse("srv        update:  - cache state: 3 prompts, 4171.081 MiB (limits: 8192.000 MiB, 262144 tokens, 262144 est)"));
        Assert.Equal(
            LogEvent.PromptCacheEntry(10327),
            LogParser.Parse("srv        update:    - prompt 0x7644b45730:   10327 tokens, checkpoints: 18,  2441.874 MiB"));
        Assert.Equal(
            LogEvent.PromptCacheEvicted(7814.445),
            LogParser.Parse("srv         alloc:  - making room for prompt cache entry, removing oldest entry (size = 7814.445 MiB)"));
        Assert.Null(LogParser.Parse("srv  get_availabl: updating prompt cache"));
        Assert.Null(LogParser.Parse("srv  get_availabl: prompt cache update took 223.15 ms"));
    }

    [Fact]
    public void IgnoresUnrelatedLines()
    {
        Assert.Null(LogParser.Parse("[GIN] 2026/11/08 - 13:36:30 | 200 |    1.61s |       127.0.0.1 | POST     \"/v1/chat/completions\""));
        Assert.Null(LogParser.Parse("slot get_availabl: id  0 | task -1 | selected slot by LRU, n_tokens = 10, t_last = 1"));
        Assert.Null(LogParser.Parse("<|im_start|>user"));
    }

    [Fact]
    public void WindowsRunnerLinesMatchTheMacs()
    {
        // From Ollama 0.40.10 on Windows with a CUDA GPU.
        Assert.Equal(
            LogEvent.NewPrompt(117, 131072, 11842),
            LogParser.Parse("slot   operator(): id  0 | task 117 | new prompt, n_ctx_slot = 131072, n_keep = 4, task.n_tokens = 11842"));
        Assert.Equal(
            LogEvent.PromptProgress(0, 512, 0.04, 30.81),
            LogParser.Parse("slot print_timing: id  0 | task 0 | prompt processing, n_tokens =    512, progress = 0.04, t =  16.62 s / 30.81 tokens per second"));
        Assert.Equal(
            LogEvent.ModelLoading,
            LogParser.Parse("time=2026-10-30T22:37:07.581+00:00 level=INFO source=llama_server.go:1341 msg=\"waiting for llama-server to start responding\""));
    }
}

public class RequestTrackerTests
{
    private static LogEvent[] FinishedRequest(
        int task, int prompt, int evaluated, double promptMs, double promptTps, int generated, double generationMs,
        double generationTps) =>
    [
        LogEvent.NewPrompt(task, 262144, prompt),
        LogEvent.PromptEval(task, promptMs, evaluated, promptTps),
        LogEvent.Eval(task, generationMs, generated, generationTps),
        LogEvent.Total(task, promptMs + generationMs, evaluated + generated),
        LogEvent.Released(task),
        LogEvent.AllIdle,
    ];

    [Fact]
    public void FinishedRequestUpdatesStats()
    {
        var tracker = new RequestTracker();
        foreach (var e in FinishedRequest(1, 1000, 200, 1000, 200, 100, 4000, 25)) tracker.Apply(e, DateTimeOffset.UtcNow);

        Assert.Empty(tracker.Active);
        Assert.Single(tracker.Recent);
        Assert.Equal(800, tracker.Recent[0].CachedTokens);
        Assert.Equal(1, tracker.Stats.Requests);
        Assert.Equal(0.8, tracker.Stats.CacheFraction);
        Assert.Equal(0.2, tracker.Stats.PromptTimeFraction);
        Assert.Equal(200, tracker.Stats.Prompt.Average);
        Assert.Equal(25, tracker.Stats.Generation.Peak);
    }

    [Fact]
    public void TinyRequestsDoNotSetSpeedRecords()
    {
        var tracker = new RequestTracker();
        foreach (var e in FinishedRequest(1, 1000, 25, 265, 94, 5, 37, 108)) tracker.Apply(e, DateTimeOffset.UtcNow);

        Assert.Equal(1, tracker.Stats.Requests);
        Assert.Null(tracker.Stats.Prompt.Peak);
        Assert.Null(tracker.Stats.Generation.Peak);
    }

    [Fact]
    public void CancelledRequestIsNotRecorded()
    {
        var tracker = new RequestTracker();
        tracker.Apply(LogEvent.NewPrompt(9, 100, 50), DateTimeOffset.UtcNow);
        tracker.Apply(LogEvent.Cancelled(9), DateTimeOffset.UtcNow);
        Assert.Empty(tracker.Active);
        Assert.Empty(tracker.Recent);
    }

    [Fact]
    public void ModelLoadResetsSession()
    {
        var tracker = new RequestTracker();
        foreach (var e in FinishedRequest(1, 1000, 200, 1000, 200, 100, 4000, 25)) tracker.Apply(e, DateTimeOffset.UtcNow);
        tracker.Apply(LogEvent.ModelLoaded, DateTimeOffset.UtcNow);
        Assert.Equal(0, tracker.Stats.Requests);
        Assert.Empty(tracker.Recent);
    }

    [Fact]
    public void PromptCacheFollowsTheLog()
    {
        var tracker = new RequestTracker();
        tracker.Apply(LogEvent.PromptCacheEnabled(24576), DateTimeOffset.UtcNow);
        tracker.Apply(LogEvent.ModelLoaded, DateTimeOffset.UtcNow);
        var cache = Assert.IsType<PromptCacheState>(tracker.PromptCache);
        Assert.Equal(25_769_803_776UL, cache.LimitBytes);
        Assert.Equal(0UL, cache.UsedBytes);
        Assert.Equal(0, cache.Fraction);

        tracker.Apply(LogEvent.PromptCacheState(2, 6144, 24576), DateTimeOffset.UtcNow);
        tracker.Apply(LogEvent.PromptCacheEntry(10_327), DateTimeOffset.UtcNow);
        tracker.Apply(LogEvent.PromptCacheEntry(43_238), DateTimeOffset.UtcNow);
        cache = tracker.PromptCache!;
        Assert.Equal(0.25, cache.Fraction);
        Assert.Equal(2, cache.SavedPrompts);
        Assert.Equal([43_238, 10_327], cache.SavedTokens);

        tracker.Apply(LogEvent.PromptCacheEvicted(7814.445), DateTimeOffset.UtcNow);
        tracker.Apply(LogEvent.PromptCacheState(1, 3072, 24576), DateTimeOffset.UtcNow);
        tracker.Apply(LogEvent.PromptCacheEntry(43_238), DateTimeOffset.UtcNow);
        cache = tracker.PromptCache!;
        Assert.Equal([43_238], cache.SavedTokens);
        Assert.Equal(1, cache.Evictions);
        Assert.Equal(["cache-evicted"], tracker.Notices.Select(notice => notice.Id));
    }

    [Fact]
    public void NewLoadStartsWithAnEmptyCache()
    {
        var tracker = new RequestTracker();
        tracker.Apply(LogEvent.PromptCacheEnabled(8192), null);
        tracker.Apply(LogEvent.PromptCacheEvicted(100), null);
        tracker.Apply(LogEvent.PromptCacheState(1, 100, 8192), null);
        Assert.Empty(tracker.Notices);
        Assert.Equal(1, tracker.PromptCache?.Evictions);

        tracker.Apply(LogEvent.PromptCacheEnabled(24576), DateTimeOffset.UtcNow);
        Assert.True(new PromptCacheState { LimitBytes = 25_769_803_776 }.SameAs(tracker.PromptCache));

        tracker.Apply(LogEvent.PromptCacheDisabled, DateTimeOffset.UtcNow);
        Assert.Null(tracker.PromptCache);
    }

    [Fact]
    public void BacklogProducesNoNoticesOrLoadingState()
    {
        var tracker = new RequestTracker();
        tracker.Apply(LogEvent.ContextShift(1), null);
        tracker.Apply(LogEvent.ModelLoading, null);
        Assert.Empty(tracker.Notices);
        Assert.False(tracker.IsLoadingModel(DateTimeOffset.UtcNow));
    }
}

public class SlidingRateTests
{
    [Fact]
    public void RateOverWindow()
    {
        var rate = new SlidingRate(3);
        var start = DateTimeOffset.UtcNow;
        for (var index = 0; index <= 20; index++) rate.Add(index * 7, start.AddSeconds(index * 0.25));
        Assert.Equal(28, rate.Rate ?? 0, 0.01);
    }

    [Fact]
    public void NeedsMinimumSpan()
    {
        var rate = new SlidingRate(3);
        var start = DateTimeOffset.UtcNow;
        rate.Add(0, start);
        rate.Add(7, start.AddSeconds(0.25));
        Assert.Null(rate.Rate);
    }

    [Fact]
    public void CountGoingBackwardRestarts()
    {
        var rate = new SlidingRate(3);
        var start = DateTimeOffset.UtcNow;
        rate.Add(500, start);
        rate.Add(0, start.AddSeconds(1));
        Assert.Null(rate.Rate);
    }
}

public class MeterEngineTests
{
    private static SlotReading Slot(bool processing = true, int task = 7, int prompt = 0, int processed = 0, int cached = 0, int decoded = 0) =>
        new(processing, task, 262144, prompt, processed, cached, decoded);

    private static string NewPromptLine(int total) =>
        $"slot   operator(): id  0 | task 7 | new prompt, n_ctx_slot = 262144, n_keep = 4, task.n_tokens = {total}";

    [Fact]
    public void Offline() => Assert.Equal(Phase.Offline, new MeterEngine().Snapshot(DateTimeOffset.UtcNow, online: false).Phase);

    [Fact]
    public void PrefillMatchesLogProgressWithACachedPrefix()
    {
        // Task 635 from a real log: 24,457 tokens, 8,472 of them cached.
        var engine = new MeterEngine();
        var now = DateTimeOffset.UtcNow;
        engine.Ingest(NewPromptLine(24_457), now);
        engine.Ingest("slot print_timing: id  0 | task 7 | prompt processing, n_tokens =   2049, progress = 0.43, t =   4.00 s / 512.25 tokens per second", now);
        engine.Update([Slot(prompt: 11_033, processed: 2_049, cached: 8_472)], now);
        var snapshot = engine.Snapshot(now, online: true);
        Assert.Equal(Phase.Prefill, snapshot.Phase);
        Assert.Equal(0.43, snapshot.PromptFraction ?? 0, 0.005);
        Assert.Equal(24_457, snapshot.PromptTokens);
        Assert.Equal(2_049, snapshot.PromptDone);
        Assert.Equal(15_985, snapshot.PromptToProcess);
        Assert.Equal((15_985 - 2_049) / 512.25, snapshot.EtaSeconds ?? 0, 0.001);
    }

    [Fact]
    public void PrefillDoesNotTreatTheGrowingSlotCountAsThePromptSize()
    {
        // A real cold prompt two batches in: the slot holds 1,024 of the 5,756 tokens.
        var engine = new MeterEngine();
        var now = DateTimeOffset.UtcNow;
        engine.Ingest(NewPromptLine(5_756), now);
        engine.Update([Slot(prompt: 1_024, processed: 512)], now);
        var snapshot = engine.Snapshot(now, online: true);
        Assert.Equal(512.0 / 5_756, snapshot.PromptFraction ?? 0, 0.0001);
        Assert.Equal(5_756, snapshot.PromptToProcess);
    }

    [Fact]
    public void PrefillUsesTheLogCountWhenItIsAhead()
    {
        var engine = new MeterEngine();
        var now = DateTimeOffset.UtcNow;
        engine.Ingest(NewPromptLine(50_000), now);
        engine.Ingest("slot print_timing: id  0 | task 7 | prompt processing, n_tokens =   5000, progress = 0.90, t =   10.00 s / 500.00 tokens per second", now);
        engine.Update([Slot(prompt: 45_512, processed: 1_000, cached: 40_000)], now);
        var snapshot = engine.Snapshot(now, online: true);
        Assert.Equal(5_000, snapshot.PromptDone);
        Assert.Equal(0.9, snapshot.PromptFraction);
        Assert.Equal(500, snapshot.PromptTps);
    }

    [Fact]
    public void PrefillWithoutThePromptSizeShowsNoPercentage()
    {
        var engine = new MeterEngine();
        var now = DateTimeOffset.UtcNow;
        engine.Update([Slot(prompt: 1_024, processed: 512)], now);
        var snapshot = engine.Snapshot(now, online: true);
        Assert.Equal(Phase.Prefill, snapshot.Phase);
        Assert.Null(snapshot.PromptFraction);
        Assert.Null(snapshot.EtaSeconds);
    }

    [Fact]
    public void LogOnlyPrefillUsesTheLogsProgress()
    {
        var engine = new MeterEngine();
        var now = DateTimeOffset.UtcNow;
        engine.Update(null, now);
        engine.Ingest(NewPromptLine(24_457), now);
        engine.Ingest("slot print_timing: id  0 | task 7 | prompt processing, n_tokens =   2049, progress = 0.43, t =   4.00 s / 512.25 tokens per second", now);
        var snapshot = engine.Snapshot(now, online: true);
        Assert.Equal(0.43, snapshot.PromptFraction);
        Assert.Equal(2_049, snapshot.PromptDone);
        Assert.Equal(2_049 + 13_940, snapshot.PromptToProcess);
    }

    [Fact]
    public void LiveRateFromSlots()
    {
        var engine = new MeterEngine();
        var start = DateTimeOffset.UtcNow;
        for (var index = 0; index <= 12; index++)
        {
            engine.Update(
                [Slot(prompt: 1000 + index * 7, processed: 25, cached: 975, decoded: 1 + index * 7)],
                start.AddSeconds(index * 0.25));
        }
        var snapshot = engine.Snapshot(start.AddSeconds(3), online: true);
        Assert.Equal(Phase.Generating, snapshot.Phase);
        Assert.Equal(28, snapshot.LiveTps ?? 0, 0.01);
        Assert.Equal(1084, snapshot.ContextUsed);
    }

    [Fact]
    public void NewTaskRestartsRate()
    {
        var engine = new MeterEngine();
        var start = DateTimeOffset.UtcNow;
        engine.Update([Slot(task: 1, prompt: 1000, processed: 5, cached: 995, decoded: 100)], start);
        engine.Update([Slot(task: 2, prompt: 1100, processed: 5, cached: 1095, decoded: 2)], start.AddSeconds(1));
        Assert.Null(engine.Snapshot(start.AddSeconds(1), online: true).LiveTps);
    }

    [Fact]
    public void FallsBackToLogWithoutSlots()
    {
        var engine = new MeterEngine();
        var now = DateTimeOffset.UtcNow;
        engine.Update(null, now);
        engine.Ingest("slot   operator(): id  0 | task 7 | new prompt, n_ctx_slot = 1000, n_keep = 4, task.n_tokens = 400", now);
        engine.Ingest("slot print_timing: id  0 | task 7 | n_gen =    100, tg =  27.70 t/s, tg_3s =  27.98 t/s", now);
        var snapshot = engine.Snapshot(now, online: true);
        Assert.Equal(Phase.Generating, snapshot.Phase);
        Assert.Equal(27.98, snapshot.LiveTps);
    }

    [Fact]
    public void BacklogRequestIsNotTreatedAsLive()
    {
        var engine = new MeterEngine();
        engine.Update(null, DateTimeOffset.UtcNow);
        engine.Ingest("slot   operator(): id  0 | task 7 | new prompt, n_ctx_slot = 1000, n_keep = 4, task.n_tokens = 400", null);
        Assert.Equal(Phase.Idle, engine.Snapshot(DateTimeOffset.UtcNow, online: true).Phase);
    }

    [Fact]
    public void IdleKeepsContextUsage()
    {
        var engine = new MeterEngine();
        var now = DateTimeOffset.UtcNow;
        engine.Update([Slot(processing: false, prompt: 54_287)], now);
        var snapshot = engine.Snapshot(now, online: true);
        Assert.Equal(Phase.Idle, snapshot.Phase);
        Assert.Equal(54_287, snapshot.ContextUsed);
    }
}

public class DisplayPresenterTests
{
    private static MeterViewModel Model(Phase phase, DateTimeOffset now, Action<LiveSnapshot>? configure = null)
    {
        var model = new MeterViewModel { Now = now };
        model.Snapshot.Phase = phase;
        model.Hardware.GpuUtilization = 97;
        configure?.Invoke(model.Snapshot);
        return model;
    }

    [Fact]
    public void GeneratingShowsSpeedAndGpu()
    {
        var result = new DisplayPresenter().Present(Model(Phase.Generating, DateTimeOffset.UtcNow, s => s.LiveTps = 27.4));
        Assert.Equal("27 t/s", result.Text);
        Assert.Equal(0.97, result.Fraction);
        Assert.Equal(Tint.Gpu, result.Tint);
    }

    [Fact]
    public void LargePrefillShowsProgressImmediately()
    {
        var result = new DisplayPresenter().Present(Model(Phase.Prefill, DateTimeOffset.UtcNow, s =>
        {
            s.TaskId = 3;
            s.PromptToProcess = 10_000;
            s.PromptFraction = 0.41;
        }));
        Assert.Equal("41%", result.Text);
        Assert.Equal(0.41, result.Fraction);
        Assert.Equal(Tint.Prompt, result.Tint);
    }

    [Fact]
    public void ShortPrefillKeepsLastSpeedInsteadOfFlickering()
    {
        var presenter = new DisplayPresenter();
        var start = DateTimeOffset.UtcNow;
        presenter.Present(Model(Phase.Generating, start, s => s.LiveTps = 27.4));
        var result = presenter.Present(Model(Phase.Prefill, start.AddSeconds(0.2), s =>
        {
            s.TaskId = 4;
            s.PromptToProcess = 25;
            s.PromptFraction = 0.5;
        }));
        Assert.Equal("27 t/s", result.Text);
        Assert.True(result.TextDimmed);
    }

    [Fact]
    public void SlowSmallPrefillEventuallyShowsProgress()
    {
        var presenter = new DisplayPresenter();
        var start = DateTimeOffset.UtcNow;
        Action<LiveSnapshot> configure = s =>
        {
            s.TaskId = 4;
            s.PromptToProcess = 200;
            s.PromptFraction = 0.5;
        };
        presenter.Present(Model(Phase.Prefill, start, configure));
        var result = presenter.Present(Model(Phase.Prefill, start.AddSeconds(1), configure));
        Assert.Equal("50%", result.Text);
    }

    [Fact]
    public void IdleHoldsThenClearsSpeed()
    {
        var presenter = new DisplayPresenter();
        var start = DateTimeOffset.UtcNow;
        presenter.Present(Model(Phase.Generating, start, s => s.LiveTps = 27.4));
        var held = presenter.Present(Model(Phase.Idle, start.AddSeconds(2)));
        Assert.Equal("27 t/s", held.Text);
        Assert.True(held.TextDimmed);
        Assert.Equal(Tint.Muted, held.Tint);
        Assert.Equal(Phase.Generating, held.DisplayPhase);
        Assert.Equal(27.4, held.HeldTps);
        var expired = presenter.Present(Model(Phase.Idle, start.AddSeconds(6)));
        Assert.Null(expired.Text);
        Assert.Equal(Phase.Idle, expired.DisplayPhase);
        Assert.Null(expired.HeldTps);
    }

    [Fact]
    public void OfflineClearsGauge()
    {
        var result = new DisplayPresenter().Present(Model(Phase.Offline, DateTimeOffset.UtcNow));
        Assert.Equal(0, result.Fraction);
        Assert.Equal(Tint.None, result.Tint);
        Assert.Null(result.Text);
    }
}

public class FormatTests
{
    [Fact]
    public void CompactTokens()
    {
        Assert.Equal("950", Format.CompactTokens(950));
        Assert.Equal("262k", Format.CompactTokens(262_144));
        Assert.Equal("7.7M", Format.CompactTokens(7_689_000));
    }

    [Fact]
    public void PercentNeverOverstatesProgress()
    {
        Assert.Equal("99%", Format.Percent(0.999));
        Assert.Equal("100%", Format.Percent(1));
    }

    [Fact]
    public void Speeds()
    {
        Assert.Equal("27.4", Format.Tps(27.44));
        Assert.Equal("301", Format.Tps(301.2));
        Assert.Equal("1,234", Format.Tps(1234.4));
        Assert.Equal("27 t/s", Format.MenuTps(27.4));
        Assert.Equal("8.4 t/s", Format.MenuTps(8.44));
        Assert.Equal("1.2k t/s", Format.MenuTps(1234));
    }

    [Fact]
    public void Durations()
    {
        Assert.Equal("265 ms", Format.Milliseconds(265.07));
        Assert.Equal("2.1 s", Format.Milliseconds(2_100));
        Assert.Equal("1m 35s", Format.Milliseconds(95_000));
        Assert.Equal("4:32", Format.Countdown(272));
        Assert.Equal("1h 05m", Format.Countdown(3_900));
    }

    [Fact]
    public void Bytes()
    {
        Assert.Equal("512 MB", Format.Bytes(536_870_912));
        Assert.Equal("24.0 GB", Format.Bytes(25_769_803_776));
    }
}

public class OllamaJsonTests
{
    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    [Fact]
    public void SlotsWithArrayNextToken()
    {
        const string json = """[{"id":0,"n_ctx":262144,"is_processing":true,"id_task":12015,"n_prompt_tokens":48185,"n_prompt_tokens_processed":30,"n_prompt_tokens_cache":48135,"next_token":[{"has_next_token":true,"n_decoded":99}]}]""";
        Assert.Equal([new SlotReading(true, 12015, 262144, 48185, 30, 48135, 99)], OllamaJson.Slots(Utf8(json)));
    }

    [Fact]
    public void SlotsWithObjectNextToken()
    {
        const string json = """[{"is_processing":false,"id_task":1,"next_token":{"n_decoded":4}}]""";
        Assert.Equal(4, OllamaJson.Slots(Utf8(json))![0].DecodedTokens);
    }

    [Fact]
    public void RejectsErrorBody()
    {
        Assert.Null(OllamaJson.Slots(Utf8("""{"error":{"code":501}}""")));
        Assert.Null(OllamaJson.Slots(Utf8("not json")));
    }

    [Fact]
    public void Models()
    {
        const string json = """{"models":[{"name":"qwen:latest","size":47023736749,"details":{"parameter_size":"176.9B","quantization_level":"IQ4_XS"},"expires_at":"2026-11-08T13:40:07.774411+02:00","size_vram":47023736749,"context_length":262144,"runner":"llamacpp"}]}""";
        var model = OllamaJson.Models(Utf8(json))![0];
        Assert.Equal("qwen:latest", model.Name);
        Assert.Equal(47_023_736_749UL, model.SizeVram);
        Assert.Equal(262144, model.ContextLength);
        Assert.Equal("IQ4_XS", model.Quantization);
        Assert.Equal(new DateTimeOffset(2026, 11, 8, 11, 40, 7, TimeSpan.Zero), model.ExpiresAt);
    }

    [Fact]
    public void DatesWithNanosecondsAndNegativeOffsets()
    {
        Assert.Equal(
            new DateTimeOffset(2026, 11, 14, 18, 1, 9, TimeSpan.Zero),
            OllamaJson.Date("2026-11-14T13:01:09.123456789-05:00"));
        Assert.Equal(new DateTimeOffset(2026, 11, 14, 21, 1, 9, TimeSpan.Zero), OllamaJson.Date("2026-11-14T21:01:09Z"));
        Assert.Null(OllamaJson.Date("soon"));
    }
}

public sealed class LogTailerTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ollameter-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string file;

    public LogTailerTests()
    {
        Directory.CreateDirectory(directory);
        file = Path.Combine(directory, "server.log");
    }

    public void Dispose()
    {
        try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
    }

    private void Append(string text)
    {
        using var stream = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        stream.Write(Encoding.UTF8.GetBytes(text));
    }

    [Fact]
    public void BacklogDropsCutOffFirstLine()
    {
        Append("first line here\nsecond\nthird\n");
        Assert.Equal(["second", "third"], new LogTailer(file).OpenAtEnd(15));
    }

    [Fact]
    public void HoldsBackUnfinishedLine()
    {
        Append("start\n");
        var tailer = new LogTailer(file);
        tailer.OpenAtEnd(0);
        Append("par");
        Assert.Empty(tailer.ReadNew());
        Append("tial\nnext\n");
        Assert.Equal(["partial", "next"], tailer.ReadNew());
    }

    [Fact]
    public void FollowsRotation()
    {
        Append("old\n");
        var tailer = new LogTailer(file);
        tailer.OpenAtEnd(0);
        File.Move(file, Path.Combine(directory, "server-1.log"));
        Append("new\n");
        Assert.Equal(["new"], tailer.ReadNew());
    }

    [Fact]
    public void DropsCarriageReturns()
    {
        Append("start\n");
        var tailer = new LogTailer(file);
        tailer.OpenAtEnd(0);
        Append("one\r\ntwo\n");
        Assert.Equal(["one", "two"], tailer.ReadNew());
    }

    [Fact]
    public void NeverStopsTheWriterRenamingTheFile()
    {
        // Ollama renames server.log when it restarts, so an open handle without delete sharing would break it.
        Append("line\n");
        var tailer = new LogTailer(file);
        tailer.OpenAtEnd(100);
        using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            File.Move(file, Path.Combine(directory, "server-1.log"));
        }
        Assert.Empty(tailer.ReadNew());
    }

    [Fact]
    public void MissingFileIsHarmless()
    {
        var tailer = new LogTailer(Path.Combine(directory, "absent.log"));
        Assert.Empty(tailer.OpenAtEnd(100));
        Assert.Empty(tailer.ReadNew());
    }
}

public class SharingTests
{
    private static MeterViewModel SampleModel()
    {
        // Whole seconds, so dates survive the trip exactly.
        var model = new MeterViewModel { Now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000) };
        model.Snapshot.Phase = Phase.Generating;
        model.Snapshot.LiveTps = 27.4;
        model.Snapshot.GeneratedTokens = 321;
        model.Version = "0.40.16";
        model.Models =
        [
            new LoadedModel
            {
                Name = "llama3.2:3b", Size = 2_000_000_000, SizeVram = 2_000_000_000, ContextLength = 8192,
                ExpiresAt = DateTimeOffset.FromUnixTimeSeconds(1_800_000_300), ParameterSize = "3.2B", Quantization = "Q4_K_M",
            },
        ];
        var stats = new SessionStats();
        for (var index = 0; index < 14; index++)
        {
            var request = new CompletedRequest
            {
                Sequence = index, TaskId = 100 + index, FinishedAt = DateTimeOffset.FromUnixTimeSeconds(1_799_999_000 + index),
                PromptTokens = 4_000, EvaluatedTokens = 300, PromptMilliseconds = 900, PromptTps = 333,
                GeneratedTokens = 200, GenerationMilliseconds = 7_000, GenerationTps = 28.5,
            };
            stats.Record(request);
            model.Requests.Insert(0, request);
        }
        model.Stats = stats;
        model.Notices = [new Notice("n", NoticeSeverity.Serious, "A notice.", model.Now)];
        model.Hardware.GpuUtilization = 97;
        model.Hardware.MemoryPressure = MemoryPressure.Warning;
        model.Runner = new RunnerStats(12, 3_000_000_000);
        model.PromptCache = new PromptCacheState { LimitBytes = 1UL << 30, UsedBytes = 1UL << 29, SavedPrompts = 2, SavedTokens = [9, 8] };
        model.Display = new DisplayState
        {
            Fraction = 0.97, Tint = Tint.Gpu, ModelLoaded = true, Text = "27 t/s", Summary = "Generating",
            DisplayPhase = Phase.Generating, HeldTps = 27,
        };
        model.TpsHistory = [0, 26.5, 27.4];
        model.GpuHistory = [3, 97];
        return model;
    }

    private static string Json(MeterViewModel model) => JsonSerializer.Serialize(model, SharedFrame.Options);

    [Fact]
    public void FrameRoundTripKeepsEverythingShown()
    {
        var model = SampleModel();
        model.Unload = UnloadState.Confirming("llama3.2:3b");
        model.Remote = new RemoteSource("elsewhere", RemoteStatus.Connected);
        var result = SharedFrame.Decode(new SharedFrame("studio", "0.2.0", model).Encoded());
        Assert.True(result.Succeeded);
        var frame = result.Frame!;

        Assert.Equal("studio", frame.Host);
        Assert.Equal("0.2.0", frame.App);
        Assert.Equal(SharedFrame.WindowsPlatform, frame.Platform);
        var expected = model.Copy();
        expected.Requests = [.. model.Requests.Take(SharedFrame.RequestLimit)];
        Assert.Equal(Json(expected), Json(frame.Model));
        // The receiving computer's own button and source aren't the sender's to set.
        Assert.Equal(UnloadState.Ready, frame.Model.Unload);
        Assert.Null(frame.Model.Remote);
        Assert.Equal(model.Stats.Generation.Average, frame.Model.Stats.Generation.Average);
        Assert.Equal(model.Stats.FirstTokenAverage, frame.Model.Stats.FirstTokenAverage);
        // Trimming the frame doesn't touch the model it came from.
        Assert.Equal(14, model.Requests.Count);
    }

    [Fact]
    public void FrameUsesTheSwiftAppsKeys()
    {
        var root = JsonNode.Parse(new SharedFrame("studio", "0.4.0", SampleModel()).Encoded())!.AsObject();
        Assert.Equal(["format", "host", "app", "platform", "model"], root.Select(pair => pair.Key));
        var model = root["model"]!.AsObject();
        Assert.Equal(
            ["now", "snapshot", "display", "stats", "requests", "notices", "models", "version", "hardware", "runner",
                "promptCache", "tpsHistory", "gpuHistory"],
            model.Select(pair => pair.Key));
        Assert.Equal(
            ["prompt", "generation", "requests", "generatedTokens", "promptTokens", "cachedTokens", "promptMilliseconds",
                "generationMilliseconds", "firstTokenMin", "firstTokenMax", "firstTokenSum"],
            model["stats"]!.AsObject().Select(pair => pair.Key));
        Assert.Equal(
            ["peak", "low", "history", "tokens", "milliseconds"],
            model["stats"]!["generation"]!.AsObject().Select(pair => pair.Key));
        Assert.Equal(
            ["fraction", "tint", "modelLoaded", "text", "textDimmed", "summary", "displayPhase", "heldTPS"],
            model["display"]!.AsObject().Select(pair => pair.Key));
        Assert.Equal(
            ["sequence", "task", "finishedAt", "promptTokens", "evaluatedTokens", "promptMilliseconds", "promptTPS",
                "generatedTokens", "generationMilliseconds", "generationTPS"],
            model["requests"]![0]!.AsObject().Select(pair => pair.Key));
        Assert.Equal(
            ["name", "size", "sizeVRAM", "contextLength", "expiresAt", "parameterSize", "quantization"],
            model["models"]![0]!.AsObject().Select(pair => pair.Key));
        Assert.Equal(["phase", "generatedTokens", "liveTPS"], model["snapshot"]!.AsObject().Select(pair => pair.Key));
        Assert.Equal(["gpuUtilization", "memoryPressure"], model["hardware"]!.AsObject().Select(pair => pair.Key));
        Assert.Equal("generating", (string?)model["snapshot"]!["phase"]);
        Assert.Equal("gpu", (string?)model["display"]!["tint"]);
        Assert.Equal("Warning", (string?)model["hardware"]!["memoryPressure"]);
        Assert.Equal("serious", (string?)model["notices"]![0]!["severity"]);
        Assert.Equal(1_800_000_000, (double?)model["now"]);
    }

    /// <summary>Tests/Fixtures holds frames both apps' tests read, so each is checked against the other's format.</summary>
    private static string Fixture(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Package.swift"))) directory = directory.Parent;
        Assert.NotNull(directory);
        return Path.Combine(directory.FullName, "Tests", "Fixtures", name);
    }

    [Fact]
    public void ReadsAFrameFromTheSwiftApp()
    {
        // Shaped like a frame from Ollameter 0.3.0 on a Mac, which sends no platform and whole dates as integers.
        var result = SharedFrame.Decode(File.ReadAllBytes(Fixture("frame-from-mac.json")));
        Assert.True(result.Succeeded);
        var model = result.Frame!.Model;
        Assert.Null(model.Platform);
        Assert.False(model.IsFromWindows);
        Assert.Equal(Phase.Generating, model.Snapshot.Phase);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1794697259), model.Models[0].ExpiresAt);
        Assert.Equal(225, model.Stats.Requests);
        Assert.Equal(134488 * 1000 / 5028542.17, model.Stats.Generation.Average!.Value, 1e-9);
        Assert.Equal(654980.19 / 225, model.Stats.FirstTokenAverage!.Value, 1e-9);
        Assert.Equal(0.5535, model.PromptCache!.Fraction!.Value, 0.001);
        Assert.Equal(MemoryPressure.Normal, model.Hardware.MemoryPressure);
        Assert.Equal(NoticeSeverity.Warning, model.Notices[0].Severity);
    }

    /// <summary>
    /// The frame this app sends, which the Swift tests decode. Set OLLAMETER_WRITE_FIXTURES=1 to write it again after
    /// a deliberate change to the format.
    /// </summary>
    [Fact]
    public void WritesTheFrameTheSwiftTestsRead()
    {
        var model = SampleModel();
        model.Hardware.GpuMemoryInUse = 21_474_836_480;
        model.Hardware.GpuMemoryTotal = 25_769_803_776;
        model.Hardware.GpuPowerWatts = 312.5;
        model.Hardware.DieTemperature = 68;
        var encoded = new SharedFrame("workstation", "0.4.0", model).Encoded();
        var path = Fixture("frame-from-windows.json");
        if (Environment.GetEnvironmentVariable("OLLAMETER_WRITE_FIXTURES") == "1") File.WriteAllBytes(path, [.. encoded, (byte)'\n']);
        Assert.Equal(File.ReadAllText(path).TrimEnd('\n'), Encoding.UTF8.GetString(encoded));
    }

    [Fact]
    public void FrameIsOneLineForTheEventStream()
    {
        var model = SampleModel();
        model.Notices = [new Notice("n", NoticeSeverity.Warning, "Two\nlines", model.Now)];
        var data = new SharedFrame("studio", null, model).Encoded();
        Assert.DoesNotContain((byte)'\n', data);
        Assert.DoesNotContain((byte)'\r', data);
    }

    [Fact]
    public void InfiniteSpeedStillEncodes()
    {
        var model = SampleModel();
        model.Snapshot.PromptTps = double.PositiveInfinity;
        var result = SharedFrame.Decode(new SharedFrame("studio", null, model).Encoded());
        Assert.True(result.Succeeded);
        Assert.Equal(double.PositiveInfinity, result.Frame!.Model.Snapshot.PromptTps);
    }

    [Fact]
    public void OtherFormatIsReportedWithItsVersion()
    {
        var other = SharedFrame.Decode("""{"format":2,"app":"0.9.0","host":"studio"}"""u8);
        Assert.False(other.Succeeded);
        Assert.Equal(SharedFrame.DecodeFailureKind.OtherFormat, other.Failure);
        Assert.Equal(2, other.Format);
        Assert.Equal("0.9.0", other.App);
        Assert.Equal(SharedFrame.DecodeFailureKind.Malformed, SharedFrame.Decode("nonsense"u8).Failure);
        Assert.Equal(SharedFrame.DecodeFailureKind.Malformed, SharedFrame.Decode("""{"format":1,"host":"studio"}"""u8).Failure);
    }

    [Fact]
    public void PlaceholderNamesTheProblem()
    {
        var model = MeterViewModels.Placeholder(new RemoteSource("studio", RemoteStatus.Unreachable));
        Assert.Equal(Phase.Offline, model.Display.DisplayPhase);
        Assert.Equal("Can't reach studio. Trying again…", model.Display.Summary);
        Assert.Equal("studio", model.Remote?.Host);
        Assert.Null(new RemoteSource("studio", RemoteStatus.Connected).Problem);
    }

    [Fact]
    public void Addresses()
    {
        Assert.Equal(MeterSource.Address("studio.local", 11480), MeterSource.ParseAddress("studio.local"));
        Assert.Equal(MeterSource.Address("192.168.1.20", 9000), MeterSource.ParseAddress(" 192.168.1.20:9000 "));
        Assert.Equal(MeterSource.Address("studio.local", 9000), MeterSource.ParseAddress("http://studio.local:9000/v1/state"));
        Assert.Equal(MeterSource.Address("fe80::1", 9000), MeterSource.ParseAddress("[fe80::1]:9000"));
        Assert.Equal(MeterSource.Address("fe80::1", 11480), MeterSource.ParseAddress("fe80::1"));
        Assert.Null(MeterSource.ParseAddress(""));
        Assert.Null(MeterSource.ParseAddress("studio local"));
        Assert.Null(MeterSource.ParseAddress("studio:0"));
        Assert.Null(MeterSource.ParseAddress("studio:99999"));
        Assert.Null(MeterSource.ParseAddress(":9000"));
    }

    [Fact]
    public void SourcesSurviveStorage()
    {
        MeterSource[] sources =
        [
            MeterSource.Local, MeterSource.Service("Studio (2)"), MeterSource.Address("studio.local", 11480),
            MeterSource.Address("10.0.0.2", 9000), MeterSource.Address("fe80::1", 9000),
        ];
        foreach (var source in sources) Assert.Equal(source, MeterSource.FromStorageValue(source.StorageValue));
        Assert.Null(MeterSource.FromStorageValue("service:"));
        Assert.Null(MeterSource.FromStorageValue("bogus"));
        Assert.Equal("studio", MeterSource.Address("studio", 11480).DisplayName);
        Assert.Equal("studio:9000", MeterSource.Address("studio", 9000).DisplayName);
    }

    [Fact]
    public void RequestHead()
    {
        Assert.Equal(HttpRequestHead.ParseKind.Incomplete, HttpRequestHead.Parse("GET /v1/state HTTP/1.1\r\nHost: x\r\n"u8).Kind);
        var complete = HttpRequestHead.Parse("GET /v1/events?x=1 HTTP/1.1\r\nHost: x\r\n\r\n"u8);
        Assert.Equal(HttpRequestHead.ParseKind.Complete, complete.Kind);
        Assert.Equal(new HttpRequestHead("GET", "/v1/events"), complete.Head);
        Assert.Equal(HttpRequestHead.ParseKind.Malformed, HttpRequestHead.Parse("hello there\r\n\r\n"u8).Kind);
        var oversized = Enumerable.Repeat((byte)0x41, HttpRequestHead.SizeLimit + 1).ToArray();
        Assert.Equal(HttpRequestHead.ParseKind.Malformed, HttpRequestHead.Parse(oversized).Kind);
    }

    [Fact]
    public void EventStreamAcrossChunks()
    {
        const string response = "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\n\r\n"
            + "data: {\"a\":1}\n\n: a comment\n\ndata: {\"b\":2}\r\n\r\ndata: first\ndata: second\n\ndata: {\"c\"";
        var parser = new EventStreamParser();
        var events = new List<string>();
        // One byte at a time, so every boundary lands mid-chunk somewhere.
        foreach (var value in Encoding.UTF8.GetBytes(response)) events.AddRange(parser.Append([value]));
        Assert.Equal(["{\"a\":1}", "{\"b\":2}", "first\nsecond"], events);
        Assert.Equal(["{\"c\":3}"], parser.Append(":3}\n\n"u8));
    }

    [Fact]
    public void EventStreamRejectsErrors()
    {
        var failure = Assert.Throws<EventStreamParser.FailureException>(
            () => new EventStreamParser().Append("HTTP/1.1 404 Not Found\r\n\r\n"u8));
        Assert.Equal(EventStreamParser.FailureKind.Status, failure.Kind);
        Assert.Equal(404, failure.Status);
        Assert.Throws<EventStreamParser.FailureException>(() => new EventStreamParser().Append("SSH-2.0-OpenSSH\r\n\r\n"u8));
    }
}

public class MultipleComputersTests
{
    private static MeterViewModel Model(Phase phase, RemoteStatus? remote = null)
    {
        var model = new MeterViewModel();
        model.Display.DisplayPhase = phase;
        model.Remote = new RemoteSource("studio", remote ?? RemoteStatus.Connected);
        return model;
    }

    [Fact]
    public void OrderPutsThisPcFirstThenNames()
    {
        var ordered = MeterSource.Ordered(
        [
            MeterSource.Service("studio"), MeterSource.Address("10.0.0.2", 11480), MeterSource.Local,
            MeterSource.Service("Mini"), MeterSource.Service("studio"),
        ]);
        Assert.Equal(
            [MeterSource.Local, MeterSource.Address("10.0.0.2", 11480), MeterSource.Service("Mini"), MeterSource.Service("studio")],
            ordered);
    }

    [Fact]
    public void BusiestPrefersWorkingComputers()
    {
        MeterViewModel[] models = [Model(Phase.Idle), Model(Phase.Generating), Model(Phase.Prefill)];
        Assert.Equal(1, MeterViewModels.BusiestIndex(models, null));
        Assert.Equal(1, MeterViewModels.BusiestIndex(models, 0));
    }

    [Fact]
    public void BusiestStaysOnTheCurrentComputerWhenTied()
    {
        MeterViewModel[] models = [Model(Phase.Generating), Model(Phase.Idle), Model(Phase.Generating)];
        Assert.Equal(2, MeterViewModels.BusiestIndex(models, 2));
    }

    [Fact]
    public void UnreachableComputersRankLowest()
    {
        var unreachable = MeterViewModels.Placeholder(new RemoteSource("mini", RemoteStatus.Unreachable));
        MeterViewModel[] models = [unreachable, Model(Phase.Offline), Model(Phase.Idle)];
        Assert.Equal(2, MeterViewModels.BusiestIndex(models, 0));
        Assert.Null(MeterViewModels.BusiestIndex([], null));
    }
}
