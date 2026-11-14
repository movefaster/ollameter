import AppKit
import MeterCore
import SwiftUI

/// Sample states for rendering the UI without needing a matching request in flight.
enum Fixtures {
    static func base() -> MeterViewModel {
        var model = MeterViewModel()
        let now = Date()
        model.now = now
        model.version = "0.40.16"
        model.models = [
            LoadedModel(
                name: "qwen3-next:80b", size: 48_210_330_812, sizeVRAM: 48_210_330_812,
                contextLength: 262_144, expiresAt: now.addingTimeInterval(272), parameterSize: "79.7B",
                quantization: "Q4_K_M")
        ]
        let requests: [(Int, Int, Double, Double, Int, Double, Double)] = [
            (57_451, 25, 265, 94.3, 321, 11_745, 27.2),
            (54_312, 641, 2_128, 301.2, 425, 15_449, 27.5),
            (49_291, 412, 1_498, 275.0, 188, 7_102, 26.5),
            (48_837, 1_880, 5_560, 338.1, 96, 3_711, 25.9),
            (25_600, 25_600, 76_190, 336.0, 512, 18_660, 27.4),
            (10_837, 10_837, 32_390, 334.6, 244, 8_911, 27.4),
            (10_329, 402, 1_702, 236.2, 67, 2_490, 26.9),
        ]
        var stats = SessionStats()
        for (index, entry) in requests.enumerated().reversed() {
            let request = CompletedRequest(
                sequence: index, task: 18_000 - index, finishedAt: now.addingTimeInterval(Double(index + 1) * -37),
                promptTokens: entry.0, evaluatedTokens: entry.1, promptMilliseconds: entry.2, promptTPS: entry.3,
                generatedTokens: entry.4, generationMilliseconds: entry.5, generationTPS: entry.6)
            stats.record(request)
            model.requests.insert(request, at: 0)
        }
        model.stats = stats
        model.hardware.gpuUtilization = 97
        model.hardware.gpuMemoryInUse = 76_281_577_472
        model.hardware.gpuPowerWatts = 16.9
        model.hardware.dieTemperature = 68
        model.hardware.swapUsed = 0
        model.hardware.memoryPressure = .normal
        model.runner = RunnerStats(cpuPercent: 18, residentBytes: 87_936_303_104)
        model.tpsHistory = (0..<60).map { index in index < 12 ? 0 : 26 + 2 * sin(Double(index) / 3) }
        model.gpuHistory = (0..<60).map { index in index < 12 ? 3 : 92 + 5 * sin(Double(index) / 4) }
        model.snapshot.contextSize = 262_144
        model.snapshot.contextUsed = 57_771
        model.promptCache = PromptCacheState(
            limitBytes: 25_769_803_776, usedBytes: 11_955_210_240, savedPrompts: 3,
            savedTokens: [90_120, 40_738, 15_202])
        return model
    }

    static func generating() -> MeterViewModel {
        var model = base()
        model.snapshot.phase = .generating
        model.snapshot.task = 18_484
        model.snapshot.liveTPS = 27.4
        model.snapshot.generatedTokens = 321
        model.snapshot.phaseElapsed = 11.7
        return model
    }

    static func prefill() -> MeterViewModel {
        var model = base()
        model.snapshot.phase = .prefill
        model.snapshot.task = 18_485
        model.snapshot.promptTokens = 54_287
        model.snapshot.promptCached = 39_381
        model.snapshot.promptDone = 6_144
        model.snapshot.promptToProcess = 14_906
        model.snapshot.promptFraction = (39_381.0 + 6_144) / 54_287
        model.snapshot.promptTPS = 301
        model.snapshot.etaSeconds = 29
        return model
    }

    static func idle() -> MeterViewModel {
        var model = base()
        model.snapshot.phase = .idle
        model.hardware.gpuUtilization = 4
        return model
    }

    static func idle(unload state: (String) -> UnloadState) -> MeterViewModel {
        var model = idle()
        model.unload = state(model.models[0].name)
        return model
    }

    static func remote(_ model: MeterViewModel, host: String = "studio", modelName: String? = nil) -> MeterViewModel {
        var model = model
        model.remote = RemoteSource(host: host, status: .connected)
        if let modelName, !model.models.isEmpty { model.models[0].name = modelName }
        return model
    }

