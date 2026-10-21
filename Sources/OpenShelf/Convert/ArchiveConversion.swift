import Foundation

/// ZIP creation and archive extraction with the tools that ship with macOS.
enum ArchiveConversion {
    private static let ditto = URL(fileURLWithPath: "/usr/bin/ditto")
    private static let zipTool = URL(fileURLWithPath: "/usr/bin/zip")
    private static let tar = URL(fileURLWithPath: "/usr/bin/tar")
    private static let gzip = URL(fileURLWithPath: "/usr/bin/gzip")

    /// One file or folder becomes "<name>.zip"; several become "Archive.zip"
    /// next to the first.
    static func zip(_ sources: [URL]) async throws -> URL {
        guard let first = sources.first else { throw ConversionError.failed("Nothing to compress.") }
        if sources.count == 1 {
            let destination = OutputNaming.uniqueURL(in: first.deletingLastPathComponent(), baseName: first.lastPathComponent, pathExtension: "zip")
            try await ProcessRunner.run(ditto, ["-c", "-k", "--sequesterRsrc", "--keepParent", first.path, destination.path])
            return destination
        }

        let directory = first.deletingLastPathComponent()
        let destination = OutputNaming.uniqueURL(in: directory, baseName: "Archive", pathExtension: "zip")
        let sameFolder = sources.allSatisfy { $0.deletingLastPathComponent().path == directory.path }
        // Relative names keep folder structure; files from different folders are stored flat.
        let names = sameFolder ? sources.map(\.lastPathComponent) : sources.map(\.path)
        try await ProcessRunner.run(
            zipTool,
            ["-r", "-q", "-y"] + (sameFolder ? [] : ["-j"]) + [destination.path] + names,
            currentDirectory: directory
        )
        return destination
    }

    /// Extracts into a new folder named after the archive.
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

        let destination = OutputNaming.uniqueURL(in: directory, baseName: baseName, pathExtension: nil)
        try FileManager.default.createDirectory(at: destination, withIntermediateDirectories: false)
        do {
            if name.hasSuffix(".zip") {
                try await ProcessRunner.run(ditto, ["-x", "-k", source.path, destination.path])
            } else {
                // bsdtar reads tar, gzip, bzip2, xz, 7z and most RAR files.
                try await ProcessRunner.run(tar, ["-xf", source.path, "-C", destination.path])
            }
        } catch {
            try? FileManager.default.removeItem(at: destination)
            throw error
        }
        return destination
    }
}
