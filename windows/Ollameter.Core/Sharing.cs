using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ollameter.Core;

public enum MeterSourceKind { Local, Service, Address }

/// <summary>Where the window's numbers come from: this PC, or another computer that shares its own.</summary>
/// <remarks>
/// <see cref="MeterSourceKind.Service"/> is a computer found with Bonjour, by its advertised name.
/// <see cref="MeterSourceKind.Address"/> is one reached by host name or IP address, for when Bonjour can't see it.
/// </remarks>
public sealed record MeterSource(MeterSourceKind Kind, string Name = "", ushort Port = 0)
{
    public const ushort DefaultPort = 11480;

    public static readonly MeterSource Local = new(MeterSourceKind.Local);
    public static MeterSource Service(string name) => new(MeterSourceKind.Service, name);
    public static MeterSource Address(string host, ushort port) => new(MeterSourceKind.Address, host, port);

    public bool IsLocal => Kind == MeterSourceKind.Local;

    /// <summary>The name to show before the other computer has reported its own.</summary>
    public string DisplayName => Kind switch
    {
        MeterSourceKind.Local => "This PC",
        MeterSourceKind.Service => Name,
        _ when Port == DefaultPort => Name,
        _ => Name.Contains(':') ? $"[{Name}]:{Port}" : $"{Name}:{Port}",
    };

    public string StorageValue => Kind switch
    {
        MeterSourceKind.Local => "local",
        MeterSourceKind.Service => "service:" + Name,
        _ => "address:" + DisplayName,
    };

    public static MeterSource? FromStorageValue(string value)
    {
        if (value == "local") return Local;
        if (value.StartsWith("service:", StringComparison.Ordinal) && value.Length > "service:".Length)
            return Service(value["service:".Length..]);
        if (value.StartsWith("address:", StringComparison.Ordinal)) return ParseAddress(value["address:".Length..]);
        return null;
    }

    /// <summary>
    /// Reads what someone types to reach a computer: <c>studio.local</c>, <c>192.168.1.20:11480</c>,
    /// <c>[fe80::1]:11480</c>, or a URL that names one of these.
    /// </summary>
    public static MeterSource? ParseAddress(string text)
    {
        var rest = text.Trim();
        var scheme = rest.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) rest = rest[(scheme + 3)..];
        var slash = rest.IndexOf('/');
        if (slash >= 0) rest = rest[..slash];
        if (rest.Length == 0 || rest.Any(char.IsWhiteSpace)) return null;

        var host = rest;
        string? portText = null;
        if (rest.StartsWith('['))
        {
            var close = rest.IndexOf(']');
            if (close < 0) return null;
            host = rest[1..close];
            var after = rest[(close + 1)..];
            if (after.Length > 0)
            {
                if (!after.StartsWith(':')) return null;
                portText = after[1..];
            }
        }
        else if (rest.Count(character => character == ':') == 1)
        {
            var colon = rest.IndexOf(':');
            host = rest[..colon];
            portText = rest[(colon + 1)..];
        }
        if (host.Length == 0) return null;
        var port = DefaultPort;
        if (portText is not null)
        {
            if (!ushort.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value == 0)
                return null;
            port = value;
        }
        return Address(host, port);
    }

    private static readonly StringComparer NameOrder =
        StringComparer.Create(CultureInfo.CurrentCulture, CompareOptions.IgnoreCase | CompareOptions.NumericOrdering);

    /// <summary>The order sections appear in: this PC first, then other computers by name. Duplicates are dropped.</summary>
    public static List<MeterSource> Ordered(IEnumerable<MeterSource> sources) =>
        sources.Distinct()
            .OrderBy(source => source.IsLocal ? 0 : 1)
            .ThenBy(source => source.DisplayName, NameOrder)
            .ToList();
}

public enum RemoteStatusKind { Connecting, Connected, Unreachable, Incompatible }