    static func remote(_ status: RemoteSource.Status, host: String = "studio") -> MeterViewModel {
        .placeholder(for: RemoteSource(host: host, status: status))
    }

    static func offline() -> MeterViewModel {
        var model = MeterViewModel()
        model.snapshot.phase = .offline
        return model
    }

    /// Every optional section at once, to expose layout collisions.
    static func crowded() -> MeterViewModel {
        var model = generating()
        model.hardware.swapUsed = 3_221_225_472
        model.hardware.memoryPressure = .warning
        model.promptCache = PromptCacheState(
            limitBytes: 25_769_803_776, usedBytes: 23_622_320_128, savedPrompts: 9,
            savedTokens: [152_004, 90_120, 74_880, 61_233, 48_109, 40_738, 22_581, 15_202, 9_197], evictions: 12)
        model.models.append(
            LoadedModel(
                name: "nomic-embed-text:latest", size: 274_000_000, sizeVRAM: 200_000_000, contextLength: 8192,
                expiresAt: model.now.addingTimeInterval(95), parameterSize: "137M", quantization: "F16"))
        model.notices = [
            Notice(
                id: "truncated", severity: .serious,
                text: "A 23,010-token prompt was cut to 4,098 tokens. Raise the model's context length to avoid this.",
                at: model.now),
            Notice(
                id: "context-shift", severity: .warning, text: "The context filled up, so older tokens were dropped.",
                at: model.now),
        ]
        return model
    }

    static let menuStates: [DisplayState] = [
        DisplayState(fraction: 0.97, tint: .gpu, modelLoaded: true, text: "27 t/s", textDimmed: false, summary: "", displayPhase: .generating),
        DisplayState(fraction: 0.41, tint: .prompt, modelLoaded: true, text: "41%", textDimmed: false, summary: "", displayPhase: .prefill),
        DisplayState(fraction: 0.2, tint: .muted, modelLoaded: true, text: "27 t/s", textDimmed: true, summary: "", displayPhase: .generating),
        DisplayState(fraction: 0.65, tint: .gpu, modelLoaded: false, text: "load", textDimmed: false, summary: "", displayPhase: .loading),
        DisplayState(fraction: 0.04, tint: .muted, modelLoaded: true, text: nil, textDimmed: false, summary: "", displayPhase: .idle),
        DisplayState(fraction: 0, tint: .none, modelLoaded: false, text: nil, textDimmed: false, summary: "", displayPhase: .offline),
    ]
}

