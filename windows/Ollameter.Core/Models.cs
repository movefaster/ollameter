using System.Text.Json.Serialization;

namespace Ollameter.Core;

// JSON names match the Swift app's synthesized Codable keys exactly, so a Mac and a PC can read each
// other's frames. Every non-optional key is always written, because the Swift decoder requires them.

[JsonConverter(typeof(JsonStringEnumConverter<Phase>))]
public enum Phase
{
    [JsonStringEnumMemberName("offline")] Offline,
    [JsonStringEnumMemberName("idle")] Idle,
    [JsonStringEnumMemberName("loading")] Loading,
    [JsonStringEnumMemberName("prefill")] Prefill,
    [JsonStringEnumMemberName("generating")] Generating,
}

public static class PhaseExtensions
{
    public static bool IsBusy(this Phase phase) => phase is Phase.Prefill or Phase.Generating or Phase.Loading;
}

/// <summary>One entry from llama-server's <c>/slots</c> endpoint, reduced to the numeric fields the meter uses.</summary>
/// <param name="PromptTokens">
/// Tokens in the slot so far. This grows batch by batch while the prompt is read and then with each generated
/// token, so it isn't the prompt's size until the last batch. Only the log has that.
/// </param>
public readonly record struct SlotReading(
    bool IsProcessing, int Task, int ContextSize, int PromptTokens, int ProcessedPromptTokens,
    int CachedPromptTokens, int DecodedTokens);

public sealed record LiveSnapshot
{
    [JsonPropertyName("phase")] public Phase Phase { get; set; } = Phase.Idle;
    [JsonPropertyName("task")] public int? TaskId { get; set; }
    [JsonPropertyName("contextSize")] public int? ContextSize { get; set; }
    [JsonPropertyName("contextUsed")] public int? ContextUsed { get; set; }

    [JsonPropertyName("promptTokens")] public int? PromptTokens { get; set; }
    [JsonPropertyName("promptCached")] public int? PromptCached { get; set; }
    [JsonPropertyName("promptDone")] public int? PromptDone { get; set; }
    [JsonPropertyName("promptToProcess")] public int? PromptToProcess { get; set; }
    [JsonPropertyName("promptFraction")] public double? PromptFraction { get; set; }
    [JsonPropertyName("promptTPS")] public double? PromptTps { get; set; }
    [JsonPropertyName("etaSeconds")] public double? EtaSeconds { get; set; }

    [JsonPropertyName("generatedTokens")] public int? GeneratedTokens { get; set; }
    [JsonPropertyName("liveTPS")] public double? LiveTps { get; set; }
    [JsonPropertyName("phaseElapsed")] public double? PhaseElapsed { get; set; }
}

public sealed record CompletedRequest
{
    [JsonPropertyName("sequence")] public int Sequence { get; set; }
    [JsonPropertyName("task")] public int TaskId { get; set; }
    /// <summary>Null for requests recovered from the existing log, which has no timestamps on these lines.</summary>
    [JsonPropertyName("finishedAt")] public DateTimeOffset? FinishedAt { get; set; }
    [JsonPropertyName("promptTokens")] public int PromptTokens { get; set; }
    [JsonPropertyName("evaluatedTokens")] public int EvaluatedTokens { get; set; }
    [JsonPropertyName("promptMilliseconds")] public double PromptMilliseconds { get; set; }
    [JsonPropertyName("promptTPS")] public double PromptTps { get; set; }
    [JsonPropertyName("generatedTokens")] public int GeneratedTokens { get; set; }
    [JsonPropertyName("generationMilliseconds")] public double GenerationMilliseconds { get; set; }
    [JsonPropertyName("generationTPS")] public double GenerationTps { get; set; }

    [JsonIgnore] public int CachedTokens => Math.Max(0, PromptTokens - EvaluatedTokens);
    [JsonIgnore] public double CacheFraction => PromptTokens > 0 ? (double)CachedTokens / PromptTokens : 0;
    [JsonIgnore] public double TotalMilliseconds => PromptMilliseconds + GenerationMilliseconds;
}

/// <summary>Peak, low and weighted average speed across requests large enough to be meaningful.</summary>
public sealed class SpeedStat
{
    public const int HistoryLimit = 30;

