import Foundation
import MeterCore

/// Chooses whose numbers the menu bar and window show, and shares this Mac's when that's turned on.
/// This Mac keeps being measured while another is shown, so sharing carries on either way.
@MainActor
final class MeterHub: ObservableObject {
    static let sourceKey = "source"
    static let sharingKey = "sharing"

    @Published private(set) var model = MeterViewModel()
    private(set) var source: MeterSource
    private(set) var isSharing: Bool

    /// Runs on every update shown, whether or not the window is open.
    var onUpdate: ((MeterViewModel) -> Void)?
    /// Runs when the Macs found nearby or the state of sharing change.
    var onNetworkChange: (() -> Void)?
    var isPanelVisible = true {
        didSet { updateDetail() }
    }

    let monitor: Monitor
    let server = ShareServer()
    let browser = PeerBrowser()
    private var client: RemoteClient?

    /// Overrides apply to this run only and aren't saved.
    init(monitor: Monitor, sourceOverride: MeterSource? = nil, sharingOverride: Bool? = nil) {
        self.monitor = monitor
        let defaults = UserDefaults.standard
        source = sourceOverride
            ?? defaults.string(forKey: Self.sourceKey).flatMap(MeterSource.init(storageValue:))
            ?? .local
        isSharing = sharingOverride ?? defaults.bool(forKey: Self.sharingKey)
    }

    func start() {
        monitor.onUpdate = { [weak self] model in self?.localUpdated(model) }
        server.onChange = { [weak self] in
            self?.updateDetail()
            self?.onNetworkChange?()
        }
        browser.onChange = { [weak self] in self?.onNetworkChange?() }
        monitor.start()
        if isSharing { server.start() }
        // Browsing asks for permission, so it waits until someone uses a network feature.
        if isSharing || !source.isLocal { browser.start() }
        follow(source)
    }

    /// Other Macs found sharing, without this one. Its own name also lingers for a moment after
    /// sharing stops, so that's left out too.
    var peers: [String] {
        let own = Sharing.hostName
        return browser.names.filter { $0 != server.advertisedName && $0 != own }
    }

    /// A line for the menu about how other Macs reach this one, when sharing is on.
    var sharingStatus: String? {
        guard isSharing else { return nil }
        guard server.isRunning else { return "Couldn't start sharing" }
        guard let port = server.port else { return "Starting…" }
        return "Others see this Mac as \(server.advertisedName ?? Sharing.hostName), port \(port)"
    }

    func select(_ newSource: MeterSource) {
        UserDefaults.standard.set(newSource.storageValue, forKey: Self.sourceKey)
        guard newSource != source else { return }
        source = newSource
        follow(newSource)
    }

    func setSharing(_ on: Bool, persist: Bool = true) {
        if persist { UserDefaults.standard.set(on, forKey: Self.sharingKey) }
        guard on != isSharing else { return }
        isSharing = on
        if on {
            server.publish(monitor.model)
            server.start()
            browser.start()
        } else {
            server.stop()
        }
        updateDetail()
        onNetworkChange?()
    }

    /// Only this Mac's models can be unloaded from here. Sharing is read-only.
    func pressUnload(model name: String) {
        guard source.isLocal else { return }
        monitor.pressUnload(model: name)
    }

    private func follow(_ source: MeterSource) {
        client?.stop()
        client = nil
        if source.isLocal {
            show(monitor.model)
        } else {
            let client = RemoteClient(source: source)
            client.onModel = { [weak self, weak client] model in
                guard let self, let client, self.client === client else { return }
                self.show(model)
            }
            self.client = client
            client.start()
        }
        updateDetail()
    }

    private func localUpdated(_ model: MeterViewModel) {
        if isSharing { server.publish(model) }
        if source.isLocal { show(model) }
    }

    private func show(_ model: MeterViewModel) {
        self.model = model
        onUpdate?(model)
    }

    /// Power, thermal and process readings are worth taking while someone can see them.
    private func updateDetail() {
        monitor.wantsDetail = (isPanelVisible && source.isLocal) || server.hasViewers
    }
}
