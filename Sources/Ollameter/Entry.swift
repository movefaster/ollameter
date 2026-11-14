import AppKit
import MeterCore

@main
enum Entry {
    private static var delegate: AppDelegate?

    static func main() {
        let arguments = CommandLine.arguments

        func directory(after flag: String) -> URL? {
            guard let index = arguments.firstIndex(of: flag), index + 1 < arguments.count else { return nil }
            return URL(fileURLWithPath: arguments[index + 1], isDirectory: true)
        }

        if let output = directory(after: "--render") {
            MainActor.assumeIsolated {
                Renderer.run(directory: output, live: arguments.contains("--live"))
            }
            return
        }

        func value(after flag: String) -> String? {
            guard let index = arguments.firstIndex(of: flag), index + 1 < arguments.count else { return nil }
            return arguments[index + 1]
        }

        var options = AppDelegate.Options()
        options.selfTestDirectory = directory(after: "--self-test")
        options.selfTestSeconds = value(after: "--seconds").flatMap(Double.init) ?? 8
        options.selfTestUnloadModel = value(after: "--unload-test")
        // `--connect` takes `local`, `service:NAME` or an address such as `studio.local:11480`.
        if let text = value(after: "--connect") {
            guard let source = MeterSource(storageValue: text) ?? MeterSource.address(text) else {
                FileHandle.standardError.write(Data("Can't read --connect \(text)\n".utf8))
                exit(64)
            }
            options.source = source
        }
        if arguments.contains("--share") { options.sharing = true }
        if arguments.contains("--no-share") { options.sharing = false }

        MainActor.assumeIsolated {
            let application = NSApplication.shared
            let appDelegate = AppDelegate(options: options)
            delegate = appDelegate
            application.delegate = appDelegate
            application.setActivationPolicy(.accessory)
            application.run()
        }
    }
}
