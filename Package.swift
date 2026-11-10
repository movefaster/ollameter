// swift-tools-version: 6.0
import PackageDescription

let language: [SwiftSetting] = [.swiftLanguageMode(.v5)]

let package = Package(
    name: "Ollameter",
    platforms: [.macOS(.v14)],
    targets: [
        .target(name: "MeterCore", swiftSettings: language),
        .executableTarget(name: "Ollameter", dependencies: ["MeterCore"], swiftSettings: language),
        .testTarget(name: "MeterCoreTests", dependencies: ["MeterCore"], swiftSettings: language),
    ]
)
