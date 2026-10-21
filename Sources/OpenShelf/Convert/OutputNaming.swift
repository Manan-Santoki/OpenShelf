import Foundation

/// Picks where a converted or edited file is saved: next to the original, never
/// overwriting anything ("Photo.jpg", "Photo Cropped.jpg", "Photo Cropped 2.jpg").
enum OutputNaming {
    /// File names are limited to 255 bytes of UTF-8; keep well under that so a
    /// counter, and the temporary names ImageIO writes first, still fit.
    static let maxNameBytes = 230
    /// Room kept for a collision counter such as " 12".
    private static let counterReserve = 4

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
        let suffixPart = suffix.map { $0.isEmpty ? "" : " \($0)" } ?? ""
        let extensionPart = pathExtension.map { ".\($0)" } ?? ""
        // Shorten the original name, never the suffix, so "… Cropped" stays readable.
        let room = maxNameBytes - counterReserve - suffixPart.utf8.count - extensionPart.utf8.count
        let base = truncated(visibleName(source.deletingPathExtension().lastPathComponent), toBytes: room)
        return uniqueURL(in: directory, baseName: base + suffixPart, pathExtension: pathExtension, fileManager: fileManager)
    }

    static func uniqueURL(in directory: URL, baseName: String, pathExtension: String?, fileManager: FileManager = .default) -> URL {
        let extensionPart = pathExtension.map { ".\($0)" } ?? ""
        let baseName = truncated(
            visibleName(baseName),
            toBytes: maxNameBytes - counterReserve - extensionPart.utf8.count
        )
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

    /// Drops leading dots, which would make the new file hidden in Finder.
    static func visibleName(_ name: String) -> String {
        let trimmed = String(name.drop { $0 == "." })
        return trimmed.isEmpty ? "Untitled" : trimmed
    }

    /// `name` cut to at most `limit` bytes of UTF-8, on a character boundary.
    static func truncated(_ name: String, toBytes limit: Int) -> String {
        guard name.utf8.count > limit else { return name }
        var result = ""
        var bytes = 0
        for character in name {
            let size = String(character).utf8.count
            guard bytes + size <= max(limit, 1) else { break }
            result.append(character)
            bytes += size
        }
        let trimmed = result.trimmingCharacters(in: .whitespaces)
        return trimmed.isEmpty ? "Untitled" : trimmed
    }
}
