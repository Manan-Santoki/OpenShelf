import Foundation
import Testing
@testable import OpenShelf

/// The vendor's signed ffmpeg 6 zips, as in ToolInstallTests. Tests that need
/// them are skipped where they aren't present.
private enum Fixtures {
    static let folder = URL(fileURLWithPath: ProcessInfo.processInfo.environment["OPENSHELF_INSTALL_FIXTURES"]
        ?? "/private/tmp/claude-501/-Users-manansantoki/13918f21-eb35-438a-8c6e-9a66dcd78487/scratchpad/dl")
    static let ffmpegZip = folder.appendingPathComponent("riedl/ffmpeg6.zip")
    static let ffprobeZip = folder.appendingPathComponent("riedl/ffprobe6.zip")

    static var hasFFmpeg: Bool {
        FileManager.default.fileExists(atPath: ffmpegZip.path) && FileManager.default.fileExists(atPath: ffprobeZip.path)
    }
    /// The one real `brew install` run, which needs Homebrew's ffmpeg already
    /// installed so it's a no-op. Run with OPENSHELF_HOMEBREW_TESTS=1.
    static var allowsHomebrew: Bool {
        ProcessInfo.processInfo.environment["OPENSHELF_HOMEBREW_TESTS"] == "1"
    }
}

private func makeTemporaryFolder(_ label: String) throws -> URL {
    let folder = FileManager.default.temporaryDirectory.appendingPathComponent("\(label)-\(UUID().uuidString)", isDirectory: true)
    try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
    return folder
}

private func temporaryLocations(in root: URL) -> InstallLocations {
    InstallLocations(
        tools: root.appendingPathComponent("Application Support/OpenShelf/Tools", isDirectory: true),
        applications: root.appendingPathComponent("Applications", isDirectory: true)
    )
}

/// Lays out zips the way the GitHub mirror does: flat, named by
/// architecture, each with a shasum-style .sha256 beside it.
private func makeMirror(in folder: URL, ffmpegZip: URL, ffprobeZip: URL, checksums: Bool = true) throws -> URL {
    try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
    for (name, zip) in [("ffmpeg-arm64.zip", ffmpegZip), ("ffprobe-arm64.zip", ffprobeZip)] {
        let copy = folder.appendingPathComponent(name)
        try FileManager.default.copyItem(at: zip, to: copy)
        if checksums {
            try "\(try ToolIntegrity.sha256(of: copy))  \(name)\n".write(to: folder.appendingPathComponent(name + ".sha256"), atomically: true, encoding: .utf8)
        }
    }
    return folder
}

/// Zips holding a script nobody signed, named like the real thing.
private func makeUnsignedZips(in root: URL) async throws -> (ffmpeg: URL, ffprobe: URL) {
    let payload = root.appendingPathComponent("payload", isDirectory: true)
    try FileManager.default.createDirectory(at: payload, withIntermediateDirectories: true)
    var zips: [URL] = []
    for name in ["ffmpeg", "ffprobe"] {
        let script = payload.appendingPathComponent(name)
        try Data("#!/bin/sh\ntouch \(root.path)/ran\n".utf8).write(to: script)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: script.path)
        let zip = root.appendingPathComponent("\(name).zip")
        try await ProcessRunner.run(URL(fileURLWithPath: "/usr/bin/ditto"), ["-c", "-k", script.path, zip.path])
        zips.append(zip)
    }
    return (zips[0], zips[1])
}

/// Records which sources were tried, from inside the attempt closure.
private final class Attempts: @unchecked Sendable {
    private let lock = NSLock()
    private var recorded: [InstallSource] = []

    func append(_ source: InstallSource) { lock.withLock { recorded.append(source) } }
    var sources: [InstallSource] { lock.withLock { recorded } }
}

private final class Lines: @unchecked Sendable {
    private let lock = NSLock()
    private var recorded: [String] = []

    func append(_ line: String) { lock.withLock { recorded.append(line) } }
    var all: [String] { lock.withLock { recorded } }
}

