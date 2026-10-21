import Foundation

/// ZIP and tar creation and archive extraction with the tools that ship with macOS.
enum ArchiveConversion {
    private static let ditto = URL(fileURLWithPath: "/usr/bin/ditto")
    private static let zipTool = URL(fileURLWithPath: "/usr/bin/zip")
    private static let tar = URL(fileURLWithPath: "/usr/bin/tar")
    private static let gzip = URL(fileURLWithPath: "/usr/bin/gzip")

    /// Files macOS keeps beside real ones: Finder's folder settings (".DS_Store"),
    /// AppleDouble resource forks and extended attributes ("._name"), and the
    /// folder archives carry those in ("__MACOSX"). They're clutter anywhere
    /// else, so archives are made without them and extracting ignores them.
    static func isMacMetadata(_ name: String) -> Bool {
        name == ".DS_Store" || name == "__MACOSX" || name.hasPrefix("._")
    }

    // MARK: Creating

    /// One file or folder becomes "<name>.zip"; several become "Archive.zip"
    /// next to the first.
    static func zip(_ sources: [URL]) async throws -> URL {
        guard let first = sources.first else { throw ConversionError.failed("Nothing to compress.") }
        let baseName = sources.count == 1 ? first.lastPathComponent : "Archive"
        let destination = OutputNaming.uniqueURL(in: first.deletingLastPathComponent(), baseName: baseName, pathExtension: "zip")
        do {
            try await makeZip(of: try archiveEntries(for: sources), at: destination)
        } catch {
            try? FileManager.default.removeItem(at: destination)
            throw FailureMessage.describe(error, source: first)
        }
        return destination
    }

    /// Adds each group's paths with `zip`, which reads the list from a file so
    /// any number of entries fit. (`ditto -c -k` can't leave .DS_Store out, and
    /// with `--sequesterRsrc` adds a "__MACOSX" folder of resource forks.)
    private static func makeZip(of groups: [ArchiveGroup], at destination: URL) async throws {
        let list = FileManager.default.temporaryDirectory.appendingPathComponent("OpenShelf-zip-\(UUID().uuidString).txt")
        defer { try? FileManager.default.removeItem(at: list) }
        for group in groups {
            // Foundation hands out decomposed names ("e" + accent); other systems
            // expect the composed form, and APFS finds the file by either.
            let names = group.paths.map(\.precomposedStringWithCanonicalMapping)
            try Data(names.joined(separator: "\n").utf8).write(to: list)
            // -y stores symbolic links as links; -@ reads the names to add.
            try await ProcessRunner.run(zipTool, ["-q", "-y", destination.path, "-@"], currentDirectory: group.directory, standardInputFile: list)
        }
    }

    /// The paths of the files and folders in `sources`, relative to the folder
    /// each one is in, grouped by that folder. Mac metadata inside a folder is
    /// left out.
    static func archiveEntries(for sources: [URL]) throws -> [ArchiveGroup] {
        var groups: [ArchiveGroup] = []
        var names = Set<String>()
        for source in sources {
            let name = source.lastPathComponent
            // Items from different folders all go in at the top, where a second
            // one with the same name would replace the first.
            guard names.insert(name).inserted else {
                throw ConversionError.failed("Two of the items are named “\(name)”, so they can’t go in one archive. Rename one and try again.")
            }
            let directory = source.deletingLastPathComponent()
            var paths = [name]
            // A link to a folder is stored as a link (the enumerator doesn't
            // follow links inside folders either).
            if isPlainFolder(source, allowsHidden: true), let enumerator = FileManager.default.enumerator(atPath: source.path) {
                while let relative = enumerator.nextObject() as? String {
                    guard !isMacMetadata((relative as NSString).lastPathComponent) else {
                        // Only a folder's own contents can be skipped; for a file
                        // this would skip the rest of the folder it's in.
                        if enumerator.fileAttributes?[.type] as? FileAttributeType == .typeDirectory {
                            enumerator.skipDescendants()
                        }
                        continue
                    }
                    paths.append(name + "/" + relative)
                }
            }
            if let index = groups.firstIndex(where: { $0.directory.path == directory.path }) {
                groups[index].paths += paths
            } else {
                groups.append(ArchiveGroup(directory: directory, paths: paths))
            }
        }
        return groups
    }

