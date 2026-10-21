import Foundation

/// Optional tools found on this Mac: ffmpeg and ffprobe, cwebp, and LibreOffice.
struct ExternalTools: Equatable, Sendable {
    var ffmpeg: URL?
    var cwebp: URL?
    var ffprobe: URL? = nil
    var libreOffice: URL? = nil

    var canEncodeWebP: Bool { cwebp != nil || ffmpeg != nil }

    /// What's installed, as last detected. Detection is a few file checks and
    /// never runs a tool, so it's simply repeated once the answer is a few
    /// seconds old: an install from Terminal shows up on the next wheel, while a
    /// drag and the conversion it starts see the same answer.
    static var current: ExternalTools {
        cache.lock.withLock {
            let now = ProcessInfo.processInfo.systemUptime
            if let tools = cache.tools, now - cache.detectedAt < cacheLifetime {
                return tools
            }
            let tools = detect()
            cache.tools = tools
            cache.detectedAt = now
            return tools
        }
    }

    /// Detects again now: when Settings opens, after an install or removal,
    /// or when a tool turns out to be gone.
    @discardableResult
    static func refresh() -> ExternalTools {
        cache.lock.withLock { cache.tools = nil }
        return current
    }

    static let cacheLifetime: TimeInterval = 5

    private final class Cache: @unchecked Sendable {
        let lock = NSLock()
        var tools: ExternalTools?
        var detectedAt: TimeInterval = 0
    }
    private static let cache = Cache()

    /// Looks in `locations`, in order. Tools named in the `IgnoredExternalTools`
    /// default are treated as missing (see `ignoredTools`).
    static func detect(in locations: ToolLocations = .standard, ignoring ignored: Set<String> = ignoredTools) -> ExternalTools {
        func find(_ name: String) -> URL? {
            locations.commandDirectories
                .map { $0.appendingPathComponent(name) }
                .first { FileManager.default.isExecutableFile(atPath: $0.path) }
        }
        let hidesFFmpeg = ignored.contains("ffmpeg")
        return ExternalTools(
            ffmpeg: hidesFFmpeg ? nil : find("ffmpeg"),
            cwebp: ignored.contains("cwebp") ? nil : find("cwebp"),
            ffprobe: hidesFFmpeg ? nil : find("ffprobe"),
            libreOffice: ignored.contains("libreoffice") ? nil : locations.libreOfficeApps
                .map { $0.appendingPathComponent("Contents/MacOS/soffice") }
                .first { FileManager.default.isExecutableFile(atPath: $0.path) }
        )
    }

    /// Tools to treat as not installed, for trying OpenShelf as someone without
    /// them would see it: `defaults write app.openshelf.OpenShelf
    /// IgnoredExternalTools -array ffmpeg cwebp libreoffice` ("ffmpeg" covers
    /// ffprobe too). `defaults delete` the key to undo.
    static var ignoredTools: Set<String> {
        let names = UserDefaults.standard.stringArray(forKey: "IgnoredExternalTools") ?? []
        return Set(names.map { $0.lowercased() })
    }
}

/// Where tools are looked for, in order of preference.
struct ToolLocations: Sendable {
    var commandDirectories: [URL]
    var libreOfficeApps: [URL]

    /// Homebrew, MacPorts and the PATH come first: tools people installed
    /// themselves and keep up to date. OpenShelf's own copies (`ManagedTools`)
    /// are the fallback, since OpenShelf never updates them in the background.
    /// GUI apps don't inherit the shell's PATH, hence the fixed folders.
    static var standard: ToolLocations {
        let path = ProcessInfo.processInfo.environment["PATH"]?.split(separator: ":").map { URL(fileURLWithPath: String($0)) } ?? []
        let home = FileManager.default.homeDirectoryForCurrentUser
        return ToolLocations(
            commandDirectories: ["/opt/homebrew/bin", "/usr/local/bin", "/opt/local/bin"].map { URL(fileURLWithPath: $0) }
                + path
                + [ManagedTools.directory],
            // `brew install --cask libreoffice` uses /Applications; OpenShelf
            // installs into ~/Applications, which needs no administrator.
            libreOfficeApps: [
                URL(fileURLWithPath: "/Applications/LibreOffice.app"),
                home.appendingPathComponent("Applications/LibreOffice.app"),
            ]
        )
    }
}

/// Tools OpenShelf downloaded itself (see `ToolInstaller`).
enum ManagedTools {
    /// ~/Library/Application Support/OpenShelf/Tools, for ffmpeg and ffprobe.
    static var directory: URL {
        let support = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first
            ?? FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/Application Support")
        return support.appendingPathComponent("OpenShelf/Tools", isDirectory: true)
    }
}