struct MirrorSourceTests {
    @Test func mirrorURLsFollowTheArchitecture() {
        #expect(FFmpegSource.mirrorBase.absoluteString == "https://github.com/Manan-Santoki/OpenShelf/releases/download/tools-ffmpeg/")
        #expect(FFmpegSource.mirrorURL(of: "ffmpeg", for: .arm64).absoluteString
            == "https://github.com/Manan-Santoki/OpenShelf/releases/download/tools-ffmpeg/ffmpeg-arm64.zip")
        let ffprobe = FFmpegSource.mirrorURL(of: "ffprobe", for: .x86_64)
        #expect(ffprobe.lastPathComponent == "ffprobe-amd64.zip")
        #expect(FFmpegSource.checksumURL(for: ffprobe).lastPathComponent == "ffprobe-amd64.zip.sha256")
    }

    @Test(.enabled(if: Fixtures.hasFFmpeg))
    func installsTheVendorsBuildFromTheMirror() async throws {
        let root = try makeTemporaryFolder("mirror")
        defer { try? FileManager.default.removeItem(at: root) }
        let mirror = try makeMirror(in: root.appendingPathComponent("mirror"), ffmpegZip: Fixtures.ffmpegZip, ffprobeZip: Fixtures.ffprobeZip)
        let locations = temporaryLocations(in: root)

        let entry = try await ToolInstallation.installFFmpegFromMirror(mirror, architecture: .arm64, into: locations) { _ in }

        let ffmpeg = locations.tools.appendingPathComponent("ffmpeg")
        #expect(entry.paths == [ffmpeg.path, locations.tools.appendingPathComponent("ffprobe").path])
        #expect(entry.source.hasSuffix("ffmpeg-arm64.zip"))
        #expect(FileManager.default.isExecutableFile(atPath: ffmpeg.path))
        try ToolIntegrity.verifySignature(of: ffmpeg, identifier: "ffmpeg", teamID: FFmpegSource.teamID, name: "ffmpeg")
    }

    @Test(.enabled(if: Fixtures.hasFFmpeg))
    func aDamagedMirrorDownloadIsRejected() async throws {
        let root = try makeTemporaryFolder("mirror-damaged")
        defer { try? FileManager.default.removeItem(at: root) }
        let mirror = try makeMirror(in: root.appendingPathComponent("mirror"), ffmpegZip: Fixtures.ffmpegZip, ffprobeZip: Fixtures.ffprobeZip)
        try "\(String(repeating: "a", count: 64))  ffmpeg-arm64.zip\n".write(
            to: mirror.appendingPathComponent("ffmpeg-arm64.zip.sha256"), atomically: true, encoding: .utf8)
        let locations = temporaryLocations(in: root)

        await #expect(throws: ToolIntegrityError.checksumMismatch(file: "ffmpeg")) {
            _ = try await ToolInstallation.installFFmpegFromMirror(mirror, architecture: .arm64, into: locations) { _ in }
        }
        #expect(try FileManager.default.contentsOfDirectory(atPath: locations.tools.path).isEmpty)
    }

    /// The mirror is only a transport: whatever it serves must carry the
    /// vendor's signature, checksum or not.
    @Test func unsignedProgramsFromTheMirrorAreNeverInstalled() async throws {
        let root = try makeTemporaryFolder("mirror-unsigned")
        defer { try? FileManager.default.removeItem(at: root) }
        let zips = try await makeUnsignedZips(in: root)
        let mirror = try makeMirror(in: root.appendingPathComponent("mirror"), ffmpegZip: zips.ffmpeg, ffprobeZip: zips.ffprobe)
        let locations = temporaryLocations(in: root)

        await #expect(throws: ToolIntegrityError.badSignature(file: "ffmpeg")) {
            _ = try await ToolInstallation.installFFmpegFromMirror(mirror, architecture: .arm64, into: locations) { _ in }
        }
        #expect(try FileManager.default.contentsOfDirectory(atPath: locations.tools.path).isEmpty)
        #expect(!FileManager.default.fileExists(atPath: root.appendingPathComponent("ran").path))
    }

    /// The build server can't be reached, so the same build comes from the mirror.
    @Test(.enabled(if: Fixtures.hasFFmpeg))
    func fallsBackFromAnUnreachableVendorToTheMirror() async throws {
        let root = try makeTemporaryFolder("fallback")
        defer { try? FileManager.default.removeItem(at: root) }
        let mirror = try makeMirror(in: root.appendingPathComponent("mirror"), ffmpegZip: Fixtures.ffmpegZip, ffprobeZip: Fixtures.ffprobeZip)
        let locations = temporaryLocations(in: root)
        let attempts = Attempts()

        let entry = try await InstallFallback.install(.ffmpeg, from: InstallFallback.automaticSources(for: .ffmpeg)) { source in
            attempts.append(source)
            switch source {
            case .vendor:
                return try await ToolInstallation.installFFmpeg(from: root.appendingPathComponent("down/ffmpeg.zip"), into: locations) { _ in }
            default:
                return try await ToolInstallation.installFFmpegFromMirror(mirror, architecture: .arm64, into: locations) { _ in }
            }
        }

        #expect(attempts.sources == [.vendor, .mirror])
        #expect(entry.source.hasSuffix("ffmpeg-arm64.zip"))
        #expect(FileManager.default.isExecutableFile(atPath: locations.tools.appendingPathComponent("ffprobe").path))
    }
}