/// Writes PNGs of every state: `--render DIR`, plus `--live` to include real data from this Mac.
@MainActor
enum Renderer {
    static func run(directory: URL, live: Bool) {
        try? FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        let states: [(String, MeterViewModel, Bool)] = [
            ("generating", Fixtures.generating(), false),
            ("prefill", Fixtures.prefill(), false),
            ("idle", Fixtures.idle(), false),
            ("offline", Fixtures.offline(), false),
            ("crowded", Fixtures.crowded(), false),
            ("compact", Fixtures.generating(), true),
            ("compact-crowded", Fixtures.crowded(), true),
            ("compact-idle", Fixtures.idle(), true),
            ("compact-unload-confirm", Fixtures.idle { .confirming(model: $0) }, true),
            ("compact-unloading", Fixtures.idle { .unloading(model: $0) }, true),
            ("compact-unload-failed", Fixtures.idle { .failed(model: $0) }, true),
            ("remote", Fixtures.remote(Fixtures.generating()), false),
            ("remote-offline", Fixtures.remote(Fixtures.offline()), false),
            ("compact-remote", Fixtures.remote(Fixtures.generating()), true),
            ("compact-remote-idle", Fixtures.remote(Fixtures.idle()), true),
            ("compact-remote-connecting", Fixtures.remote(.connecting), true),
            ("compact-remote-unreachable", Fixtures.remote(.unreachable), true),
            ("compact-remote-incompatible", Fixtures.remote(.incompatible(app: "0.3.0")), true),
        ]
        // Several Macs at once: this one, a busy one, and a third in various states.
        let watching: [(String, [MeterViewModel], Bool)] = [
            ("multi", [Fixtures.idle(), Fixtures.remote(Fixtures.generating()), Fixtures.remote(Fixtures.prefill(), host: "mini", modelName: "llama3.3:70b")], false),
            ("multi-unreachable", [Fixtures.idle(), Fixtures.remote(Fixtures.generating()), Fixtures.remote(.unreachable, host: "mini")], false),
            ("compact-multi", [Fixtures.idle(), Fixtures.remote(Fixtures.generating()), Fixtures.remote(Fixtures.prefill(), host: "mini", modelName: "llama3.3:70b")], true),
            ("compact-multi-unreachable", [Fixtures.idle(), Fixtures.remote(Fixtures.generating()), Fixtures.remote(.unreachable, host: "mini")], true),
        ]
        for scheme in [ColorScheme.light, .dark] {
            for (name, fixture, compact) in states {
                render([presented(fixture)], compact: compact, scheme: scheme, to: directory, name: name)
            }
            for (name, fixtures, compact) in watching {
                render(fixtures.map(presented), compact: compact, scheme: scheme, to: directory, name: name)
            }
        }
        renderMenuBar(to: directory.appendingPathComponent("menubar.png"))

        if live {
            let monitor = Monitor()
            monitor.start()
            RunLoop.main.run(until: Date().addingTimeInterval(9))
            render([monitor.model], compact: false, scheme: .dark, to: directory, name: "live")
            print("live phase: \(monitor.model.snapshot.phase.rawValue)")
            print("live tps: \(monitor.model.snapshot.liveTPS.map(Format.tps) ?? "none")")
            print("live generated: \(monitor.model.snapshot.generatedTokens ?? -1)")
            print("live requests: \(monitor.model.stats.requests)")
            print("live model: \(monitor.model.models.first?.name ?? "none")")
            print("live gpu: \(monitor.model.hardware)")
            print("live runner: \(String(describing: monitor.model.runner))")
        }
    }

    /// A placeholder for a Mac that can't be reached already says what to show.
    private static func presented(_ fixture: MeterViewModel) -> MeterViewModel {
        guard fixture.remote?.problem == nil else { return fixture }
        var model = fixture
        var presenter = DisplayPresenter()
        model.display = presenter.present(model)
        return model
    }

    private static func render(
        _ models: [MeterViewModel], compact: Bool, scheme: ColorScheme, to directory: URL, name: String
    ) {
        let renderer = ImageRenderer(
            content: MeterBoard(models: models, compact: compact, localHost: "macbook")
                .fixedSize()
                .environment(\.colorScheme, scheme))
        renderer.scale = 2
        guard let image = renderer.cgImage else {
            print("render failed: \(name)")
            return
        }
        let suffix = scheme == .dark ? "dark" : "light"
        write(NSBitmapImageRep(cgImage: image), to: directory.appendingPathComponent("window-\(name)-\(suffix).png"))
    }

    /// Menu bar states over solid and saturated backgrounds, since the real bar is tinted by the wallpaper.
    private static func renderMenuBar(to url: URL) {
        let rows: [(NSColor, NSAppearance.Name)] = [
            (NSColor(srgbRed: 0.93, green: 0.93, blue: 0.93, alpha: 1), .aqua),
            (NSColor(srgbRed: 0.95, green: 0.70, blue: 0.45, alpha: 1), .aqua),
            (NSColor(srgbRed: 0.16, green: 0.16, blue: 0.17, alpha: 1), .darkAqua),
            (NSColor(srgbRed: 0.10, green: 0.22, blue: 0.42, alpha: 1), .darkAqua),
        ]
        let gap: CGFloat = 14
        let scale: CGFloat = 4
        let width = Fixtures.menuStates.reduce(gap) { $0 + MenuBarRenderer.size(for: $1).width + gap }
        let rowHeight = MenuBarRenderer.height + 6
        let height = rowHeight * CGFloat(rows.count)

        guard
            let bitmap = NSBitmapImageRep(
                bitmapDataPlanes: nil, pixelsWide: Int(width * scale), pixelsHigh: Int(height * scale),
                bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
                colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)
        else { return }
        // The context takes its scale from the bitmap's point size, so this has to be set first.
        bitmap.size = NSSize(width: width, height: height)
        guard let context = NSGraphicsContext(bitmapImageRep: bitmap) else { return }

        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = context
        for (index, row) in rows.enumerated() {
            let y = height - rowHeight * CGFloat(index + 1)
            row.0.setFill()
            NSRect(x: 0, y: y, width: width, height: rowHeight).fill()
            guard let appearance = NSAppearance(named: row.1) else { continue }
            var x = gap
            for state in Fixtures.menuStates {
                let image = MenuBarRenderer.image(for: state, appearance: appearance)
                image.draw(in: NSRect(origin: NSPoint(x: x, y: y + 3), size: image.size))
                x += image.size.width + gap
            }
        }
        NSGraphicsContext.restoreGraphicsState()
        write(bitmap, to: url)
    }

