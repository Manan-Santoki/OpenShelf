import Foundation
import Testing
@testable import OpenShelf

/// Programs OpenShelf starts get an allowlisted environment, never the
/// app's own (which, opened from a terminal, holds the shell's secrets).
struct ChildEnvironmentTests {
    static let secrets: [String: String] = [
        "AWS_SECRET_ACCESS_KEY": "a", "AWS_REGION": "b", "GITHUB_TOKEN": "c", "OP_SERVICE_ACCOUNT_TOKEN": "d",
        "SSH_AUTH_SOCK": "/tmp/agent", "PGPASSWORD": "e", "MY_API_KEY": "f", "SOME_SECRET": "g", "LC_SECRET_TOKEN": "test", "DYLD_INSERT_LIBRARIES": "h",
    ]
    static let kept: [String: String] = [
        "HOME": "/Users/x", "TMPDIR": "/tmp/", "USER": "x", "LOGNAME": "x", "SHELL": "/bin/zsh", "LANG": "en_GB.UTF-8", "LC_ALL": "en_GB.UTF-8", "TERM": "xterm",
    ]

    @Test func keepsOnlyTheAllowlist() {
        let environment = ChildEnvironment.minimal(from: Self.secrets.merging(Self.kept) { a, _ in a }.merging(["PATH": "/Users/x/evil:/usr/bin"]) { a, _ in a })
        #expect(environment == Self.kept.merging(["PATH": ChildEnvironment.searchPath]) { a, _ in a })
        #expect(!environment["PATH", default: ""].contains("/Users/x/evil"))
    }

    @Test func credentialNames() {
        for name in Self.secrets.keys where name != "DYLD_INSERT_LIBRARIES" {
            #expect(ChildEnvironment.isCredential(name), "\(name)")
        }
        for name in ["HOMEBREW_CASK_OPTS", "HOMEBREW_NO_AUTO_UPDATE", "HOME", "LANG"] {
            #expect(!ChildEnvironment.isCredential(name), "\(name)")
        }
    }

    /// The environment a real child sees, as /usr/bin/env prints it.
    private func childEnvironment(_ run: (URL) async throws -> Void) async throws -> [String: String] {
        let output = FileManager.default.temporaryDirectory.appendingPathComponent("openshelf-env-\(UUID().uuidString).txt")
        defer { try? FileManager.default.removeItem(at: output) }
        try await run(output)
        let text = try String(contentsOf: output, encoding: .utf8)
        return Dictionary(text.split(separator: "\n").compactMap { line in
            let parts = line.split(separator: "=", maxSplits: 1).map(String.init)
            return parts.count == 2 ? (parts[0], parts[1]) : nil
        }) { a, _ in a }
    }

    /// A variable set in this process (as an app inherits a shell's) reaches
    /// none of the ways OpenShelf starts programs.
    @Test func childrenNeverSeeTheParentsSecrets() async throws {
        let name = "OPENSHELF_TEST_\(UUID().uuidString.prefix(8))_TOKEN"
        setenv(name, "leaked", 1)
        defer { unsetenv(name) }
        #expect(ProcessInfo.processInfo.environment[name] == "leaked")
        let env = URL(fileURLWithPath: "/usr/bin/env")

        let viaRunner = try await childEnvironment { output in
            try await ProcessRunner.run(env, [], standardOutputFile: output)
        }
        let viaProbe = try await childEnvironment { output in
            try (MediaProbe.runSync(env, []) ?? "").write(to: output, atomically: true, encoding: .utf8)
        }
        let viaBrew = try await childEnvironment { output in
            let lines = LineCollector()
            _ = try await GroupedProcess.run(env, [], environment: Homebrew.environment(for: URL(fileURLWithPath: "/opt/homebrew/bin/brew"))) { lines.append($0) }
            try lines.text.write(to: output, atomically: true, encoding: .utf8)
        }
        for (path, seen) in [("ProcessRunner", viaRunner), ("MediaProbe", viaProbe), ("brew", viaBrew)] {
            #expect(seen[name] == nil, "\(path) passed the parent's \(name)")
            #expect(seen["HOME"] == NSHomeDirectory(), "\(path)")
            #expect(seen.keys.allSatisfy { !ChildEnvironment.isCredential($0) }, "\(path): \(seen.keys.sorted())")
        }
        #expect(viaBrew["HOMEBREW_NO_AUTO_UPDATE"] == "1")
        #expect(viaRunner["PATH"] == ChildEnvironment.searchPath)
        // zip, through PackageZip, gets the same minimal environment.
        #expect(ChildEnvironment.process(URL(fileURLWithPath: "/usr/bin/zip"), []).environment?[name] == nil)
    }

    /// LibreOffice, the one tool family other suites don't run for real.
    @Test(.enabled(if: FileManager.default.isExecutableFile(atPath: "/Applications/LibreOffice.app/Contents/MacOS/soffice")))
    func libreOfficeStillConverts() async throws {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("openshelf-office-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: folder) }
        let csv = folder.appendingPathComponent("Sheet.csv")
        try "a,b\n1,2\n".write(to: csv, atomically: true, encoding: .utf8)
        let tools = ExternalTools(ffmpeg: nil, cwebp: nil, libreOffice: URL(fileURLWithPath: "/Applications/LibreOffice.app/Contents/MacOS/soffice"))
        let output = try await Converter.convert(csv, to: .ods, tools: tools) { _ in }
        #expect(output.map(\.lastPathComponent) == ["Sheet.ods"])
        #expect(try Data(contentsOf: output[0]).prefix(2) == Data("PK".utf8))
    }
}

private final class LineCollector: @unchecked Sendable {
    private let lock = NSLock()
    private var lines: [String] = []
    func append(_ line: String) { lock.withLock { lines.append(line) } }
    var text: String { lock.withLock { lines.joined(separator: "\n") } }
}