struct InstallFallbackTests {
    @Test func whichSourcesAreTriedOnTheirOwn() {
        #expect(InstallFallback.automaticSources(for: .ffmpeg) == [.vendor, .mirror])
        #expect(InstallFallback.automaticSources(for: .libreOffice) == [.vendor])
    }

    @Test func serverAndNetworkErrorsMoveOn() {
        #expect(InstallFallback.isSourceUnavailable(DownloadError.httpStatus(521, host: FFmpegSource.host)))
        #expect(InstallFallback.isSourceUnavailable(DownloadError.httpStatus(404, host: "github.com")))
        #expect(InstallFallback.isSourceUnavailable(DownloadError.unreachable(host: FFmpegSource.host)))
    }

    @Test func verificationFailuresNeverMoveOn() {
        #expect(!InstallFallback.isSourceUnavailable(ToolIntegrityError.checksumMismatch(file: "ffmpeg")))
        #expect(!InstallFallback.isSourceUnavailable(ToolIntegrityError.checksumMissing(file: "ffmpeg")))
        #expect(!InstallFallback.isSourceUnavailable(ToolIntegrityError.badSignature(file: "ffmpeg")))
        #expect(!InstallFallback.isSourceUnavailable(DownloadError.untrustedLocation("example.com")))
        #expect(!InstallFallback.isSourceUnavailable(CancellationError()))
        #expect(!InstallFallback.isSourceUnavailable(ConversionError.failed("The download didn’t contain ffmpeg.")))
    }

    @Test func vendor5xxFallsBackToTheMirror() async throws {
        let attempts = Attempts()
        let result: String = try await InstallFallback.install(.ffmpeg, from: [.vendor, .mirror]) { source in
            attempts.append(source)
            if source == .vendor { throw DownloadError.httpStatus(521, host: FFmpegSource.host) }
            return "from the mirror"
        }
        #expect(result == "from the mirror")
        #expect(attempts.sources == [.vendor, .mirror])
    }

    @Test func mirror404LeadsToHomebrew() async throws {
        let attempts = Attempts()
        var thrown: Error?
        do {
            let _: String = try await InstallFallback.install(.ffmpeg, from: [.vendor, .mirror]) { source in
                attempts.append(source)
                if source == .vendor { throw DownloadError.httpStatus(521, host: FFmpegSource.host) }
                throw DownloadError.httpStatus(404, host: "github.com")
            }
        } catch {
            thrown = error
        }
        #expect(attempts.sources == [.vendor, .mirror])
        let unavailable = try #require(thrown as? SourcesUnavailableError)
        #expect(unavailable.detail == DownloadError.httpStatus(404, host: "github.com").localizedDescription)

        let failure = InstallFallback.failure(after: unavailable, from: .vendor)
        #expect(failure.kind == .sourcesUnavailable)
        #expect(failure.message == "The ffmpeg download server isn’t responding right now.")
        #expect(InstallFallback.offers(after: failure.kind, homebrewInstalled: true) == [.installWithHomebrew, .retry])
        #expect(InstallFallback.offers(after: failure.kind, homebrewInstalled: false) == [.retry, .getHomebrew])
    }

