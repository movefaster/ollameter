import Foundation

/// Where the window's numbers come from: this Mac, or another computer, Mac or PC, that shares its own.
public enum MeterSource: Equatable, Hashable, Sendable {
    case local
    /// A computer found with Bonjour, by its advertised name.
    case service(name: String)
    /// A computer reached by host name or IP address, for when Bonjour can't see it.
    case address(host: String, port: UInt16)

    public static let defaultPort: UInt16 = 11480

    /// The name to show before the other Mac has reported its own.
    public var displayName: String {
        switch self {
        case .local: return "This Mac"
        case .service(let name): return name
        case .address(let host, let port):
            guard port != Self.defaultPort else { return host }
            return host.contains(":") ? "[\(host)]:\(port)" : "\(host):\(port)"
        }
    }

    public var isLocal: Bool { self == .local }

    public var storageValue: String {
        switch self {
        case .local: return "local"
        case .service(let name): return "service:\(name)"
        case .address: return "address:\(displayName)"
        }
    }

    public init?(storageValue: String) {
        if storageValue == "local" {
            self = .local
        } else if storageValue.hasPrefix("service:"), storageValue.count > "service:".count {
            self = .service(name: String(storageValue.dropFirst("service:".count)))
        } else if storageValue.hasPrefix("address:"), let source = Self.address(String(storageValue.dropFirst("address:".count))) {
            self = source
        } else {
            return nil
        }
    }

    /// Reads what someone types to reach a Mac: `studio.local`, `192.168.1.20:11480`, `[fe80::1]:11480`,
    /// or a URL that names one of these.
    public static func address(_ text: String) -> MeterSource? {
        var rest = text.trimmingCharacters(in: .whitespacesAndNewlines)
        if let scheme = rest.range(of: "://") { rest = String(rest[scheme.upperBound...]) }
        if let slash = rest.firstIndex(of: "/") { rest = String(rest[..<slash]) }
        guard !rest.isEmpty, !rest.contains(where: \.isWhitespace) else { return nil }

        var host = rest
        var portText: Substring?
        if rest.hasPrefix("[") {
            guard let close = rest.firstIndex(of: "]") else { return nil }
            host = String(rest[rest.index(after: rest.startIndex)..<close])
            let after = rest[rest.index(after: close)...]
            if !after.isEmpty {
                guard after.hasPrefix(":") else { return nil }
                portText = after.dropFirst()
            }
        } else if rest.filter({ $0 == ":" }).count == 1, let colon = rest.firstIndex(of: ":") {
            host = String(rest[..<colon])
            portText = rest[rest.index(after: colon)...]
        }
        guard !host.isEmpty else { return nil }
        var port = defaultPort
        if let portText {
            guard let value = UInt16(portText), value > 0 else { return nil }
            port = value
        }
        return .address(host: host, port: port)
    }
}

/// How the window describes another Mac it's showing.
public struct RemoteSource: Equatable, Sendable {
    public enum Status: Equatable, Sendable {
        case connecting
        case connected
        /// The last attempt failed. The client keeps trying.
        case unreachable
        /// The other Mac runs a version of Ollameter whose data this one can't read.
        case incompatible(app: String?)
    }

    public var host: String
    public var status: Status

    public init(host: String, status: Status) {
        self.host = host
        self.status = status
    }

    /// What to say instead of the numbers when there are none to show.
    public var problem: String? {
        switch status {
        case .connected: return nil
        case .connecting: return "Connecting to \(host)…"
        case .unreachable: return "Can't reach \(host). Trying again…"
        case .incompatible(let app):
            let version = app.map { "Ollameter \($0)" } ?? "another version of Ollameter"
            return "\(host) runs \(version), which this version can't read."
        }
    }
}

extension MeterViewModel {
    /// Stands in for another Mac's numbers until they arrive, or while they can't.
    public static func placeholder(for remote: RemoteSource) -> MeterViewModel {
        var model = MeterViewModel()
        model.snapshot.phase = .offline
        model.display = DisplayState(
            fraction: 0, tint: .none, modelLoaded: false, text: nil, textDimmed: false,
            summary: remote.problem ?? "", displayPhase: .offline)
        model.remote = remote
        return model
    }
}

/// One update as it travels between computers. The Windows app in `windows/` reads and writes the same JSON.
public struct SharedFrame: Codable, Equatable, Sendable {
    public static let currentFormat = 1
    public static let macPlatform = "macos"
    public static let windowsPlatform = "windows"
    /// The window shows six recent requests. A few more leave room without sending all forty.
    public static let requestLimit = 10

    public enum DecodeFailure: Error, Equatable {
        case otherFormat(format: Int, app: String?)
        case malformed
    }

    public var format: Int
    public var host: String
    public var app: String?
    /// Added after format 1 shipped, so older Macs neither send nor need it.
    public var platform: String?
    public var model: MeterViewModel

    public init(host: String, app: String?, model: MeterViewModel, platform: String? = SharedFrame.macPlatform) {
        format = Self.currentFormat
        self.host = host
        self.app = app
        self.platform = platform
        var trimmed = model
        trimmed.requests = Array(model.requests.prefix(Self.requestLimit))
        self.model = trimmed
    }

    public func encoded() -> Data? {
        try? Self.encoder.encode(self)
    }

    public static func decode(_ data: Data) -> Result<SharedFrame, DecodeFailure> {
        guard let header = try? JSONDecoder().decode(Header.self, from: data) else { return .failure(.malformed) }
        guard header.format == currentFormat else {
            return .failure(.otherFormat(format: header.format, app: header.app))
        }
        guard let frame = try? decoder.decode(SharedFrame.self, from: data) else { return .failure(.malformed) }
        return .success(frame)
    }