    [JsonPropertyName("peak"), JsonInclude] public double? Peak { get; private set; }
    [JsonPropertyName("low"), JsonInclude] public double? Low { get; private set; }
    [JsonPropertyName("history"), JsonInclude] public List<double> History { get; private set; } = [];
    [JsonPropertyName("tokens"), JsonInclude] internal long Tokens { get; private set; }
    [JsonPropertyName("milliseconds"), JsonInclude] internal double Milliseconds { get; private set; }

    [JsonIgnore] public double? Average => Milliseconds > 0 ? Tokens * 1000.0 / Milliseconds : null;

    public void Record(long tokens, double milliseconds, double tps)
    {
        Peak = Math.Max(Peak ?? tps, tps);
        Low = Math.Min(Low ?? tps, tps);
        Tokens += tokens;
        Milliseconds += milliseconds;
        History.Add(tps);
        if (History.Count > HistoryLimit) History.RemoveRange(0, History.Count - HistoryLimit);
    }

    public SpeedStat Clone() => new()
    {
        Peak = Peak, Low = Low, History = [.. History], Tokens = Tokens, Milliseconds = Milliseconds,
    };
}

public sealed class SessionStats
{
    public const int PromptMinTokens = 64;
    public const int GenerationMinTokens = 16;

    [JsonPropertyName("prompt"), JsonInclude] public SpeedStat Prompt { get; private set; } = new();
    [JsonPropertyName("generation"), JsonInclude] public SpeedStat Generation { get; private set; } = new();
    [JsonPropertyName("requests"), JsonInclude] public long Requests { get; private set; }
    [JsonPropertyName("generatedTokens"), JsonInclude] public long GeneratedTokens { get; private set; }
    [JsonPropertyName("promptTokens"), JsonInclude] public long PromptTokens { get; private set; }
    [JsonPropertyName("cachedTokens"), JsonInclude] public long CachedTokens { get; private set; }
    [JsonPropertyName("promptMilliseconds"), JsonInclude] public double PromptMilliseconds { get; private set; }
    [JsonPropertyName("generationMilliseconds"), JsonInclude] public double GenerationMilliseconds { get; private set; }
    [JsonPropertyName("firstTokenMin"), JsonInclude] public double? FirstTokenMin { get; private set; }
    [JsonPropertyName("firstTokenMax"), JsonInclude] public double? FirstTokenMax { get; private set; }
    [JsonPropertyName("firstTokenSum"), JsonInclude] internal double FirstTokenSum { get; private set; }

    [JsonIgnore] public double? FirstTokenAverage => Requests > 0 ? FirstTokenSum / Requests : null;
    [JsonIgnore] public double? CacheFraction => PromptTokens > 0 ? (double)CachedTokens / PromptTokens : null;

    /// <summary>Share of inference time spent reading prompts rather than generating.</summary>
    [JsonIgnore]
    public double? PromptTimeFraction
    {
        get
        {
            var total = PromptMilliseconds + GenerationMilliseconds;
            return total > 0 ? PromptMilliseconds / total : null;
        }
    }

    public void Record(CompletedRequest request)
    {
        Requests += 1;
        GeneratedTokens += request.GeneratedTokens;
        PromptTokens += request.PromptTokens;
        CachedTokens += request.CachedTokens;
        PromptMilliseconds += request.PromptMilliseconds;
        GenerationMilliseconds += request.GenerationMilliseconds;
        FirstTokenSum += request.PromptMilliseconds;
        FirstTokenMin = Math.Min(FirstTokenMin ?? request.PromptMilliseconds, request.PromptMilliseconds);
        FirstTokenMax = Math.Max(FirstTokenMax ?? request.PromptMilliseconds, request.PromptMilliseconds);
        if (request.EvaluatedTokens >= PromptMinTokens)
            Prompt.Record(request.EvaluatedTokens, request.PromptMilliseconds, request.PromptTps);
        if (request.GeneratedTokens >= GenerationMinTokens)
            Generation.Record(request.GeneratedTokens, request.GenerationMilliseconds, request.GenerationTps);
    }

    public SessionStats Clone()
    {
        var copy = (SessionStats)MemberwiseClone();
        copy.Prompt = Prompt.Clone();
        copy.Generation = Generation.Clone();
        return copy;
    }
}

