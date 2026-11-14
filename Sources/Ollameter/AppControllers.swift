import AppKit
import MeterCore
import SwiftUI

@MainActor
final class StatusItemController: NSObject {
    private struct Shown: Equatable {
        var display: DisplayState
        var host: String?
        var appearance: NSAppearance.Name?
    }

    private(set) var item: NSStatusItem
    private var lastShown: Shown?
    private weak var openMenu: NSMenu?
    private let hub: MeterHub
    private let isPanelVisible: () -> Bool
    private let togglePanel: () -> Void

    init(hub: MeterHub, isPanelVisible: @escaping () -> Bool, togglePanel: @escaping () -> Void) {
        item = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        self.hub = hub
        self.isPanelVisible = isPanelVisible
        self.togglePanel = togglePanel
        super.init()
        guard let button = item.button else { return }
        button.imagePosition = .imageOnly
        button.target = self
        button.action = #selector(clicked)
        button.sendAction(on: [.leftMouseUp, .rightMouseUp])
    }

    func update(_ model: MeterViewModel) {
        guard let button = item.button else { return }
        let appearance = button.effectiveAppearance
        let shown = Shown(
            display: model.display, host: model.remote?.host,
            appearance: appearance.bestMatch(from: [.darkAqua, .aqua]))
        guard shown != lastShown else { return }
        lastShown = shown
        let presentation = model.display
        button.image = MenuBarRenderer.image(for: presentation, appearance: appearance)
        if let remote = model.remote {
            // A connection problem's summary already names the Mac.
            let summary = remote.problem ?? "\(remote.host): \(presentation.summary)"
            button.toolTip = summary
            button.setAccessibilityLabel("Ollama on \(remote.host): \(remote.problem ?? presentation.summary)")
        } else {
            button.toolTip = presentation.summary
            button.setAccessibilityLabel("Ollama: \(presentation.summary)")
        }
    }

    /// The menu's items as text, for the self-test.
    func menuSnapshot() -> [String] {
        let menu = NSMenu()
        menu.autoenablesItems = false
        populate(menu)
        return menu.items.map { item in
            if item.isSeparatorItem { return "---" }
            let mark = item.state == .on ? "[x] " : item.action == nil && !item.isSectionHeader ? "(disabled) " : ""
            return mark + item.title
        }
    }

    /// Rebuilds the menu while it's open, as Macs are found or sharing starts.
    func networkChanged() {
        if let openMenu { populate(openMenu) }
    }

    @objc private func clicked() {
        let event = NSApp.currentEvent
        if event?.type == .rightMouseUp || event?.modifierFlags.contains(.control) == true {
            showMenu()
        } else {
            togglePanel()
        }
    }

    private func showMenu() {
        hub.browser.start()
        let menu = NSMenu()
        menu.autoenablesItems = false
        populate(menu)
        openMenu = menu
        item.menu = menu
        item.button?.performClick(nil)
        item.menu = nil
        openMenu = nil
    }

