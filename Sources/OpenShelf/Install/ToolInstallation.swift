import Foundation

/// Where OpenShelf puts the tools it installs. Tests pass temporary folders.
struct InstallLocations: Sendable {
    /// ffmpeg and ffprobe, and the record of what OpenShelf installed.
    var tools: URL
    /// Where LibreOffice.app goes. ~/Applications needs no administrator.
    var applications: URL

    static var standard: InstallLocations {
        InstallLocations(
            tools: ManagedTools.directory,
            applications: FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Applications", isDirectory: true)
        )
    }

    var record: URL { tools.appendingPathComponent("Installed.json") }

    /// Whether `url` is somewhere OpenShelf installs to, so a damaged or
    /// edited record can never make Remove delete anything else.
    func contains(_ url: URL) -> Bool {
        let path = url.standardizedFileURL.path
        return [tools, applications].contains { folder in
            path.hasPrefix(folder.standardizedFileURL.path + "/")
        }
    }
}

/// What OpenShelf installed itself, kept so only those copies can be removed.
struct InstalledTools: Codable, Equatable, Sendable {
    struct Entry: Codable, Equatable, Sendable {
        var version: String
        var paths: [String]
        /// Where it was downloaded from.
        var source: String
    }

    /// Keyed by `ExtraTool.rawValue`.
    var entries: [String: Entry] = [:]

    subscript(tool: ExtraTool) -> Entry? {
        get { entries[tool.rawValue] }
        set { entries[tool.rawValue] = newValue }
    }

    static func load(from url: URL) -> InstalledTools {
        guard let data = try? Data(contentsOf: url) else { return InstalledTools() }
        return (try? JSONDecoder().decode(InstalledTools.self, from: data)) ?? InstalledTools()
    }

    func save(to url: URL) throws {
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        try encoder.encode(self).write(to: url, options: .atomic)
    }
}

/// How far an install has got, for the progress bar in Settings.
struct InstallProgress: Equatable, Sendable {
    enum Step: Equatable, Sendable {
        case downloading
        case verifying
        case installing
    }

    var step: Step
    /// 0...1, or nil while there's no measure of it.
    var fraction: Double?
    /// "12 of 55 MB", "Checking the signature…"
    var detail: String
    /// Where it's coming from, for the status line under the progress.
    var source: InstallSource? = nil
}

/// The steps of downloading, checking and installing a tool. Every download
/// is checked against the publisher's checksum and code signature before
/// anything in it runs, and goes into place only once it has passed, by
/// renaming it from a hidden folder beside its destination.
enum ToolInstallation {
    typealias Report = @Sendable (InstallProgress) -> Void

    private static let ditto = URL(fileURLWithPath: "/usr/bin/ditto")
    private static let hdiutil = URL(fileURLWithPath: "/usr/bin/hdiutil")

    // MARK: ffmpeg

    /// Installs ffmpeg and ffprobe of the same build into `locations.tools`.
    /// - Parameter ffmpegURL: Where to get ffmpeg.zip; ffprobe.zip is taken
    ///   from the same folder once redirects are followed.
    static func installFFmpeg(
        from ffmpegURL: URL = FFmpegSource.latestURL(of: "ffmpeg", for: .current),
        into locations: InstallLocations,
        report: @escaping Report
    ) async throws -> InstalledTools.Entry {
        let staging = try makeStagingFolder(in: locations.tools)
        defer { try? FileManager.default.removeItem(at: staging) }

        let ffmpegZip = staging.appendingPathComponent("ffmpeg.zip")
        let resolved = try await download(ffmpegURL, to: ffmpegZip, label: "ffmpeg", part: 0, of: 2, report: report)
        guard resolved.isFileURL || FFmpegSource.isTrusted(resolved) else {
            throw DownloadError.untrustedLocation(resolved.host ?? resolved.absoluteString)
        }
        let ffprobeURL = FFmpegSource.sibling("ffprobe.zip", of: resolved)
        let ffprobeZip = staging.appendingPathComponent("ffprobe.zip")
        try await download(ffprobeURL, to: ffprobeZip, label: "ffprobe", part: 1, of: 2, report: report)

        let zips = [
            FFmpegZip(name: "ffmpeg", file: ffmpegZip, checksum: FFmpegSource.checksumURL(for: resolved), listedAs: resolved.lastPathComponent),
            FFmpegZip(name: "ffprobe", file: ffprobeZip, checksum: FFmpegSource.checksumURL(for: ffprobeURL), listedAs: ffprobeURL.lastPathComponent),
        ]
        let paths = try await installVerified(zips, staging: staging, into: locations, report: report)
        let version = FFmpegSource.version(fromDownloadURL: resolved) ?? "latest"
        return InstalledTools.Entry(version: version, paths: paths, source: resolved.absoluteString)
    }

