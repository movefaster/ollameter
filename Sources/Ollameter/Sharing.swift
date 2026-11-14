import Foundation
import MeterCore
import Network
import SystemConfiguration

enum Sharing {
    static let serviceType = "_ollameter._tcp"

    /// The Mac's short local name, such as `studio` for `studio.local`.
    static let hostName: String = {
        if let name = SCDynamicStoreCopyLocalHostName(nil) as String?, !name.isEmpty { return name }
        let name = ProcessInfo.processInfo.hostName
        return name.hasSuffix(".local") ? String(name.dropLast(".local".count)) : name
    }()

    static var appVersion: String? {
        Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String
    }
}

/// Serves this Mac's numbers to other Macs, read-only, and advertises them with Bonjour.
///
/// `GET /v1/state` returns the latest update as JSON. `GET /v1/events` streams every update as
/// server-sent events, a few per second while a model works and one a second otherwise.
@MainActor
final class ShareServer {
    private static let connectionLimit = 32
    private static let requestTimeout: Duration = .seconds(10)
    // Each update replaces the last, so a reader that falls behind skips updates rather than queuing them.
    private static let unsentLimit = 2

    private let host = Sharing.hostName
    private var listener: NWListener?
    private var connections: [ObjectIdentifier: NWConnection] = [:]
    private var streams: [ObjectIdentifier: NWConnection] = [:]
    private var unsent: [ObjectIdentifier: Int] = [:]
    private var latest: MeterViewModel?
    private var triedFallbackPort = false

    private(set) var port: UInt16?
    private(set) var advertisedName: String?
    var isRunning: Bool { listener != nil }
    var hasViewers: Bool { !streams.isEmpty }
    /// Runs when the port, the advertised name or the number of viewers changes.
    var onChange: (() -> Void)?

    func start() {
        guard listener == nil else { return }
        triedFallbackPort = false
        listen(on: NWEndpoint.Port(rawValue: MeterSource.defaultPort) ?? .any)
    }

    func stop() {
        listener?.cancel()
        listener = nil
        for connection in connections.values { connection.cancel() }
        connections.removeAll()
        streams.removeAll()
        unsent.removeAll()
        port = nil
        advertisedName = nil
        onChange?()
    }

    func publish(_ model: MeterViewModel) {
        latest = model
        guard !streams.isEmpty, let data = frameData() else { return }
        var event = Data("data: ".utf8)
        event.append(data)
        event.append(Data("\n\n".utf8))
        for (id, connection) in streams where unsent[id, default: 0] < Self.unsentLimit {
            send(event, on: connection, id: id)
        }
    }

    private func frameData() -> Data? {
        guard let latest else { return nil }
        return SharedFrame(host: host, app: Sharing.appVersion, model: latest).encoded()
    }

    private func listen(on port: NWEndpoint.Port) {
        guard let listener = try? NWListener(using: .tcp, on: port) else {
            if port != .any { listen(on: .any) }
            return
        }
        self.listener = listener
        listener.service = NWListener.Service(name: host, type: Sharing.serviceType)
        listener.stateUpdateHandler = { [weak self, weak listener] state in
            MainActor.assumeIsolated {
                guard let self, let listener, self.listener === listener else { return }
                switch state {
                case .ready:
                    self.port = listener.port?.rawValue
                    self.onChange?()
                case .failed:
                    // Usually the usual port is taken, so any free one will do. Bonjour still finds it.
                    listener.cancel()
                    self.listener = nil
                    self.port = nil
                    if !self.triedFallbackPort {
                        self.triedFallbackPort = true
                        self.listen(on: .any)
                    } else {
                        self.onChange?()
                    }
                default:
                    break
                }
            }
        }
        listener.serviceRegistrationUpdateHandler = { [weak self] change in
            MainActor.assumeIsolated {
                guard let self else { return }
                switch change {
                case .add(let endpoint):
                    if case .service(let name, _, _, _) = endpoint { self.advertisedName = name }
                case .remove:
                    self.advertisedName = nil
                @unknown default:
                    break
                }
                self.onChange?()
            }
        }
        listener.newConnectionHandler = { [weak self] connection in
            MainActor.assumeIsolated { self?.accept(connection) }
        }
        listener.start(queue: .main)
    }

    private func accept(_ connection: NWConnection) {
        guard connections.count < Self.connectionLimit else {
            connection.cancel()
            return
        }
        let id = ObjectIdentifier(connection)
        connections[id] = connection
        connection.stateUpdateHandler = { [weak self] state in
            switch state {
            case .failed, .cancelled:
                MainActor.assumeIsolated { self?.forget(id) }
            default:
                break
            }
        }
        connection.start(queue: .main)
        readRequest(on: connection, buffer: Data())
        // A client that never finishes its request doesn't get to hold the connection.
        Task { [weak self, weak connection] in
            try? await Task.sleep(for: Self.requestTimeout)
            guard let self, let connection, self.streams[id] == nil, self.connections[id] != nil else { return }
            connection.cancel()
        }
    }