    @Test func aVerificationFailureStopsAtTheVendor() async {
        let attempts = Attempts()
        await #expect(throws: ToolIntegrityError.badSignature(file: "ffmpeg")) {
            let _: String = try await InstallFallback.install(.ffmpeg, from: [.vendor, .mirror]) { source in
                attempts.append(source)
                throw ToolIntegrityError.badSignature(file: "ffmpeg")
            }
        }
        #expect(attempts.sources == [.vendor])
    }

    @Test func aVerificationFailureAtTheMirrorIsNeverPassedToHomebrew() async throws {
        let attempts = Attempts()
        var thrown: Error?
        do {
            let _: String = try await InstallFallback.install(.ffmpeg, from: [.vendor, .mirror]) { source in
                attempts.append(source)
                if source == .vendor { throw DownloadError.unreachable(host: FFmpegSource.host) }
                throw ToolIntegrityError.checksumMismatch(file: "ffmpeg")
            }
        } catch {
            thrown = error
        }
        #expect(attempts.sources == [.vendor, .mirror])
        #expect(thrown as? ToolIntegrityError == .checksumMismatch(file: "ffmpeg"))
        let failure = InstallFallback.failure(after: try #require(thrown), from: .vendor)
        #expect(failure.kind == .other)
        #expect(!InstallFallback.offers(after: failure.kind, homebrewInstalled: true).contains(.installWithHomebrew))
    }

    @Test func libreOfficeGoesStraightToHomebrewWhenTheFoundationIsDown() async throws {
        await #expect(throws: SourcesUnavailableError.self) {
            let _: String = try await InstallFallback.install(.libreOffice, from: InstallFallback.automaticSources(for: .libreOffice)) { _ in
                throw DownloadError.unreachable(host: "download.documentfoundation.org")
            }
        }
        let message = SourcesUnavailableError(tool: .libreOffice, detail: "").localizedDescription
        #expect(message == "The LibreOffice download server isn’t responding right now.")
    }

    @Test func otherFailuresOfferRetryAndTheTerminalCommand() {
        #expect(InstallFallback.offers(after: .other, homebrewInstalled: true) == [.retry, .copyHomebrewCommand])
        #expect(InstallFallback.offers(after: .homebrew, homebrewInstalled: true) == [.retry, .copyHomebrewCommand])
        let failure = InstallFallback.failure(after: HomebrewError(message: "Homebrew couldn’t install ffmpeg: x"), from: .homebrew)
        #expect(failure == InstallFailure(message: "Homebrew couldn’t install ffmpeg: x", kind: .homebrew))
    }

    @Test func theStatusLineSaysWhereItsFrom() {
        #expect(InstallSource.vendor.statusLine(for: .ffmpeg, step: .downloading) == "Downloading from ffmpeg.martin-riedl.de")
        #expect(InstallSource.mirror.statusLine(for: .ffmpeg, step: .downloading) == "Downloading from OpenShelf’s mirror")
        #expect(InstallSource.mirror.statusLine(for: .ffmpeg, step: .verifying) == "From OpenShelf’s mirror")
        #expect(InstallSource.vendor.statusLine(for: .libreOffice, step: .installing) == "From libreoffice.org")
        #expect(InstallSource.homebrew.statusLine(for: .ffmpeg, step: .downloading) == "Installing with Homebrew")
    }
}

struct HomebrewTests {
    @Test func findsBrewWhereItIs() throws {
        let root = try makeTemporaryFolder("brew")
        defer { try? FileManager.default.removeItem(at: root) }
        let missing = root.appendingPathComponent("opt/homebrew/bin/brew")
        let present = root.appendingPathComponent("usr/local/bin/brew")
        try FileManager.default.createDirectory(at: present.deletingLastPathComponent(), withIntermediateDirectories: true)
        try Data("#!/bin/sh\n".utf8).write(to: present)
        #expect(Homebrew.find(in: [missing, present]) == nil)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: present.path)
        #expect(Homebrew.find(in: [missing, present]) == present)
        #expect(Homebrew.find(in: [missing]) == nil)
        #expect(Homebrew.standardLocations.map(\.path) == ["/opt/homebrew/bin/brew", "/usr/local/bin/brew"])
    }

    @Test func runsTheSameCommandItSuggests() {
        for tool in ExtraTool.allCases {
            #expect((["brew"] + Homebrew.arguments(for: tool)).joined(separator: " ") == tool.homebrewCommand)
        }
    }

