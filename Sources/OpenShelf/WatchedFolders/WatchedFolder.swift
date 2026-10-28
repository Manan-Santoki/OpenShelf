import Foundation

/// A folder OpenShelf watches for new files (Settings → Shelves).
struct WatchedFolder: Codable, Equatable, Identifiable, Sendable {
    var id = UUID()
    var path: String
    /// Finds the folder again if it was moved or renamed while OpenShelf was closed.
    var bookmark: Data?
    var isEnabled = true
    /// The screenshot folder: follows the location chosen in the Screenshot
    /// app's Options instead of `path`, which is where it was when added.
    var isScreenshots = false
    /// What to do with each new file once it's on the shelf.
    var action = WatchedFolderAction.addOnly

    init(url: URL, isScreenshots: Bool = false) {
        path = url.standardizedFileURL.path
        bookmark = try? url.bookmarkData(options: [], includingResourceValuesForKeys: nil, relativeTo: nil)
        self.isScreenshots = isScreenshots
    }

    var name: String {
        isScreenshots ? "Screenshots" : URL(fileURLWithPath: path).lastPathComponent
    }

    /// The folder's current location, or nil when it's gone: the screenshot
    /// location, else the bookmark (so a moved folder is found), else the
    /// saved path.
    func resolve(fileManager: FileManager = .default) -> URL? {
        if isScreenshots { return ScreenshotLocation.current() }
        func isFolder(_ url: URL) -> Bool {
            var isDirectory: ObjCBool = false
            return fileManager.fileExists(atPath: url.path, isDirectory: &isDirectory) && isDirectory.boolValue
        }
        if let bookmark {
            var isStale = false
            if let url = try? URL(resolvingBookmarkData: bookmark, options: [.withoutUI, .withoutMounting], bookmarkDataIsStale: &isStale),
               isFolder(url) {
                return url
            }
        }
        let url = URL(fileURLWithPath: path)
        return isFolder(url) ? url : nil
    }
}

/// What a watched folder does with a new file after putting it on its shelf.
/// Kept to a short list that suits files arriving on their own; a conversion
/// or tool only runs on the files it applies to.
enum WatchedFolderAction: Hashable, Sendable {
    case addOnly
    case convert(OutputFormat)
    case tool(FileTool)

    /// Conversions offered in Settings.
    static let conversions: [WatchedFolderAction] = ([
        .jpg, .png, .heic, .webp, .avif, .pdf, .mp4, .gif, .m4a, .mp3,
    ] as [OutputFormat]).map { .convert($0) }

    /// Tools offered in Settings. Ones that need several files (Combine,
    /// Merge) are left out, since files arrive one at a time.
    static let tools: [WatchedFolderAction] = ([
        .compress, .metadata, .annotate, .crop, .resize, .removeBackground, .redact, .ocr, .readQR,
        .compressPDF, .trim, .compressVideo, .mute,
    ] as [FileTool]).map { .tool($0) }

    var title: String {
        switch self {
        case .addOnly: "Just Add to the Shelf"
        case .convert(let format): "Convert to \(format.title)"
        case .tool(let tool):
            switch tool {
            case .compress: "Compress Image"
            case .metadata: "Image Metadata"
            case .compressPDF: "Compress PDF"
            case .compressVideo: "Compress Video"
            case .trim: "Trim Video or Audio"
            case .readQR: "Read QR Code"
            default: tool.title
            }
        }
    }

    /// Stored as text, e.g. "none", "convert:jpg" or "tool:compress".
    var stored: String {
        switch self {
        case .addOnly: "none"
        case .convert(let format): "convert:\(format.rawValue)"
        case .tool(let tool): "tool:\(String(describing: tool))"
        }
    }

    /// Nil for anything unreadable, e.g. a format or tool that no longer exists.
    init?(stored: String) {
        if stored == "none" {
            self = .addOnly
            return
        }
        let parts = stored.split(separator: ":", maxSplits: 1).map(String.init)
        guard parts.count == 2 else { return nil }
        switch parts[0] {
        case "convert":
            guard let format = OutputFormat(rawValue: parts[1]) else { return nil }
            self = .convert(format)
        case "tool":
            guard let tool = FileTool.allCases.first(where: { String(describing: $0) == parts[1] }) else { return nil }
            self = .tool(tool)
        default:
            return nil
        }
    }
}

extension WatchedFolderAction: Codable {
    init(from decoder: any Decoder) throws {
        let stored = try decoder.singleValueContainer().decode(String.self)
        self = WatchedFolderAction(stored: stored) ?? .addOnly
    }

    func encode(to encoder: any Encoder) throws {
        var container = encoder.singleValueContainer()
        try container.encode(stored)
    }
}

/// Reads and writes watched folders to Application Support, like `ShelfStore`.
enum WatchedFolderStore {
    private struct File: Codable {
        var version = 1
        var folders: [WatchedFolder]
    }

    static var defaultURL: URL {
        let support = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        return support.appendingPathComponent("OpenShelf/watched-folders.json")
    }

    static func save(_ folders: [WatchedFolder], to url: URL = defaultURL) throws {
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        try encoder.encode(File(folders: folders)).write(to: url, options: .atomic)
    }

    /// Saved folders, or none when nothing was saved or the file is unreadable.
    static func load(from url: URL = defaultURL) -> [WatchedFolder] {
        guard let data = try? Data(contentsOf: url),
              let file = try? JSONDecoder().decode(File.self, from: data)
        else { return [] }
        return file.folders
    }
}

/// Where screenshots are saved: the Screenshot app's chosen folder, or the
/// Desktop when none was chosen (or the chosen one is gone).
enum ScreenshotLocation {
    static func current(fileManager: FileManager = .default) -> URL {
        resolve(
            stored: UserDefaults(suiteName: "com.apple.screencapture")?.string(forKey: "location"),
            home: fileManager.homeDirectoryForCurrentUser
        ) { url in
            var isDirectory: ObjCBool = false
            return fileManager.fileExists(atPath: url.path, isDirectory: &isDirectory) && isDirectory.boolValue
        }
    }

    /// - Parameters:
    ///   - stored: The `location` default, which may start with "~" or be a file URL.
    ///   - isFolder: Whether a folder exists at a URL.
    static func resolve(stored: String?, home: URL, isFolder: (URL) -> Bool) -> URL {
        let desktop = home.appendingPathComponent("Desktop", isDirectory: true)
        guard let stored = stored?.trimmingCharacters(in: .whitespacesAndNewlines), !stored.isEmpty else { return desktop }
        let url: URL
        if stored.hasPrefix("file://"), let fileURL = URL(string: stored) {
            url = fileURL
        } else if stored == "~" || stored.hasPrefix("~/") {
            url = home.appendingPathComponent(String(stored.dropFirst(2)), isDirectory: true)
        } else if stored.hasPrefix("/") {
            url = URL(fileURLWithPath: stored, isDirectory: true)
        } else {
            return desktop
        }
        return isFolder(url) ? url.standardizedFileURL : desktop
    }
}