    private func forget(_ id: ObjectIdentifier) {
        connections[id] = nil
        unsent[id] = nil
        if streams.removeValue(forKey: id) != nil { onChange?() }
    }

    private func readRequest(on connection: NWConnection, buffer: Data) {
        connection.receive(minimumIncompleteLength: 1, maximumLength: 8192) { [weak self] data, _, isComplete, error in
            MainActor.assumeIsolated {
                guard let self else { return }
                var buffer = buffer
                if let data { buffer.append(data) }
                switch HTTPRequestHead.parse(buffer) {
                case .complete(let head):
                    self.respond(to: head, on: connection)
                case .malformed:
                    self.reply(400, "Bad Request", on: connection)
                case .incomplete:
                    if error != nil || isComplete {
                        connection.cancel()
                    } else {
                        self.readRequest(on: connection, buffer: buffer)
                    }
                }
            }
        }
    }

    private func respond(to head: HTTPRequestHead, on connection: NWConnection) {
        guard head.method == "GET" else {
            reply(405, "Method Not Allowed", on: connection)
            return
        }
        switch head.path {
        case "/v1/events":
            openStream(on: connection)
        case "/v1/state":
            if let data = frameData() {
                reply(200, "OK", type: "application/json", body: data, on: connection)
            } else {
                reply(503, "Service Unavailable", on: connection)
            }
        case "/":
            let text = "Ollameter on \(host). GET /v1/state for the latest update, or /v1/events to stream them.\n"
            reply(200, "OK", type: "text/plain; charset=utf-8", body: Data(text.utf8), on: connection)
        default:
            reply(404, "Not Found", on: connection)
        }
    }

    private func openStream(on connection: NWConnection) {
        let id = ObjectIdentifier(connection)
        // The body runs until either side closes the connection, so it has no length.
        let head = "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n"
        var first = Data(head.utf8)
        if let data = frameData() {
            first.append(Data("data: ".utf8))
            first.append(data)
            first.append(Data("\n\n".utf8))
        }
        streams[id] = connection
        send(first, on: connection, id: id)
        watchForClose(connection)
        onChange?()
    }

    private func send(_ data: Data, on connection: NWConnection, id: ObjectIdentifier) {
        unsent[id, default: 0] += 1
        connection.send(content: data, completion: .contentProcessed { [weak self] error in
            MainActor.assumeIsolated {
                guard let self else { return }
                if let count = self.unsent[id] { self.unsent[id] = count - 1 }
                if error != nil { connection.cancel() }
            }
        })
    }

    /// Readers send nothing after their request, so anything that arrives is the connection ending.
    private func watchForClose(_ connection: NWConnection) {
        connection.receive(minimumIncompleteLength: 1, maximumLength: 1024) { [weak self] _, _, isComplete, error in
            MainActor.assumeIsolated {
                if isComplete || error != nil {
                    connection.cancel()
                } else {
                    self?.watchForClose(connection)
                }
            }
        }
    }

    private func reply(
        _ code: Int, _ reason: String, type: String = "text/plain; charset=utf-8", body: Data? = nil,
        on connection: NWConnection
    ) {
        let body = body ?? Data("\(reason)\n".utf8)
        var data = Data(
            "HTTP/1.1 \(code) \(reason)\r\nContent-Type: \(type)\r\nContent-Length: \(body.count)\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n"
                .utf8)
        data.append(body)
        connection.send(content: data, isComplete: true, completion: .contentProcessed { _ in connection.cancel() })
    }
}

/// Follows another Mac's stream, and keeps reconnecting when it drops.
@MainActor
final class RemoteClient {
    private static let connectTimeout = 5
    private static let firstFrameLimit: Duration = .seconds(8)
    private static let stallLimit: TimeInterval = 6
    private static let retryDelays: [Double] = [1, 2, 4, 8, 15]
    private static let incompatibleRetry: Double = 60

    let source: MeterSource
    /// Every model it produces: the other Mac's numbers, or a placeholder while there are none.
    var onModel: ((MeterViewModel) -> Void)?

    private var host: String
    private var connection: NWConnection?
    private var parser = EventStreamParser()
    private var failures = 0
    private var lastFrame = Date.distantPast
    private var retry: Task<Void, Never>?
    private var watchdog: Task<Void, Never>?
    private var firstFrameDeadline: Task<Void, Never>?
    private var stopped = false
    private(set) var status = RemoteSource.Status.connecting

    init(source: MeterSource) {
        self.source = source
        host = source.displayName
    }

    func start() {
        stopped = false
        report(.connecting)
        connect()
        watchdog = Task { [weak self] in
            while !Task.isCancelled {
                try? await Task.sleep(for: .seconds(2))
                guard let self, !self.stopped else { return }
                if self.status == .connected, !self.isFresh { self.fail() }
            }
        }
    }

    func stop() {
        stopped = true
        retry?.cancel()
        watchdog?.cancel()
        firstFrameDeadline?.cancel()
        connection?.cancel()
        connection = nil
    }

