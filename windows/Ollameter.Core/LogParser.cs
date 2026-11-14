using System.Globalization;

namespace Ollameter.Core;

public enum LogEventKind
{
    NewPrompt,
    PromptProgress,
    GenerationProgress,
    PromptEval,
    Eval,
    Total,
    Released,
    Cancelled,
    FullReprocess,
    ContextShift,
    InputTruncated,
    AllIdle,
    ModelLoading,
    ModelLoaded,
    PromptCacheEnabled,
    PromptCacheDisabled,
    PromptCacheState,
    PromptCacheEntry,
    PromptCacheEvicted,
}

/// <summary>
/// One line of interest from <c>server.log</c>. Which fields are set depends on the kind, as in the Swift enum's
/// associated values.
/// </summary>
public readonly record struct LogEvent(
    LogEventKind Kind,
    int Task = 0,
    int Tokens = 0,
    int ContextSize = 0,
    double Value = 0,
    double Rate = 0,
    double RecentRate = 0,
    double? Limit = null)
{
    public static LogEvent NewPrompt(int task, int contextSize, int promptTokens) =>
        new(LogEventKind.NewPrompt, task, promptTokens, contextSize);
    /// <summary><see cref="Value"/> is the log's progress fraction.</summary>
    public static LogEvent PromptProgress(int task, int tokens, double progress, double tps) =>
        new(LogEventKind.PromptProgress, task, tokens, Value: progress, Rate: tps);
    public static LogEvent GenerationProgress(int task, int tokens, double tps, double recentTps) =>
        new(LogEventKind.GenerationProgress, task, tokens, Rate: tps, RecentRate: recentTps);
    /// <summary><see cref="Value"/> is milliseconds.</summary>
    public static LogEvent PromptEval(int task, double milliseconds, int tokens, double tps) =>
        new(LogEventKind.PromptEval, task, tokens, Value: milliseconds, Rate: tps);
    public static LogEvent Eval(int task, double milliseconds, int tokens, double tps) =>
        new(LogEventKind.Eval, task, tokens, Value: milliseconds, Rate: tps);
    public static LogEvent Total(int task, double milliseconds, int tokens) =>
        new(LogEventKind.Total, task, tokens, Value: milliseconds);
    public static LogEvent Released(int task) => new(LogEventKind.Released, task);
    public static LogEvent Cancelled(int task) => new(LogEventKind.Cancelled, task);
    public static LogEvent FullReprocess(int task) => new(LogEventKind.FullReprocess, task);
    public static LogEvent ContextShift(int task) => new(LogEventKind.ContextShift, task);
    /// <summary><see cref="ContextSize"/> is the limit the prompt was cut to.</summary>
    public static LogEvent InputTruncated(int limit, int promptTokens) =>
        new(LogEventKind.InputTruncated, Tokens: promptTokens, ContextSize: limit);
    public static readonly LogEvent AllIdle = new(LogEventKind.AllIdle);
    public static readonly LogEvent ModelLoading = new(LogEventKind.ModelLoading);
    public static readonly LogEvent ModelLoaded = new(LogEventKind.ModelLoaded);
    /// <summary><see cref="Limit"/> is the size limit in MiB, null for no limit.</summary>
    public static LogEvent PromptCacheEnabled(double? limitMiB) => new(LogEventKind.PromptCacheEnabled, Limit: limitMiB);
    public static readonly LogEvent PromptCacheDisabled = new(LogEventKind.PromptCacheDisabled);
    /// <summary><see cref="Value"/> is MiB used and <see cref="Limit"/> the limit in MiB.</summary>
    public static LogEvent PromptCacheState(int savedPrompts, double usedMiB, double limitMiB) =>
        new(LogEventKind.PromptCacheState, Tokens: savedPrompts, Value: usedMiB, Limit: limitMiB);
    public static LogEvent PromptCacheEntry(int tokens) => new(LogEventKind.PromptCacheEntry, Tokens: tokens);
    /// <summary><see cref="Value"/> is the dropped entry's size in MiB.</summary>
    public static LogEvent PromptCacheEvicted(double sizeMiB) => new(LogEventKind.PromptCacheEvicted, Value: sizeMiB);
}

/// <summary>Parses the lines Ollama's llama-server runner writes to <c>server.log</c>.</summary>
public static class LogParser
{
    public static LogEvent? Parse(string line)
    {
        if (line.StartsWith("slot ", StringComparison.Ordinal)) return ParseSlot(line);
        if (line.StartsWith("srv ", StringComparison.Ordinal)) return ParseServer(line);
        if (line.StartsWith("time=", StringComparison.Ordinal)) return ParseRuntime(line);
        return null;
    }

    private static bool Has(string line, string text) => line.Contains(text, StringComparison.Ordinal);