/// <summary>
/// How a connection to another computer stands. <see cref="RemoteStatusKind.Unreachable"/> means the last attempt
/// failed and the client keeps trying. <see cref="RemoteStatusKind.Incompatible"/> means it runs a version of
/// Ollameter whose data this one can't read.
/// </summary>
public readonly record struct RemoteStatus(RemoteStatusKind Kind, string? App = null)
{
    public static readonly RemoteStatus Connecting = new(RemoteStatusKind.Connecting);
    public static readonly RemoteStatus Connected = new(RemoteStatusKind.Connected);
    public static readonly RemoteStatus Unreachable = new(RemoteStatusKind.Unreachable);
    public static RemoteStatus Incompatible(string? app) => new(RemoteStatusKind.Incompatible, app);
}

/// <summary>How the window describes another computer it's showing.</summary>
public sealed record RemoteSource(string Host, RemoteStatus Status)
{
    /// <summary>What to say instead of the numbers when there are none to show.</summary>
    public string? Problem => Status.Kind switch
    {
        RemoteStatusKind.Connected => null,
        RemoteStatusKind.Connecting => $"Connecting to {Host}…",
        RemoteStatusKind.Unreachable => $"Can't reach {Host}. Trying again…",
        _ => $"{Host} runs {(Status.App is { } app ? "Ollameter " + app : "another version of Ollameter")}, which this version can't read.",
    };
}

public static class MeterViewModels
{
    /// <summary>Stands in for another computer's numbers until they arrive, or while they can't.</summary>
    public static MeterViewModel Placeholder(RemoteSource remote) => new()
    {
        Snapshot = new LiveSnapshot { Phase = Phase.Offline },
        Display = new DisplayState { Tint = Tint.None, Summary = remote.Problem ?? "", DisplayPhase = Phase.Offline },
        Remote = remote,
        Platform = null,
    };

    /// <summary>How much is happening, for choosing which computer the tray icon follows.</summary>
    public static int ActivityRank(this MeterViewModel model)
    {
        if (model.Remote?.Problem is not null) return 0;
        return model.Display.DisplayPhase switch
        {
            Phase.Prefill or Phase.Generating or Phase.Loading => 2,
            Phase.Idle => 1,
            _ => 0,
        };
    }

    /// <summary>
    /// The busiest of several computers. The current one stays while nothing is busier, so the icon doesn't jump
    /// between computers that are equally busy.
    /// </summary>
    public static int? BusiestIndex(IReadOnlyList<MeterViewModel> models, int? current)
    {
        if (models.Count == 0) return null;
        var best = models.Max(ActivityRank);
        if (current is { } index && index >= 0 && index < models.Count && models[index].ActivityRank() == best) return index;
        for (var i = 0; i < models.Count; i++)
            if (models[i].ActivityRank() == best) return i;
        return null;
    }
}

/// <summary>One update as it travels between computers.</summary>
public sealed class SharedFrame
{
    public const int CurrentFormat = 1;
    /// <summary>The window shows six recent requests. A few more leave room without sending all forty.</summary>
    public const int RequestLimit = 10;
    /// <summary>Sent by this app. The Mac app sends "macos" from 0.4, and nothing before.</summary>
    public const string WindowsPlatform = "windows";

    [JsonPropertyName("format")] public int Format { get; set; } = CurrentFormat;
    [JsonPropertyName("host")] public string Host { get; set; } = "";
    [JsonPropertyName("app")] public string? App { get; set; }
    [JsonPropertyName("platform")] public string? Platform { get; set; }
    [JsonPropertyName("model")] public MeterViewModel Model { get; set; } = new();

    public SharedFrame() { }

    public SharedFrame(string host, string? app, MeterViewModel model, string? platform = WindowsPlatform)
    {
        Host = host;
        App = app;
        Platform = platform;
        var trimmed = model.Copy();
        trimmed.Requests = [.. model.Requests.Take(RequestLimit)];
        Model = trimmed;
    }

    public byte[] Encoded() => JsonSerializer.SerializeToUtf8Bytes(this, Options);

    public enum DecodeFailureKind { OtherFormat, Malformed }