/// <summary>
/// What llama-server last logged about the prompts it keeps in RAM. It only writes this when the cache changes,
/// so the last report stays accurate until the next one. The prompt in the slot isn't included.
/// </summary>
public sealed class PromptCacheState
{
    /// <summary>Null when llama-server runs with no limit.</summary>
    [JsonPropertyName("limitBytes")] public ulong? LimitBytes { get; set; }
    [JsonPropertyName("usedBytes")] public ulong UsedBytes { get; set; }
    [JsonPropertyName("savedPrompts")] public int SavedPrompts { get; set; }
    /// <summary>Size of each saved prompt, newest first.</summary>
    [JsonPropertyName("savedTokens")] public List<int> SavedTokens { get; set; } = [];
    /// <summary>Prompts dropped to make room since this model loaded.</summary>
    [JsonPropertyName("evictions")] public int Evictions { get; set; }

    [JsonIgnore]
    public double? Fraction => LimitBytes is { } limit && limit > 0 ? (double)UsedBytes / limit : null;

    public PromptCacheState Clone() => new()
    {
        LimitBytes = LimitBytes, UsedBytes = UsedBytes, SavedPrompts = SavedPrompts, SavedTokens = [.. SavedTokens],
        Evictions = Evictions,
    };

    public bool SameAs(PromptCacheState? other) =>
        other is not null && LimitBytes == other.LimitBytes && UsedBytes == other.UsedBytes
        && SavedPrompts == other.SavedPrompts && Evictions == other.Evictions && SavedTokens.SequenceEqual(other.SavedTokens);
}

public enum UnloadKind { Ready, Confirming, Unloading, Failed }

/// <summary>
/// The window's unload button. A stray click would cost a long reload and every saved prompt, so the first
/// press only arms it and the next one sends the unload.
/// </summary>
public readonly record struct UnloadState(UnloadKind Kind, string? Model)
{
    public static readonly UnloadState Ready = new(UnloadKind.Ready, null);
    public static UnloadState Confirming(string model) => new(UnloadKind.Confirming, model);
    public static UnloadState Unloading(string model) => new(UnloadKind.Unloading, model);
    public static UnloadState Failed(string model) => new(UnloadKind.Failed, model);

    public bool IsUnloading => Kind == UnloadKind.Unloading;

    /// <summary>The state after a press on the button for <paramref name="name"/>, and whether that press sends the unload.</summary>
    public (UnloadState Next, bool SendsUnload) Pressed(string name) => Kind switch
    {
        UnloadKind.Unloading => (this, false),
        UnloadKind.Confirming when Model == name => (Unloading(name), true),
        _ => (Confirming(name), false),
    };
}

[JsonConverter(typeof(JsonStringEnumConverter<NoticeSeverity>))]
public enum NoticeSeverity
{
    [JsonStringEnumMemberName("warning")] Warning,
    [JsonStringEnumMemberName("serious")] Serious,
}

public sealed record Notice(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("severity")] NoticeSeverity Severity,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("at")] DateTimeOffset At);

public sealed record LoadedModel
{
    [JsonPropertyName("name")] public string Name { get; set; } = "unknown";
    [JsonPropertyName("size")] public ulong Size { get; set; }
    [JsonPropertyName("sizeVRAM")] public ulong SizeVram { get; set; }
    [JsonPropertyName("contextLength")] public int? ContextLength { get; set; }
    [JsonPropertyName("expiresAt")] public DateTimeOffset? ExpiresAt { get; set; }
    [JsonPropertyName("parameterSize")] public string? ParameterSize { get; set; }
    [JsonPropertyName("quantization")] public string? Quantization { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter<MemoryPressure>))]
public enum MemoryPressure
{
    [JsonStringEnumMemberName("Normal")] Normal,
    [JsonStringEnumMemberName("Warning")] Warning,
    [JsonStringEnumMemberName("Critical")] Critical,
}

public sealed record HardwareSample
{
    [JsonPropertyName("gpuUtilization")] public double? GpuUtilization { get; set; }
    [JsonPropertyName("gpuMemoryInUse")] public ulong? GpuMemoryInUse { get; set; }
    [JsonPropertyName("gpuPowerWatts")] public double? GpuPowerWatts { get; set; }
    /// <summary>The SoC die on a Mac, the GPU on a PC.</summary>
    [JsonPropertyName("dieTemperature")] public double? DieTemperature { get; set; }
    /// <summary>Macs only. Windows keeps idle pages in its page file as a matter of course, so it says nothing there.</summary>
    [JsonPropertyName("swapUsed")] public ulong? SwapUsed { get; set; }
    [JsonPropertyName("memoryPressure")] public MemoryPressure? MemoryPressure { get; set; }
    /// <summary>PCs only: the GPU's own memory, where Macs share theirs with the system. The Swift app ignores the key.</summary>
    [JsonPropertyName("gpuMemoryTotal")] public ulong? GpuMemoryTotal { get; set; }
}