    private var endpoint: NWEndpoint? {
        switch source {
        case .local:
            return nil
        case .service(let name):
            return .service(name: name, type: Sharing.serviceType, domain: "local.", interface: nil)
        case .address(let host, let port):
            guard let port = NWEndpoint.Port(rawValue: port) else { return nil }
            return .hostPort(host: NWEndpoint.Host(host), port: port)
        }
    }

    private func connect() {
        guard !stopped, let endpoint else { return }
        let tcp = NWProtocolTCP.Options()
        tcp.connectionTimeout = Self.connectTimeout
        let connection = NWConnection(to: endpoint, using: NWParameters(tls: nil, tcp: tcp))
        self.connection = connection
        parser = EventStreamParser()
        // Finding a Mac by name can stall without an error, such as while macOS asks for local network
        // access, so an attempt that hasn't brought an update in time is dropped and tried again.
        firstFrameDeadline?.cancel()
        firstFrameDeadline = Task { [weak self, weak connection] in
            try? await Task.sleep(for: Self.firstFrameLimit)
            guard !Task.isCancelled, let self, let connection, self.connection === connection else { return }
            self.fail()
        }
        connection.stateUpdateHandler = { [weak self, weak connection] state in
            MainActor.assumeIsolated {
                guard let self, let connection, self.connection === connection else { return }
                switch state {
                case .ready:
                    self.request(on: connection)
                case .waiting, .failed:
                    self.fail()
                default:
                    break
                }
            }
        }
        connection.start(queue: .main)
    }

    private func request(on connection: NWConnection) {
        let text = "GET /v1/events HTTP/1.1\r\nHost: \(source.displayName)\r\nAccept: text/event-stream\r\n\r\n"
        connection.send(content: Data(text.utf8), completion: .contentProcessed { _ in })
        receive(on: connection)
    }

    private func receive(on connection: NWConnection) {
        connection.receive(minimumIncompleteLength: 1, maximumLength: 256 * 1024) { [weak self] data, _, isComplete, error in
            MainActor.assumeIsolated {
                guard let self, self.connection === connection else { return }
                if let data, !data.isEmpty {
                    do {
                        for event in try self.parser.append(data) { self.handle(event) }
                    } catch {
                        self.fail()
                        return
                    }
                }
                guard self.connection === connection else { return }
                if isComplete || error != nil {
                    self.fail()
                } else {
                    self.receive(on: connection)
                }
            }
        }
    }

    private func handle(_ event: String) {
        switch SharedFrame.decode(Data(event.utf8)) {
        case .success(let frame):
            firstFrameDeadline?.cancel()
            firstFrameDeadline = nil
            failures = 0
            lastFrame = Date()
            host = frame.host
            status = .connected
            var model = frame.model
            model.remote = RemoteSource(host: frame.host, status: .connected)
            onModel?(model)
        case .failure(.otherFormat(_, let app)):
            connection?.cancel()
            connection = nil
            report(.incompatible(app: app))
            scheduleRetry(after: Self.incompatibleRetry)
        case .failure(.malformed):
            fail()
        }
    }

    private var isFresh: Bool { Date().timeIntervalSince(lastFrame) <= Self.stallLimit }

    private func fail() {
        guard !stopped else { return }
        connection?.cancel()
        connection = nil
        // A short drop, such as the other Mac restarting the app, leaves the last numbers up while this
        // reconnects. The watchdog takes them down once they're too old.
        if !(status == .connected && isFresh) { report(.unreachable) }
        let delay = Self.retryDelays[min(failures, Self.retryDelays.count - 1)]
        failures += 1
        scheduleRetry(after: delay)
    }

    private func scheduleRetry(after seconds: Double) {
        retry?.cancel()
        retry = Task { [weak self] in
            try? await Task.sleep(for: .seconds(seconds))
            guard !Task.isCancelled, let self, !self.stopped, self.connection == nil else { return }
            self.connect()
        }
    }

    private func report(_ status: RemoteSource.Status) {
        self.status = status
        onModel?(.placeholder(for: RemoteSource(host: host, status: status)))
    }
}

/// Finds other Macs sharing with Bonjour. It starts on first use, since browsing asks for permission.
@MainActor
final class PeerBrowser {
    private var browser: NWBrowser?
    private(set) var names: [String] = []
    var onChange: (() -> Void)?

    func start() {
        guard browser == nil else { return }
        let browser = NWBrowser(for: .bonjour(type: Sharing.serviceType, domain: nil), using: .tcp)
        self.browser = browser
        browser.browseResultsChangedHandler = { [weak self] results, _ in
            MainActor.assumeIsolated {
                guard let self else { return }
                let found = Set(results.compactMap { result -> String? in
                    if case .service(let name, _, _, _) = result.endpoint { return name }
                    return nil
                })
                let sorted = found.sorted { $0.localizedStandardCompare($1) == .orderedAscending }
                guard sorted != self.names else { return }
                self.names = sorted
                self.onChange?()
            }
        }
        browser.start(queue: .main)
    }
}