    private struct Header: Decodable {
        var format: Int
        var app: String?
    }

    // A speed divided by a zero duration would otherwise stop the whole frame from encoding.
    private static let floats = (positiveInfinity: "inf", negativeInfinity: "-inf", nan: "nan")

    private static var encoder: JSONEncoder {
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .secondsSince1970
        encoder.nonConformingFloatEncodingStrategy = .convertToString(
            positiveInfinity: floats.positiveInfinity, negativeInfinity: floats.negativeInfinity, nan: floats.nan)
        return encoder
    }

    private static var decoder: JSONDecoder {
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .secondsSince1970
        decoder.nonConformingFloatDecodingStrategy = .convertFromString(
            positiveInfinity: floats.positiveInfinity, negativeInfinity: floats.negativeInfinity, nan: floats.nan)
        return decoder
    }
}

/// The request line of an HTTP request, which is all the sharing server needs.
public struct HTTPRequestHead: Equatable, Sendable {
    public enum Parse: Equatable, Sendable {
        case incomplete
        case malformed
        case complete(HTTPRequestHead)
    }

    public static let sizeLimit = 16 * 1024

    public var method: String
    /// Without any query string.
    public var path: String

    public init(method: String, path: String) {
        self.method = method
        self.path = path
    }

    public static func parse(_ data: Data) -> Parse {
        guard let end = data.range(of: Data("\r\n\r\n".utf8)) else {
            return data.count > sizeLimit ? .malformed : .incomplete
        }
        guard let text = String(data: data[data.startIndex..<end.lowerBound], encoding: .utf8),
            let line = text.split(separator: "\r\n", omittingEmptySubsequences: false).first
        else { return .malformed }
        let parts = line.split(separator: " ")
        guard parts.count == 3, parts[2].hasPrefix("HTTP/1."), parts[1].hasPrefix("/") else { return .malformed }
        let target = parts[1]
        let path = target.firstIndex(of: "?").map { target[..<$0] } ?? target
        return .complete(HTTPRequestHead(method: String(parts[0]), path: String(path)))
    }
}

/// Reads an HTTP response that carries server-sent events, as bytes arrive in any size of chunk.
public struct EventStreamParser: Sendable {
    public enum Failure: Error, Equatable {
        case status(Int)
        case malformed
        case tooLarge
    }

    public static let sizeLimit = 4 << 20

    private var buffer = Data()
    private var headRead = false

    public init() {}

    /// Returns the data of each event completed by `chunk`.
    public mutating func append(_ chunk: Data) throws -> [String] {
        if headRead {
            // JSON escapes carriage returns inside strings, so any left here are line endings.
            buffer.append(contentsOf: chunk.filter { $0 != 0x0D })
        } else {
            buffer.append(chunk)
            guard let end = buffer.range(of: Data("\r\n\r\n".utf8)) else {
                if buffer.count > HTTPRequestHead.sizeLimit { throw Failure.malformed }
                return []
            }
            let head = String(decoding: buffer[buffer.startIndex..<end.lowerBound], as: UTF8.self)
            let statusLine = head.split(separator: "\r\n", maxSplits: 1).first ?? ""
            let fields = statusLine.split(separator: " ")
            guard fields.count >= 2, fields[0].hasPrefix("HTTP/"), let code = Int(fields[1]) else {
                throw Failure.malformed
            }
            guard code == 200 else { throw Failure.status(code) }
            headRead = true
            let body = buffer[end.upperBound...].filter { $0 != 0x0D }
            buffer = Data(body)
        }

        var events: [String] = []
        let separator = Data("\n\n".utf8)
        while let boundary = buffer.range(of: separator) {
            let block = String(decoding: buffer[buffer.startIndex..<boundary.lowerBound], as: UTF8.self)
            buffer.removeSubrange(buffer.startIndex..<boundary.upperBound)
            let lines = block.split(separator: "\n", omittingEmptySubsequences: false).compactMap { line -> Substring? in
                guard line.hasPrefix("data:") else { return nil }
                let value = line.dropFirst("data:".count)
                return value.hasPrefix(" ") ? value.dropFirst() : value
            }
            if !lines.isEmpty { events.append(lines.joined(separator: "\n")) }
        }
        if buffer.count > Self.sizeLimit { throw Failure.tooLarge }
        return events
    }
}

extension MeterSource {
    /// The order sections appear in: this Mac first, then other Macs by name. Duplicates are dropped.
    public static func ordered(_ sources: [MeterSource]) -> [MeterSource] {
        var seen = Set<MeterSource>()
        return sources.filter { seen.insert($0).inserted }.sorted { first, second in
            if first.isLocal != second.isLocal { return first.isLocal }
            return first.displayName.localizedStandardCompare(second.displayName) == .orderedAscending
        }
    }
}

extension MeterViewModel {
    /// How much is happening, for choosing which Mac the menu bar icon follows.
    public var activityRank: Int {
        if remote?.problem != nil { return 0 }
        switch display.displayPhase {
        case .prefill, .generating, .loading: return 2
        case .idle: return 1
        case .offline: return 0
        }
    }

    /// The busiest of several Macs. The current one stays while nothing is busier, so the icon doesn't
    /// jump between Macs that are equally busy.
    public static func busiestIndex(of models: [MeterViewModel], keeping current: Int?) -> Int? {
        guard let best = models.map(\.activityRank).max() else { return nil }
        if let current, models.indices.contains(current), models[current].activityRank == best { return current }
        return models.firstIndex { $0.activityRank == best }
    }
}
