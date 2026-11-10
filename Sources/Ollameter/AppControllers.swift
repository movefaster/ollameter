import AppKit
import MeterCore
import SwiftUI

@MainActor
final class StatusItemController: NSObject {
    private(set) var item: NSStatusItem
    private var lastPresentation: DisplayState?
    private var lastAppearance: NSAppearance.Name?
    private let isPanelVisible: () -> Bool
    private let togglePanel: () -> Void

    init(isPanelVisible: @escaping () -> Bool, togglePanel: @escaping () -> Void) {
        item = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        self.isPanelVisible = isPanelVisible
        self.togglePanel = togglePanel
        super.init()
        guard let button = item.button else { return }
        button.imagePosition = .imageOnly
        button.target = self
        button.action = #selector(clicked)
        button.sendAction(on: [.leftMouseUp, .rightMouseUp])
    }

    func update(_ presentation: DisplayState) {
        guard let button = item.button else { return }
        let appearance = button.effectiveAppearance
        let appearanceName = appearance.bestMatch(from: [.darkAqua, .aqua])
        guard presentation != lastPresentation || appearanceName != lastAppearance else { return }
        lastPresentation = presentation
        lastAppearance = appearanceName
        button.image = MenuBarRenderer.image(for: presentation, appearance: appearance)
        button.toolTip = presentation.summary
        button.setAccessibilityLabel("Ollama: \(presentation.summary)")
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
        let menu = NSMenu()
        let toggle = NSMenuItem(
            title: isPanelVisible() ? "Hide Window" : "Show Window", action: #selector(toggleFromMenu), keyEquivalent: "")
        toggle.target = self
        menu.addItem(toggle)
        menu.addItem(.separator())
        menu.addItem(NSMenuItem(title: "Quit Ollameter", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q"))
        item.menu = menu
        item.button?.performClick(nil)
        item.menu = nil
    }

    @objc private func toggleFromMenu() {
        togglePanel()
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
    private let monitor: Monitor

    init(monitor: Monitor) {
        self.monitor = monitor
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
            monitor: monitor,
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
        monitor.wantsDetail = visible
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
    private let selfTestDirectory: URL?
    private let selfTestSeconds: Double
    private let selfTestUnloadModel: String?
    private var monitor: Monitor?
    private var panelController: PanelController?
    private var statusController: StatusItemController?

    init(selfTestDirectory: URL?, selfTestSeconds: Double, selfTestUnloadModel: String?) {
        self.selfTestDirectory = selfTestDirectory
        self.selfTestSeconds = selfTestSeconds
        self.selfTestUnloadModel = selfTestUnloadModel
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        let monitor = Monitor()
        let panelController = PanelController(monitor: monitor)
        let statusController = StatusItemController(
            isPanelVisible: { [weak panelController] in panelController?.isVisible ?? false },
            togglePanel: { [weak panelController] in panelController?.toggle() })
        monitor.onUpdate = { [weak statusController] model in statusController?.update(model.display) }
        self.monitor = monitor
        self.panelController = panelController
        self.statusController = statusController

        panelController.restoreVisibility()
        monitor.start()

        if let selfTestDirectory {
            SelfTest.schedule(
                directory: selfTestDirectory, seconds: selfTestSeconds, unloadModel: selfTestUnloadModel, monitor: monitor,
                panel: panelController.panel, statusItem: statusController.item)
        }
    }
}
