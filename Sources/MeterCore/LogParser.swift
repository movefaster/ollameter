import Foundation

public enum LogEvent: Equatable, Sendable {
    case newPrompt(task: Int, contextSize: Int, promptTokens: Int)
    case promptProgress(task: Int, tokens: Int, progress: Double, tps: Double)
    case generationProgress(task: Int, tokens: Int, tps: Double, recentTPS: Double)
    case promptEval(task: Int, milliseconds: Double, tokens: Int, tps: Double)
    case eval(task: Int, milliseconds: Double, tokens: Int, tps: Double)
    case total(task: Int, milliseconds: Double, tokens: Int)
    case released(task: Int)
    case cancelled(task: Int)
    case fullReprocess(task: Int)
    case contextShift(task: Int)
    case inputTruncated(limit: Int, promptTokens: Int)
    case allIdle
    case modelLoading
    case modelLoaded
    case promptCacheEnabled(limitMiB: Double?)
    case promptCacheDisabled
    case promptCacheState(savedPrompts: Int, usedMiB: Double, limitMiB: Double)
    case promptCacheEntry(tokens: Int)
    case promptCacheEvicted(sizeMiB: Double)
}

/// Parses the lines Ollama's llama-server runner writes to `server.log`.
public enum LogParser {
    public static func parse(_ line: String) -> LogEvent? {
        if line.hasPrefix("slot ") { return parseSlot(line) }
        if line.hasPrefix("srv ") { return parseServer(line) }
        if line.hasPrefix("time=") { return parseRuntime(line) }
        return nil
    }

    private static func parseSlot(_ line: String) -> LogEvent? {
        guard let task = int(after: "| task ", in: line), task >= 0 else { return nil }

        if line.contains("print_timing") {
            if line.contains("prompt processing,") {
                guard let tokens = int(after: "n_tokens", in: line),
                    let progress = number(after: "progress", in: line),
                    let tps = number(after: " s /", in: line)
                else { return nil }
                return .promptProgress(task: task, tokens: tokens, progress: progress, tps: tps)
            }
            if line.contains("n_gen =") {
                guard let tokens = int(after: "n_gen", in: line),
                    let tps = number(after: ", tg", in: line)
                else { return nil }
                return .generationProgress(
                    task: task, tokens: tokens, tps: tps, recentTPS: recentRate(in: line) ?? tps)
            }
            if line.contains("prompt eval time") {
                guard let timing = timing(after: "prompt eval time", in: line) else { return nil }
                return .promptEval(task: task, milliseconds: timing.0, tokens: timing.1, tps: timing.2)
            }
            if line.contains("eval time") {
                guard let timing = timing(after: "eval time", in: line) else { return nil }
                return .eval(task: task, milliseconds: timing.0, tokens: timing.1, tps: timing.2)
            }
            if line.contains("total time") {
                guard let milliseconds = number(after: "total time", in: line),
                    let tokens = int(after: "ms /", in: line)
                else { return nil }
                return .total(task: task, milliseconds: milliseconds, tokens: tokens)
            }
            return nil
        }

        if line.contains("new prompt,") {
            guard let context = int(after: "n_ctx_slot", in: line),
                let tokens = int(after: "task.n_tokens", in: line)
            else { return nil }
            return .newPrompt(task: task, contextSize: context, promptTokens: tokens)
        }
        if line.contains("stop processing:") { return .released(task: task) }
        if line.contains("forcing full prompt re-processing") { return .fullReprocess(task: task) }
        if line.contains("slot context shift") { return .contextShift(task: task) }
        return nil
    }

    private static func parseServer(_ line: String) -> LogEvent? {
        if line.contains("all slots are idle") { return .allIdle }
        if line.contains("cancel task"), let task = int(after: "id_task", in: line) {
            return .cancelled(task: task)
        }
        if line.contains("prompt cache is enabled") {
            // Reads nil from "size limit: no limit".
            return .promptCacheEnabled(limitMiB: number(after: "size limit:", in: line))
        }
        if line.contains("prompt cache is disabled") { return .promptCacheDisabled }
        if line.contains("removing oldest entry"), let size = number(after: "size =", in: line) {
            return .promptCacheEvicted(sizeMiB: size)
        }
        if line.contains("cache state:") {
            guard let saved = int(after: "cache state:", in: line),
                let used = number(after: "prompts,", in: line),
                let limit = number(after: "limits:", in: line)
            else { return nil }
            return .promptCacheState(savedPrompts: saved, usedMiB: used, limitMiB: limit)
        }
        if let prefix = line.range(of: "- prompt 0x"),
            let colon = line[prefix.upperBound...].firstIndex(of: ":"),
            let tokens = leadingNumber(in: line[line.index(after: colon)...])
        {
            return .promptCacheEntry(tokens: Int(tokens))
        }
        return nil
    }

    private static func parseRuntime(_ line: String) -> LogEvent? {
        if line.contains("waiting for llama-server to start responding") { return .modelLoading }
        if line.contains("msg=\"llama-server started in") { return .modelLoaded }
        if line.contains("msg=\"truncating input prompt\"") {
            guard let limit = int(after: "limit=", in: line),
                let prompt = int(after: " prompt=", in: line)
            else { return nil }
            return .inputTruncated(limit: limit, promptTokens: prompt)
        }
        return nil
    }

    /// Reads `... = 284.71 ms /  30 tokens ( 9.49 ms per token, 105.37 tokens per second)`.
    private static func timing(after key: String, in line: String) -> (Double, Int, Double)? {
        guard let milliseconds = number(after: key, in: line),
            let tokens = int(after: "ms /", in: line),
            let tps = number(after: "per token,", in: line)
        else { return nil }
        return (milliseconds, tokens, tps)
    }

    /// Reads the windowed rate from `tg_3s = 27.98 t/s`. The window length can vary between versions.
    private static func recentRate(in line: String) -> Double? {
        guard let keyRange = line.range(of: "tg_"),
            let equals = line[keyRange.upperBound...].firstIndex(of: "=")
        else { return nil }
        return leadingNumber(in: line[line.index(after: equals)...])
    }

    private static func int(after key: String, in line: String) -> Int? {
        number(after: key, in: line).map { Int($0) }
    }

    private static func number(after key: String, in line: String) -> Double? {
        guard let range = line.range(of: key) else { return nil }
        return leadingNumber(in: line[range.upperBound...])
    }

    private static func leadingNumber(in text: Substring) -> Double? {
        var index = text.startIndex
        while index < text.endIndex, text[index] == " " || text[index] == "=" {
            index = text.index(after: index)
        }
        let start = index
        while index < text.endIndex, text[index].isNumber || text[index] == "." || text[index] == "-" {
            index = text.index(after: index)
        }
        return Double(text[start..<index])
    }
}
