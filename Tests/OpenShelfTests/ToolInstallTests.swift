import Foundation
import Testing
@testable import OpenShelf

/// Real signed builds from the vendors, downloaded once for these tests (the
/// ffmpeg ones from the Internet Archive's copy of the build server). Tests
/// that need them are skipped where they aren't present.
private enum Fixtures {
    static let folder = URL(fileURLWithPath: ProcessInfo.processInfo.environment["OPENSHELF_INSTALL_FIXTURES"]
        ?? "/private/tmp/claude-501/-Users-manansantoki/13918f21-eb35-438a-8c6e-9a66dcd78487/scratchpad/dl")
    static let ffmpegZip = folder.appendingPathComponent("riedl/ffmpeg6.zip")
    static let ffprobeZip = folder.appendingPathComponent("riedl/ffprobe6.zip")
    static let libreOfficeImage = folder.appendingPathComponent("LibreOffice_26.8.1_MacOS_aarch64.dmg")
    static let libreOfficeSHA256 = "ca074e0b13571efb30a31fc531c5f95e997030637325e5d5de92d67aefdf0dfc"
    /// Pandoc's own Apple silicon zip, e.g. pandoc-3.12.1-arm64-macOS.zip, renamed.
    static let pandocZip = folder.appendingPathComponent("pandoc-arm64-macOS.zip")

    static var hasFFmpeg: Bool {
        FileManager.default.fileExists(atPath: ffmpegZip.path) && FileManager.default.fileExists(atPath: ffprobeZip.path)
    }
    static var hasPandoc: Bool {
        FileManager.default.fileExists(atPath: pandocZip.path) && MachineArchitecture.current == .arm64
    }
    static var hasLibreOffice: Bool {
        FileManager.default.fileExists(atPath: libreOfficeImage.path) && MachineArchitecture.current == .arm64
    }
    /// Tests that download from the vendors run only when asked to.
    static var allowsNetwork: Bool {
        ProcessInfo.processInfo.environment["OPENSHELF_NETWORK_TESTS"] == "1"
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

/// Lays out zips the way the build server does: one versioned folder with
/// each zip and its .sha256 beside it.
private func makeBuildFolder(in root: URL, ffmpegZip: URL, ffprobeZip: URL, checksums: Bool = true) throws -> URL {
    let build = root.appendingPathComponent("download/macos/arm64/1677945948_6.0", isDirectory: true)
    try FileManager.default.createDirectory(at: build, withIntermediateDirectories: true)
    for (name, zip) in [("ffmpeg.zip", ffmpegZip), ("ffprobe.zip", ffprobeZip)] {
        let copy = build.appendingPathComponent(name)
        try FileManager.default.copyItem(at: zip, to: copy)
        if checksums {
            try "\(try ToolIntegrity.sha256(of: copy))  \(name)\n".write(to: build.appendingPathComponent(name + ".sha256"), atomically: true, encoding: .utf8)
        }
    }
    return build.appendingPathComponent("ffmpeg.zip")
}

private func hiddenItems(in folder: URL) -> [String] {
    ((try? FileManager.default.contentsOfDirectory(atPath: folder.path)) ?? []).filter { $0.hasPrefix(".openshelf") }
}

private func run(_ executable: String, _ arguments: [String]) throws -> (status: Int32, output: String) {
    let process = Process()
    process.executableURL = URL(fileURLWithPath: executable)
    process.arguments = arguments
    let pipe = Pipe()
    process.standardOutput = pipe
    process.standardError = pipe
    try process.run()
    let data = pipe.fileHandleForReading.readDataToEndOfFile()
    process.waitUntilExit()
    return (process.terminationStatus, String(decoding: data, as: UTF8.self))
}

struct ToolSourceTests {
    @Test func ffmpegURLsFollowTheArchitecture() {
        #expect(FFmpegSource.latestURL(of: "ffmpeg", for: .arm64).absoluteString
            == "https://ffmpeg.martin-riedl.de/redirect/latest/macos/arm64/release/ffmpeg.zip")
        #expect(FFmpegSource.latestURL(of: "ffprobe", for: .x86_64).absoluteString
            == "https://ffmpeg.martin-riedl.de/redirect/latest/macos/amd64/release/ffprobe.zip")
    }

