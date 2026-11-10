import Foundation

// GPU power and die temperature have no public API. These symbols are resolved at runtime so a future
// macOS that removes them costs those two readings and nothing else.

/// GPU power from IOReport's energy counters.
final class PowerReader {
    private typealias CopyChannels = @convention(c) (CFString, CFString?, UInt64, UInt64, UInt64) -> Unmanaged<CFDictionary>?
    private typealias CreateSubscription = @convention(c) (
        UnsafeRawPointer?, CFMutableDictionary, UnsafeMutablePointer<Unmanaged<CFMutableDictionary>?>, UInt64, CFTypeRef?
    ) -> OpaquePointer?
    private typealias CreateSamples = @convention(c) (OpaquePointer, CFMutableDictionary, CFTypeRef?) -> Unmanaged<CFDictionary>?
    private typealias CreateDelta = @convention(c) (CFDictionary, CFDictionary, CFTypeRef?) -> Unmanaged<CFDictionary>?
    private typealias GetString = @convention(c) (CFDictionary) -> Unmanaged<CFString>?
    private typealias GetInteger = @convention(c) (CFDictionary, Int32) -> Int64

    private var createSamples: CreateSamples?
    private var createDelta: CreateDelta?
    private var channelName: GetString?
    private var unitLabel: GetString?
    private var integerValue: GetInteger?

    private var subscription: OpaquePointer?
    private var subscribedChannels: CFMutableDictionary?
    private var previous: (sample: CFDictionary, time: Date)?

    /// A sample older than this would average over a stretch nobody was watching.
    private static let baselineExpiry: TimeInterval = 5

    init() {
        guard let library = dlopen("/usr/lib/libIOReport.dylib", RTLD_NOW),
            let copyChannelsSymbol = dlsym(library, "IOReportCopyChannelsInGroup"),
            let createSubscriptionSymbol = dlsym(library, "IOReportCreateSubscription"),
            let createSamplesSymbol = dlsym(library, "IOReportCreateSamples"),
            let createDeltaSymbol = dlsym(library, "IOReportCreateSamplesDelta"),
            let channelNameSymbol = dlsym(library, "IOReportChannelGetChannelName"),
            let unitLabelSymbol = dlsym(library, "IOReportChannelGetUnitLabel"),
            let integerValueSymbol = dlsym(library, "IOReportSimpleGetIntegerValue")
        else { return }

        let copyChannels = unsafeBitCast(copyChannelsSymbol, to: CopyChannels.self)
        let createSubscription = unsafeBitCast(createSubscriptionSymbol, to: CreateSubscription.self)
        createSamples = unsafeBitCast(createSamplesSymbol, to: CreateSamples.self)
        createDelta = unsafeBitCast(createDeltaSymbol, to: CreateDelta.self)
        channelName = unsafeBitCast(channelNameSymbol, to: GetString.self)
        unitLabel = unsafeBitCast(unitLabelSymbol, to: GetString.self)
        integerValue = unsafeBitCast(integerValueSymbol, to: GetInteger.self)

        guard let channels = copyChannels("Energy Model" as CFString, nil, 0, 0, 0)?.takeRetainedValue(),
            let mutable = CFDictionaryCreateMutableCopy(kCFAllocatorDefault, 0, channels)
        else { return }
        var subscribed: Unmanaged<CFMutableDictionary>?
        subscription = createSubscription(nil, mutable, &subscribed, 0, nil)
        subscribedChannels = subscribed?.takeRetainedValue()
    }

    func watts() -> Double? {
        guard let subscription, let subscribedChannels, let createSamples, let createDelta,
            let channelName, let unitLabel, let integerValue,
            let sample = createSamples(subscription, subscribedChannels, nil)?.takeRetainedValue()
        else { return nil }

        let now = Date()
        defer { previous = (sample, now) }
        guard let previous else { return nil }
        let elapsed = now.timeIntervalSince(previous.time)
        guard elapsed > 0.05, elapsed < Self.baselineExpiry,
            let delta = createDelta(previous.sample, sample, nil)?.takeRetainedValue() as NSDictionary?,
            let items = delta["IOReportChannels"] as? [NSDictionary]
        else { return nil }

        var joules = 0.0
        var found = false
        for item in items {
            let channel = item as CFDictionary
            guard channelName(channel)?.takeUnretainedValue() as String? == "GPU Energy" else { continue }
            let unit = unitLabel(channel)?.takeUnretainedValue() as String? ?? ""
            joules += Double(integerValue(channel, 0)) * Self.joulesPerUnit(unit)
            found = true
        }
        return found ? joules / elapsed : nil
    }

