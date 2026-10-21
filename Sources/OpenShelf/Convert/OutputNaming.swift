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
    ///   - suffix: Appended to the base name, e.g. "Cropped". A name that already
    ///     ends with it doesn't get it twice: compressing "Photo Compressed.jpg"
    ///     saves "Photo Compressed 2.jpg", not "Photo Compressed Compressed.jpg".
    ///   - pathExtension: Extension of the new file, or nil for a folder.
    static func destination(
        for source: URL,
        suffix: String? = nil,
        pathExtension: String?,
        fileManager: FileManager = .default
    ) -> URL {
        let directory = source.deletingLastPathComponent()
        let (name, suffixPart) = splitSuffix(suffix, from: visibleName(source.deletingPathExtension().lastPathComponent))
        let extensionPart = pathExtension.map { ".\($0)" } ?? ""
        // Shorten the original name, never the suffix, so "… Cropped" stays readable.
        let room = maxNameBytes - counterReserve - suffixPart.utf8.count - extensionPart.utf8.count
        let base = truncated(name, toBytes: room)
        return uniqueURL(in: directory, baseName: base + suffixPart, pathExtension: pathExtension, fileManager: fileManager)
    }

    /// Splits off the part of a new name that comes after the original: " <suffix>".
    /// A name that already ends with the suffix, in any letter case, keeps its
    /// own spelling of it; any other name gets it appended.
    static func splitSuffix(_ suffix: String?, from name: String) -> (name: String, suffix: String) {
        guard let suffix, !suffix.isEmpty else { return (name, "") }
        let suffixPart = " \(suffix)"
        if let range = name.range(of: suffixPart, options: [.anchored, .backwards, .caseInsensitive]) {
            let stem = String(name[..<range.lowerBound])
            if !stem.trimmingCharacters(in: .whitespaces).isEmpty {
                return (stem, String(name[range]))
            }
        }
        return (name, suffixPart)
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
