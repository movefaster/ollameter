using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Windows.Threading;
using Ollameter.Core;

namespace Ollameter;

internal static class Sharing
{
    /// <summary>This PC's name, as other computers see it.</summary>
    public static readonly string HostName = Dns.GetHostName();

    public static readonly string? AppVersion = Assembly.GetExecutingAssembly()
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0];
}

/// <summary>
/// Serves this PC's numbers to other computers, read-only, and advertises them with Bonjour.
/// </summary>
/// <remarks>
/// <c>GET /v1/state</c> returns the latest update as JSON. <c>GET /v1/events</c> streams every update as
/// server-sent events, a few per second while a model works and one a second otherwise. The same protocol as
/// the Mac app, so either can watch the other.
/// </remarks>
internal sealed class ShareServer
{
    private const int ConnectionLimit = 32;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    // Each update replaces the last, so a reader that falls behind skips updates rather than queuing them.
    private const int UnsentLimit = 2;

    private sealed class Connection(TcpClient client)
    {
        public TcpClient Client { get; } = client;
        public NetworkStream Stream { get; } = client.GetStream();
        public int Unsent;
        public bool IsStream;
        public bool Closed;

        public void Close()
        {
            if (Closed) return;
            Closed = true;
            Client.Dispose();
        }
    }

    private readonly string host = Sharing.HostName;
    private readonly HashSet<Connection> connections = [];
    private TcpListener? listener;
    private Dnssd.Registration? registration;
    private MeterViewModel? latest;
    private int generation;

    public ushort? Port { get; private set; }
    public string? AdvertisedName { get; private set; }
    public bool IsRunning => listener is not null;
    public bool HasViewers => connections.Any(connection => connection.IsStream);

    /// <summary>Runs when the port, the advertised name or the number of viewers changes.</summary>
    public event Action? Changed;

    public void Start()
    {
        if (listener is not null) return;
        // Usually the usual port is free. If not, any free one will do, since Bonjour still finds it.
        listener = Listen(MeterSource.DefaultPort) ?? Listen(0);
        if (listener is null)
        {
            Changed?.Invoke();
            return;
        }
        Port = (ushort)((IPEndPoint)listener.LocalEndpoint).Port;
        var current = ++generation;
        registration = new Dnssd.Registration(host, Port.Value, name =>
        {
            if (current != generation) return;
            AdvertisedName = name;
            Changed?.Invoke();
        });
        Changed?.Invoke();
        _ = AcceptLoop(listener, current);
    }

    private static TcpListener? Listen(int port)
    {
        foreach (var dualStack in new[] { true, false })
        {
            TcpListener? candidate = null;
            try
            {
                candidate = dualStack ? new TcpListener(IPAddress.IPv6Any, port) : new TcpListener(IPAddress.Any, port);
                if (dualStack) candidate.Server.DualMode = true;
                candidate.ExclusiveAddressUse = true;
                candidate.Start();
                return candidate;
            }
            catch (SocketException)
            {
                candidate?.Stop();
            }
        }
        return null;
    }

    public void Stop()
    {
        generation++;
        listener?.Stop();
        listener = null;
        registration?.Dispose();
        registration = null;
        foreach (var connection in connections) connection.Close();
        connections.Clear();
        Port = null;
        AdvertisedName = null;
        Changed?.Invoke();
    }

    public void Publish(MeterViewModel model)
    {
        latest = model;
        if (!HasViewers) return;
        var data = EventBytes();
        foreach (var connection in connections.Where(c => c.IsStream && c.Unsent < UnsentLimit).ToList())
            _ = Send(connection, data);
    }

    private byte[]? FrameData() => latest is null ? null : new SharedFrame(host, Sharing.AppVersion, latest).Encoded();

    private byte[] EventBytes()
    {
        var frame = FrameData() ?? [];
        var data = new byte[6 + frame.Length + 2];
        "data: "u8.CopyTo(data);
        frame.CopyTo(data, 6);
        data[^2] = (byte)'\n';
        data[^1] = (byte)'\n';
        return data;
    }

