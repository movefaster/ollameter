namespace Ollameter.Core;

public sealed class ActiveRequest
{
    public int Task { get; set; }
    public int ContextSize { get; set; }
    public int PromptTokens { get; set; }
    /// <summary>New tokens read so far. The log's count leaves out the prefix that came from the cache.</summary>
    public int? PromptTokensRead { get; set; }
    /// <summary>The log's own progress figure, which does cover the whole prompt.</summary>
    public double? PromptProgress { get; set; }
    public double? PromptTps { get; set; }
    public int? GeneratedTokens { get; set; }
    public double? RecentTps { get; set; }
    public DateTimeOffset? LastUpdate { get; set; }
}

/// <summary>
/// Turns the log event stream into in-flight requests, finished requests and session statistics. A null date
/// means the event came from the backlog read at launch, not from a live tail.
/// </summary>
public sealed class RequestTracker
{
    public const int RecentLimit = 40;
    public static readonly TimeSpan LoadingTimeout = TimeSpan.FromSeconds(600);

    private sealed class Timing
    {
        public double? PromptMilliseconds;
        public int PromptTokens;
        public double PromptTps;
        public double? GenerationMilliseconds;
        public int GeneratedTokens;
        public double GenerationTps;
    }

    private readonly Dictionary<int, Timing> timings = [];
    private int nextSequence;

    public Dictionary<int, ActiveRequest> Active { get; } = [];
    public List<CompletedRequest> Recent { get; } = [];
    public SessionStats Stats { get; private set; } = new();
    public List<Notice> Notices { get; } = [];
    public DateTimeOffset? LoadingSince { get; private set; }
    public PromptCacheState? PromptCache { get; private set; }

    public void Apply(LogEvent e, DateTimeOffset? date)
    {
        switch (e.Kind)
        {
            case LogEventKind.NewPrompt:
                Active[e.Task] = new ActiveRequest
                {
                    Task = e.Task, ContextSize = e.ContextSize, PromptTokens = e.Tokens, LastUpdate = date,
                };
                timings.Remove(e.Task);
                LoadingSince = null;
                break;
            case LogEventKind.PromptProgress:
                if (Active.TryGetValue(e.Task, out var reading))
                {
                    reading.PromptTokensRead = e.Tokens;
                    reading.PromptProgress = e.Value;
                    reading.PromptTps = e.Rate;
                    reading.LastUpdate = date;
                }
                break;
            case LogEventKind.GenerationProgress:
                if (Active.TryGetValue(e.Task, out var generating))
                {
                    generating.GeneratedTokens = e.Tokens;
                    generating.RecentTps = e.RecentRate;
                    generating.LastUpdate = date;
                }
                break;
            case LogEventKind.PromptEval:
                {
                    var timing = TimingFor(e.Task);
                    timing.PromptMilliseconds = e.Value;
                    timing.PromptTokens = e.Tokens;
                    timing.PromptTps = e.Rate;
                }
                break;
            case LogEventKind.Eval:
                {
                    var timing = TimingFor(e.Task);
                    timing.GenerationMilliseconds = e.Value;
                    timing.GeneratedTokens = e.Tokens;
                    timing.GenerationTps = e.Rate;
                }
                break;
            case LogEventKind.Total:
                Finish(e.Task, date);
                break;
            case LogEventKind.Released:
            case LogEventKind.Cancelled:
                Active.Remove(e.Task);
                timings.Remove(e.Task);
                break;
            case LogEventKind.AllIdle:
                Active.Clear();
                break;
            case LogEventKind.FullReprocess:
                AddNotice("full-reprocess", NoticeSeverity.Warning,
                    "The prompt cache missed, so the whole prompt was read again.", date);
                break;
            case LogEventKind.ContextShift:
                AddNotice("context-shift", NoticeSeverity.Warning,
                    "The context filled up, so older tokens were dropped.", date);
                break;
            case LogEventKind.InputTruncated:
                AddNotice("truncated", NoticeSeverity.Serious,
                    $"A {Format.Integer(e.Tokens)}-token prompt was cut to {Format.Integer(e.ContextSize)} tokens. "
                    + "Raise the model's context length to avoid this.", date);
                break;
            case LogEventKind.ModelLoading:
                if (date is { } loading) LoadingSince = loading;
                break;
            case LogEventKind.ModelLoaded:
                // Speeds from one model mean nothing for the next, so statistics follow the loaded model.
                LoadingSince = null;
                Active.Clear();
                Recent.Clear();
                timings.Clear();
                Stats = new SessionStats();
                break;
            case LogEventKind.PromptCacheEnabled:
                // Logged once per load, before the model is ready, and the old cache died with the old runner.
                PromptCache = new PromptCacheState { LimitBytes = e.Limit is { } enabledLimit ? Bytes(enabledLimit) : null };
                break;
            case LogEventKind.PromptCacheDisabled:
                PromptCache = null;
                break;
            case LogEventKind.PromptCacheState:
                {
                    var cache = PromptCache ?? new PromptCacheState();
                    cache.UsedBytes = Bytes(e.Value);
                    cache.LimitBytes = e.Limit is { } limit && limit > 0 ? Bytes(limit) : null;
                    cache.SavedPrompts = e.Tokens;
                    cache.SavedTokens = [];
                    PromptCache = cache;
                }
                break;
            case LogEventKind.PromptCacheEntry:
                // The log lists these oldest first.
                PromptCache?.SavedTokens.Insert(0, e.Tokens);
                break;
            case LogEventKind.PromptCacheEvicted:
                {
                    var cache = PromptCache ?? new PromptCacheState();
                    cache.Evictions += 1;
                    PromptCache = cache;
                    AddNotice("cache-evicted", NoticeSeverity.Warning,
                        $"The prompt cache was full, so the oldest saved prompt ({Format.Bytes(Bytes(e.Value))}) was dropped.",
                        date);
                }
                break;
        }
    }