    private static func joulesPerUnit(_ unit: String) -> Double {
        switch unit.trimmingCharacters(in: .whitespaces) {
        case "mJ": return 1e-3
        case "uJ", "µJ": return 1e-6
        case "nJ": return 1e-9
        default: return 1
        }
    }
}

/// SoC die temperature from the IOHID sensors the kernel publishes.
final class ThermalReader {
    private typealias ClientCreate = @convention(c) (CFAllocator?) -> Unmanaged<AnyObject>?
    private typealias SetMatching = @convention(c) (AnyObject, CFDictionary) -> Int32
    private typealias CopyServices = @convention(c) (AnyObject) -> Unmanaged<CFArray>?
    private typealias CopyProperty = @convention(c) (AnyObject, CFString) -> Unmanaged<CFTypeRef>?
    private typealias CopyEvent = @convention(c) (AnyObject, Int64, Int32, Int64) -> Unmanaged<AnyObject>?
    private typealias FloatValue = @convention(c) (AnyObject, Int32) -> Double

    private static let temperatureEventType: Int64 = 15
    private static let temperatureField: Int32 = 15 << 16

    private var copyEvent: CopyEvent?
    private var floatValue: FloatValue?
    private var client: AnyObject?
    private var dieServices: [AnyObject] = []

    init() {
        guard let library = dlopen("/System/Library/Frameworks/IOKit.framework/IOKit", RTLD_NOW),
            let clientCreateSymbol = dlsym(library, "IOHIDEventSystemClientCreate"),
            let setMatchingSymbol = dlsym(library, "IOHIDEventSystemClientSetMatching"),
            let copyServicesSymbol = dlsym(library, "IOHIDEventSystemClientCopyServices"),
            let copyPropertySymbol = dlsym(library, "IOHIDServiceClientCopyProperty"),
            let copyEventSymbol = dlsym(library, "IOHIDServiceClientCopyEvent"),
            let floatValueSymbol = dlsym(library, "IOHIDEventGetFloatValue")
        else { return }

        let clientCreate = unsafeBitCast(clientCreateSymbol, to: ClientCreate.self)
        let setMatching = unsafeBitCast(setMatchingSymbol, to: SetMatching.self)
        let copyServices = unsafeBitCast(copyServicesSymbol, to: CopyServices.self)
        let copyProperty = unsafeBitCast(copyPropertySymbol, to: CopyProperty.self)
        copyEvent = unsafeBitCast(copyEventSymbol, to: CopyEvent.self)
        floatValue = unsafeBitCast(floatValueSymbol, to: FloatValue.self)

        guard let created = clientCreate(kCFAllocatorDefault)?.takeRetainedValue() else { return }
        client = created
        _ = setMatching(created, ["PrimaryUsagePage": 0xff00, "PrimaryUsage": 5] as CFDictionary)
        let services = (copyServices(created)?.takeRetainedValue() as? [AnyObject]) ?? []
        dieServices = services.filter { service in
            let product = copyProperty(service, "Product" as CFString)?.takeRetainedValue() as? String
            return product?.hasPrefix("PMU tdie") == true
        }
    }

    func hottestDie() -> Double? {
        guard let copyEvent, let floatValue else { return nil }
        let readings = dieServices.compactMap { service -> Double? in
            guard let event = copyEvent(service, Self.temperatureEventType, 0, 0)?.takeRetainedValue() else {
                return nil
            }
            let value = floatValue(event, Self.temperatureField)
            return value > 0 && value < 150 ? value : nil
        }
        return readings.max()
    }
}