    static func write(_ bitmap: NSBitmapImageRep, to url: URL) {
        try? bitmap.representation(using: .png, properties: [:])?.write(to: url)
    }
}

/// Inspects the running app from the inside: `--self-test DIR [--seconds N]` records every published
/// frame, drives the real panel and status item, writes a report and images, then quits.
/// `--unload-test MODEL` also clicks the real unload button, and only if MODEL is the sole model loaded.
@MainActor
enum SelfTest {
    static func schedule(
        directory: URL, seconds: Double, unloadModel: String?, hub: MeterHub, panel: NSPanel,
        statusItem: NSStatusItem, menu: @escaping () -> [String], setOnTop: @escaping (Bool) -> Void
    ) {
        let monitor = hub.monitor
        try? FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        var trace = "seconds,snapshot,display,text,dimmed,liveTPS,panelHeight,footprintMB,task,promptFraction,remote\n"
        let start = Date()
        let forward = hub.onUpdate
        hub.onUpdate = {
            forward?()
            let model = hub.primary
            let elapsed = String(format: "%.2f", Date().timeIntervalSince(start))
            let tps = model.snapshot.liveTPS.map { String(format: "%.1f", $0) } ?? ""
            let display = model.display
            trace += "\(elapsed),\(model.snapshot.phase.rawValue),\(display.displayPhase.rawValue),"
                + "\(display.text ?? ""),\(display.textDimmed),\(tps),\(Int(panel.frame.height)),\(footprintMegabytes()),"
                + "\(model.snapshot.task.map(String.init) ?? ""),\(model.snapshot.promptFraction.map { String(format: "%.4f", $0) } ?? ""),"
                + hub.models.map { "\($0.remote?.host ?? "local"):\($0.remote.map { "\($0.status)" } ?? "ok")" }
                .joined(separator: ";") + "\n"
        }

        Task { @MainActor in
            try? await Task.sleep(for: .seconds(seconds))
            // The toggling below has to leave the window the way the user had it.
            let startedCompact = UserDefaults.standard.bool(forKey: "compact")
            if startedCompact {
                UserDefaults.standard.set(false, forKey: "compact")
                try? await Task.sleep(for: .seconds(1))
            }
            let button = statusItem.button
            capture(panel.contentView, to: directory.appendingPathComponent("panel.png"))
            if let button, let image = button.image {
                captureStatusImage(image, appearance: button.effectiveAppearance, to: directory.appendingPathComponent("status.png"))
            }

            let expanded = panel.frame
            UserDefaults.standard.set(true, forKey: "compact")
            try? await Task.sleep(for: .seconds(1))
            let compact = panel.frame
            capture(panel.contentView, to: directory.appendingPathComponent("panel-compact.png"))
            UserDefaults.standard.set(false, forKey: "compact")
            try? await Task.sleep(for: .seconds(1))
            let restored = panel.frame

            button?.performClick(nil)
            try? await Task.sleep(for: .milliseconds(300))
            let hiddenByClick = !panel.isVisible
            button?.performClick(nil)
            try? await Task.sleep(for: .milliseconds(300))
            let shownByClick = panel.isVisible
            UserDefaults.standard.set(startedCompact, forKey: "compact")
            try? await Task.sleep(for: .seconds(1))
            // The menu's on-top item, without saving the change.
            let startedOnTop = panel.level == .floating
            var onTopToggle: [String: Any] = ["startedOnTop": startedOnTop]
            setOnTop(!startedOnTop)
            onTopToggle["flippedLevel"] = panel.level.rawValue
            onTopToggle["flippedFloating"] = panel.isFloatingPanel
            onTopToggle["menuWhileFlipped"] = menu().filter { $0.contains("on Top") }
            setOnTop(startedOnTop)
            onTopToggle["restoredLevel"] = panel.level.rawValue
            onTopToggle["menuRestored"] = menu().filter { $0.contains("on Top") }

            // The menu's sharing item, without saving the change.
            var sharingToggle: [String: Any] = [:]
            if hub.isSharing {
                sharingToggle["menuWhileOn"] = menu()
                hub.setSharing(false, persist: false)
                try? await Task.sleep(for: .seconds(1))
                sharingToggle["afterOff"] = ["running": hub.server.isRunning, "port": hub.server.port.map(Int.init) ?? -1]
                sharingToggle["menuWhileOff"] = menu()
                hub.setSharing(true, persist: false)
                try? await Task.sleep(for: .seconds(3))
                sharingToggle["afterOn"] = [
                    "running": hub.server.isRunning, "port": hub.server.port.map(Int.init) ?? -1,
                    "advertisedName": hub.server.advertisedName ?? "none",
                ]
            }
            var unloadResult: [String: Any] = [:]
            if let unloadModel, hub.sources == [.local] {
                unloadResult = await exerciseUnload(unloadModel, monitor: monitor, panel: panel, directory: directory)
            }

            let report: [String: Any] = [
                "unloadTest": unloadResult,
                "sharingToggle": sharingToggle,
                "onTopToggle": onTopToggle,
                "normalLevel": NSWindow.Level.normal.rawValue,
                "menu": menu(),
                "sources": hub.sources.map(\.storageValue),
                "sections": hub.models.map { model -> [String: String] in
                    [
                        "host": model.remote?.host ?? "this Mac",
                        "status": model.remote.map { String(describing: $0.status) } ?? "local",
                        "phase": model.display.displayPhase.rawValue,
                        "model": model.models.first?.name ?? "none",
                    ]
                },
                "primaryHost": hub.primary.remote?.host ?? "this Mac",
                "peers": hub.peers,
                "sharing": hub.isSharing,
                "sharingStatus": hub.sharingStatus ?? "off",
                "sharingPort": hub.server.port.map(Int.init) ?? -1,
                "advertisedName": hub.server.advertisedName ?? "none",
                "detailReadings": monitor.wantsDetail,
                "panelVisible": panel.isVisible,
                "panelLevel": panel.level.rawValue,
                "floatingLevel": NSWindow.Level.floating.rawValue,
                "panelFloating": panel.isFloatingPanel,
                "joinsAllSpaces": panel.collectionBehavior.contains(.canJoinAllSpaces),
                "overFullScreen": panel.collectionBehavior.contains(.fullScreenAuxiliary),
                "expandedFrame": NSStringFromRect(expanded),
                "screenFrame": NSStringFromRect(panel.screen?.visibleFrame ?? .zero),
                "compactFrame": NSStringFromRect(compact),
                "restoredFrame": NSStringFromRect(restored),
                "topEdgeHeldInCompact": abs(expanded.maxY - compact.maxY) < 1,
                "hiddenByClick": hiddenByClick,
                "shownByClick": shownByClick,
                "statusButtonOnScreen": button?.window?.isVisible ?? false,
                "statusImageSize": button?.image.map { NSStringFromSize($0.size) } ?? "none",
                "statusAppearance": button?.effectiveAppearance.name.rawValue ?? "none",
                "statusTooltip": button?.toolTip ?? "",
                "requestsSeen": hub.primary.stats.requests,
                "lastRequestTPS": hub.primary.requests.first?.generationTPS ?? -1,
            ]
            if let data = try? JSONSerialization.data(withJSONObject: report, options: [.prettyPrinted, .sortedKeys]) {
                try? data.write(to: directory.appendingPathComponent("report.json"))
            }
            try? trace.write(to: directory.appendingPathComponent("trace.csv"), atomically: true, encoding: .utf8)
            NSApp.terminate(nil)
        }
    }

