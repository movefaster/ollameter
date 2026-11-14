using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace Ollameter;

/// <summary>
/// Bonjour through the DNS-SD functions Windows 10 added to dnsapi.dll, which use the system's own mDNS
/// responder. Macs running Ollameter see this PC, and it sees them, with nothing extra installed.
/// </summary>
/// <remarks>
/// The callbacks arrive on thread-pool threads. Each copies what it needs and hands it to the UI thread.
/// </remarks>
internal static class Dnssd
{
    public const string ServiceType = "_ollameter._tcp";
    private const string Domain = "local";
    private const uint QueryRequestVersion1 = 1;
    private const int RequestPending = 9506;
    private const ushort TypePtr = 12;

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceInstance
    {
        public IntPtr InstanceName;
        public IntPtr HostName;
        public IntPtr Ip4Address;
        public IntPtr Ip6Address;
        public ushort Port;
        public ushort Priority;
        public ushort Weight;
        public uint PropertyCount;
        public IntPtr Keys;
        public IntPtr Values;
        public uint InterfaceIndex;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RegisterRequest
    {
        public uint Version;
        public uint InterfaceIndex;
        public IntPtr ServiceInstance;
        public IntPtr Callback;
        public IntPtr Context;
        public IntPtr Credentials;
        public int UnicastEnabled;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BrowseRequest
    {
        public uint Version;
        public uint InterfaceIndex;
        public IntPtr QueryName;
        public IntPtr Callback;
        public IntPtr Context;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ResolveRequest
    {
        public uint Version;
        public uint InterfaceIndex;
        public IntPtr QueryName;
        public IntPtr Callback;
        public IntPtr Context;
    }

    /// <summary>The start of DNS_RECORDW, as far as the PTR data that follows it.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct DnsRecord
    {
        public IntPtr Next;
        public IntPtr Name;
        public ushort Type;
        public ushort DataLength;
        public uint Flags;
        public uint Ttl;
        public uint Reserved;
        public IntPtr PtrNameHost;
    }

    [DllImport("dnsapi.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DnsServiceConstructInstance(
        string serviceName, string hostName, IntPtr ip4, IntPtr ip6, ushort port, ushort priority, ushort weight,
        uint propertyCount, IntPtr keys, IntPtr values);

    [DllImport("dnsapi.dll")]
    private static extern void DnsServiceFreeInstance(IntPtr instance);

    [DllImport("dnsapi.dll")]
    private static extern int DnsServiceRegister(IntPtr request, IntPtr cancel);

    [DllImport("dnsapi.dll")]
    private static extern int DnsServiceDeRegister(IntPtr request, IntPtr cancel);

    [DllImport("dnsapi.dll")]
    private static extern int DnsServiceBrowse(IntPtr request, IntPtr cancel);

    [DllImport("dnsapi.dll")]
    private static extern int DnsServiceBrowseCancel(IntPtr cancel);

    [DllImport("dnsapi.dll")]
    private static extern int DnsServiceResolve(IntPtr request, IntPtr cancel);

    [DllImport("dnsapi.dll")]
    private static extern int DnsServiceResolveCancel(IntPtr cancel);

    [DllImport("dnsapi.dll")]
    private static extern void DnsRecordListFree(IntPtr records, int freeType);

    /// <summary>False on Windows builds older than the DNS-SD functions, where sharing works by address only.</summary>
    public static bool Supported { get; private set; } = true;

    private static bool Call(Func<int> start)
    {
        if (!Supported) return false;
        try
        {
            return start() == RequestPending;
        }
        catch (EntryPointNotFoundException)
        {
            Supported = false;
            return false;
        }
    }

    private static string InstanceQuery(string name) => $"{Escape(name)}.{ServiceType}.{Domain}";

    /// <summary>Instance names can hold dots, which DNS-SD writes escaped.</summary>
    private static string Escape(string name) => name.Replace("\\", "\\\\").Replace(".", "\\.");

    private static string Unescape(string name)
    {
        var result = new System.Text.StringBuilder(name.Length);
        for (var index = 0; index < name.Length; index++)
        {
            var escaped = name[index] == '\\';
            if (escaped && index + 3 < name.Length && char.IsAsciiDigit(name[index + 1]) && char.IsAsciiDigit(name[index + 2])
                && char.IsAsciiDigit(name[index + 3]))
            {
                result.Append((char)int.Parse(name.AsSpan(index + 1, 3)));
                index += 3;
            }
            else if (escaped && index + 1 < name.Length)
            {
                result.Append(name[++index]);
            }
            else
            {
                result.Append(name[index]);
            }
        }
        return result.ToString();
    }

    /// <summary>The instance name from a full name like <c>studio._ollameter._tcp.local</c>.</summary>
    private static string? InstanceName(string fullName)
    {
        var suffix = $".{ServiceType}.{Domain}";
        var name = fullName.TrimEnd('.');
        if (!name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return null;
        var instance = name[..^suffix.Length];
        return instance.Length == 0 ? null : Unescape(instance);
    }

    /// <summary>Advertises this PC's sharing server under its name.</summary>
    public sealed unsafe class Registration : IDisposable
    {
        private readonly Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        private readonly Action<string?> onRegistered;
        private GCHandle self;
        private IntPtr request;
        private IntPtr instance;
        private bool registered;
        private bool disposed;

        /// <summary>Calls back with the name it went out under, which can differ if another computer took it.</summary>
        public Registration(string name, ushort port, Action<string?> onRegistered)
        {
            this.onRegistered = onRegistered;
            try
            {
                if (Supported)
                {
                    instance = DnsServiceConstructInstance(
                        InstanceQuery(name), $"{Dns.GetHostName()}.{Domain}", IntPtr.Zero, IntPtr.Zero, port, 0, 0, 0,
                        IntPtr.Zero, IntPtr.Zero);
                }
            }
            catch (EntryPointNotFoundException)
            {
                Supported = false;
            }
            if (instance == IntPtr.Zero)
            {
                onRegistered(null);
                return;
            }
            self = GCHandle.Alloc(this);
            request = Marshal.AllocHGlobal(sizeof(RegisterRequest));
            *(RegisterRequest*)request = new RegisterRequest
            {
                Version = QueryRequestVersion1,
                ServiceInstance = instance,
                Callback = (IntPtr)(delegate* unmanaged[Stdcall]<uint, IntPtr, IntPtr, void>)&Completed,
                Context = GCHandle.ToIntPtr(self),
            };
            if (!Call(() => DnsServiceRegister(request, IntPtr.Zero)))
            {
                Release();
                onRegistered(null);
            }
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static void Completed(uint status, IntPtr context, IntPtr result)
        {
            string? name = null;
            if (result != IntPtr.Zero)
            {
                var full = Marshal.PtrToStringUni(((ServiceInstance*)result)->InstanceName);
                name = full is null ? null : InstanceName(full) ?? full;
                DnsServiceFreeInstance(result);
            }
            if (GCHandle.FromIntPtr(context).Target is not Registration registration) return;
            var succeeded = status == 0;
            registration.dispatcher.BeginInvoke(() => registration.Finish(succeeded, name));
        }

        private void Finish(bool succeeded, string? name)
        {
            if (disposed) return;
            registered = succeeded;
            onRegistered(succeeded ? name : null);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            // Deregistering sends the goodbye that tells other computers this one stopped sharing.
            if (registered && request != IntPtr.Zero)
            {
                DnsServiceDeRegister(request, IntPtr.Zero);
                // The deregister completes on its own time and still needs the request, so it's left allocated
                // and the handle kept. A handful of bytes per sharing toggle.
                return;
            }
            Release();
        }

        private void Release()
        {
            if (instance != IntPtr.Zero) DnsServiceFreeInstance(instance);
            instance = IntPtr.Zero;
            if (request != IntPtr.Zero) Marshal.FreeHGlobal(request);
            request = IntPtr.Zero;
            if (self.IsAllocated) self.Free();
        }
    }

    /// <summary>Finds other computers sharing. Names stay until they say goodbye or their records expire.</summary>
    public sealed unsafe class Browser : IDisposable
    {
        private static readonly TimeSpan RestartAfter = TimeSpan.FromSeconds(5);

        private readonly Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        private readonly Dictionary<string, DateTime> expiries = new(StringComparer.OrdinalIgnoreCase);
        private readonly DispatcherTimer sweep;
        private GCHandle self;
        private IntPtr request;
        private IntPtr cancel;
        private IntPtr queryName;
        private bool running;
        private bool stopped;

        public IReadOnlyList<string> Names { get; private set; } = [];
        public event Action? Changed;

        public Browser()
        {
            sweep = new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Background, (_, _) => Expire(), dispatcher);
        }

        public void Start()
        {
            if (running || stopped) return;
            running = true;
            self = GCHandle.Alloc(this);
            queryName = Marshal.StringToHGlobalUni($"{ServiceType}.{Domain}");
            request = Marshal.AllocHGlobal(sizeof(BrowseRequest));
            cancel = Marshal.AllocHGlobal(IntPtr.Size);
            *(IntPtr*)cancel = IntPtr.Zero;
            *(BrowseRequest*)request = new BrowseRequest
            {
                Version = QueryRequestVersion1,
                QueryName = queryName,
                Callback = (IntPtr)(delegate* unmanaged[Stdcall]<uint, IntPtr, IntPtr, void>)&Found,
                Context = GCHandle.ToIntPtr(self),
            };
            if (!Call(() => DnsServiceBrowse(request, cancel)))
            {
                Release();
                // Windows without these functions won't gain them, so only a failed attempt is retried.
                if (Supported) ScheduleRestart();
                return;
            }
            sweep.Start();
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static void Found(uint status, IntPtr context, IntPtr records)
        {
            var seen = new List<(string Name, uint Ttl)>();
            for (var record = records; record != IntPtr.Zero; record = ((DnsRecord*)record)->Next)
            {
                var entry = (DnsRecord*)record;
                if (entry->Type != TypePtr || entry->PtrNameHost == IntPtr.Zero) continue;
                if (Marshal.PtrToStringUni(entry->PtrNameHost) is { } full && InstanceName(full) is { } name)
                    seen.Add((name, entry->Ttl));
            }
            if (records != IntPtr.Zero) DnsRecordListFree(records, 1);
            if (GCHandle.FromIntPtr(context).Target is not Browser browser) return;
            browser.dispatcher.BeginInvoke(() => browser.Receive(status, seen));
        }

        private void Receive(uint status, List<(string Name, uint Ttl)> seen)
        {
            if (stopped) return;
            var now = DateTime.UtcNow;
            foreach (var (name, ttl) in seen)
            {
                // A zero TTL is the goodbye a computer sends when it stops sharing.
                if (ttl == 0) expiries.Remove(name);
                else expiries[name] = now.AddSeconds(Math.Max(ttl, 120));
            }
            Publish();
            // A status other than success ends this browse, so another starts.
            if (status != 0 && running)
            {
                running = false;
                ScheduleRestart();
            }
        }

        private void Expire()
        {
            var now = DateTime.UtcNow;
            foreach (var name in expiries.Where(pair => pair.Value <= now).Select(pair => pair.Key).ToList())
                expiries.Remove(name);
            Publish();
        }

        private void Publish()
        {
            var names = expiries.Keys
                .Order(StringComparer.Create(System.Globalization.CultureInfo.CurrentCulture,
                    System.Globalization.CompareOptions.IgnoreCase | System.Globalization.CompareOptions.NumericOrdering))
                .ToList();
            if (names.SequenceEqual(Names)) return;
            Names = names;
            Changed?.Invoke();
        }

        private void ScheduleRestart()
        {
            var timer = new DispatcherTimer(RestartAfter, DispatcherPriority.Background, (sender, _) =>
            {
                ((DispatcherTimer)sender!).Stop();
                Start();
            }, dispatcher);
            timer.Start();
        }

        public void Dispose()
        {
            stopped = true;
            sweep.Stop();
            if (running && cancel != IntPtr.Zero)
            {
                DnsServiceBrowseCancel(cancel);
                // Windows may still call back once with the cancellation, so the request is left allocated.
                return;
            }
            Release();
        }

        private void Release()
        {
            running = false;
            if (request != IntPtr.Zero) Marshal.FreeHGlobal(request);
            if (cancel != IntPtr.Zero) Marshal.FreeHGlobal(cancel);
            if (queryName != IntPtr.Zero) Marshal.FreeHGlobal(queryName);
            request = cancel = queryName = IntPtr.Zero;
            if (self.IsAllocated) self.Free();
        }
    }

    public readonly record struct Endpoint(string Host, ushort Port, IPAddress[] Addresses);

    /// <summary>Everything a resolve hands to Windows, which has to outlive the call.</summary>
    private sealed class PendingResolve
    {
        public readonly TaskCompletionSource<Endpoint?> Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public GCHandle Handle;
        public IntPtr QueryName, Request, Cancel;

        public void Release()
        {
            Marshal.FreeHGlobal(QueryName);
            Marshal.FreeHGlobal(Request);
            Marshal.FreeHGlobal(Cancel);
            if (Handle.IsAllocated) Handle.Free();
        }
    }

    private static unsafe bool StartResolve(string name, PendingResolve pending)
    {
        pending.Handle = GCHandle.Alloc(pending);
        pending.QueryName = Marshal.StringToHGlobalUni(InstanceQuery(name));
        pending.Request = Marshal.AllocHGlobal(sizeof(ResolveRequest));
        pending.Cancel = Marshal.AllocHGlobal(IntPtr.Size);
        *(IntPtr*)pending.Cancel = IntPtr.Zero;
        *(ResolveRequest*)pending.Request = new ResolveRequest
        {
            Version = QueryRequestVersion1,
            QueryName = pending.QueryName,
            Callback = (IntPtr)(delegate* unmanaged[Stdcall]<uint, IntPtr, IntPtr, void>)&Resolved,
            Context = GCHandle.ToIntPtr(pending.Handle),
        };
        var request = pending.Request;
        var cancel = pending.Cancel;
        return Call(() => DnsServiceResolve(request, cancel));
    }

    /// <summary>Looks up where a computer found by name is sharing.</summary>
    public static async Task<Endpoint?> Resolve(string name, TimeSpan timeout)
    {
        var pending = new PendingResolve();
        if (!StartResolve(name, pending))
        {
            pending.Release();
            return null;
        }
        var winner = await Task.WhenAny(pending.Result.Task, Task.Delay(timeout));
        if (winner == pending.Result.Task)
        {
            pending.Release();
            return pending.Result.Task.Result;
        }
        DnsServiceResolveCancel(pending.Cancel);
        // After a cancel Windows still calls back, so everything it might touch stays allocated until then.
        _ = pending.Result.Task.ContinueWith(_ => pending.Release(), TaskScheduler.Default);
        return null;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe void Resolved(uint status, IntPtr context, IntPtr result)
    {
        Endpoint? endpoint = null;
        if (status == 0 && result != IntPtr.Zero)
        {
            var instance = (ServiceInstance*)result;
            var addresses = new List<IPAddress>();
            if (instance->Ip4Address != IntPtr.Zero) addresses.Add(new IPAddress(new ReadOnlySpan<byte>((void*)instance->Ip4Address, 4)));
            if (instance->Ip6Address != IntPtr.Zero) addresses.Add(new IPAddress(new ReadOnlySpan<byte>((void*)instance->Ip6Address, 16)));
            var host = Marshal.PtrToStringUni(instance->HostName) ?? "";
            endpoint = new Endpoint(host.TrimEnd('.'), instance->Port, [.. addresses]);
        }
        if (result != IntPtr.Zero) DnsServiceFreeInstance(result);
        if (GCHandle.FromIntPtr(context).Target is PendingResolve pending) pending.Result.TrySetResult(endpoint);
    }
}