    @Test func ffprobeComesFromTheSameBuildAsFFmpeg() {
        let resolved = URL(string: "https://ffmpeg.martin-riedl.de/download/macos/arm64/1783011502_8.1.2/ffmpeg.zip")!
        #expect(FFmpegSource.sibling("ffprobe.zip", of: resolved).absoluteString
            == "https://ffmpeg.martin-riedl.de/download/macos/arm64/1783011502_8.1.2/ffprobe.zip")
        #expect(FFmpegSource.checksumURL(for: resolved).absoluteString
            == "https://ffmpeg.martin-riedl.de/download/macos/arm64/1783011502_8.1.2/ffmpeg.zip.sha256")
        #expect(FFmpegSource.version(fromDownloadURL: resolved) == "8.1.2")
        #expect(FFmpegSource.isTrusted(resolved))
        #expect(!FFmpegSource.isTrusted(URL(string: "http://ffmpeg.martin-riedl.de/x.zip")!))
        #expect(!FFmpegSource.isTrusted(URL(string: "https://example.com/ffmpeg.zip")!))
    }

    @Test func libreOfficePicksTheNewestStableVersion() {
        let index = #"""
        <a href="/libreoffice/">Parent Directory</a>
        <a href="25.8.7/">25.8.7/</a> <a href="26.2.6/">26.2.6/</a>
        <a href="26.8.1/">26.8.1/</a> <a href="26.8.0/">26.8.0/</a> <a href="26.10.0/">x</a>
        """#
        #expect(LibreOfficeSource.versions(inIndex: index) == ["26.10.0", "26.8.1", "26.8.0", "26.2.6", "25.8.7"])
        #expect(LibreOfficeSource.versions(inIndex: "<html></html>").isEmpty)
    }

    @Test func libreOfficeDiskImagesFollowTheArchitecture() {
        let arm = LibreOfficeSource.diskImageURL(version: "26.8.1", architecture: .arm64)
        #expect(arm.absoluteString
            == "https://download.documentfoundation.org/libreoffice/stable/26.8.1/mac/aarch64/LibreOffice_26.8.1_MacOS_aarch64.dmg")
        #expect(LibreOfficeSource.checksumURL(for: arm).absoluteString == arm.absoluteString + ".sha256")
        #expect(LibreOfficeSource.diskImageURL(version: "26.8.1", architecture: .x86_64).lastPathComponent
            == "LibreOffice_26.8.1_MacOS_x86-64.dmg")
    }

    @Test func thisMacsArchitecture() {
        #if arch(arm64)
        #expect(MachineArchitecture.current == .arm64)
        #endif
    }
}

struct ToolIntegrityTests {
    @Test func checksumsOfFiles() throws {
        let folder = try makeTemporaryFolder("sha")
        defer { try? FileManager.default.removeItem(at: folder) }
        let file = folder.appendingPathComponent("hello.txt")
        try Data("hello\n".utf8).write(to: file)
        let expected = "5891b5b522d5df086d0ff0b110fbd9d21bb4fc7163af34d08286a2e846f6be03"
        #expect(try ToolIntegrity.sha256(of: file) == expected)
        try ToolIntegrity.verify(file, sha256: expected.uppercased(), name: "hello")
        #expect(throws: ToolIntegrityError.checksumMismatch(file: "hello")) {
            try ToolIntegrity.verify(file, sha256: String(repeating: "0", count: 64), name: "hello")
        }
    }

    @Test func readsPublishedChecksums() {
        let hash = "ca074e0b13571efb30a31fc531c5f95e997030637325e5d5de92d67aefdf0dfc"
        #expect(ToolIntegrity.publishedChecksum(in: "\(hash)  ffmpeg.zip\n", for: "ffmpeg.zip") == hash)
        #expect(ToolIntegrity.publishedChecksum(in: "\(hash.uppercased()) *ffmpeg.zip", for: "ffmpeg.zip") == hash)
        #expect(ToolIntegrity.publishedChecksum(in: "\(hash)  ffprobe.zip", for: "ffmpeg.zip") == nil)
        #expect(ToolIntegrity.publishedChecksum(in: hash) == hash)
        #expect(ToolIntegrity.publishedChecksum(in: "<html>Not Found</html>") == nil)
        #expect(ToolIntegrity.publishedChecksum(in: "abc123  ffmpeg.zip") == nil)
    }

    @Test func requirementPinsTheTeam() {
        let requirement = ToolIntegrity.developerIDRequirement(identifier: "ffmpeg", teamID: FFmpegSource.teamID)
        #expect(requirement.contains(#"identifier "ffmpeg""#))
        #expect(requirement.contains(#"certificate leaf[subject.OU] = "KU3N25YGLU""#))
        #expect(requirement.contains("anchor apple generic"))
    }

    @Test func rejectsCodeFromAnyoneElse() {
        // Apple's own tools are signed, but not with the publisher's Developer ID.
        #expect(throws: ToolIntegrityError.badSignature(file: "ls")) {
            try ToolIntegrity.verifySignature(of: URL(fileURLWithPath: "/bin/ls"), identifier: "com.apple.ls", teamID: FFmpegSource.teamID, name: "ls")
        }
    }

    @Test(.enabled(if: Fixtures.hasFFmpeg))
    func acceptsTheRealBuildAndRejectsATamperedOne() async throws {
        let folder = try makeTemporaryFolder("signature")
        defer { try? FileManager.default.removeItem(at: folder) }
        try await ProcessRunner.run(URL(fileURLWithPath: "/usr/bin/ditto"), ["-x", "-k", Fixtures.ffprobeZip.path, folder.path])
        let ffprobe = folder.appendingPathComponent("ffprobe")
        try ToolIntegrity.verifySignature(of: ffprobe, identifier: "ffprobe", teamID: FFmpegSource.teamID, name: "ffprobe")
        // The right signer but the wrong program, or the wrong signer.
        #expect(throws: ToolIntegrityError.self) {
            try ToolIntegrity.verifySignature(of: ffprobe, identifier: "ffmpeg", teamID: FFmpegSource.teamID, name: "ffprobe")
        }
        #expect(throws: ToolIntegrityError.self) {
            try ToolIntegrity.verifySignature(of: ffprobe, identifier: "ffprobe", teamID: LibreOfficeSource.teamID, name: "ffprobe")
        }

        let tampered = folder.appendingPathComponent("tampered")
        var bytes = try Data(contentsOf: ffprobe)
        bytes[bytes.count / 2] ^= 0xFF
        try bytes.write(to: tampered)
        #expect(throws: ToolIntegrityError.badSignature(file: "ffprobe")) {
            try ToolIntegrity.verifySignature(of: tampered, identifier: "ffprobe", teamID: FFmpegSource.teamID, name: "ffprobe")
        }
    }
}

struct ToolInstallationTests {
    @Test(.enabled(if: Fixtures.hasFFmpeg))
    func installsVerifiedFFmpegAndFFprobe() async throws {
        let root = try makeTemporaryFolder("install")
        defer { try? FileManager.default.removeItem(at: root) }
        let source = try makeBuildFolder(in: root.appendingPathComponent("server"), ffmpegZip: Fixtures.ffmpegZip, ffprobeZip: Fixtures.ffprobeZip)
        let locations = temporaryLocations(in: root)
        let steps = StepRecorder()

        let entry = try await ToolInstallation.installFFmpeg(from: source, into: locations) { steps.append($0.step) }

        let ffmpeg = locations.tools.appendingPathComponent("ffmpeg")
        #expect(entry.version == "6.0")
        #expect(entry.paths == [ffmpeg.path, locations.tools.appendingPathComponent("ffprobe").path])
        #expect(FileManager.default.isExecutableFile(atPath: ffmpeg.path))
        #expect(hiddenItems(in: locations.tools).isEmpty)
        #expect(steps.steps.first == .downloading && steps.steps.contains(.verifying) && steps.steps.last == .installing)
        // Found by detection, and runs.
        let tools = ExternalTools.detect(in: ToolLocations(commandDirectories: [locations.tools], libreOfficeApps: []), ignoring: [])
        #expect(tools.ffmpeg == ffmpeg)
        let version = try run(ffmpeg.path, ["-version"])
        #expect(version.status == 0)
        #expect(version.output.hasPrefix("ffmpeg version"))

        // Installing again replaces the copies in place.
        _ = try await ToolInstallation.installFFmpeg(from: source, into: locations) { _ in }
        #expect(hiddenItems(in: locations.tools).isEmpty)

        try ToolInstallation.remove(entry, from: locations)
        #expect(!FileManager.default.fileExists(atPath: ffmpeg.path))
    }

    @Test(.enabled(if: Fixtures.hasFFmpeg))
    func aDamagedDownloadIsDeletedAndNothingIsInstalled() async throws {
        let root = try makeTemporaryFolder("damaged")
        defer { try? FileManager.default.removeItem(at: root) }
        let source = try makeBuildFolder(in: root.appendingPathComponent("server"), ffmpegZip: Fixtures.ffmpegZip, ffprobeZip: Fixtures.ffprobeZip)
        try "\(String(repeating: "a", count: 64))  ffmpeg.zip\n".write(
            to: source.deletingLastPathComponent().appendingPathComponent("ffmpeg.zip.sha256"), atomically: true, encoding: .utf8)
        let locations = temporaryLocations(in: root)

        await #expect(throws: ToolIntegrityError.checksumMismatch(file: "ffmpeg")) {
            _ = try await ToolInstallation.installFFmpeg(from: source, into: locations) { _ in }
        }
        #expect(try FileManager.default.contentsOfDirectory(atPath: locations.tools.path).isEmpty)
    }

    @Test func unsignedProgramsAreNeverInstalled() async throws {
        let root = try makeTemporaryFolder("unsigned")
        defer { try? FileManager.default.removeItem(at: root) }
        // A zip that looks right but holds a script nobody signed.
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
        let source = try makeBuildFolder(in: root.appendingPathComponent("server"), ffmpegZip: zips[0], ffprobeZip: zips[1], checksums: false)
        let locations = temporaryLocations(in: root)

        await #expect(throws: ToolIntegrityError.badSignature(file: "ffmpeg")) {
            _ = try await ToolInstallation.installFFmpeg(from: source, into: locations) { _ in }
        }
        #expect(try FileManager.default.contentsOfDirectory(atPath: locations.tools.path).isEmpty)
        #expect(!FileManager.default.fileExists(atPath: root.appendingPathComponent("ran").path))
    }

    @Test func aMissingDownloadFailsCleanly() async throws {
        let root = try makeTemporaryFolder("missing")
        defer { try? FileManager.default.removeItem(at: root) }
        let locations = temporaryLocations(in: root)
        await #expect(throws: (any Error).self) {
            _ = try await ToolInstallation.installFFmpeg(from: root.appendingPathComponent("nothing/ffmpeg.zip"), into: locations) { _ in }
        }
        #expect(try FileManager.default.contentsOfDirectory(atPath: locations.tools.path).isEmpty)
    }

    @Test(.enabled(if: Fixtures.hasLibreOffice && ProcessInfo.processInfo.environment["OPENSHELF_LIBREOFFICE_TESTS"] == "1"))
    func installsLibreOfficeFromTheDiskImage() async throws {
        let root = try makeTemporaryFolder("libreoffice")
        defer { try? FileManager.default.removeItem(at: root) }
        let locations = temporaryLocations(in: root)
        let entry = try await ToolInstallation.installLibreOffice(
            from: Fixtures.libreOfficeImage,
            sha256: Fixtures.libreOfficeSHA256,
            version: "26.8.1",
            into: locations
        ) { _ in }
        let app = locations.applications.appendingPathComponent("LibreOffice.app")
        #expect(entry.paths == [app.path])
        #expect(FileManager.default.isExecutableFile(atPath: app.appendingPathComponent("Contents/MacOS/soffice").path))
        #expect(hiddenItems(in: locations.applications).isEmpty)
        #expect(hiddenItems(in: locations.tools).isEmpty)
        #expect(ToolInstaller.version(of: app.appendingPathComponent("Contents/MacOS/soffice"))?.hasPrefix("26.8.1") == true)
        // The disk image was ejected.
        let mounts = try run("/sbin/mount", []).output
        #expect(!mounts.contains(root.path))
    }

    @Test(.enabled(if: Fixtures.hasLibreOffice))
    func aDiskImageWithTheWrongChecksumIsNeverOpened() async throws {
        let root = try makeTemporaryFolder("libreoffice-bad")
        defer { try? FileManager.default.removeItem(at: root) }
        let locations = temporaryLocations(in: root)
        await #expect(throws: ToolIntegrityError.checksumMismatch(file: "LibreOffice")) {
            _ = try await ToolInstallation.installLibreOffice(
                from: Fixtures.libreOfficeImage,
                sha256: String(repeating: "b", count: 64),
                version: "26.8.1",
                into: locations
            ) { _ in }
        }
        #expect(!FileManager.default.fileExists(atPath: locations.applications.appendingPathComponent("LibreOffice.app").path))
        #expect(try FileManager.default.contentsOfDirectory(atPath: locations.tools.path).isEmpty)
    }

    @Test func removeOnlyTouchesOpenShelfsFolders() throws {
        let root = try makeTemporaryFolder("remove")
        defer { try? FileManager.default.removeItem(at: root) }
        let locations = temporaryLocations(in: root)
        try FileManager.default.createDirectory(at: locations.tools, withIntermediateDirectories: true)
        let own = locations.tools.appendingPathComponent("ffmpeg")
        let elsewhere = root.appendingPathComponent("ffmpeg")
        try Data().write(to: own)
        try Data().write(to: elsewhere)
        let entry = InstalledTools.Entry(version: "1", paths: [own.path, elsewhere.path, "/bin/ls"], source: "test")
        try ToolInstallation.remove(entry, from: locations)
        #expect(!FileManager.default.fileExists(atPath: own.path))
        #expect(FileManager.default.fileExists(atPath: elsewhere.path))
        #expect(!locations.contains(URL(fileURLWithPath: "/bin/ls")))
        #expect(!locations.contains(locations.tools.appendingPathComponent("../../../../x")))
    }

    @Test func theRecordRoundTrips() throws {
        let root = try makeTemporaryFolder("record")
        defer { try? FileManager.default.removeItem(at: root) }
        let locations = temporaryLocations(in: root)
        #expect(InstalledTools.load(from: locations.record) == InstalledTools())
        var record = InstalledTools()
        record[.ffmpeg] = InstalledTools.Entry(version: "8.1.2", paths: ["/x/ffmpeg"], source: "https://ffmpeg.martin-riedl.de/…")
        try record.save(to: locations.record)
        #expect(InstalledTools.load(from: locations.record) == record)
        try Data("not json".utf8).write(to: locations.record)
        #expect(InstalledTools.load(from: locations.record) == InstalledTools())
    }
}

/// Downloads from the vendors themselves. Run with OPENSHELF_NETWORK_TESTS=1.
struct ToolDownloadNetworkTests {
    @Test(.enabled(if: Fixtures.allowsNetwork))
    func installsTheLatestFFmpegFromTheBuildServer() async throws {
        let root = try makeTemporaryFolder("network-ffmpeg")
        defer { try? FileManager.default.removeItem(at: root) }
        let locations = temporaryLocations(in: root)
        let entry = try await ToolInstallation.installFFmpeg(into: locations) { _ in }
        let ffmpeg = locations.tools.appendingPathComponent("ffmpeg")
        let version = try run(ffmpeg.path, ["-version"])
        #expect(version.status == 0)
        #expect(version.output.contains(entry.version))
        let ffprobe = try run(locations.tools.appendingPathComponent("ffprobe").path, ["-version"])
        #expect(ffprobe.status == 0)
    }

    @Test(.enabled(if: Fixtures.allowsNetwork))
    func findsTheLatestLibreOfficeAndItsChecksum() async throws {
        let release = try await ToolInstallation.latestLibreOffice(architecture: .arm64)
        #expect(release.diskImage.lastPathComponent == "LibreOffice_\(release.version)_MacOS_aarch64.dmg")
        #expect(release.sha256.count == 64)
        let intel = try await ToolInstallation.latestLibreOffice(architecture: .x86_64)
        #expect(intel.diskImage.path.contains("/mac/x86_64/"))
    }

    /// A small file from the same server, which sends it from a mirror: the
    /// download follows the redirect and matches the main server's checksum.
    @Test(.enabled(if: Fixtures.allowsNetwork))
    func downloadsFromAMirrorAndMatchesTheChecksum() async throws {
        let release = try await ToolInstallation.latestLibreOffice(architecture: .arm64)
        let name = "LibreOffice_\(release.version)_MacOS_aarch64_langpack_ab.dmg"
        let url = release.diskImage.deletingLastPathComponent().appendingPathComponent(name)
        let folder = try makeTemporaryFolder("mirror")
        defer { try? FileManager.default.removeItem(at: folder) }
        let file = folder.appendingPathComponent(name)
        let progress = ProgressCounter()

        let finalURL = try await FileDownload.download(url, to: file) { _, _ in progress.increment() }

        let published = try #require(try await FileDownload.text(at: LibreOfficeSource.checksumURL(for: url)))
        let expected = try #require(ToolIntegrity.publishedChecksum(in: published, for: name))
        try ToolIntegrity.verify(file, sha256: expected, name: name)
        #expect(finalURL.lastPathComponent == name)
        #expect(progress.count > 0)
    }
}

struct ToolStatusTests {
    let locations = InstallLocations(
        tools: URL(fileURLWithPath: "/Users/x/Library/Application Support/OpenShelf/Tools"),
        applications: URL(fileURLWithPath: "/Users/x/Applications")
    )

    private func status(_ tool: ExtraTool, _ tools: ExternalTools, installed: InstalledTools = InstalledTools(), progress: InstallProgress? = nil, failure: String? = nil) -> ToolStatus {
        ToolInstaller.status(of: tool, tools: tools, installed: installed, progress: progress, failure: failure, locations: locations)
    }

    @Test func missingTools() {
        #expect(status(.ffmpeg, ExternalTools()) == .notInstalled)
        #expect(status(.libreOffice, ExternalTools()) == .notInstalled)
    }

    @Test func progressAndFailuresComeFirst() {
        let progress = InstallProgress(step: .downloading, fraction: 0.5, detail: "1 of 2 MB")
        let brew = ExternalTools(ffmpeg: URL(fileURLWithPath: "/opt/homebrew/bin/ffmpeg"))
        #expect(status(.ffmpeg, ExternalTools(), progress: progress) == .installing(progress))
        #expect(status(.ffmpeg, ExternalTools(), failure: "Couldn’t reach it") == .failed("Couldn’t reach it"))
        #expect(status(.ffmpeg, brew, progress: progress) == .installing(progress))
    }

    @Test func homebrewCopiesCantBeRemovedFromSettings() {
        let brew = ExternalTools(ffmpeg: URL(fileURLWithPath: "/opt/homebrew/bin/ffmpeg"))
        guard case .installed(let info) = status(.ffmpeg, brew) else {
            Issue.record("Expected installed")
            return
        }
        #expect(info.source == .homebrew)
        #expect(!info.canRemove)
    }

    @Test func openShelfsOwnCopiesCanBeRemoved() {
        let own = locations.tools.appendingPathComponent("ffmpeg")
        var installed = InstalledTools()
        installed[.ffmpeg] = InstalledTools.Entry(version: "8.1.2", paths: [own.path], source: "https://ffmpeg.martin-riedl.de")
        let expected = InstalledToolInfo(path: own, source: .openShelf, version: "8.1.2", canRemove: true)
        #expect(status(.ffmpeg, ExternalTools(ffmpeg: own), installed: installed) == .installed(expected))
        // An OpenShelf copy that Homebrew's takes precedence over isn't what's shown.
        guard case .installed(let info) = status(.ffmpeg, ExternalTools(ffmpeg: URL(fileURLWithPath: "/opt/homebrew/bin/ffmpeg")), installed: installed) else {
            Issue.record("Expected installed")
            return
        }
        #expect(info.source == .homebrew && !info.canRemove)

        let app = locations.applications.appendingPathComponent("LibreOffice.app")
        installed[.libreOffice] = InstalledTools.Entry(version: "26.8.1", paths: [app.path], source: "https://download.documentfoundation.org")
        let soffice = ExternalTools(libreOffice: app.appendingPathComponent("Contents/MacOS/soffice"))
        guard case .installed(let office) = status(.libreOffice, soffice, installed: installed) else {
            Issue.record("Expected installed")
            return
        }
        #expect(office.source == .openShelf && office.canRemove && office.version == "26.8.1")
    }

    @Test func sourcesAndVersionsFromPaths() {
        #expect(ToolInstaller.source(of: URL(fileURLWithPath: "/opt/homebrew/bin/ffmpeg")) == .homebrew)
        #expect(ToolInstaller.source(of: URL(fileURLWithPath: "/opt/local/bin/ffmpeg")) == .macPorts)
        #expect(ToolInstaller.source(of: URL(fileURLWithPath: "/Applications/LibreOffice.app/Contents/MacOS/soffice")) == .applications)
        #expect(ToolInstaller.source(of: URL(fileURLWithPath: "/usr/bin/ffmpeg")) == .other)
        #expect(ToolInstaller.version(of: URL(fileURLWithPath: "/tmp/nowhere/Cellar/ffmpeg/9.0.2_1/bin/ffmpeg")) == "9.0.2")
        #expect(ToolInstaller.version(of: URL(fileURLWithPath: "/usr/bin/ffmpeg")) == nil)
    }
}

private final class ProgressCounter: @unchecked Sendable {
    private let lock = NSLock()
    private var value = 0

    func increment() { lock.withLock { value += 1 } }
    var count: Int { lock.withLock { value } }
}

/// Collects install steps from the progress callback.
private final class StepRecorder: @unchecked Sendable {
    private let lock = NSLock()
    private var recorded: [InstallProgress.Step] = []

    func append(_ step: InstallProgress.Step) {
        lock.withLock {
            if recorded.last != step { recorded.append(step) }
        }
    }

    var steps: [InstallProgress.Step] { lock.withLock { recorded } }
}

/// Pandoc from its GitHub releases, installed from a local copy of a release:
/// GitHub's listing, with the zip it points to beside it.
struct PandocInstallTests {
    /// A listing like GitHub's, naming `zip` for both processors.
    private func release(in root: URL, zip: URL, digest: String?) throws -> URL {
        let assets = ["arm64", "x86_64"].map { architecture -> [String: Any] in
            var asset: [String: Any] = [
                "name": "pandoc-3.12.1-\(architecture)-macOS.zip",
                "browser_download_url": zip.absoluteString,
            ]
            if let digest { asset["digest"] = digest }
            return asset
        } + [["name": "pandoc-3.12.1-windows-x86_64.msi", "browser_download_url": "https://example.com/x.msi", "digest": "sha256:" + String(repeating: "0", count: 64)]]
        let url = root.appendingPathComponent("latest.json")
        try JSONSerialization.data(withJSONObject: ["tag_name": "3.12.1", "assets": assets]).write(to: url)
        return url
    }

    @Test func picksThisMacsZipAndItsDigest() throws {
        let json = Data(#"{"tag_name":"3.12.1","assets":[{"name":"pandoc-3.12.1-x86_64-macOS.zip","browser_download_url":"https://github.com/jgm/pandoc/releases/download/3.12.1/pandoc-3.12.1-x86_64-macOS.zip","digest":"sha256:81ED3E2CFECF096DF15D407152F89A684FD65221082B919963F738E977FAD3BC"},{"name":"pandoc-3.12.1-arm64-macOS.zip","browser_download_url":"https://github.com/jgm/pandoc/releases/download/3.12.1/pandoc-3.12.1-arm64-macOS.zip"}]}"#.utf8)
        let intel = try #require(PandocSource.download(inRelease: json, for: .x86_64))
        #expect(intel.version == "3.12.1")
        #expect(intel.url.lastPathComponent == "pandoc-3.12.1-x86_64-macOS.zip")
        #expect(intel.sha256 == "81ed3e2cfecf096df15d407152f89a684fd65221082b919963f738e977fad3bc")
        #expect(try #require(PandocSource.download(inRelease: json, for: .arm64)).sha256 == nil)
        #expect(PandocSource.download(inRelease: Data("{}".utf8), for: .arm64) == nil)
        #expect(PandocSource.zipName(version: "3.12.1", architecture: .arm64) == "pandoc-3.12.1-arm64-macOS.zip")
    }

    /// A zip laid out like Pandoc's, holding a script nobody signed.
    private func unsignedZip(in root: URL) async throws -> URL {
        let bin = root.appendingPathComponent("payload/pandoc-3.12.1-arm64/bin", isDirectory: true)
        try FileManager.default.createDirectory(at: bin, withIntermediateDirectories: true)
        let script = bin.appendingPathComponent("pandoc")
        try Data("#!/bin/sh\ntouch \(root.path)/ran\n".utf8).write(to: script)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: script.path)
        let zip = root.appendingPathComponent("pandoc.zip")
        try await ProcessRunner.run(URL(fileURLWithPath: "/usr/bin/ditto"), ["-c", "-k", root.appendingPathComponent("payload").path, zip.path])
        return zip
    }

    @Test func unsignedPandocIsNeverInstalled() async throws {
        let root = try makeTemporaryFolder("pandoc-unsigned")
        defer { try? FileManager.default.removeItem(at: root) }
        let zip = try await unsignedZip(in: root)
        let listing = try release(in: root, zip: zip, digest: "sha256:" + (try ToolIntegrity.sha256(of: zip)))
        let locations = temporaryLocations(in: root)
        await #expect(throws: ToolIntegrityError.badSignature(file: "Pandoc")) {
            _ = try await ToolInstallation.installPandoc(release: listing, architecture: .arm64, into: locations) { _ in }
        }
        #expect(try FileManager.default.contentsOfDirectory(atPath: locations.tools.path).isEmpty)
        #expect(!FileManager.default.fileExists(atPath: root.appendingPathComponent("ran").path))
    }

    @Test func aDamagedOrUnlistedDownloadIsRefused() async throws {
        let root = try makeTemporaryFolder("pandoc-checksum")
        defer { try? FileManager.default.removeItem(at: root) }
        let zip = try await unsignedZip(in: root)
        let locations = temporaryLocations(in: root)
        let wrong = try release(in: root, zip: zip, digest: "sha256:" + String(repeating: "a", count: 64))
        await #expect(throws: ToolIntegrityError.checksumMismatch(file: "Pandoc")) {
            _ = try await ToolInstallation.installPandoc(release: wrong, architecture: .arm64, into: locations) { _ in }
        }
        let unlisted = try release(in: root, zip: zip, digest: nil)
        await #expect(throws: ToolIntegrityError.checksumMissing(file: "Pandoc")) {
            _ = try await ToolInstallation.installPandoc(release: unlisted, architecture: .arm64, into: locations) { _ in }
        }
        #expect(hiddenItems(in: locations.tools).isEmpty)
    }

    @Test(.enabled(if: Fixtures.hasPandoc))
    func installsTheSignedBuild() async throws {
        let root = try makeTemporaryFolder("pandoc-real")
        defer { try? FileManager.default.removeItem(at: root) }
        let listing = try release(in: root, zip: Fixtures.pandocZip, digest: "sha256:" + (try ToolIntegrity.sha256(of: Fixtures.pandocZip)))
        let locations = temporaryLocations(in: root)
        let entry = try await ToolInstallation.installPandoc(release: listing, architecture: .arm64, into: locations) { _ in }
        let installed = locations.tools.appendingPathComponent("pandoc")
        #expect(entry.version == "3.12.1")
        #expect(entry.paths == [installed.path])
        #expect(hiddenItems(in: locations.tools).isEmpty)
        let result = try run(installed.path, ["--version"])
        #expect(result.status == 0)
        #expect(result.output.hasPrefix("pandoc"))
        // And OpenShelf finds it there.
        let tools = ExternalTools.detect(in: ToolLocations(commandDirectories: [locations.tools], libreOfficeApps: []), ignoring: [])
        #expect(tools.pandoc == installed)
    }
}