    @Test func environmentForAGUIApp() {
        let base = ["PATH": "/usr/bin:/bin:/usr/sbin:/sbin", "HOME": "/Users/x", "SUDO_ASKPASS": "/usr/local/bin/askpass"]
        let environment = Homebrew.environment(for: URL(fileURLWithPath: "/opt/homebrew/bin/brew"), base: base)
        #expect(environment["PATH"] == "/opt/homebrew/bin:/opt/homebrew/sbin:/usr/bin:/bin:/usr/sbin:/sbin")
        #expect(environment["HOMEBREW_NO_AUTO_UPDATE"] == "1")
        #expect(environment["NONINTERACTIVE"] == "1")
        #expect(environment["HOMEBREW_NO_INSTALL_UPGRADE"] == "1")
        #expect(environment["HOME"] == "/Users/x")
        #expect(environment["SUDO_ASKPASS"] == nil)

        let intel = Homebrew.environment(for: URL(fileURLWithPath: "/usr/local/bin/brew"), base: [:])
        #expect(intel["PATH"] == "/usr/local/bin:/usr/local/sbin:/usr/bin:/bin:/usr/sbin:/sbin")
    }

    @Test func readsBrewsProgress() {
        let cases: [(String, String?)] = [
            ("==> Fetching downloads for: ffmpeg", "Downloading ffmpeg…"),
            ("==> Fetching ffmpeg", "Downloading ffmpeg…"),
            ("==> Fetching downloads for: ffmpeg, x265 and lame", "Downloading…"),
            ("==> Fetching dependency downloads", "Downloading…"),
            ("==> Running post-install for ffmpeg", "Running post-install for ffmpeg…"),
            ("==> Downloading https://ghcr.io/v2/homebrew/core/x265/blobs/sha256:abc", "Downloading…"),
            ("✔︎ Bottle Manifest x265 (4.1)", "Downloaded x265"),
            ("✔︎ Bottle ffmpeg (9.0.2_1)", "Downloaded ffmpeg"),
            ("✔︎ Cask libreoffice (26.8.1)", "Downloaded libreoffice"),
            ("==> Installing dependencies for ffmpeg: x264, x265 and lame", "Installing dependencies…"),
            ("==> Installing ffmpeg dependency: x265", "Installing x265…"),
            ("==> Pouring ffmpeg--9.0.2_1.arm64_sequoia.bottle.tar.gz", "Pouring ffmpeg…"),
            ("==> Installing Cask libreoffice", "Installing libreoffice…"),
            ("==> Moving App 'LibreOffice.app' to '/Applications/LibreOffice.app'", "Moving LibreOffice to Applications…"),
            ("\u{1B}[34m==>\u{1B}[0m \u{1B}[1mPouring x265--4.1.bottle.tar.gz\u{1B}[0m", "Pouring x265…"),
            ("Warning: ffmpeg 9.0.2_1 is already installed and up-to-date.", "Already installed"),
            ("🍺  /opt/homebrew/Cellar/ffmpeg/9.0.2_1: 285 files, 55.2MB", "Finishing…"),
            ("🍺  libreoffice was successfully installed!", "Finishing…"),
            ("==> Caveats", nil),
            ("To reinstall 9.0.2_1, run:", nil),
            ("######################################## 100.0%", nil),
            ("", nil),
        ]
        for (line, expected) in cases {
            #expect(Homebrew.progressDetail(for: line) == expected, "\(line)")
        }
    }

    @Test func explainsFailures() {
        #expect(Homebrew.failureMessage(for: .ffmpeg, output: ["==> Fetching ffmpeg", "Error: No such file or directory @ rb_sysopen"])
            == "Homebrew couldn’t install ffmpeg: No such file or directory @ rb_sysopen")
        #expect(Homebrew.failureMessage(for: .libreOffice, output: ["==> Installing Cask libreoffice", "sudo: a terminal is required to read the password"])
            == "Homebrew needs an administrator password for LibreOffice. Run “brew install --cask libreoffice” in Terminal.")
        #expect(Homebrew.failureMessage(for: .ffmpeg, output: []) == "Homebrew couldn’t install ffmpeg.")
        #expect(Homebrew.failureMessage(for: .ffmpeg, output: ["curl: (6) Could not resolve host: ghcr.io"])
            == "Homebrew couldn’t install ffmpeg: (6) Could not resolve host: ghcr.io")
    }

    /// The one real run: Homebrew's ffmpeg is already installed, so this is a
    /// quick no-op that goes through the whole path, from finding brew to
    /// detecting ffmpeg afterwards.
    @Test(.enabled(if: Fixtures.allowsHomebrew && Homebrew.find() != nil))
    func installsWithTheRealHomebrew() async throws {
        let brew = try #require(Homebrew.find())
        let details = Lines()
        let started = ContinuousClock.now
        try await Homebrew.install(.ffmpeg, using: brew) { progress in
            #expect(progress.source == .homebrew)
            details.append(progress.detail)
        }
        #expect(ContinuousClock.now - started < .seconds(60))
        #expect(details.all.first == "Starting Homebrew…")
        #expect(details.all.contains("Already installed"))
        let tools = ExternalTools.detect(ignoring: [])
        #expect(tools.ffmpeg?.path.hasPrefix(brew.deletingLastPathComponent().path) == true)
    }
}

