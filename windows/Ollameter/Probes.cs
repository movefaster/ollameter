using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Ollameter.Core;

namespace Ollameter;

internal readonly record struct RunnerProcess(int Pid, int Port);

internal sealed record ProcessScan(bool OllamaRunning, IReadOnlyList<RunnerProcess> Runners)
{
    public static readonly ProcessScan None = new(false, []);

    public bool SameAs(ProcessScan other) => OllamaRunning == other.OllamaRunning && Runners.SequenceEqual(other.Runners);
}

/// <summary>Finds Ollama and its llama-server runners. Each runner listens on a random port named in its arguments.</summary>
internal static class ProcessProbe
{
    public static ProcessScan Scan()
    {
        var running = false;
        var runners = new List<RunnerProcess>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                string name;
                try { name = process.ProcessName; }
                catch (InvalidOperationException) { continue; }
                if (name.Equals("ollama", StringComparison.OrdinalIgnoreCase))
                {
                    running = true;
                }
                else if (name.Equals("llama-server", StringComparison.OrdinalIgnoreCase) && Port(process.Id) is { } port)
                {
                    runners.Add(new RunnerProcess(process.Id, port));
                }
            }
        }
        runners.Sort((first, second) => first.Pid.CompareTo(second.Pid));
        return new ProcessScan(running, runners);
    }

    private static int? Port(int pid)
    {
        var arguments = Arguments(pid);
        var index = Array.IndexOf(arguments, "--port");
        return index >= 0 && index + 1 < arguments.Length && int.TryParse(arguments[index + 1], out var port) ? port : null;
    }

    private static string[] Arguments(int pid) => CommandLine(pid) is { } line ? Split(line) : [];

    /// <summary>Reads another process's command line, which needs no more access than its name does.</summary>
    private static string? CommandLine(int pid)
    {
        const int ProcessCommandLineInformation = 60;
        const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);
        var process = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero) return null;
        try
        {
            var length = 1024;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var buffer = Marshal.AllocHGlobal(length);
                try
                {
                    var status = Native.NtQueryInformationProcess(process, ProcessCommandLineInformation, buffer, length, out var needed);
                    if (status == StatusInfoLengthMismatch || (status < 0 && needed > length))
                    {
                        length = Math.Max(needed, length * 2);
                        continue;
                    }
                    if (status < 0) return null;
                    // A UNICODE_STRING whose buffer points further into the same block.
                    var bytes = (ushort)Marshal.ReadInt16(buffer);
                    var text = Marshal.ReadIntPtr(buffer, IntPtr.Size);
                    return text == IntPtr.Zero ? null : Marshal.PtrToStringUni(text, bytes / 2);
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            return null;
        }
        finally
        {
            Native.CloseHandle(process);
        }
    }

    private static string[] Split(string commandLine)
    {
        var argv = Native.CommandLineToArgvW(commandLine, out var count);
        if (argv == IntPtr.Zero) return [];
        try
        {
            var result = new string[count];
            for (var index = 0; index < count; index++)
                result[index] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, index * IntPtr.Size)) ?? "";
            return result;
        }
        finally
        {
            Native.LocalFree(argv);
        }
    }

    /// <summary>Cumulative CPU time in 100 ns units and the working set for a process.</summary>
    public static (ulong CpuTime, ulong Resident)? Usage(int pid)
    {
        var process = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero) return null;
        try
        {
            if (!Native.GetProcessTimes(process, out _, out _, out var kernel, out var user)) return null;
            var counters = new Native.PROCESS_MEMORY_COUNTERS { cb = Marshal.SizeOf<Native.PROCESS_MEMORY_COUNTERS>() };
            var resident = Native.GetProcessMemoryInfo(process, out counters, counters.cb) ? (ulong)counters.WorkingSetSize : 0;
            return (kernel.Value + user.Value, resident);
        }
        finally
        {
            Native.CloseHandle(process);
        }
    }
}

internal readonly record struct GpuReading(double? Utilization, ulong? MemoryInUse, ulong? MemoryTotal, double? PowerWatts, double? Temperature);

