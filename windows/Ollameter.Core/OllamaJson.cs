using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ollameter.Core;

/// <summary>
/// Decodes only the numeric and identifying fields the meter needs. <c>/slots</c> can also carry prompt text and
/// sampler settings, which are never read.
/// </summary>
public static class OllamaJson
{
    public static List<SlotReading>? Slots(ReadOnlyMemory<byte> data)
    {
        using var document = TryParse(data);
        if (document?.RootElement is not { ValueKind: JsonValueKind.Array } root) return null;
        var result = new List<SlotReading>();
        foreach (var slot in root.EnumerateArray())
        {
            if (slot.ValueKind != JsonValueKind.Object) return null;
            // Some llama-server builds return an array here and others a single object.
            var next = default(JsonElement);
            if (slot.TryGetProperty("next_token", out var token))
            {
                if (token.ValueKind == JsonValueKind.Array)
                {
                    foreach (var first in token.EnumerateArray())
                    {
                        next = first;
                        break;
                    }
                }
                else
                {
                    next = token;
                }
            }
            result.Add(new SlotReading(
                IsProcessing: slot.TryGetProperty("is_processing", out var processing) && processing.ValueKind == JsonValueKind.True,
                Task: Int(slot, "id_task"),
                ContextSize: Int(slot, "n_ctx"),
                PromptTokens: Int(slot, "n_prompt_tokens"),
                ProcessedPromptTokens: Int(slot, "n_prompt_tokens_processed"),
                CachedPromptTokens: Int(slot, "n_prompt_tokens_cache"),
                DecodedTokens: Int(next, "n_decoded")));
        }
        return result;
    }

    public static List<LoadedModel>? Models(ReadOnlyMemory<byte> data)
    {
        using var document = TryParse(data);
        if (document?.RootElement is not { ValueKind: JsonValueKind.Object } root
            || !root.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array) return null;
        var result = new List<LoadedModel>();
        foreach (var model in models.EnumerateArray())
        {
            if (model.ValueKind != JsonValueKind.Object) return null;
            var details = model.TryGetProperty("details", out var found) && found.ValueKind == JsonValueKind.Object
                ? found : default;
            result.Add(new LoadedModel
            {
                Name = String(model, "name") ?? "unknown",
                Size = (ulong)Math.Max(Long(model, "size"), 0),
                SizeVram = (ulong)Math.Max(Long(model, "size_vram"), 0),
                ContextLength = model.TryGetProperty("context_length", out var context)
                    && context.ValueKind == JsonValueKind.Number ? (int)context.GetDouble() : null,
                ExpiresAt = String(model, "expires_at") is { } expires ? Date(expires) : null,
                ParameterSize = String(details, "parameter_size"),
                Quantization = String(details, "quantization_level"),
            });
        }
        return result;
    }

    /// <summary>The <c>/api/generate</c> body that unloads a model. With no prompt, Ollama generates nothing.</summary>
    public static byte[] UnloadBody(string model) =>
        JsonSerializer.SerializeToUtf8Bytes(new JsonObject { ["model"] = model, ["keep_alive"] = 0 });

    public static string? Version(ReadOnlyMemory<byte> data)
    {
        using var document = TryParse(data);
        return document?.RootElement is { ValueKind: JsonValueKind.Object } root ? String(root, "version") : null;
    }

    /// <summary>
    /// Ollama emits up to nanosecond precision, which .NET can't parse, so the fractional part is dropped first.
    /// </summary>
    public static DateTimeOffset? Date(string text)
    {
        var trimmed = text;
        var dot = trimmed.IndexOf('.');
        if (dot >= 0)
        {
            var zone = dot + 1;
            while (zone < trimmed.Length && char.IsDigit(trimmed[zone])) zone++;
            trimmed = trimmed[..dot] + trimmed[zone..];
        }
        return DateTimeOffset.TryParseExact(
            trimmed, "yyyy-MM-dd'T'HH:mm:ssK", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date : null;
    }

    private static JsonDocument? TryParse(ReadOnlyMemory<byte> data)
    {
        try
        {
            return JsonDocument.Parse(data);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static int Int(JsonElement element, string key) => (int)Long(element, key);

    private static long Long(JsonElement element, string key)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(key, out var value)) return 0;
        if (value.ValueKind != JsonValueKind.Number) return 0;
        return value.TryGetInt64(out var whole) ? whole : (long)value.GetDouble();
    }

    private static string? String(JsonElement element, string key) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var value)
        && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