    private async Task AcceptLoop(TcpListener listener, int current)
    {
        while (current == generation)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync();
            }
            catch (Exception exception) when (exception is SocketException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }
            if (current != generation || connections.Count >= ConnectionLimit)
            {
                client.Dispose();
                continue;
            }
            client.NoDelay = true;
            var connection = new Connection(client);
            connections.Add(connection);
            _ = Serve(connection, current);
        }
    }

    private async Task Serve(Connection connection, int current)
    {
        var buffer = new MemoryStream();
        var chunk = new byte[8192];
        // A client that never finishes its request doesn't get to hold the connection.
        using var timeout = new CancellationTokenSource(RequestTimeout);
        try
        {
            while (true)
            {
                int count;
                try { count = await connection.Stream.ReadAsync(chunk, timeout.Token); }
                catch (Exception exception) when (exception is IOException or ObjectDisposedException or OperationCanceledException)
                {
                    Forget(connection);
                    return;
                }
                if (count == 0 || current != generation)
                {
                    Forget(connection);
                    return;
                }
                buffer.Write(chunk, 0, count);
                var (kind, head) = HttpRequestHead.Parse(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
                if (kind == HttpRequestHead.ParseKind.Incomplete) continue;
                if (kind == HttpRequestHead.ParseKind.Malformed)
                {
                    await Reply(connection, 400, "Bad Request");
                    return;
                }
                await Respond(connection, head);
                return;
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or SocketException)
        {
            Forget(connection);
        }
    }

    private async Task Respond(Connection connection, HttpRequestHead head)
    {
        if (head.Method != "GET")
        {
            await Reply(connection, 405, "Method Not Allowed");
            return;
        }
        switch (head.Path)
        {
            case "/v1/events":
                await OpenStream(connection);
                break;
            case "/v1/state":
                if (FrameData() is { } data) await Reply(connection, 200, "OK", "application/json", data);
                else await Reply(connection, 503, "Service Unavailable");
                break;
            case "/":
                var text = $"Ollameter on {host}. GET /v1/state for the latest update, or /v1/events to stream them.\n";
                await Reply(connection, 200, "OK", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(text));
                break;
            default:
                await Reply(connection, 404, "Not Found");
                break;
        }
    }

    private async Task OpenStream(Connection connection)
    {
        // The body runs until either side closes the connection, so it has no length.
        var head = "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n"u8;
        var first = new MemoryStream();
        first.Write(head);
        if (latest is not null) first.Write(EventBytes());
        connection.IsStream = true;
        Changed?.Invoke();
        _ = Send(connection, first.ToArray());
        await WatchForClose(connection);
    }

    private async Task Send(Connection connection, byte[] data)
    {
        connection.Unsent++;
        try
        {
            await connection.Stream.WriteAsync(data);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or SocketException)
        {
            Forget(connection);
        }
        finally
        {
            connection.Unsent--;
        }
    }

    /// <summary>Readers send nothing after their request, so anything that arrives is the connection ending.</summary>
    private async Task WatchForClose(Connection connection)
    {
        var scratch = new byte[1024];
        try
        {
            while (await connection.Stream.ReadAsync(scratch) > 0) { }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or SocketException)
        {
        }
        Forget(connection);
    }

    private void Forget(Connection connection)
    {
        connection.Close();
        if (connections.Remove(connection) && connection.IsStream) Changed?.Invoke();
    }

    private async Task Reply(Connection connection, int code, string reason, string type = "text/plain; charset=utf-8", byte[]? body = null)
    {
        body ??= Encoding.UTF8.GetBytes(reason + "\n");
        var head = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {code} {reason}\r\nContent-Type: {type}\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
        try
        {
            await connection.Stream.WriteAsync(head);
            await connection.Stream.WriteAsync(body);
            connection.Client.Client.Shutdown(SocketShutdown.Send);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or SocketException)
        {
        }
        Forget(connection);
    }
}

/// <summary>Follows another computer's stream, and keeps reconnecting when it drops.</summary>
internal sealed class RemoteClient
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FirstFrameLimit = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan StallLimit = TimeSpan.FromSeconds(6);
    private static readonly double[] RetryDelays = [1, 2, 4, 8, 15];
    private const double IncompatibleRetry = 60;

    private readonly DispatcherTimer watchdog;
    private string host;
    private int failures;
    private DateTime lastFrame = DateTime.MinValue;
    private CancellationTokenSource? attempt;
    private CancellationTokenSource? retry;
    private bool stopped = true;

    public MeterSource Source { get; }
    public RemoteStatus Status { get; private set; } = RemoteStatus.Connecting;

    /// <summary>Every model it produces: the other computer's numbers, or a placeholder while there are none.</summary>
    public event Action<MeterViewModel>? ModelChanged;

    public RemoteClient(MeterSource source)
    {
        Source = source;
        host = source.DisplayName;
        watchdog = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Normal, (_, _) =>
        {
            if (!stopped && Status == RemoteStatus.Connected && !IsFresh) Fail();
        }, Dispatcher.CurrentDispatcher);
        watchdog.Stop();
    }

    public void Start()
    {
        stopped = false;
        Report(RemoteStatus.Connecting);
        Connect();
        watchdog.Start();
    }

    public void Stop()
    {
        stopped = true;
        watchdog.Stop();
        retry?.Cancel();
        attempt?.Cancel();
        attempt = null;
    }

    private bool IsFresh => DateTime.UtcNow - lastFrame <= StallLimit;

    private void Connect()
    {
        if (stopped) return;
        var current = new CancellationTokenSource();
        attempt = current;
        _ = Run(current);
        _ = FirstFrameDeadline(current);
    }

    /// <summary>
    /// Finding a computer by name can stall without an error, so an attempt that hasn't brought an update in time is
    /// dropped and tried again.
    /// </summary>
    private async Task FirstFrameDeadline(CancellationTokenSource current)
    {
        try { await Task.Delay(FirstFrameLimit, current.Token); }
        catch (TaskCanceledException) { return; }
        if (attempt == current && lastFrameAttempt != current) Fail();
    }

    private CancellationTokenSource? lastFrameAttempt;

    private async Task Run(CancellationTokenSource current)
    {
        var token = current.Token;
        using var client = new TcpClient(AddressFamily.InterNetworkV6) { NoDelay = true };
        client.Client.DualMode = true;
        try
        {
            using (var connecting = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                connecting.CancelAfter(ConnectTimeout);
                if (Source.Kind == MeterSourceKind.Service)
                {
                    var endpoint = await Dnssd.Resolve(Source.Name, ConnectTimeout);
                    if (endpoint is not { } found) throw new SocketException((int)SocketError.HostNotFound);
                    if (found.Addresses.Length > 0)
                        await client.ConnectAsync(found.Addresses.Select(Mapped).ToArray(), found.Port, connecting.Token);
                    else
                        await client.ConnectAsync(found.Host, found.Port, connecting.Token);
                }
                else
                {
                    await client.ConnectAsync(Source.Name, Source.Port, connecting.Token);
                }
            }
            var stream = client.GetStream();
            var request = $"GET /v1/events HTTP/1.1\r\nHost: {Source.DisplayName}\r\nAccept: text/event-stream\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(request), token);
            var parser = new EventStreamParser();
            var buffer = new byte[256 * 1024];
            while (!token.IsCancellationRequested)
            {
                var count = await stream.ReadAsync(buffer, token);
                if (count == 0) break;
                foreach (var e in parser.Append(buffer.AsSpan(0, count)))
                {
                    if (attempt != current) return;
                    Handle(e, current);
                    if (attempt != current) return;
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested && attempt != current)
        {
            return;
        }
        catch (Exception exception) when (exception is SocketException or IOException or OperationCanceledException
            or EventStreamParser.FailureException or ObjectDisposedException)
        {
        }
        if (attempt == current) Fail();
    }

    /// <summary>A dual-stack socket reaches IPv4 addresses through their mapped IPv6 form.</summary>
    private static IPAddress Mapped(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetwork ? address.MapToIPv6() : address;

    private void Handle(string e, CancellationTokenSource current)
    {
        var result = SharedFrame.Decode(Encoding.UTF8.GetBytes(e));
        if (result.Frame is { } frame)
        {
            lastFrameAttempt = current;
            failures = 0;
            lastFrame = DateTime.UtcNow;
            host = frame.Host;
            Status = RemoteStatus.Connected;
            var model = frame.Model;
            model.Remote = new RemoteSource(frame.Host, RemoteStatus.Connected);
            ModelChanged?.Invoke(model);
        }
        else if (result.Failure == SharedFrame.DecodeFailureKind.OtherFormat)
        {
            attempt?.Cancel();
            attempt = null;
            Report(RemoteStatus.Incompatible(result.App));
            ScheduleRetry(IncompatibleRetry);
        }
        else
        {
            Fail();
        }
    }

    private void Fail()
    {
        if (stopped) return;
        attempt?.Cancel();
        attempt = null;
        // A short drop, such as the other computer restarting the app, leaves the last numbers up while this
        // reconnects. The watchdog takes them down once they're too old.
        if (!(Status == RemoteStatus.Connected && IsFresh)) Report(RemoteStatus.Unreachable);
        var delay = RetryDelays[Math.Min(failures, RetryDelays.Length - 1)];
        failures++;
        ScheduleRetry(delay);
    }

    private void ScheduleRetry(double seconds)
    {
        retry?.Cancel();
        var current = new CancellationTokenSource();
        retry = current;
        _ = RetryAfter(seconds, current.Token);
    }

    private async Task RetryAfter(double seconds, CancellationToken token)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(seconds), token); }
        catch (TaskCanceledException) { return; }
        if (!stopped && attempt is null) Connect();
    }

    private void Report(RemoteStatus status)
    {
        Status = status;
        ModelChanged?.Invoke(MeterViewModels.Placeholder(new RemoteSource(host, status)));
    }
}