    /// Sends real mouse events at the unload button, then waits for Ollama to report the model gone.
    private static func exerciseUnload(
        _ name: String, monitor: Monitor, panel: NSPanel, directory: URL
    ) async -> [String: Any] {
        let loaded = monitor.model.models.map(\.name)
        guard loaded == [name] else { return ["skipped": "loaded models were \(loaded)"] }

        func click() async {
            guard monitor.model.models.map(\.name) == [name] else { return }
            // The middle of the button, which is the second row of the header at the right edge.
            let point = NSPoint(x: panel.frame.width - MeterLayout.gutter - 30, y: panel.frame.height - 46)
            for type in [NSEvent.EventType.leftMouseDown, .leftMouseUp] {
                guard
                    let event = NSEvent.mouseEvent(
                        with: type, location: point, modifierFlags: [],
                        timestamp: ProcessInfo.processInfo.systemUptime, windowNumber: panel.windowNumber, context: nil,
                        eventNumber: 0, clickCount: 1, pressure: 1)
                else { continue }
                panel.sendEvent(event)
                try? await Task.sleep(for: .milliseconds(60))
            }
            try? await Task.sleep(for: .milliseconds(300))
        }
        func waitUntil(seconds limit: Double, _ condition: () -> Bool) async -> Bool {
            let deadline = Date().addingTimeInterval(limit)
            while !condition(), Date() < deadline { try? await Task.sleep(for: .milliseconds(100)) }
            return condition()
        }
        func state() -> String { String(describing: monitor.model.unload) }

        var result: [String: Any] = [:]
        if await waitUntil(seconds: 25, { monitor.model.isWorking }) {
            await click()
            result["afterClickWhileWorking"] = state()
            capture(panel.contentView, to: directory.appendingPathComponent("unload-working.png"))
        } else {
            result["afterClickWhileWorking"] = "never saw a request"
        }
        guard await waitUntil(seconds: 60, { !monitor.model.isWorking }) else {
            result["skipped"] = "the model stayed busy"
            return result
        }

        await click()
        result["afterFirstClick"] = state()
        capture(panel.contentView, to: directory.appendingPathComponent("unload-confirm.png"))
        try? await Task.sleep(for: .seconds(5))
        result["fiveSecondsLater"] = state()
        result["stillLoadedAfterConfirmExpired"] = monitor.model.models.map(\.name)

        await click()
        await click()
        result["afterConfirmingClick"] = state()
        capture(panel.contentView, to: directory.appendingPathComponent("unload-sent.png"))
        let began = Date()
        result["modelGone"] = await waitUntil(seconds: 35) { monitor.model.models.isEmpty }
        result["secondsUntilGone"] = Date().timeIntervalSince(began)
        result["finalState"] = state()
        try? await Task.sleep(for: .seconds(1))
        capture(panel.contentView, to: directory.appendingPathComponent("unload-done.png"))
        return result
    }

