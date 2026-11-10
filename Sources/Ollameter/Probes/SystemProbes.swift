import Darwin
import Foundation
import IOKit
import MeterCore

/// GPU load and memory from the IORegistry. Needs no root and no private API.
enum GPUReader {
    static func read() -> (utilization: Double?, inUse: UInt64?)? {
        let service = IOServiceGetMatchingService(kIOMainPortDefault, IOServiceMatching("IOAccelerator"))
        guard service != 0 else { return nil }
        defer { IOObjectRelease(service) }
        guard
            let property = IORegistryEntryCreateCFProperty(
                service, "PerformanceStatistics" as CFString, kCFAllocatorDefault, 0)?
                .takeRetainedValue() as? [String: Any]
        else { return nil }
        return (
            (property["Device Utilization %"] as? NSNumber)?.doubleValue,
            (property["In use system memory"] as? NSNumber)?.uint64Value
        )
    }
}

enum SystemReader {
    static func swapUsed() -> UInt64? {
        var usage = xsw_usage()
        var size = MemoryLayout<xsw_usage>.size
        guard sysctlbyname("vm.swapusage", &usage, &size, nil, 0) == 0 else { return nil }
        return usage.xsu_used
    }

    static func memoryPressure() -> MemoryPressure? {
        var level: Int32 = 0
        var size = MemoryLayout<Int32>.size
        guard sysctlbyname("kern.memorystatus_vm_pressure_level", &level, &size, nil, 0) == 0 else { return nil }
        switch level {
        case 1: return .normal
        case 2: return .warning
        case 4: return .critical
        default: return nil
        }
    }
}

struct RunnerProcess: Equatable, Sendable {
    let pid: pid_t
    let port: Int
}

struct ProcessScan: Equatable, Sendable {
    var ollamaRunning = false
    var runners: [RunnerProcess] = []
}

/// Finds Ollama and its llama-server runners. Each runner listens on a random port named in its arguments.
enum ProcessProbe {
    static func scan() -> ProcessScan {
        let estimate = proc_listallpids(nil, 0)
        guard estimate > 0 else { return ProcessScan() }
        var pids = [pid_t](repeating: 0, count: Int(estimate) + 64)
        let count = proc_listallpids(&pids, Int32(pids.count * MemoryLayout<pid_t>.size))
        guard count > 0 else { return ProcessScan() }

        var result = ProcessScan()
        var name = [CChar](repeating: 0, count: 256)
        for pid in pids.prefix(Int(count)) where pid > 0 {
            guard proc_name(pid, &name, UInt32(name.count)) > 0 else { continue }
            let processName = String(cString: name)
            if processName == "ollama" {
                result.ollamaRunning = true
            } else if processName == "llama-server", let port = port(of: pid) {
                result.runners.append(RunnerProcess(pid: pid, port: port))
            }
        }
        result.runners.sort { $0.pid < $1.pid }
        return result
    }

    private static func port(of pid: pid_t) -> Int? {
        let arguments = arguments(of: pid)
        guard let index = arguments.firstIndex(of: "--port"), index + 1 < arguments.count else { return nil }
        return Int(arguments[index + 1])
    }

    private static func arguments(of pid: pid_t) -> [String] {
        var mib: [Int32] = [CTL_KERN, KERN_PROCARGS2, pid]
        var size = 0
        guard sysctl(&mib, 3, nil, &size, nil, 0) == 0, size > 4 else { return [] }
        var buffer = [UInt8](repeating: 0, count: size)
        guard sysctl(&mib, 3, &buffer, &size, nil, 0) == 0, size > 4 else { return [] }

        let argumentCount = buffer.withUnsafeBytes { Int($0.load(as: Int32.self)) }
        var index = 4
        while index < size, buffer[index] != 0 { index += 1 }
        while index < size, buffer[index] == 0 { index += 1 }

        var result: [String] = []
        while index < size, result.count < argumentCount {
            let start = index
            while index < size, buffer[index] != 0 { index += 1 }
            result.append(String(decoding: buffer[start..<index], as: UTF8.self))
            index += 1
        }
        return result
    }

    /// Cumulative CPU time in nanoseconds and resident memory for a process.
    static func usage(of pid: pid_t) -> (cpuNanoseconds: UInt64, resident: UInt64)? {
        var info = rusage_info_v4()
        let status = withUnsafeMutablePointer(to: &info) { pointer in
            pointer.withMemoryRebound(to: rusage_info_t?.self, capacity: 1) {
                proc_pid_rusage(pid, RUSAGE_INFO_V4, $0)
            }
        }
        guard status == 0 else { return nil }
        return ((info.ri_user_time + info.ri_system_time) &* timebase.numer / timebase.denom, info.ri_resident_size)
    }

    private static let timebase: (numer: UInt64, denom: UInt64) = {
        var info = mach_timebase_info_data_t()
        mach_timebase_info(&info)
        return (UInt64(info.numer), UInt64(info.denom))
    }()
}

/// All hardware reads happen here, away from the main thread.
actor HardwareSampler {
    private lazy var power = PowerReader()
    private lazy var thermal = ThermalReader()
    private var cpuBaselines: [pid_t: (time: Date, nanoseconds: UInt64)] = [:]

    func scan() -> ProcessScan {
        ProcessProbe.scan()
    }

    /// GPU load is always read for the menu bar. The rest is only needed while the window is open.
    func sample(detail: Bool, runners: [RunnerProcess]) -> (HardwareSample, RunnerStats?) {
        var sample = HardwareSample()
        if let gpu = GPUReader.read() {
            sample.gpuUtilization = gpu.utilization
            sample.gpuMemoryInUse = gpu.inUse
        }
        guard detail else { return (sample, nil) }

        sample.gpuPowerWatts = power.watts()
        sample.dieTemperature = thermal.hottestDie()
        sample.swapUsed = SystemReader.swapUsed()
        sample.memoryPressure = SystemReader.memoryPressure()
        return (sample, runnerStats(for: runners))
    }

    private func runnerStats(for runners: [RunnerProcess]) -> RunnerStats? {
        guard !runners.isEmpty else {
            cpuBaselines.removeAll()
            return nil
        }
        let now = Date()
        var totalPercent: Double?
        var totalResident: UInt64 = 0
        var nextBaselines: [pid_t: (time: Date, nanoseconds: UInt64)] = [:]
        for runner in runners {
            guard let usage = ProcessProbe.usage(of: runner.pid) else { continue }
            totalResident += usage.resident
            nextBaselines[runner.pid] = (now, usage.cpuNanoseconds)
            if let baseline = cpuBaselines[runner.pid], usage.cpuNanoseconds >= baseline.nanoseconds {
                let elapsed = now.timeIntervalSince(baseline.time)
                if elapsed > 0 {
                    let percent = Double(usage.cpuNanoseconds - baseline.nanoseconds) / 1e9 / elapsed * 100
                    totalPercent = (totalPercent ?? 0) + percent
                }
            }
        }
        cpuBaselines = nextBaselines
        return RunnerStats(cpuPercent: totalPercent, residentBytes: totalResident)
    }
}
