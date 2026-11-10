import AppKit

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

        MainActor.assumeIsolated {
            let application = NSApplication.shared
            let appDelegate = AppDelegate(
                selfTestDirectory: directory(after: "--self-test"),
                selfTestSeconds: arguments.firstIndex(of: "--seconds").flatMap { index in
                    index + 1 < arguments.count ? Double(arguments[index + 1]) : nil
                } ?? 8,
                selfTestUnloadModel: arguments.firstIndex(of: "--unload-test").flatMap { index in
                    index + 1 < arguments.count ? arguments[index + 1] : nil
                })
            delegate = appDelegate
            application.delegate = appDelegate
            application.setActivationPolicy(.accessory)
            application.run()
        }
    }
}
