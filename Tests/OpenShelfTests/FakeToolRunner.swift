import Foundation
@testable import OpenShelf

/// Stands in for `ProcessRunner.live` in tests of the optional-tool
/// converters: records every invocation, prints the lines it's given, and
/// lets each test write the files the real tool would have.
final class FakeToolRunner: @unchecked Sendable {
    private let lock = NSLock()
    private var recorded: [ToolInvocation] = []
    /// Called for each invocation; writes outputs or throws like the tool.
    let respond: @Sendable (ToolInvocation) throws -> [String]

    init(respond: @escaping @Sendable (ToolInvocation) throws -> [String] = { _ in [] }) {
        self.respond = respond
    }

    var invocations: [ToolInvocation] { lock.withLock { recorded } }

    var runner: ToolRunner {
        { invocation, onLine in
            self.lock.withLock { self.recorded.append(invocation) }
            try Task.checkCancellation()
            for line in try self.respond(invocation) { onLine(line) }
        }
    }

    /// The path following `flag` in `arguments`.
    static func value(after flag: String, in arguments: [String]) -> String? {
        guard let index = arguments.firstIndex(of: flag), arguments.indices.contains(index + 1) else { return nil }
        return arguments[index + 1]
    }
}

/// A folder of fake executables (and anything else) for detection tests.
struct ToolFolder {
    let root: URL

    init(_ label: String) throws {
        root = FileManager.default.temporaryDirectory.appendingPathComponent("openshelf-\(label)-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
    }

    @discardableResult
    func executable(_ path: String) throws -> URL {
        let url = root.appendingPathComponent(path)
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try "#!/bin/sh\n".write(to: url, atomically: true, encoding: .utf8)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: url.path)
        return url
    }

    @discardableResult
    func file(_ path: String, contents: String = "x") throws -> URL {
        let url = root.appendingPathComponent(path)
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try contents.write(to: url, atomically: true, encoding: .utf8)
        return url
    }

    func remove() { try? FileManager.default.removeItem(at: root) }
}

/// End-to-end tests that run the real whisper.cpp, Pandoc and calibre. They
/// keep every core busy (calibre starts Chromium for PDFs), which slows the
/// timing-sensitive media tests running beside them, so they're opt-in:
/// `OPENSHELF_TOOL_TESTS=1 swift test`, with the tools installed.
enum RealToolTests {
    static var enabled: Bool { ProcessInfo.processInfo.environment["OPENSHELF_TOOL_TESTS"] == "1" }
}