    /// A tar archive (gzip-compressed when `gzip` is true) of one file or folder
    /// ("<name>.tar.gz"), or of several ("Archive.tar.gz" next to the first).
    static func tar(_ sources: [URL], gzip: Bool) async throws -> URL {
        guard let first = sources.first else { throw ConversionError.failed("Nothing to archive.") }
        let directory = first.deletingLastPathComponent()
        let pathExtension = gzip ? "tar.gz" : "tar"
        let baseName = sources.count == 1 ? first.lastPathComponent : "Archive"
        let destination = OutputNaming.uniqueURL(in: directory, baseName: baseName, pathExtension: pathExtension)
        // Paths relative to the first file's folder keep the structure; files from
        // elsewhere are added with their full path (bsdtar strips the leading /).
        let names = sources.map { $0.deletingLastPathComponent().path == directory.path ? $0.lastPathComponent : $0.path }
        // --no-mac-metadata keeps extended attributes from becoming "._" entries;
        // the excludes leave out the same Mac files as a ZIP does.
        let excludes = [".DS_Store", "._*", "__MACOSX"].flatMap { ["--exclude", $0] }
        do {
            try await ProcessRunner.run(
                tar,
                [gzip ? "-czf" : "-cf", destination.path, "--no-mac-metadata"] + excludes + ["-C", directory.path] + names
            )
        } catch {
            try? FileManager.default.removeItem(at: destination)
            throw FailureMessage.describe(error, source: first)
        }
        return destination
    }

    // MARK: Extracting

    /// Extracts next to the archive. An archive holding a single folder gives
    /// that folder; anything else goes in a new folder named after the archive.
    static func extract(_ source: URL) async throws -> URL {
        let name = source.lastPathComponent.lowercased()
        var baseName = source.deletingPathExtension().lastPathComponent
        if baseName.lowercased().hasSuffix(".tar") {
            baseName = String(baseName.dropLast(4))
        }
        let directory = source.deletingLastPathComponent()

        // A lone .gz (not a tarball) is a single compressed file, not an archive.
        if name.hasSuffix(".gz") && !name.hasSuffix(".tar.gz") {
            let destination = OutputNaming.uniqueURL(in: directory, baseName: baseName, pathExtension: nil)
            try await ProcessRunner.run(gzip, ["-dc", source.path], standardOutputFile: destination)
            return destination
        }

        // Unpack into a hidden folder beside the archive (same volume, so the
        // result moves into place instantly), then decide what to keep it as.
        let staging = directory.appendingPathComponent(".OpenShelf-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: staging, withIntermediateDirectories: false)
        do {
            if name.hasSuffix(".zip") {
                try await ProcessRunner.run(ditto, ["-x", "-k", source.path, staging.path])
            } else {
                // bsdtar reads tar, gzip, bzip2, xz, 7z and most RAR files.
                try await ProcessRunner.run(tar, ["-xf", source.path, "-C", staging.path])
            }
            return try placeExtracted(staging, archiveName: baseName, in: directory)
        } catch {
            try? FileManager.default.removeItem(at: staging)
            throw error
        }
    }

    /// Moves what was unpacked into `staging` into `directory`. When it's one
    /// folder (Mac metadata aside), that folder goes there directly, renamed
    /// "Album 2" if "Album" is taken, rather than nested as "Archive/Album".
    /// Otherwise `staging` itself becomes a folder named `archiveName`.
    static func placeExtracted(_ staging: URL, archiveName: String, in directory: URL) throws -> URL {
        let fileManager = FileManager.default
        var contents: [URL] = []
        for item in try fileManager.contentsOfDirectory(at: staging, includingPropertiesForKeys: [.isDirectoryKey, .isSymbolicLinkKey]) {
            if isMacMetadata(item.lastPathComponent) {
                try? fileManager.removeItem(at: item)
            } else {
                contents.append(item)
            }
        }

        if contents.count == 1, let folder = contents.first, isPlainFolder(folder) {
            let destination = uniqueDestination(for: folder.lastPathComponent, isPackage: isPackage(folder), in: directory)
            try fileManager.moveItem(at: folder, to: destination)
            try? fileManager.removeItem(at: staging)
            return destination
        }

        let destination = OutputNaming.uniqueURL(in: directory, baseName: archiveName, pathExtension: nil)
        try fileManager.moveItem(at: staging, to: destination)
        return destination
    }

    /// A folder (or package, such as an app), not a link to one; visible
    /// unless `allowsHidden`.
    private static func isPlainFolder(_ url: URL, allowsHidden: Bool = false) -> Bool {
        guard allowsHidden || !url.lastPathComponent.hasPrefix("."),
              let values = try? url.resourceValues(forKeys: [.isDirectoryKey, .isSymbolicLinkKey])
        else { return false }
        return values.isDirectory == true && values.isSymbolicLink != true
    }

    private static func isPackage(_ url: URL) -> Bool {
        (try? url.resourceValues(forKeys: [.isPackageKey]))?.isPackage == true
    }

    /// "Album" → "Album 2" when taken; a package keeps its extension last
    /// ("Tool 2.app") so it still opens as one.
    private static func uniqueDestination(for name: String, isPackage: Bool, in directory: URL) -> URL {
        let pathExtension = (name as NSString).pathExtension
        guard isPackage, !pathExtension.isEmpty else {
            return OutputNaming.uniqueURL(in: directory, baseName: name, pathExtension: nil)
        }
        return OutputNaming.uniqueURL(in: directory, baseName: (name as NSString).deletingPathExtension, pathExtension: pathExtension)
    }
}

/// Paths to archive, relative to the folder they're in.
struct ArchiveGroup: Equatable {
    var directory: URL
    var paths: [String]
}
