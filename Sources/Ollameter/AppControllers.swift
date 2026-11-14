import AppKit
import MeterCore
import SwiftUI

@MainActor
final class StatusItemController: NSObject {
    private struct Shown: Equatable {
        var display: DisplayState
        var lines: [String]
        var appearance: NSAppearance.Name?
    }

    private(set) var item: NSStatusItem
    private var lastShown: Shown?
    private weak var openMenu: NSMenu?
    private let hub: MeterHub
    private let isPanelVisible: () -> Bool
    private let togglePanel: () -> Void
    private let isOnTop: () -> Bool
    private let setOnTop: (Bool) -> Void

    init(
        hub: MeterHub, isPanelVisible: @escaping () -> Bool, togglePanel: @escaping () -> Void,
        isOnTop: @escaping () -> Bool, setOnTop: @escaping (Bool) -> Void
    ) {
        item = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        self.hub = hub
        self.isPanelVisible = isPanelVisible
        self.togglePanel = togglePanel
        self.isOnTop = isOnTop
        self.setOnTop = setOnTop
        super.init()
        guard let button = item.button else { return }
        button.imagePosition = .imageOnly
        button.target = self
        button.action = #selector(clicked)
        button.sendAction(on: [.leftMouseUp, .rightMouseUp])
    }