    public readonly record struct DecodeResult(SharedFrame? Frame, DecodeFailureKind Failure = default, int Format = 0, string? App = null)
    {
        public bool Succeeded => Frame is not null;
    }

    public static DecodeResult Decode(ReadOnlySpan<byte> data)
    {
        Header? header;
        try { header = JsonSerializer.Deserialize<Header>(data); }
        catch (JsonException) { return new(null, DecodeFailureKind.Malformed); }
        if (header?.Format is not { } format) return new(null, DecodeFailureKind.Malformed);
        if (format != CurrentFormat) return new(null, DecodeFailureKind.OtherFormat, format, header.App);
        try
        {
            var frame = JsonSerializer.Deserialize<SharedFrame>(data, Options);
            if (frame is null || frame.Model is null || string.IsNullOrEmpty(frame.Host) || !HasModel(data))
                return new(null, DecodeFailureKind.Malformed);
            frame.Model.Platform = frame.Platform;
            return new(frame);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
        {
            return new(null, DecodeFailureKind.Malformed);
        }
    }

    /// <summary>The Swift decoder rejects a frame without a model, so this one does too.</summary>
    private static bool HasModel(ReadOnlySpan<byte> data)
    {
        var reader = new Utf8JsonReader(data);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return false;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var isModel = reader.ValueTextEquals("model");
            reader.Read();
            if (isModel) return reader.TokenType == JsonTokenType.StartObject;
            reader.Skip();
        }
        return false;
    }

    private sealed class Header
    {
        [JsonPropertyName("format")] public int? Format { get; set; }
        [JsonPropertyName("app")] public string? App { get; set; }
    }

    public static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Only control characters need escaping. The frame never goes into a web page.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new SwiftDoubleConverter(), new UnixSecondsConverter() },
    };
}

/// <summary>
/// Writes infinities and NaN as the strings the Swift app uses, since a speed divided by a zero duration would
/// otherwise stop the whole frame from encoding.
/// </summary>
internal sealed class SwiftDoubleConverter : JsonConverter<double>
{
    public override double Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return reader.GetString() switch
            {
                "inf" => double.PositiveInfinity,
                "-inf" => double.NegativeInfinity,
                "nan" => double.NaN,
                _ => throw new JsonException("Not a number."),
            };
        }
        return reader.GetDouble();
    }

    public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options)
    {
        if (double.IsPositiveInfinity(value)) writer.WriteStringValue("inf");
        else if (double.IsNegativeInfinity(value)) writer.WriteStringValue("-inf");
        else if (double.IsNaN(value)) writer.WriteStringValue("nan");
        else writer.WriteNumberValue(value);
    }
}

/// <summary>Dates travel as seconds since 1970, as Swift's <c>secondsSince1970</c> strategy writes them.</summary>
internal sealed class UnixSecondsConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var seconds = reader.GetDouble();
        if (!double.IsFinite(seconds) || Math.Abs(seconds) > 253_402_300_799) throw new JsonException("Date out of range.");
        return DateTimeOffset.UnixEpoch.AddTicks((long)Math.Round(seconds * TimeSpan.TicksPerSecond));
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteNumberValue((value - DateTimeOffset.UnixEpoch).Ticks / (double)TimeSpan.TicksPerSecond);
}