    private static LogEvent? ParseSlot(string line)
    {
        if (Int("| task ", line) is not { } task || task < 0) return null;

        if (Has(line, "print_timing"))
        {
            if (Has(line, "prompt processing,"))
            {
                if (Int("n_tokens", line) is not { } tokens || Number("progress", line) is not { } progress
                    || Number(" s /", line) is not { } tps) return null;
                return LogEvent.PromptProgress(task, tokens, progress, tps);
            }
            if (Has(line, "n_gen ="))
            {
                if (Int("n_gen", line) is not { } tokens || Number(", tg", line) is not { } tps) return null;
                return LogEvent.GenerationProgress(task, tokens, tps, RecentRate(line) ?? tps);
            }
            if (Has(line, "prompt eval time"))
            {
                if (Timing("prompt eval time", line) is not { } timing) return null;
                return LogEvent.PromptEval(task, timing.Milliseconds, timing.Tokens, timing.Tps);
            }
            if (Has(line, "eval time"))
            {
                if (Timing("eval time", line) is not { } timing) return null;
                return LogEvent.Eval(task, timing.Milliseconds, timing.Tokens, timing.Tps);
            }
            if (Has(line, "total time"))
            {
                if (Number("total time", line) is not { } milliseconds || Int("ms /", line) is not { } tokens) return null;
                return LogEvent.Total(task, milliseconds, tokens);
            }
            return null;
        }

        if (Has(line, "new prompt,"))
        {
            if (Int("n_ctx_slot", line) is not { } context || Int("task.n_tokens", line) is not { } tokens) return null;
            return LogEvent.NewPrompt(task, context, tokens);
        }
        if (Has(line, "stop processing:")) return LogEvent.Released(task);
        if (Has(line, "forcing full prompt re-processing")) return LogEvent.FullReprocess(task);
        if (Has(line, "slot context shift")) return LogEvent.ContextShift(task);
        return null;
    }

    private static LogEvent? ParseServer(string line)
    {
        if (Has(line, "all slots are idle")) return LogEvent.AllIdle;
        if (Has(line, "cancel task") && Int("id_task", line) is { } cancelled) return LogEvent.Cancelled(cancelled);
        // Reads null from "size limit: no limit".
        if (Has(line, "prompt cache is enabled")) return LogEvent.PromptCacheEnabled(Number("size limit:", line));
        if (Has(line, "prompt cache is disabled")) return LogEvent.PromptCacheDisabled;
        if (Has(line, "removing oldest entry") && Number("size =", line) is { } size) return LogEvent.PromptCacheEvicted(size);
        if (Has(line, "cache state:"))
        {
            if (Int("cache state:", line) is not { } saved || Number("prompts,", line) is not { } used
                || Number("limits:", line) is not { } limit) return null;
            return LogEvent.PromptCacheState(saved, used, limit);
        }
        var prefix = line.IndexOf("- prompt 0x", StringComparison.Ordinal);
        if (prefix >= 0)
        {
            var colon = line.IndexOf(':', prefix + "- prompt 0x".Length);
            if (colon >= 0 && LeadingNumber(line.AsSpan(colon + 1)) is { } tokens) return LogEvent.PromptCacheEntry((int)tokens);
        }
        return null;
    }

    private static LogEvent? ParseRuntime(string line)
    {
        if (Has(line, "waiting for llama-server to start responding")) return LogEvent.ModelLoading;
        if (Has(line, "msg=\"llama-server started in")) return LogEvent.ModelLoaded;
        if (Has(line, "msg=\"truncating input prompt\""))
        {
            if (Int("limit=", line) is not { } limit || Int(" prompt=", line) is not { } prompt) return null;
            return LogEvent.InputTruncated(limit, prompt);
        }
        return null;
    }

    /// <summary>Reads <c>... = 284.71 ms /  30 tokens ( 9.49 ms per token, 105.37 tokens per second)</c>.</summary>
    private static (double Milliseconds, int Tokens, double Tps)? Timing(string key, string line)
    {
        if (Number(key, line) is not { } milliseconds || Int("ms /", line) is not { } tokens
            || Number("per token,", line) is not { } tps) return null;
        return (milliseconds, tokens, tps);
    }

    /// <summary>Reads the windowed rate from <c>tg_3s = 27.98 t/s</c>. The window length can vary between versions.</summary>
    private static double? RecentRate(string line)
    {
        var key = line.IndexOf("tg_", StringComparison.Ordinal);
        if (key < 0) return null;
        var equals = line.IndexOf('=', key + 3);
        return equals < 0 ? null : LeadingNumber(line.AsSpan(equals + 1));
    }

    private static int? Int(string key, string line) => Number(key, line) is { } value ? (int)value : null;

    private static double? Number(string key, string line)
    {
        var index = line.IndexOf(key, StringComparison.Ordinal);
        return index < 0 ? null : LeadingNumber(line.AsSpan(index + key.Length));
    }

    private static double? LeadingNumber(ReadOnlySpan<char> text)
    {
        var index = 0;
        while (index < text.Length && (text[index] == ' ' || text[index] == '=')) index++;
        var start = index;
        while (index < text.Length && (char.IsDigit(text[index]) || text[index] == '.' || text[index] == '-')) index++;
        return double.TryParse(
            text[start..index], NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out var value) ? value : null;
    }
}