    /// Installs ffmpeg and ffprobe from OpenShelf's mirror of the build
    /// server (`FFmpegSource.mirrorBase`), checked exactly as if they came
    /// from the build server itself.
    static func installFFmpegFromMirror(
        _ base: URL = FFmpegSource.mirrorBase,
        architecture: MachineArchitecture = .current,
        into locations: InstallLocations,
        report: @escaping Report
    ) async throws -> InstalledTools.Entry {
        let staging = try makeStagingFolder(in: locations.tools)
        defer { try? FileManager.default.removeItem(at: staging) }

        var zips: [FFmpegZip] = []
        for (part, name) in ["ffmpeg", "ffprobe"].enumerated() {
            let url = FFmpegSource.mirrorURL(of: name, for: architecture, base: base)
            let file = staging.appendingPathComponent("\(name).zip")
            // GitHub serves release files from its storage hosts, so the
            // redirect's target isn't pinned; nothing is derived from it, and
            // the signature decides what the file is.
            let resolved = try await download(url, to: file, label: name, part: part, of: 2, report: report)
            guard resolved.isFileURL || resolved.scheme == "https" else {
                throw DownloadError.untrustedLocation(resolved.host ?? resolved.absoluteString)
            }
            zips.append(FFmpegZip(name: name, file: file, checksum: FFmpegSource.checksumURL(for: url), listedAs: url.lastPathComponent))
        }
        let paths = try await installVerified(zips, staging: staging, into: locations, report: report)
        let source = FFmpegSource.mirrorURL(of: "ffmpeg", for: architecture, base: base)
        return InstalledTools.Entry(version: "latest", paths: paths, source: source.absoluteString)
    }

    /// A downloaded zip of ffmpeg or ffprobe, and where its checksum is listed.
    private struct FFmpegZip {
        /// "ffmpeg" or "ffprobe": the program inside and its signing identifier.
        var name: String
        var file: URL
        var checksum: URL
        /// The file name the checksum file lists it under.
        var listedAs: String
    }

    /// Checks both zips, unpacks them, checks the programs' signatures and
    /// moves them into place, returning their paths. Nothing is moved unless
    /// both pass.
    private static func installVerified(
        _ zips: [FFmpegZip],
        staging: URL,
        into locations: InstallLocations,
        report: @escaping Report
    ) async throws -> [String] {
        report(InstallProgress(step: .verifying, fraction: nil, detail: "Checking the download…"))
        var binaries: [(name: String, file: URL)] = []
        for zip in zips {
            // The signature below is what proves who built it; the checksum,
            // when the server lists one, catches a damaged download early.
            if let expected = try await publishedChecksum(at: zip.checksum, for: zip.listedAs) {
                try ToolIntegrity.verify(zip.file, sha256: expected, name: zip.name)
            }
            let folder = staging.appendingPathComponent(zip.name, isDirectory: true)
            try await ProcessRunner.run(ditto, ["-x", "-k", zip.file.path, folder.path])
            let binary = folder.appendingPathComponent(zip.name)
            guard FileManager.default.fileExists(atPath: binary.path) else {
                throw ConversionError.failed("The download didn’t contain \(zip.name).")
            }
            try ToolIntegrity.verifySignature(of: binary, identifier: zip.name, teamID: FFmpegSource.teamID, name: zip.name)
            try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: binary.path)
            binaries.append((zip.name, binary))
        }