    private Timing TimingFor(int task)
    {
        if (!timings.TryGetValue(task, out var timing))
        {
            timing = new Timing();
            timings[task] = timing;
        }
        return timing;
    }

    private static ulong Bytes(double mebibytes) => (ulong)(mebibytes * 1_048_576);

    public bool IsLoadingModel(DateTimeOffset now) => LoadingSince is { } since && now - since < LoadingTimeout;

    private void Finish(int task, DateTimeOffset? date)
    {
        if (!timings.Remove(task, out var timing)) return;
        if (timing.PromptMilliseconds is not { } promptMilliseconds
            || timing.GenerationMilliseconds is not { } generationMilliseconds) return;
        var request = new CompletedRequest
        {
            Sequence = nextSequence,
            TaskId = task,
            FinishedAt = date,
            PromptTokens = Active.TryGetValue(task, out var active) ? active.PromptTokens : timing.PromptTokens,
            EvaluatedTokens = timing.PromptTokens,
            PromptMilliseconds = promptMilliseconds,
            PromptTps = timing.PromptTps,
            GeneratedTokens = timing.GeneratedTokens,
            GenerationMilliseconds = generationMilliseconds,
            GenerationTps = timing.GenerationTps,
        };
        nextSequence += 1;
        Recent.Insert(0, request);
        if (Recent.Count > RecentLimit) Recent.RemoveRange(RecentLimit, Recent.Count - RecentLimit);
        Stats.Record(request);
    }

    private void AddNotice(string id, NoticeSeverity severity, string text, DateTimeOffset? date)
    {
        if (date is not { } at) return;
        Notices.RemoveAll(notice => notice.Id == id);
        Notices.Insert(0, new Notice(id, severity, text, at));
    }
}

/// <summary>Tokens per second over a sliding window of cumulative token counts.</summary>
public sealed class SlidingRate(double windowSeconds)
{
    public const double MinimumSpan = 0.4;

    private readonly List<(DateTimeOffset Time, int Count)> points = [];

    public void Add(int count, DateTimeOffset time)
    {
        if (points.Count > 0 && count < points[^1].Count) points.Clear();
        points.Add((time, count));
        var cutoff = time.AddSeconds(-windowSeconds);
        while (points.Count > 2 && points[1].Time <= cutoff) points.RemoveAt(0);
    }

    public void Reset() => points.Clear();

    public double? Rate
    {
        get
        {
            if (points.Count == 0) return null;
            var span = (points[^1].Time - points[0].Time).TotalSeconds;
            if (span < MinimumSpan) return null;
            return (points[^1].Count - points[0].Count) / span;
        }
    }
}
