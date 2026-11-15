using System.IO;
using System.Net;
using System.Net.NetworkInformation;
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
    /// <summary>How long Bonjour gets when there's somewhere else to try. Working, it answers well inside this.</summary>
    private static readonly TimeSpan QuickResolveTimeout = TimeSpan.FromSeconds(3);
    /// <summary>A <c>.local</c> name that nobody answers for takes Windows under 3 seconds to give up on.</summary>
    private static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(3);
    /// <summary>Long enough for a Bonjour lookup that doesn't answer, then a name lookup and a connection.</summary>
    private static readonly TimeSpan FirstFrameLimit = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan StallLimit = TimeSpan.FromSeconds(6);
    private static readonly double[] RetryDelays = [1, 2, 4, 8, 15];
    private const double IncompatibleRetry = 60;

    private readonly Settings settings = Settings.Current;
    private readonly Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
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
        NetworkChange.NetworkAddressChanged += NetworkChanged;
    }

    public void Stop()
    {
        stopped = true;
        NetworkChange.NetworkAddressChanged -= NetworkChanged;
        watchdog.Stop();
        retry?.Cancel();
        attempt?.Cancel();
        attempt = null;
    }

    /// <summary>
    /// Waking from sleep or joining a network changes this PC's addresses. A client waiting to try again tries at
    /// once, rather than sitting out a delay that grew while the network was down.
    /// </summary>
    private void NetworkChanged(object? sender, EventArgs e) => dispatcher.BeginInvoke(() =>
    {
        if (stopped || attempt is not null || Status.Kind != RemoteStatusKind.Unreachable) return;
        failures = 0;
        retry?.Cancel();
        Connect();
    });

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
            if (Source.Kind == MeterSourceKind.Service)
            {
                await ConnectToService(client, token);
            }
            else
            {
                using var connecting = CancellationTokenSource.CreateLinkedTokenSource(token);
                connecting.CancelAfter(ConnectTimeout);
                await client.ConnectAsync(Source.Name, Source.Port, connecting.Token);
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

    /// <summary>
    /// Bonjour says where the computer is sharing. Windows' Bonjour can stop answering for good, such as after the PC
    /// wakes from sleep, while plain lookups of <c>.local</c> names keep working. So without an answer, the host and
    /// port Bonjour gave last time are tried, then the addresses they had. With nothing from last time, it tries the
    /// computer's own name, which both apps advertise under, on the usual port.
    /// </summary>
    private async Task ConnectToService(TcpClient client, CancellationToken token)
    {
        var fallback = Remembered() ?? Guessed();
        var found = await Dnssd.Resolve(Source.Name, fallback is null ? ConnectTimeout : QuickResolveTimeout);
        token.ThrowIfCancellationRequested();
        if (found is { } endpoint)
        {
            Remember(endpoint);
            using var connecting = CancellationTokenSource.CreateLinkedTokenSource(token);
            connecting.CancelAfter(ConnectTimeout);
            if (endpoint.Addresses.Length > 0)
                await client.ConnectAsync(endpoint.Addresses.Select(Mapped).ToArray(), endpoint.Port, connecting.Token);
            else
                await client.ConnectAsync(endpoint.Host, endpoint.Port, connecting.Token);
            return;
        }
        if (fallback is not { } known) throw new SocketException((int)SocketError.HostNotFound);
        // The name comes first, in case the computer's address has changed since.
        IPAddress[] current = [];
        using (var lookup = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            lookup.CancelAfter(LookupTimeout);
            try { current = await Dns.GetHostAddressesAsync(known.Host, lookup.Token); }
            catch (Exception exception) when (exception is SocketException
                || (exception is OperationCanceledException && !token.IsCancellationRequested))
            {
            }
        }
        token.ThrowIfCancellationRequested();
        var addresses = current.Concat(known.Addresses).Distinct().Select(Mapped).ToArray();
        if (addresses.Length == 0) throw new SocketException((int)SocketError.HostNotFound);
        using (var connecting = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            connecting.CancelAfter(ConnectTimeout);
            await client.ConnectAsync(addresses, known.Port, connecting.Token);
        }
    }

    private Dnssd.Endpoint? Remembered()
    {
        if (settings.LastEndpoints?.GetValueOrDefault(Source.StorageValue) is not { } known) return null;
        var addresses = known.Addresses.Select(text => IPAddress.TryParse(text, out var address) ? address : null)
            .OfType<IPAddress>().ToArray();
        return new Dnssd.Endpoint(known.Host, known.Port, addresses);
    }

    private void Remember(Dnssd.Endpoint endpoint)
    {
        var known = new KnownEndpoint(endpoint.Host, endpoint.Port, endpoint.Addresses.Select(address => address.ToString()).ToList());
        var all = settings.LastEndpoints ??= [];
        if (all.GetValueOrDefault(Source.StorageValue) is { } old && old.SameAs(known)) return;
        all[Source.StorageValue] = known;
        settings.Save();
    }

    /// <summary>A Bonjour name that's also a valid host name, as a computer's own name is, is worth a try as one.</summary>
    private Dnssd.Endpoint? Guessed()
    {
        var name = Source.Name;
        var isLabel = name.Length is > 0 and <= 63 && name[0] != '-' && name[^1] != '-'
            && name.All(character => char.IsAsciiLetterOrDigit(character) || character == '-');
        return isLabel ? new Dnssd.Endpoint($"{name}.local", MeterSource.DefaultPort, []) : null;
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