/// <summary>
/// NVIDIA GPUs through NVML, which the driver installs. Its functions are looked up at runtime, so a PC without
/// it costs these readings and nothing else.
/// </summary>
internal sealed unsafe class NvmlReader
{
    private const int Success = 0;
    private const int TemperatureGpu = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct Utilization
    {
        public uint Gpu;
        public uint Memory;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Memory
    {
        public ulong Total;
        public ulong Free;
        public ulong Used;
    }

    private readonly delegate* unmanaged<IntPtr, Utilization*, int> getUtilization;
    private readonly delegate* unmanaged<IntPtr, uint*, int> getPower;
    private readonly delegate* unmanaged<IntPtr, int, uint*, int> getTemperature;
    private readonly delegate* unmanaged<IntPtr, Memory*, int> getMemory;
    private readonly IntPtr[] devices;

    public int DeviceCount => devices.Length;

    private NvmlReader(IntPtr library, IntPtr[] devices)
    {
        this.devices = devices;
        getUtilization = (delegate* unmanaged<IntPtr, Utilization*, int>)Export(library, "nvmlDeviceGetUtilizationRates");
        getPower = (delegate* unmanaged<IntPtr, uint*, int>)Export(library, "nvmlDeviceGetPowerUsage");
        getTemperature = (delegate* unmanaged<IntPtr, int, uint*, int>)Export(library, "nvmlDeviceGetTemperature");
        getMemory = (delegate* unmanaged<IntPtr, Memory*, int>)Export(library, "nvmlDeviceGetMemoryInfo");
    }

    private static IntPtr Export(IntPtr library, string name) =>
        NativeLibrary.TryGetExport(library, name, out var address) ? address : IntPtr.Zero;

    public static NvmlReader? Create()
    {
        if (Environment.GetEnvironmentVariable("OLLAMETER_NO_NVML") == "1") return null;
        IntPtr library;
        var fallback = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvml.dll");
        if (!NativeLibrary.TryLoad("nvml.dll", out library) && !NativeLibrary.TryLoad(fallback, out library)) return null;

        var init = (delegate* unmanaged<int>)Export(library, "nvmlInit_v2");
        var count = (delegate* unmanaged<uint*, int>)Export(library, "nvmlDeviceGetCount_v2");
        var handle = (delegate* unmanaged<uint, IntPtr*, int>)Export(library, "nvmlDeviceGetHandleByIndex_v2");
        if (init == null || count == null || handle == null || init() != Success) return null;
        uint total;
        if (count(&total) != Success || total == 0) return null;
        var devices = new List<IntPtr>();
        for (uint index = 0; index < total; index++)
        {
            IntPtr device;
            if (handle(index, &device) == Success) devices.Add(device);
        }
        return devices.Count == 0 ? null : new NvmlReader(library, [.. devices]);
    }

    /// <summary>
    /// Ollama may split a model across GPUs, so load is the busiest GPU's, memory and power are summed and the
    /// temperature is the hottest.
    /// </summary>
    public GpuReading Read(bool detail)
    {
        double? utilization = null, power = null, temperature = null;
        ulong? used = null, total = null;
        foreach (var device in devices)
        {
            Utilization rates;
            if (getUtilization != null && getUtilization(device, &rates) == Success)
                utilization = Math.Max(utilization ?? 0, rates.Gpu);
            Memory memory;
            if (getMemory != null && getMemory(device, &memory) == Success)
            {
                used = (used ?? 0) + memory.Used;
                total = (total ?? 0) + memory.Total;
            }
            if (!detail) continue;
            uint milliwatts, degrees;
            if (getPower != null && getPower(device, &milliwatts) == Success) power = (power ?? 0) + milliwatts / 1000.0;
            if (getTemperature != null && getTemperature(device, TemperatureGpu, &degrees) == Success)
                temperature = Math.Max(temperature ?? 0, degrees);
        }
        return new GpuReading(utilization, used, total, power, temperature);
    }
}

/// <summary>
/// Any GPU's load and memory from the performance counters Task Manager reads. Used when NVML isn't there,
/// such as on AMD and Intel GPUs. These give no power or temperature.
/// </summary>
internal sealed class PdhGpuReader : IDisposable
{
    private const uint FormatDouble = 0x00000200;
    private const uint FormatLarge = 0x00000400;
    private const int MoreData = unchecked((int)0x800007D2);

    private IntPtr query;
    private readonly IntPtr engines;
    private readonly IntPtr memory;

    private PdhGpuReader(IntPtr query, IntPtr engines, IntPtr memory)
    {
        this.query = query;
        this.engines = engines;
        this.memory = memory;
    }

    public static PdhGpuReader? Create()
    {
        if (PdhOpenQuery(null, IntPtr.Zero, out var query) != 0) return null;
        if (PdhAddEnglishCounter(query, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out var engines) != 0
            || PdhAddEnglishCounter(query, @"\GPU Adapter Memory(*)\Dedicated Usage", IntPtr.Zero, out var memory) != 0)
        {
            PdhCloseQuery(query);
            return null;
        }
        // Utilization is a rate, so it needs a first sample to measure from.
        PdhCollectQueryData(query);
        return new PdhGpuReader(query, engines, memory);
    }

    public GpuReading Read()
    {
        if (query == IntPtr.Zero || PdhCollectQueryData(query) != 0) return default;
        // Task Manager's figure: each engine's load summed over processes, then the busiest engine.
        var byEngine = new Dictionary<string, double>();
        foreach (var (name, value) in Values(engines, FormatDouble))
        {
            var engine = name.IndexOf("_luid_", StringComparison.Ordinal) is var at and >= 0 ? name[at..] : name;
            byEngine[engine] = byEngine.GetValueOrDefault(engine) + BitConverter.Int64BitsToDouble(value);
        }
        double? utilization = byEngine.Count > 0 ? Math.Min(byEngine.Values.Max(), 100) : null;
        ulong? used = null;
        foreach (var (_, value) in Values(memory, FormatLarge)) used = Math.Max(used ?? 0, (ulong)Math.Max(value, 0));
        return new GpuReading(utilization, used, null, null, null);
    }