struct GroupedProcessTests {
    private let shell = URL(fileURLWithPath: "/bin/sh")

    @Test func streamsLinesAndReturnsTheStatus() async throws {
        let lines = Lines()
        let status = try await GroupedProcess.run(
            shell, ["-c", "echo one; printf 'two\\rthree\\n'; echo four >&2; printf five; exit 3"],
            environment: ["PATH": "/usr/bin:/bin"]
        ) { lines.append($0) }
        #expect(status == 3)
        #expect(lines.all == ["one", "two", "three", "four", "five"])
    }

    @Test func passesTheEnvironmentAndNoInput() async throws {
        let lines = Lines()
        let environment = Homebrew.environment(for: URL(fileURLWithPath: "/opt/homebrew/bin/brew"), base: ["HOME": NSHomeDirectory()])
        // `cat` returns at once: standard input is /dev/null, not a pipe that stays open.
        let status = try await GroupedProcess.run(shell, ["-c", "cat; echo \"$HOMEBREW_NO_AUTO_UPDATE $NONINTERACTIVE $PATH\""], environment: environment) {
            lines.append($0)
        }
        #expect(status == 0)
        #expect(lines.all == ["1 1 /opt/homebrew/bin:/opt/homebrew/sbin:/usr/bin:/bin:/usr/sbin:/sbin"])
    }

    @Test func runsInItsOwnProcessGroup() async throws {
        let lines = Lines()
        _ = try await GroupedProcess.run(shell, ["-c", "echo $$ $(ps -o pgid= -p $$)"], environment: ["PATH": "/usr/bin:/bin"]) {
            lines.append($0)
        }
        let ids = try #require(lines.all.first).split(separator: " ").map(String.init)
        #expect(ids.count == 2 && ids[0] == ids[1])
        #expect(ids.first != String(getpgrp()))
    }

    /// Cancel stops what the program started too, even what ignores Ctrl-C
    /// (a non-interactive shell's background jobs do).
    @Test func cancellingStopsTheWholeGroup() async throws {
        let root = try makeTemporaryFolder("group")
        defer { try? FileManager.default.removeItem(at: root) }
        let pidFile = root.appendingPathComponent("child")
        let task = Task {
            try await GroupedProcess.run(
                shell, ["-c", "sleep 60 & echo $! > '\(pidFile.path)'; wait"],
                environment: ["PATH": "/usr/bin:/bin"], grace: .milliseconds(200)
            ) { _ in }
        }
        var child: pid_t?
        for _ in 0..<100 where child == nil {
            try await Task.sleep(for: .milliseconds(20))
            child = (try? String(contentsOf: pidFile, encoding: .utf8)).flatMap { pid_t($0.trimmingCharacters(in: .whitespacesAndNewlines)) }
        }
        let pid = try #require(child)
        #expect(kill(pid, 0) == 0)

        let started = ContinuousClock.now
        task.cancel()
        await #expect(throws: CancellationError.self) { try await task.value }
        #expect(ContinuousClock.now - started < .seconds(5))
        var isGone = false
        for _ in 0..<100 where !isGone {
            isGone = kill(pid, 0) == -1 && errno == ESRCH
            if !isGone { try await Task.sleep(for: .milliseconds(20)) }
        }
        #expect(isGone)
    }

    @Test func aMissingProgramFailsToStart() async {
        await #expect(throws: (any Error).self) {
            _ = try await GroupedProcess.run(URL(fileURLWithPath: "/nonexistent/brew"), [], environment: [:]) { _ in }
        }
    }

    @Test func splitsLinesAcrossChunks() {
        let lines = Lines()
        let splitter = LineSplitter { lines.append($0) }
        splitter.append(Data("==> Down".utf8))
        splitter.append(Data("loading\r\n##  50%\r##  100%\n  \nlast".utf8))
        splitter.finish()
        #expect(lines.all == ["==> Downloading", "##  50%", "##  100%", "last"])
    }
}
