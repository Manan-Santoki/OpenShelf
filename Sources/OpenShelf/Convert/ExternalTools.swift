import Foundation

/// Optional tools found on this Mac: ffmpeg and ffprobe, cwebp, LibreOffice,
/// Pandoc with a program it can make PDFs with, and Pages, Numbers and Keynote.
struct ExternalTools: Equatable, Sendable {
    var ffmpeg: URL?
    var cwebp: URL?
    var ffprobe: URL? = nil
    var libreOffice: URL? = nil
    var pandoc: URL? = nil
    /// A typesetter Pandoc can make PDFs with (see `PandocConversion.pdfEngines`).
    var pdfEngine: URL? = nil
    /// Which of Pages, Numbers and Keynote are installed, to save files as theirs.
    var iWorkApps: Set<IWorkFormat> = []

    /// Never runs ffmpeg: see `WebPEncoder.isOffered`.
    var canEncodeWebP: Bool {
        WebPEncoder.isOffered(cwebp: cwebp, ffmpeg: ffmpeg, ffmpegEncoders: ffmpeg.flatMap(FFmpegEncoders.known))
    }

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
            // Learn what this ffmpeg can encode before a wheel needs to know.
            FFmpegEncoders.warm(tools.ffmpeg)
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

    /// Looks in `locations`, in order. For tools named in the
    /// `IgnoredExternalTools` default, only OpenShelf's own copies count (see
    /// `ignoredTools`).
    static func detect(in locations: ToolLocations = .standard, ignoring ignored: Set<String> = ignoredTools) -> ExternalTools {
        func find(_ name: String, ignoredAs key: String) -> URL? {
            let directories = ignored.contains(key) ? locations.ownCommandDirectories : locations.commandDirectories
            return directories
                .map { $0.appendingPathComponent(name) }
                .first { FileManager.default.isExecutableFile(atPath: $0.path) }
        }
        let apps = ignored.contains("libreoffice") ? locations.ownLibreOfficeApps : locations.libreOfficeApps
        let pandoc = find("pandoc", ignoredAs: "pandoc")
        let pdfEngine = pandoc == nil ? nil : PandocConversion.pdfEngines.lazy.compactMap { name in
            (locations.commandDirectories + locations.texDirectories)
                .map { $0.appendingPathComponent(name) }
                .first { FileManager.default.isExecutableFile(atPath: $0.path) }
        }.first
        return ExternalTools(
            ffmpeg: find("ffmpeg", ignoredAs: "ffmpeg"),
            cwebp: find("cwebp", ignoredAs: "cwebp"),
            ffprobe: find("ffprobe", ignoredAs: "ffmpeg"),
            libreOffice: apps
                .map { $0.appendingPathComponent("Contents/MacOS/soffice") }
                .first { FileManager.default.isExecutableFile(atPath: $0.path) },
            pandoc: pandoc,
            pdfEngine: pdfEngine,
            iWorkApps: ignored.contains("iwork") ? [] : Set(IWorkFormat.allCases.filter { app in
                locations.applicationDirectories.contains { directory in
                    app.appFileNames.contains { name in
                        Bundle(url: directory.appendingPathComponent(name))?.bundleIdentifier.map(app.bundleIdentifiers.contains) == true
                    }
                }
            })
        )
    }

    /// Tools whose Homebrew, MacPorts and /Applications copies are ignored, to
    /// try OpenShelf as someone without them sees it, and to try installing
    /// them from Settings: `defaults write app.openshelf.OpenShelf
    /// IgnoredExternalTools -array ffmpeg cwebp libreoffice pandoc` ("ffmpeg"
    /// covers ffprobe too; "iwork" hides Pages, Numbers and Keynote).
    /// `defaults delete` the key to undo.
    static var ignoredTools: Set<String> {
        let names = UserDefaults.standard.stringArray(forKey: "IgnoredExternalTools") ?? []
        return Set(names.map { $0.lowercased() })
    }
}

/// Where tools are looked for, in order of preference.
struct ToolLocations: Sendable {
    var commandDirectories: [URL]
    var libreOfficeApps: [URL]
    /// Where MacTeX and BasicTeX put LaTeX, for Pandoc's PDFs.
    var texDirectories: [URL] = [URL(fileURLWithPath: "/Library/TeX/texbin")]
    /// Where OpenShelf installs tools itself (see `ToolInstallation`).
    var ownCommandDirectories: [URL] = []
    var ownLibreOfficeApps: [URL] = []
    /// Where Pages, Numbers and Keynote are looked for.
    var applicationDirectories: [URL] = []

    /// Homebrew, MacPorts and the PATH come first: tools people installed
    /// themselves and keep up to date. OpenShelf's own copies (`ManagedTools`)
    /// are the fallback, since OpenShelf never updates them in the background.
    /// GUI apps don't inherit the shell's PATH, hence the fixed folders.
    static var standard: ToolLocations {
        let path = ProcessInfo.processInfo.environment["PATH"]?.split(separator: ":").map { URL(fileURLWithPath: String($0)) } ?? []
        // `brew install --cask libreoffice` uses /Applications; OpenShelf
        // installs into ~/Applications, which needs no administrator.
        let ownApp = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Applications/LibreOffice.app")
        return ToolLocations(
            commandDirectories: ["/opt/homebrew/bin", "/usr/local/bin", "/opt/local/bin"].map { URL(fileURLWithPath: $0) }
                + path
                + [ManagedTools.directory],
            libreOfficeApps: [URL(fileURLWithPath: "/Applications/LibreOffice.app"), ownApp],
            ownCommandDirectories: [ManagedTools.directory],
            ownLibreOfficeApps: [ownApp],
            // Where the App Store puts Pages, Numbers and Keynote.
            applicationDirectories: [
                URL(fileURLWithPath: "/Applications"),
                FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Applications"),
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
