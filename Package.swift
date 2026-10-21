// swift-tools-version: 6.0
import PackageDescription

let package = Package(
    name: "OpenShelf",
    platforms: [.macOS(.v14)],
    targets: [
        .executableTarget(
            name: "OpenShelf",
            path: "Sources/OpenShelf"
        ),
        .testTarget(
            name: "OpenShelfTests",
            dependencies: ["OpenShelf"],
            path: "Tests/OpenShelfTests"
        ),
    ],
    swiftLanguageModes: [.v6]
)
