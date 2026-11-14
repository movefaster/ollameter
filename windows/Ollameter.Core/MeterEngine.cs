namespace Ollameter.Core;

/// <summary>
/// Combines the log stream and <c>/slots</c> polling into one live view of what the runner is doing.
/// </summary>
/// <remarks>
/// <c>/slots</c> is the primary live source because it updates per token. The log only reports generation speed
/// every few seconds, but it has exact final timings and is the fallback if <c>/slots</c> goes away.
/// </remarks>
public sealed class MeterEngine
{
    private static readonly TimeSpan LogOnlyStaleAfter = TimeSpan.FromSeconds(120);

    private List<SlotReading> slots = [];
    private bool hasSlots;
    private readonly SlidingRate generationRate = new(3);
    private int? trackedTask;
    private DateTimeOffset? prefillBegan;
    private DateTimeOffset? generationBegan;

    public RequestTracker Tracker { get; } = new();

    public LogEvent? Ingest(string line, DateTimeOffset? date)
    {
        if (LogParser.Parse(line) is not { } e) return null;
        Tracker.Apply(e, date);
        return e;
    }

    /// <summary>Pass null when no runner answered, which switches live state to the log.</summary>
    public void Update(IReadOnlyList<SlotReading>? newSlots, DateTimeOffset now)
    {
        if (newSlots is null)
        {
            slots = [];
            hasSlots = false;
            ClearTracking();
            return;
        }
        slots = [.. newSlots];
        hasSlots = true;
        if (Processing() is not { } current)
        {
            ClearTracking();
            return;
        }
        if (current.Task != trackedTask)
        {
            ClearTracking();
            trackedTask = current.Task;
            prefillBegan = now;
        }
        if (current.DecodedTokens > 0)
        {
            generationBegan ??= now;
            generationRate.Add(current.DecodedTokens, now);
        }
    }

    public LiveSnapshot Snapshot(DateTimeOffset now, bool online)
    {
        var snapshot = new LiveSnapshot();
        if (!online)
        {
            snapshot.Phase = Phase.Offline;
            return snapshot;
        }
        if (Processing() is { } current)
        {
            Fill(snapshot, current, now);
            return snapshot;
        }
        if (!hasSlots && FreshLogRequest(now) is { } request)
        {
            Fill(snapshot, request);
            return snapshot;
        }
        snapshot.Phase = Tracker.IsLoadingModel(now) ? Phase.Loading : Phase.Idle;
        if (slots.Count > 0 && slots[0].PromptTokens > 0)
        {
            snapshot.ContextSize = slots[0].ContextSize;
            snapshot.ContextUsed = slots[0].PromptTokens;
        }
        return snapshot;
    }

    private SlotReading? Processing()
    {
        foreach (var slot in slots)
            if (slot.IsProcessing) return slot;
        return null;
    }

    private void ClearTracking()
    {
        trackedTask = null;
        prefillBegan = null;
        generationBegan = null;
        generationRate.Reset();
    }

    private ActiveRequest? FreshLogRequest(DateTimeOffset now) =>
        Tracker.Active.Values
            .Where(request => request.LastUpdate is { } updated && now - updated < LogOnlyStaleAfter)
            .MaxBy(request => request.LastUpdate);

    private void Fill(LiveSnapshot snapshot, SlotReading slot, DateTimeOffset now)
    {
        Tracker.Active.TryGetValue(slot.Task, out var log);
        snapshot.TaskId = slot.Task;
        snapshot.ContextSize = slot.ContextSize;
        snapshot.ContextUsed = slot.PromptTokens;

        if (slot.DecodedTokens > 0)
        {
            snapshot.Phase = Phase.Generating;
            snapshot.GeneratedTokens = slot.DecodedTokens;
            snapshot.LiveTps = generationRate.Rate ?? log?.RecentTps;
            snapshot.PhaseElapsed = generationBegan is { } began ? (now - began).TotalSeconds : null;
            return;
        }

        snapshot.Phase = Phase.Prefill;
        var cached = slot.CachedPromptTokens;
        // Both counts leave out the cached prefix. /slots updates between the log's progress lines.
        var read = Math.Max(slot.ProcessedPromptTokens, log?.PromptTokensRead ?? 0);
        var elapsed = prefillBegan is { } start ? (now - start).TotalSeconds : 0;
        var rate = log?.PromptTps ?? (elapsed > 0.5 && read > 0 ? read / elapsed : (double?)null);

        snapshot.PromptCached = cached;
        snapshot.PromptDone = read;
        snapshot.PromptTps = rate;
        snapshot.PhaseElapsed = elapsed;

        // A percentage needs the prompt's full size, which only the log's "new prompt" line has.
        if (log is null || log.PromptTokens <= 0) return;
        var total = log.PromptTokens;
        var toProcess = Math.Max(total - cached, 0);
        var done = Math.Min(read, toProcess);
        snapshot.PromptTokens = total;
        snapshot.PromptDone = done;
        snapshot.PromptToProcess = toProcess;
        // The same definition as the log's `progress`, so the two can be compared directly.
        snapshot.PromptFraction = (double)(cached + done) / total;
        if (rate is { } r && r > 0) snapshot.EtaSeconds = (toProcess - done) / r;
    }

    private static void Fill(LiveSnapshot snapshot, ActiveRequest request)
    {
        snapshot.TaskId = request.Task;
        snapshot.ContextSize = request.ContextSize;
        snapshot.ContextUsed = request.PromptTokens + (request.GeneratedTokens ?? 0);

        if (request.GeneratedTokens is { } generated)
        {
            snapshot.Phase = Phase.Generating;
            snapshot.GeneratedTokens = generated;
            snapshot.LiveTps = request.RecentTps;
            return;
        }

        snapshot.Phase = Phase.Prefill;
        snapshot.PromptTokens = request.PromptTokens;
        snapshot.PromptDone = request.PromptTokensRead;
        snapshot.PromptTps = request.PromptTps;
        if (request.PromptProgress is not { } progress || request.PromptTokensRead is not { } read)
        {
            snapshot.PromptToProcess = request.PromptTokens;
            return;
        }
        var remaining = Math.Max((int)((1 - progress) * request.PromptTokens), 0);
        snapshot.PromptToProcess = read + remaining;
        snapshot.PromptFraction = Math.Min(progress, 1);
        if (request.PromptTps is { } rate && rate > 0) snapshot.EtaSeconds = remaining / rate;
    }
}