        report(InstallProgress(step: .installing, fraction: nil, detail: "Installing…"))
        var paths: [String] = []
        for (name, binary) in binaries {
            let destination = locations.tools.appendingPathComponent(name)
            try moveIntoPlace(binary, at: destination)
            paths.append(destination.path)
        }
        return paths
    }

    // MARK: LibreOffice

    /// The newest stable LibreOffice for this Mac, with its published checksum.
    static func latestLibreOffice(architecture: MachineArchitecture = .current) async throws -> (version: String, diskImage: URL, sha256: String) {
        guard let index = try await FileDownload.text(at: LibreOfficeSource.stableIndex) else {
            throw DownloadError.httpStatus(404, host: LibreOfficeSource.stableIndex.host ?? "")
        }
        // A brand-new version can be listed before its Mac builds are up.
        for version in LibreOfficeSource.versions(inIndex: index).prefix(3) {
            let diskImage = LibreOfficeSource.diskImageURL(version: version, architecture: architecture)
            if let sha256 = try await publishedChecksum(at: LibreOfficeSource.checksumURL(for: diskImage), for: diskImage.lastPathComponent) {
                return (version, diskImage, sha256)
            }
        }
        throw ConversionError.failed("No LibreOffice download for this Mac was found. Try again later.")
    }

    /// Downloads the LibreOffice disk image, checks it, and copies the app
    /// into `locations.applications`.
    static func installLibreOffice(
        into locations: InstallLocations,
        report: @escaping Report
    ) async throws -> InstalledTools.Entry {
        report(InstallProgress(step: .downloading, fraction: nil, detail: "Finding the latest version…"))
        let release = try await latestLibreOffice()
        return try await installLibreOffice(from: release.diskImage, sha256: release.sha256, version: release.version, into: locations, report: report)
    }

    static func installLibreOffice(
        from diskImageURL: URL,
        sha256: String,
        version: String,
        into locations: InstallLocations,
        report: @escaping Report
    ) async throws -> InstalledTools.Entry {
        let destination = locations.applications.appendingPathComponent("LibreOffice.app")
        guard !FileManager.default.fileExists(atPath: destination.path) else {
            throw ConversionError.failed("There’s already a LibreOffice in \(locations.applications.lastPathComponent).")
        }
        let staging = try makeStagingFolder(in: locations.tools)
        defer { try? FileManager.default.removeItem(at: staging) }

        let diskImage = staging.appendingPathComponent("LibreOffice.dmg")
        try await download(diskImageURL, to: diskImage, label: "LibreOffice", part: 0, of: 1, report: report)
        report(InstallProgress(step: .verifying, fraction: nil, detail: "Checking the download…"))
        // Disk images are only opened once they match the published checksum.
        try ToolIntegrity.verify(diskImage, sha256: sha256, name: "LibreOffice")

        report(InstallProgress(step: .installing, fraction: nil, detail: "Copying LibreOffice…"))
        let appStaging = try makeStagingFolder(in: locations.applications)
        defer { try? FileManager.default.removeItem(at: appStaging) }
        let mountPoint = staging.appendingPathComponent("Volume", isDirectory: true)
        let stagedApp = appStaging.appendingPathComponent("LibreOffice.app")
        try await ProcessRunner.run(hdiutil, ["attach", "-nobrowse", "-readonly", "-noautoopen", "-mountpoint", mountPoint.path, diskImage.path])
        do {
            try await ProcessRunner.run(ditto, [mountPoint.appendingPathComponent("LibreOffice.app").path, stagedApp.path])
        } catch {
            await detach(mountPoint)
            throw error
        }
        await detach(mountPoint)

        report(InstallProgress(step: .verifying, fraction: nil, detail: "Checking the signature…"))
        try await Task.detached(priority: .userInitiated) {
            try ToolIntegrity.verifySignature(
                of: stagedApp,
                identifier: LibreOfficeSource.bundleIdentifier,
                teamID: LibreOfficeSource.teamID,
                name: "LibreOffice"
            )
        }.value
        try Task.checkCancellation()
        try moveIntoPlace(stagedApp, at: destination)
        return InstalledTools.Entry(version: version, paths: [destination.path], source: diskImageURL.absoluteString)
    }

    // MARK: Pandoc

    /// Installs the newest Pandoc into `locations.tools`.
    /// - Parameter release: GitHub's listing of the release to install.
    static func installPandoc(
        release: URL = PandocSource.latestRelease,
        architecture: MachineArchitecture = .current,
        into locations: InstallLocations,
        report: @escaping Report
    ) async throws -> InstalledTools.Entry {
        report(InstallProgress(step: .downloading, fraction: nil, detail: "Finding the latest version…"))
        let listing: String?
        if release.isFileURL {
            listing = try? String(contentsOf: release, encoding: .utf8)
        } else {
            listing = try await FileDownload.text(at: release)
        }
        guard let listing, let found = PandocSource.download(inRelease: Data(listing.utf8), for: architecture) else {
            throw ConversionError.failed("No Pandoc download for this Mac was found. Try again later.")
        }
        // GitHub lists a digest for every release file; without one, nothing is installed.
        guard let sha256 = found.sha256 else { throw ToolIntegrityError.checksumMissing(file: "Pandoc") }

        let staging = try makeStagingFolder(in: locations.tools)
        defer { try? FileManager.default.removeItem(at: staging) }
        let zip = staging.appendingPathComponent("pandoc.zip")
        // GitHub serves release files from its storage hosts; the checksum
        // and signature decide what the file is.
        let resolved = try await download(found.url, to: zip, label: "Pandoc", part: 0, of: 1, report: report)
        guard resolved.isFileURL || resolved.scheme == "https" else {
            throw DownloadError.untrustedLocation(resolved.host ?? resolved.absoluteString)
        }

        report(InstallProgress(step: .verifying, fraction: nil, detail: "Checking the download…"))
        try ToolIntegrity.verify(zip, sha256: sha256, name: "Pandoc")
        let folder = staging.appendingPathComponent("pandoc", isDirectory: true)
        try await ProcessRunner.run(ditto, ["-x", "-k", zip.path, folder.path])
        // The zip holds pandoc-<version>-<architecture>/bin/pandoc.
        let binary = (try? FileManager.default.contentsOfDirectory(at: folder, includingPropertiesForKeys: nil))?
            .map { $0.appendingPathComponent("bin/pandoc") }
            .first { FileManager.default.fileExists(atPath: $0.path) }
        guard let binary else { throw ConversionError.failed("The download didn’t contain Pandoc.") }
        report(InstallProgress(step: .verifying, fraction: nil, detail: "Checking the signature…"))
        try ToolIntegrity.verifySignature(of: binary, identifier: "pandoc", teamID: PandocSource.teamID, name: "Pandoc")
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: binary.path)

        report(InstallProgress(step: .installing, fraction: nil, detail: "Installing…"))
        let destination = locations.tools.appendingPathComponent("pandoc")
        try moveIntoPlace(binary, at: destination)
        return InstalledTools.Entry(version: found.version, paths: [destination.path], source: found.url.absoluteString)
    }

    // MARK: Removing

    /// Removes what OpenShelf installed for `entry`: command-line tools are
    /// deleted, an app goes to the Trash. Paths outside OpenShelf's own
    /// install folders are left alone.
    static func remove(_ entry: InstalledTools.Entry, from locations: InstallLocations) throws {
        for path in entry.paths {
            let url = URL(fileURLWithPath: path)
            guard locations.contains(url), FileManager.default.fileExists(atPath: url.path) else { continue }
            if url.pathExtension == "app" {
                try FileManager.default.trashItem(at: url, resultingItemURL: nil)
            } else {
                try FileManager.default.removeItem(at: url)
            }
        }
    }

    // MARK: Plumbing

    /// A hidden folder in `directory`, on the same volume as the final
    /// location so moving into place is a rename.
    static func makeStagingFolder(in directory: URL) throws -> URL {
        let folder = directory.appendingPathComponent(".openshelf-install-\(UUID().uuidString.prefix(8))", isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        return folder
    }

    /// Renames a checked file or app into place, replacing an older copy.
    static func moveIntoPlace(_ item: URL, at destination: URL) throws {
        if FileManager.default.fileExists(atPath: destination.path) {
            _ = try FileManager.default.replaceItemAt(destination, withItemAt: item)
        } else {
            try FileManager.default.moveItem(at: item, to: destination)
        }
    }

    /// Downloads one of `parts` files, reporting overall progress.
    @discardableResult
    private static func download(_ url: URL, to destination: URL, label: String, part: Int, of parts: Int, report: @escaping Report) async throws -> URL {
        report(InstallProgress(step: .downloading, fraction: Double(part) / Double(parts), detail: "Downloading \(label)…"))
        return try await FileDownload.download(url, to: destination) { received, expected in
            guard let expected else {
                report(InstallProgress(step: .downloading, fraction: nil, detail: "Downloading \(label)… \(megabytes(received))"))
                return
            }
            let share: Double = Double(received) / Double(max(expected, 1))
            let fraction: Double = (Double(part) + share) / Double(parts)
            report(InstallProgress(step: .downloading, fraction: fraction, detail: "\(megabytes(received)) of \(megabytes(expected))"))
        }
    }

    private static func megabytes(_ bytes: Int64) -> String {
        ByteCountFormatter.string(fromByteCount: bytes, countStyle: .file)
    }

    /// The checksum a server lists for `fileName`, or nil when it lists none.
    private static func publishedChecksum(at url: URL, for fileName: String) async throws -> String? {
        if url.isFileURL {
            guard let text = try? String(contentsOf: url, encoding: .utf8) else { return nil }
            return ToolIntegrity.publishedChecksum(in: text, for: fileName)
        }
        guard let text = try await FileDownload.text(at: url) else { return nil }
        return ToolIntegrity.publishedChecksum(in: text, for: fileName)
    }

    /// Ejects a mounted disk image, even when the install was cancelled.
    private static func detach(_ mountPoint: URL) async {
        await Task.detached {
            try? await ProcessRunner.run(hdiutil, ["detach", mountPoint.path, "-force"])
        }.value
    }
}