public sealed record RunnerStats(
    [property: JsonPropertyName("cpuPercent")] double? CpuPercent,
    [property: JsonPropertyName("residentBytes")] ulong? ResidentBytes);

public sealed class MeterViewModel
{
    public const int HistoryLimit = 60;
    public static readonly TimeSpan NoticeLifetime = TimeSpan.FromSeconds(300);

    [JsonPropertyName("now")] public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
    [JsonPropertyName("snapshot")] public LiveSnapshot Snapshot { get; set; } = new();
    [JsonPropertyName("display")] public DisplayState Display { get; set; } = DisplayState.Initial;
    [JsonPropertyName("stats")] public SessionStats Stats { get; set; } = new();
    [JsonPropertyName("requests")] public List<CompletedRequest> Requests { get; set; } = [];
    [JsonPropertyName("notices")] public List<Notice> Notices { get; set; } = [];
    [JsonPropertyName("models")] public List<LoadedModel> Models { get; set; } = [];
    [JsonPropertyName("version")] public string? Version { get; set; }
    [JsonPropertyName("hardware")] public HardwareSample Hardware { get; set; } = new();
    [JsonPropertyName("runner")] public RunnerStats? Runner { get; set; }
    [JsonPropertyName("promptCache")] public PromptCacheState? PromptCache { get; set; }
    [JsonPropertyName("tpsHistory")] public List<double> TpsHistory { get; set; } = [];
    [JsonPropertyName("gpuHistory")] public List<double> GpuHistory { get; set; } = [];

    // The unload button and the remote source belong to the computer showing the window, so they aren't shared.
    [JsonIgnore] public UnloadState Unload { get; set; } = UnloadState.Ready;
    /// <summary>Set when the numbers come from another computer rather than this one.</summary>
    [JsonIgnore] public RemoteSource? Remote { get; set; }
    /// <summary>
    /// What the numbers' computer runs, from its frame: "windows" or "macos". Macs on Ollameter 0.3 and earlier
    /// don't say, so anything but "windows" is a Mac.
    /// </summary>
    [JsonIgnore] public string? Platform { get; set; } = SharedFrame.WindowsPlatform;

    [JsonIgnore] public bool IsFromWindows => Platform == SharedFrame.WindowsPlatform;

    /// <summary>True during a request and the short gaps between an agent's requests, when unloading would cut in.</summary>
    [JsonIgnore] public bool IsWorking => Snapshot.Phase.IsBusy() || Display.DisplayPhase.IsBusy();

    /// <summary>Log-derived notices plus conditions read from the current hardware and model state.</summary>
    [JsonIgnore]
    public List<Notice> VisibleNotices
    {
        get
        {
            var result = Notices.Where(notice => Now - notice.At < NoticeLifetime).ToList();
            if (Hardware.SwapUsed is { } swap && swap >= 1UL << 30 && Models.Count > 0)
            {
                result.Add(new Notice(
                    "swap", NoticeSeverity.Serious, $"{Format.Bytes(swap)} of swap in use, which can slow inference.", Now));
            }
            foreach (var model in Models.Where(model => model.Size > 0 && model.SizeVram < model.Size))
            {
                var share = (double)model.SizeVram / model.Size;
                result.Add(new Notice(
                    $"cpu-{model.Name}", NoticeSeverity.Serious,
                    $"Only {Format.Percent(share)} of {model.Name} is on the GPU. The rest runs on the CPU.", Now));
            }
            return result;
        }
    }

    /// <summary>Generation energy per token, available only during generation and the short gaps between requests.</summary>
    [JsonIgnore]
    public double? JoulesPerToken
    {
        get
        {
            if (Display.DisplayPhase != Phase.Generating) return null;
            if ((Snapshot.LiveTps ?? Display.HeldTps) is not { } tps || tps <= 0) return null;
            return Hardware.GpuPowerWatts is { } watts ? watts / tps : null;
        }
    }

    /// <summary>A copy whose lists can be changed without touching this one.</summary>
    public MeterViewModel Copy()
    {
        var copy = (MeterViewModel)MemberwiseClone();
        copy.Requests = [.. Requests];
        copy.Notices = [.. Notices];
        copy.Models = [.. Models];
        copy.TpsHistory = [.. TpsHistory];
        copy.GpuHistory = [.. GpuHistory];
        return copy;
    }
}