    private static List<(string Name, long Value)> Values(IntPtr counter, uint format)
    {
        var result = new List<(string, long)>();
        var size = 0;
        if (PdhGetFormattedCounterArray(counter, format, ref size, out _, IntPtr.Zero) != MoreData || size == 0) return result;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (PdhGetFormattedCounterArray(counter, format, ref size, out var count, buffer) != 0) return result;
            // PDH_FMT_COUNTERVALUE_ITEM_W: a name pointer, then a status and an 8-byte value.
            var stride = IntPtr.Size + 8 + 8;
            for (var index = 0; index < count; index++)
            {
                var item = buffer + index * stride;
                var status = (uint)Marshal.ReadInt32(item, IntPtr.Size);
                if (status > 1) continue;
                var name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item)) ?? "";
                result.Add((name, Marshal.ReadInt64(item, IntPtr.Size + 8)));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return result;
    }

    public void Dispose()
    {
        if (query == IntPtr.Zero) return;
        PdhCloseQuery(query);
        query = IntPtr.Zero;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhOpenQuery(string? source, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhAddEnglishCounter(IntPtr query, string path, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern int PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhGetFormattedCounterArray(IntPtr counter, uint format, ref int bufferSize, out int itemCount, IntPtr buffer);

    [DllImport("pdh.dll")]
    private static extern int PdhCloseQuery(IntPtr query);
}

internal static class SystemReader
{
    /// <summary>
    /// Windows has no memory pressure level like macOS's, so this reads it from what's left: physical memory
    /// running out, or commit charge reaching the limit, past which allocations fail.
    /// </summary>
    public static MemoryPressure? Pressure()
    {
        var status = new Native.MEMORYSTATUSEX { dwLength = Marshal.SizeOf<Native.MEMORYSTATUSEX>() };
        if (!Native.GlobalMemoryStatusEx(ref status) || status.ullTotalPhys == 0) return null;
        var available = (double)status.ullAvailPhys / status.ullTotalPhys;
        var commit = status.ullTotalPageFile > 0
            ? 1 - (double)status.ullAvailPageFile / status.ullTotalPageFile
            : 0;
        if (available < 0.05 || commit >= 0.95) return MemoryPressure.Critical;
        if (available < 0.10 || commit >= 0.90) return MemoryPressure.Warning;
        return MemoryPressure.Normal;
    }
}

/// <summary>All hardware reads happen here, away from the UI thread, one at a time.</summary>
internal sealed class HardwareSampler
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Lazy<NvmlReader?> nvml = new(NvmlReader.Create);
    private PdhGpuReader? pdh;
    private bool triedPdh;
    private Dictionary<int, (DateTime Time, ulong CpuTime)> cpuBaselines = [];

    public string GpuSource => nvml.Value is not null ? "nvml" : pdh is not null ? "pdh" : "none";

    public Task<ProcessScan> Scan() => Run(ProcessProbe.Scan);

    /// <summary>GPU load is always read for the tray icon. The rest is only needed while someone can see it.</summary>
    public Task<(HardwareSample Sample, RunnerStats? Runner)> Sample(bool detail, IReadOnlyList<RunnerProcess> runners) =>
        Run(() =>
        {
            var gpu = ReadGpu(detail);
            var sample = new HardwareSample
            {
                GpuUtilization = gpu.Utilization,
                GpuMemoryInUse = gpu.MemoryInUse,
                GpuMemoryTotal = gpu.MemoryTotal,
            };
            if (!detail) return (sample, (RunnerStats?)null);
            sample.GpuPowerWatts = gpu.PowerWatts;
            sample.DieTemperature = gpu.Temperature;
            sample.MemoryPressure = SystemReader.Pressure();
            return (sample, RunnerStatsFor(runners));
        });

    private GpuReading ReadGpu(bool detail)
    {
        if (nvml.Value is { } reader) return reader.Read(detail);
        if (!triedPdh)
        {
            triedPdh = true;
            pdh = PdhGpuReader.Create();
        }
        return pdh?.Read() ?? default;
    }

    private async Task<T> Run<T>(Func<T> work)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await Task.Run(work).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private RunnerStats? RunnerStatsFor(IReadOnlyList<RunnerProcess> runners)
    {
        if (runners.Count == 0)
        {
            cpuBaselines.Clear();
            return null;
        }
        var now = DateTime.UtcNow;
        double? totalPercent = null;
        ulong totalResident = 0;
        var next = new Dictionary<int, (DateTime, ulong)>();
        foreach (var runner in runners)
        {
            if (ProcessProbe.Usage(runner.Pid) is not { } usage) continue;
            totalResident += usage.Resident;
            next[runner.Pid] = (now, usage.CpuTime);
            if (cpuBaselines.TryGetValue(runner.Pid, out var baseline) && usage.CpuTime >= baseline.CpuTime)
            {
                var elapsed = (now - baseline.Time).TotalSeconds;
                if (elapsed > 0)
                    totalPercent = (totalPercent ?? 0) + (usage.CpuTime - baseline.CpuTime) / 1e7 / elapsed * 100;
            }
        }
        cpuBaselines = next;
        return new RunnerStats(totalPercent, totalResident);
    }
}