    private func populate(_ menu: NSMenu) {
        menu.removeAllItems()
        menu.addItem(action(isPanelVisible() ? "Hide Window" : "Show Window", #selector(toggleFromMenu)))
        menu.addItem(.separator())

        menu.addItem(.sectionHeader(title: "Show Stats From"))
        menu.addItem(sourceItem(.local))
        var listed: [MeterSource] = [.local]
        for name in hub.peers {
            let source = MeterSource.service(name: name)
            menu.addItem(sourceItem(source))
            listed.append(source)
        }
        // The Mac being followed stays listed while Bonjour can't see it, or when it was typed in.
        if !listed.contains(hub.source) {
            menu.addItem(sourceItem(hub.source))
        }
        if hub.peers.isEmpty {
            let none = NSMenuItem(title: "No other Macs found sharing", action: nil, keyEquivalent: "")
            none.isEnabled = false
            menu.addItem(none)
        }
        menu.addItem(action("Other Mac…", #selector(enterAddress)))
        menu.addItem(.separator())

        let sharing = action("Share on Local Network", #selector(toggleSharing))
        sharing.state = hub.isSharing ? .on : .off
        sharing.toolTip = "Lets other Macs on this network watch this one's stats. They can't change anything here."
        menu.addItem(sharing)
        if let status = hub.sharingStatus {
            let line = NSMenuItem(title: status, action: nil, keyEquivalent: "")
            line.isEnabled = false
            menu.addItem(line)
        }
        menu.addItem(.separator())
        menu.addItem(NSMenuItem(title: "Quit Ollameter", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q"))
    }

    private func action(_ title: String, _ selector: Selector) -> NSMenuItem {
        let item = NSMenuItem(title: title, action: selector, keyEquivalent: "")
        item.target = self
        return item
    }

    private func sourceItem(_ source: MeterSource) -> NSMenuItem {
        let item = action(source.displayName, #selector(chooseSource(_:)))
        item.representedObject = source.storageValue
        item.state = source == hub.source ? .on : .off
        return item
    }

    @objc private func toggleFromMenu() {
        togglePanel()
    }

    @objc private func chooseSource(_ sender: NSMenuItem) {
        guard let value = sender.representedObject as? String, let source = MeterSource(storageValue: value) else { return }
        hub.select(source)
    }

    @objc private func toggleSharing() {
        hub.setSharing(!hub.isSharing)
    }

    @objc private func enterAddress() {
        var text = ""
        if case .address = hub.source { text = hub.source.displayName }
        var problem: String?
        while true {
            let alert = NSAlert()
            alert.messageText = problem ?? "Watch Another Mac"
            alert.informativeText =
                "Enter the name or IP address of a Mac that has Share on Local Network turned on, such as "
                + "studio.local or 192.168.1.20. Add a port after a colon if it isn't \(MeterSource.defaultPort)."
            let field = NSTextField(frame: NSRect(x: 0, y: 0, width: 280, height: 24))
            field.placeholderString = "studio.local"
            field.stringValue = text
            alert.accessoryView = field
            alert.addButton(withTitle: "Connect")
            alert.addButton(withTitle: "Cancel")
            alert.window.initialFirstResponder = field
            NSApp.activate(ignoringOtherApps: true)
            guard alert.runModal() == .alertFirstButtonReturn else { return }
            text = field.stringValue
            if let source = MeterSource.address(text) {
                hub.select(source)
                return
            }
            problem = "That isn't a name or address Ollameter can use."
        }
    }
}

/// A view that takes clicks even while its panel isn't key, so one click always presses a button.
final class FirstMouseHostingView<Content: View>: NSHostingView<Content> {
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
}

@MainActor
final class PanelController: NSObject, NSWindowDelegate {
    private static let frameName = "OllameterPanel"
    private static let visibleKey = "panelVisible"
    private static let screenMargin: CGFloat = 12

    let panel: NSPanel
    private let hub: MeterHub

    init(hub: MeterHub) {
        self.hub = hub
        panel = NSPanel(
            contentRect: NSRect(x: 0, y: 0, width: MeterLayout.width, height: 600),
            styleMask: [.titled, .closable, .fullSizeContentView, .nonactivatingPanel],
            backing: .buffered, defer: false)
        super.init()

        panel.titleVisibility = .hidden
        panel.titlebarAppearsTransparent = true
        panel.isFloatingPanel = true
        panel.level = .floating
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        panel.hidesOnDeactivate = false
        panel.isMovableByWindowBackground = true
        panel.isReleasedWhenClosed = false
        panel.delegate = self
        for button in [NSWindow.ButtonType.closeButton, .miniaturizeButton, .zoomButton] {
            panel.standardWindowButton(button)?.isHidden = true
        }

        let root = MeterWindowRoot(
            hub: hub,
            onClose: { [weak self] in self?.setVisible(false) },
            onHeightChange: { [weak self] height in self?.fit(height: height) })
        let host = FirstMouseHostingView(rootView: root)
        host.sizingOptions = []
        panel.contentView = host

        if UserDefaults.standard.string(forKey: "NSWindow Frame \(Self.frameName)") != nil {
            panel.setFrameUsingName(Self.frameName)
        } else if let screen = NSScreen.main {
            let area = screen.visibleFrame
            panel.setFrameOrigin(
                NSPoint(
                    x: area.maxX - panel.frame.width - Self.screenMargin,
                    y: area.maxY - panel.frame.height - Self.screenMargin))
        }
        panel.setFrameAutosaveName(Self.frameName)
    }

    var isVisible: Bool { panel.isVisible }

    func restoreVisibility() {
        setVisible(UserDefaults.standard.object(forKey: Self.visibleKey) as? Bool ?? true)
    }

    func toggle() {
        setVisible(!panel.isVisible)
    }

    private func setVisible(_ visible: Bool) {
        hub.isPanelVisible = visible
        UserDefaults.standard.set(visible, forKey: Self.visibleKey)
        if visible {
            panel.orderFrontRegardless()
        } else {
            panel.orderOut(nil)
        }
    }

    /// Keeps the top edge in place as the content grows or shrinks.
    private func fit(height: CGFloat) {
        guard height > 0, abs(panel.frame.height - height) > 0.5 else { return }
        var frame = panel.frame
        frame.origin.y = frame.maxY - height
        frame.size.height = height
        panel.setFrame(frame, display: true)
    }
}

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    struct Options {
        var selfTestDirectory: URL?
        var selfTestSeconds: Double = 8
        var selfTestUnloadModel: String?
        /// For this run only: whose numbers to show, and whether to share.
        var source: MeterSource?
        var sharing: Bool?
    }

    private let options: Options
    private var hub: MeterHub?
    private var panelController: PanelController?
    private var statusController: StatusItemController?

    init(options: Options) {
        self.options = options
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        let hub = MeterHub(monitor: Monitor(), sourceOverride: options.source, sharingOverride: options.sharing)
        let panelController = PanelController(hub: hub)
        let statusController = StatusItemController(
            hub: hub,
            isPanelVisible: { [weak panelController] in panelController?.isVisible ?? false },
            togglePanel: { [weak panelController] in panelController?.toggle() })
        hub.onUpdate = { [weak statusController] model in statusController?.update(model) }
        hub.onNetworkChange = { [weak statusController] in statusController?.networkChanged() }
        self.hub = hub
        self.panelController = panelController
        self.statusController = statusController

        panelController.restoreVisibility()
        hub.start()

        if let directory = options.selfTestDirectory {
            SelfTest.schedule(
                directory: directory, seconds: options.selfTestSeconds, unloadModel: options.selfTestUnloadModel,
                hub: hub, panel: panelController.panel, statusItem: statusController.item,
                menu: { [weak statusController] in statusController?.menuSnapshot() ?? [] })
        }
    }
}