    private static func footprintMegabytes() -> Int {
        var info = rusage_info_v4()
        let status = withUnsafeMutablePointer(to: &info) { pointer in
            pointer.withMemoryRebound(to: rusage_info_t?.self, capacity: 1) {
                proc_pid_rusage(getpid(), RUSAGE_INFO_V4, $0)
            }
        }
        return status == 0 ? Int(info.ri_phys_footprint >> 20) : -1
    }

    /// Draws the button's real image on a backdrop matching the appearance the menu bar reports.
    private static func captureStatusImage(_ image: NSImage, appearance: NSAppearance, to url: URL) {
        let scale: CGFloat = 4
        guard
            let bitmap = NSBitmapImageRep(
                bitmapDataPlanes: nil, pixelsWide: Int(image.size.width * scale), pixelsHigh: Int(image.size.height * scale),
                bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
                colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)
        else { return }
        bitmap.size = image.size
        guard let context = NSGraphicsContext(bitmapImageRep: bitmap) else { return }
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = context
        let isDark = appearance.bestMatch(from: [.darkAqua, .aqua]) == .darkAqua
        NSColor(white: isDark ? 0.18 : 0.92, alpha: 1).setFill()
        NSRect(origin: .zero, size: image.size).fill()
        image.draw(in: NSRect(origin: .zero, size: image.size))
        NSGraphicsContext.restoreGraphicsState()
        Renderer.write(bitmap, to: url)
    }

    private static func capture(_ view: NSView?, to url: URL) {
        guard let view, let cached = view.bitmapImageRepForCachingDisplay(in: view.bounds) else { return }
        view.cacheDisplay(in: view.bounds, to: cached)
        Renderer.write(cached, to: url)
    }
}
