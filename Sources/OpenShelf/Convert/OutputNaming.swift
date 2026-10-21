import Foundation

/// Picks where a converted or edited file is saved: next to the original, never
/// overwriting anything ("Photo.jpg", "Photo Cropped.jpg", "Photo Cropped 2.jpg").
enum OutputNaming {
    /// - Parameters:
    ///   - suffix: Appended to the base name, e.g. "Cropped".
    ///   - pathExtension: Extension of the new file, or nil for a folder.
    static func destination(
        for source: URL,
        suffix: String? = nil,
        pathExtension: String?,
        fileManager: FileManager = .default
    ) -> URL {
        let directory = source.deletingLastPathComponent()
        var base = source.deletingPathExtension().lastPathComponent
        if let suffix, !suffix.isEmpty {
            base += " \(suffix)"
        }
        return uniqueURL(in: directory, baseName: base, pathExtension: pathExtension, fileManager: fileManager)
    }

    static func uniqueURL(in directory: URL, baseName: String, pathExtension: String?, fileManager: FileManager = .default) -> URL {
        func candidate(_ name: String) -> URL {
            let url = directory.appendingPathComponent(name)
            guard let pathExtension else { return url }
            return url.appendingPathExtension(pathExtension)
        }
        var url = candidate(baseName)
        var counter = 2
        while fileManager.fileExists(atPath: url.path) {
            url = candidate("\(baseName) \(counter)")
            counter += 1
        }
        return url
    }
}
