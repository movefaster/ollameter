import Foundation

/// Decodes only the numeric and identifying fields the meter needs. `/slots` can also carry prompt
/// text and sampler settings, which are never read.
public enum OllamaJSON {
    public static func slots(from data: Data) -> [SlotReading]? {
        guard let objects = (try? JSONSerialization.jsonObject(with: data)) as? [[String: Any]] else {
            return nil
        }
        return objects.map { object in
            // Some llama-server builds return an array here and others a single object.
            let next = (object["next_token"] as? [[String: Any]])?.first
                ?? (object["next_token"] as? [String: Any])
                ?? [:]
            return SlotReading(
                isProcessing: object["is_processing"] as? Bool ?? false,
                task: int(object["id_task"]),
                contextSize: int(object["n_ctx"]),
                promptTokens: int(object["n_prompt_tokens"]),
                processedPromptTokens: int(object["n_prompt_tokens_processed"]),
                cachedPromptTokens: int(object["n_prompt_tokens_cache"]),
                decodedTokens: int(next["n_decoded"]))
        }
    }

    public static func models(from data: Data) -> [LoadedModel]? {
        guard let root = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any],
            let objects = root["models"] as? [[String: Any]]
        else { return nil }
        return objects.map { object in
            let details = object["details"] as? [String: Any] ?? [:]
            return LoadedModel(
                name: object["name"] as? String ?? "unknown",
                size: UInt64(max(int(object["size"]), 0)),
                sizeVRAM: UInt64(max(int(object["size_vram"]), 0)),
                contextLength: (object["context_length"] as? NSNumber)?.intValue,
                expiresAt: (object["expires_at"] as? String).flatMap(date),
                parameterSize: details["parameter_size"] as? String,
                quantization: details["quantization_level"] as? String)
        }
    }

    /// The `/api/generate` body that unloads a model. With no prompt, Ollama generates nothing.
    public static func unloadBody(model: String) -> Data? {
        try? JSONSerialization.data(withJSONObject: ["model": model, "keep_alive": 0] as [String: Any])
    }

    public static func version(from data: Data) -> String? {
        ((try? JSONSerialization.jsonObject(with: data)) as? [String: Any])?["version"] as? String
    }

    /// Ollama emits microsecond precision, so the fractional part is dropped before parsing.
    public static func date(_ text: String) -> Date? {
        var trimmed = text
        if let dot = trimmed.firstIndex(of: ".") {
            let afterDot = trimmed[trimmed.index(after: dot)...]
            let zone = afterDot.firstIndex { !$0.isNumber } ?? trimmed.endIndex
            trimmed.removeSubrange(dot..<zone)
        }
        return ISO8601DateFormatter().date(from: trimmed)
    }

    private static func int(_ value: Any?) -> Int {
        (value as? NSNumber)?.intValue ?? 0
    }
}
