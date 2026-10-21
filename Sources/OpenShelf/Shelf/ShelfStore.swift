import Foundation

/// A shelf as saved between launches.
struct ShelfSnapshot: Codable, Equatable {
    struct Item: Codable, Equatable {
        var path: String
        /// Finds the file again if it was moved or renamed while OpenShelf was closed.
        var bookmark: Data?
    }

    var items: [Item]
    /// The shelf's on-screen frame (where it shows when not hidden at the edge).
    var frame: CGRect
    var dockedEdge: ShelfEdge?
    var isExpanded: Bool
    var viewMode: ShelfViewMode
}

/// Reads and writes open shelves to Application Support.
enum ShelfStore {
    private struct File: Codable {
        var version = 1
        var shelves: [ShelfSnapshot]
    }

    static var defaultURL: URL {
        let support = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        return support.appendingPathComponent("OpenShelf/shelves.json")
    }

    static func save(_ shelves: [ShelfSnapshot], to url: URL = defaultURL) throws {
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        try encoder.encode(File(shelves: shelves)).write(to: url, options: .atomic)
    }

    /// Saved shelves, or none when nothing was saved or the file is unreadable.
    static func load(from url: URL = defaultURL) -> [ShelfSnapshot] {
        guard let data = try? Data(contentsOf: url),
              let file = try? JSONDecoder().decode(File.self, from: data)
        else { return [] }
        return file.shelves
    }

    /// The file's current location: the bookmark first, so moved files are
    /// found, then the saved path. Nil when the file is gone.
    static func resolve(_ item: ShelfSnapshot.Item, fileManager: FileManager = .default) -> URL? {
        if let bookmark = item.bookmark {
            var isStale = false
            if let url = try? URL(resolvingBookmarkData: bookmark, options: [.withoutUI, .withoutMounting], bookmarkDataIsStale: &isStale),
               fileManager.fileExists(atPath: url.path) {
                return url
            }
        }
        return fileManager.fileExists(atPath: item.path) ? URL(fileURLWithPath: item.path) : nil
    }
}
