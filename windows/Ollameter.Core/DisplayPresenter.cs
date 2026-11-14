using System.Text.Json.Serialization;

namespace Ollameter.Core;

[JsonConverter(typeof(JsonStringEnumConverter<Tint>))]
public enum Tint
{
    [JsonStringEnumMemberName("none")] None,
    [JsonStringEnumMemberName("muted")] Muted,
    [JsonStringEnumMemberName("gpu")] Gpu,
    [JsonStringEnumMemberName("prompt")] Prompt,
}

public sealed record DisplayState
{
    [JsonPropertyName("fraction")] public double Fraction { get; set; }
    [JsonPropertyName("tint")] public Tint Tint { get; set; }
    [JsonPropertyName("modelLoaded")] public bool ModelLoaded { get; set; }
    [JsonPropertyName("text")] public string? Text { get; set; }
    [JsonPropertyName("textDimmed")] public bool TextDimmed { get; set; }
    [JsonPropertyName("summary")] public string Summary { get; set; } = "";
    /// <summary>The phase to show. A short gap between requests still counts as generating.</summary>
    [JsonPropertyName("displayPhase")] public Phase DisplayPhase { get; set; } = Phase.Idle;
    /// <summary>The last speed, carried across a gap while no tokens are being produced.</summary>
    [JsonPropertyName("heldTPS")] public double? HeldTps { get; set; }

    public static DisplayState Initial => new() { Tint = Tint.None, DisplayPhase = Phase.Idle };
}

/// <summary>
/// Decides what the tray icon and window show. The ring is GPU load, except while a prompt is being read, when
/// it fills with prompt progress. Timing rules keep agent loops from flickering.
/// </summary>
public sealed class DisplayPresenter
{
    public const double HoldSeconds = 5;
    public const double PrefillDelay = 0.75;
    public const int LargePrefillTokens = 2048;

    private double? lastTps;
    private DateTimeOffset? lastGeneratingAt;
    private int? prefillTask;
    private DateTimeOffset? prefillSeenAt;

    public DisplayState Present(MeterViewModel model)
    {
        var snapshot = model.Snapshot;
        var now = model.Now;
        var gpu = Math.Clamp((model.Hardware.GpuUtilization ?? 0) / 100, 0, 1);
        var result = new DisplayState
        {
            Fraction = gpu, Tint = Tint.Muted, ModelLoaded = model.Models.Count > 0, DisplayPhase = snapshot.Phase,
        };

        switch (snapshot.Phase)
        {
            case Phase.Offline:
                lastTps = null;
                lastGeneratingAt = null;
                result.Fraction = 0;
                result.Tint = Tint.None;
                result.Summary = "Ollama is not running";
                break;

            case Phase.Loading:
                result.Tint = Tint.Gpu;
                result.Text = "load";
                result.Summary = "Ollama is loading a model";
                break;

            case Phase.Generating:
                if (snapshot.LiveTps is { } tps) lastTps = tps;
                lastGeneratingAt = now;
                result.Tint = Tint.Gpu;
                result.Text = lastTps is { } last ? Format.MenuTps(last) : "– t/s";
                result.Summary = $"Generating at {result.Text}, GPU {Format.Percent(gpu)}";
                break;

            case Phase.Prefill:
                if (snapshot.TaskId != prefillTask)
                {
                    prefillTask = snapshot.TaskId;
                    prefillSeenAt = now;
                }
                var age = (now - (prefillSeenAt ?? now)).TotalSeconds;
                var sustained = age >= PrefillDelay || (snapshot.PromptToProcess ?? 0) >= LargePrefillTokens;
                if (sustained && snapshot.PromptFraction is { } fraction)
                {
                    result.Fraction = Math.Clamp(fraction, 0, 1);
                    result.Tint = Tint.Prompt;
                    result.Text = Format.Percent(fraction);
                    result.Summary = $"Reading the prompt, {Format.Percent(fraction)} done";
                }
                else
                {
                    result.Tint = Tint.Gpu;
                    ApplyHold(result, now);
                    result.Summary = "Reading the prompt";
                }
                break;

            case Phase.Idle:
                ApplyHold(result, now);
                result.Summary = model.Models.Count == 0
                    ? "Ollama is idle with no model loaded"
                    : $"Ollama is idle with {model.Models[0].Name} loaded";
                break;
        }
        return result;
    }

    private void ApplyHold(DisplayState result, DateTimeOffset now)
    {
        if (lastTps is not { } tps || lastGeneratingAt is not { } at || (now - at).TotalSeconds >= HoldSeconds) return;
        result.Text = Format.MenuTps(tps);
        result.TextDimmed = true;
        result.DisplayPhase = Phase.Generating;
        result.HeldTps = tps;
    }
}