/// <summary>The request line of an HTTP request, which is all the sharing server needs.</summary>
public readonly record struct HttpRequestHead(string Method, string Path)
{
    public const int SizeLimit = 16 * 1024;

    public enum ParseKind { Incomplete, Malformed, Complete }

    private static readonly UTF8Encoding StrictUtf8 = new(false, throwOnInvalidBytes: true);

    /// <summary>Path is without any query string.</summary>
    public static (ParseKind Kind, HttpRequestHead Head) Parse(ReadOnlySpan<byte> data)
    {
        var end = data.IndexOf("\r\n\r\n"u8);
        if (end < 0) return (data.Length > SizeLimit ? ParseKind.Malformed : ParseKind.Incomplete, default);
        string text;
        try { text = StrictUtf8.GetString(data[..end]); }
        catch (DecoderFallbackException) { return (ParseKind.Malformed, default); }
        var lineEnd = text.IndexOf("\r\n", StringComparison.Ordinal);
        var line = lineEnd >= 0 ? text[..lineEnd] : text;
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 || !parts[2].StartsWith("HTTP/1.", StringComparison.Ordinal)
            || !parts[1].StartsWith('/')) return (ParseKind.Malformed, default);
        var target = parts[1];
        var query = target.IndexOf('?');
        return (ParseKind.Complete, new HttpRequestHead(parts[0], query >= 0 ? target[..query] : target));
    }
}

/// <summary>Reads an HTTP response that carries server-sent events, as bytes arrive in any size of chunk.</summary>
public sealed class EventStreamParser
{
    public const int SizeLimit = 4 << 20;

    public enum FailureKind { Status, Malformed, TooLarge }

    public sealed class FailureException(FailureKind kind, int status = 0) : Exception($"Event stream failed: {kind} {status}")
    {
        public FailureKind Kind { get; } = kind;
        public int Status { get; } = status;
    }

    private byte[] buffer = new byte[16 * 1024];
    private int count;
    private bool headRead;

    /// <summary>Returns the data of each event completed by <paramref name="chunk"/>.</summary>
    public List<string> Append(ReadOnlySpan<byte> chunk)
    {
        if (headRead)
        {
            // JSON escapes carriage returns inside strings, so any left here are line endings.
            AppendWithoutReturns(chunk);
        }
        else
        {
            AppendRaw(chunk);
            var end = Buffered.IndexOf("\r\n\r\n"u8);
            if (end < 0)
            {
                if (count > HttpRequestHead.SizeLimit) throw new FailureException(FailureKind.Malformed);
                return [];
            }
            var head = Encoding.UTF8.GetString(Buffered[..end]);
            var lineEnd = head.IndexOf("\r\n", StringComparison.Ordinal);
            var statusLine = lineEnd >= 0 ? head[..lineEnd] : head;
            var fields = statusLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 2 || !fields[0].StartsWith("HTTP/", StringComparison.Ordinal)
                || !int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var code))
                throw new FailureException(FailureKind.Malformed);
            if (code != 200) throw new FailureException(FailureKind.Status, code);
            headRead = true;
            var body = Buffered[(end + 4)..].ToArray();
            count = 0;
            AppendWithoutReturns(body);
        }

        var events = new List<string>();
        int boundary;
        while ((boundary = Buffered.IndexOf("\n\n"u8)) >= 0)
        {
            var block = Encoding.UTF8.GetString(Buffered[..boundary]);
            Consume(boundary + 2);
            var lines = new List<string>();
            foreach (var line in block.Split('\n'))
            {
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                var value = line["data:".Length..];
                lines.Add(value.StartsWith(' ') ? value[1..] : value);
            }
            if (lines.Count > 0) events.Add(string.Join("\n", lines));
        }
        if (count > SizeLimit) throw new FailureException(FailureKind.TooLarge);
        return events;
    }

    private ReadOnlySpan<byte> Buffered => buffer.AsSpan(0, count);

    private void Reserve(int extra)
    {
        if (count + extra <= buffer.Length) return;
        Array.Resize(ref buffer, Math.Max(buffer.Length * 2, count + extra));
    }

    private void AppendRaw(ReadOnlySpan<byte> chunk)
    {
        Reserve(chunk.Length);
        chunk.CopyTo(buffer.AsSpan(count));
        count += chunk.Length;
    }

    private void AppendWithoutReturns(ReadOnlySpan<byte> chunk)
    {
        Reserve(chunk.Length);
        foreach (var value in chunk)
            if (value != '\r') buffer[count++] = value;
    }

    private void Consume(int length)
    {
        Buffer.BlockCopy(buffer, length, buffer, 0, count - length);
        count -= length;
    }
}