    /// The icon follows the busiest Mac. The tooltip has a line for each one.
    func update() {
        guard let button = item.button else { return }
        let appearance = button.effectiveAppearance
        let models = hub.models
        let lines: [String]
        if models.count == 1, models[0].remote == nil {
            lines = [models[0].display.summary]
        } else {
            // A connection problem already names the Mac.
            lines = models.map { model in
                model.remote?.problem ?? "\(model.remote?.host ?? Sharing.hostName): \(model.display.summary)"
            }
        }
        let primary = hub.primary.display
        let shown = Shown(display: primary, lines: lines, appearance: appearance.bestMatch(from: [.darkAqua, .aqua]))
        guard shown != lastShown else { return }
        lastShown = shown
        button.image = MenuBarRenderer.image(for: primary, appearance: appearance)
        button.toolTip = lines.joined(separator: "\n")
        button.setAccessibilityLabel("Ollama: " + lines.joined(separator: ". "))
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
        let onTop = action("Keep Window on Top", #selector(toggleOnTop))
        onTop.state = isOnTop() ? .on : .off
        onTop.toolTip = "Keeps the window above other apps' windows. Turn it off to let them cover it."
        menu.addItem(onTop)
        menu.addItem(.separator())

        menu.addItem(.sectionHeader(title: "Show Stats From"))
        var listed: [MeterSource] = [.local] + hub.peers.map { .service(name: $0) }
        // Macs being watched stay listed while Bonjour can't see them, or when they were typed in.
        listed += hub.sources.filter { !listed.contains($0) }
        for source in listed {
            menu.addItem(sourceItem(source))
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
        let watched = hub.sources.contains(source)
        item.state = watched ? .on : .off
        if watched, hub.sources.count == 1 {
            item.toolTip = "Pick another Mac before turning this one off."
        }
        return item
    }

    @objc private func toggleFromMenu() {
        togglePanel()
    }

    @objc private func toggleOnTop() {
        setOnTop(!isOnTop())
    }

    @objc private func chooseSource(_ sender: NSMenuItem) {
        guard let value = sender.representedObject as? String, let source = MeterSource(storageValue: value) else { return }
        hub.toggle(source)
    }

    @objc private func toggleSharing() {
        hub.setSharing(!hub.isSharing)
    }

    @objc private func enterAddress() {
        var text = ""
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
                hub.add(source)
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
    private static let onTopKey = "alwaysOnTop"
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
        setAlwaysOnTop(UserDefaults.standard.object(forKey: Self.onTopKey) as? Bool ?? true, persist: false)
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
            onSizeChange: { [weak self] size in self?.fit(size) })
        let host = FirstMouseHostingView(rootView: root)
        host.sizingOptions = []
        panel.contentView = host

        let saved = UserDefaults.standard.string(forKey: "NSWindow Frame \(Self.frameName)")
        if saved != nil {
            panel.setFrameUsingName(Self.frameName)
        } else if let screen = NSScreen.main {
            let area = screen.visibleFrame
            panel.setFrameOrigin(
                NSPoint(
                    x: area.maxX - panel.frame.width - Self.screenMargin,
                    y: area.maxY - panel.frame.height - Self.screenMargin))
        }
        panel.setFrameAutosaveName(Self.frameName)
        // AppKit moves a window saved on a second screen to the main one when that screen's usable area
        // differs at all from when it was saved, so a spot that's still on a screen is put back as is.
        if let saved, let frame = Self.frame(from: saved), Self.screen(containing: frame) != nil {
            panel.setFrame(frame, display: false)
        }
    }

    /// The window's own rectangle from AppKit's saved string, which goes on to describe the screen.
    private static func frame(from saved: String) -> NSRect? {
        let numbers = saved.split(separator: " ").compactMap { Double($0) }
        guard numbers.count >= 4, numbers[2] > 0, numbers[3] > 0 else { return nil }
        return NSRect(x: numbers[0], y: numbers[1], width: numbers[2], height: numbers[3])
    }

    /// The screen holding the top of the window, where its buttons are.
    private static func screen(containing frame: NSRect) -> NSScreen? {
        let top = NSRect(x: frame.minX, y: frame.maxY - 24, width: frame.width, height: 24)
        return NSScreen.screens.max { first, second in
            first.frame.intersection(top).width < second.frame.intersection(top).width
        }.flatMap { $0.frame.intersects(top) ? $0 : nil }
    }

    var isVisible: Bool { panel.isVisible }

    var isAlwaysOnTop: Bool { panel.level == .floating }

    /// On by default. Off, the window sits among other windows and goes behind them like any other.
    func setAlwaysOnTop(_ onTop: Bool, persist: Bool = true) {
        if persist { UserDefaults.standard.set(onTop, forKey: Self.onTopKey) }
        panel.isFloatingPanel = onTop
        panel.level = onTop ? .floating : .normal
    }

    func restoreVisibility() {
        setVisible(UserDefaults.standard.object(forKey: Self.visibleKey) as? Bool ?? true)
    }

    func toggle() {
        // When it isn't kept on top and other windows cover it completely, a click brings it forward
        // rather than hiding a window nobody can see.
        if panel.isVisible, !isAlwaysOnTop, !panel.occlusionState.contains(.visible) {
            panel.orderFrontRegardless()
            return
        }
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

    /// Keeps the top edge in place as the content grows or shrinks. Columns for more Macs grow away
    /// from the nearer side of the screen, and the window stays on screen.
    private func fit(_ size: CGSize) {
        guard size.width > 0, size.height > 0 else { return }
        var frame = panel.frame
        guard abs(frame.width - size.width) > 0.5 || abs(frame.height - size.height) > 0.5 else { return }
        let area = (Self.screen(containing: frame) ?? panel.screen ?? NSScreen.main)?.visibleFrame
        if let area, frame.midX > area.midX {
            frame.origin.x = frame.maxX - size.width
        }
        frame.origin.y = frame.maxY - size.height
        frame.size = size
        if let area {
            frame.origin.x = min(max(frame.origin.x, area.minX), max(area.maxX - frame.width, area.minX))
        }
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
        var sources: [MeterSource]?
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
        let hub = MeterHub(monitor: Monitor(), sourcesOverride: options.sources, sharingOverride: options.sharing)
        let panelController = PanelController(hub: hub)
        let statusController = StatusItemController(
            hub: hub,
            isPanelVisible: { [weak panelController] in panelController?.isVisible ?? false },
            togglePanel: { [weak panelController] in panelController?.toggle() },
            isOnTop: { [weak panelController] in panelController?.isAlwaysOnTop ?? true },
            setOnTop: { [weak panelController] onTop in panelController?.setAlwaysOnTop(onTop) })
        hub.onUpdate = { [weak statusController] in statusController?.update() }
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
                menu: { [weak statusController] in statusController?.menuSnapshot() ?? [] },
                setOnTop: { [weak panelController] onTop in panelController?.setAlwaysOnTop(onTop, persist: false) })
        }
    }
}
