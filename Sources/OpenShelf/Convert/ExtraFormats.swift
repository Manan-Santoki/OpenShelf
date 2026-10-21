import Foundation

/// A free tool that adds formats macOS can't handle itself, as presented in
/// Settings → Extra formats and in messages that point there.
enum ExtraTool: String, CaseIterable, Identifiable, Sendable {
    case ffmpeg
    case libreOffice

    var id: String { rawValue }

    var name: String {
        switch self {
        case .ffmpeg: "ffmpeg"
        case .libreOffice: "LibreOffice"
        }
    }

    /// One short line on what installing it adds: these formats as outputs,
    /// and for ffmpeg also as files that can be converted.
    var unlocks: String {
        switch self {
        case .ffmpeg: "MP3, OGG, WebP, MKV, WebM, AVI and WMV"
        case .libreOffice: "PowerPoint, ODP, XLS and ODS"
        }
    }

    /// The Homebrew alternative to installing from Settings.
    var homebrewCommand: String {
        switch self {
        case .ffmpeg: "brew install ffmpeg"
        case .libreOffice: "brew install --cask libreoffice"
        }
    }

    /// The second line of the toast after an install.
    var installedConfirmation: String {
        switch self {
        case .ffmpeg: "MP3, WebP, MKV… are now on the wheel"
        case .libreOffice: "PowerPoint, XLS… are now on the wheel"
        }
    }

    func path(in tools: ExternalTools) -> URL? {
        switch self {
        case .ffmpeg: tools.ffmpeg
        case .libreOffice: tools.libreOffice
        }
    }

    func isInstalled(in tools: ExternalTools) -> Bool {
        path(in: tools) != nil
    }

    /// `tools` as if this one were installed, to ask the catalog what it would
    /// add. The placeholder path is never run; ffprobe stays as it is so the
    /// question never starts a process.
    func assumedInstalled(in tools: ExternalTools) -> ExternalTools {
        var tools = tools
        let placeholder = URL(fileURLWithPath: "/nonexistent/\(rawValue)")
        switch self {
        case .ffmpeg: tools.ffmpeg = placeholder
        case .libreOffice: tools.libreOffice = placeholder
        }
        return tools
    }
}

/// What to say when a missing tool is the reason a wheel has little to offer.
/// Pure decisions, so they can be tested without a drag.
enum MissingToolAdvice {
    /// The second line of every "needs …" message.
    static let installHint = "Install it in Settings → Extra formats"

    /// The tool whose absence leaves these files with nothing but ZIP on the
    /// Convert wheel, when installing it would offer real conversions. Nil
    /// for folders, unknown files, Keynote decks and mixes that share no
    /// format anyway: their ZIP wheel is all there is.
    static func blockingTool(for urls: [URL], formats: [OutputFormat], tools: ExternalTools) -> ExtraTool? {
        guard !urls.isEmpty, formats == [.zip] else { return nil }
        return ExtraTool.allCases.first { tool in
            !tool.isInstalled(in: tools)
                && ConversionCatalog.formats(for: urls, tools: tool.assumedInstalled(in: tools)).contains { $0 != .zip }
        }
    }

    /// "MKV needs ffmpeg", shown instead of a ZIP-only wheel. Names only the
    /// files that need the tool (not the MP4 dragged along with an MKV). One
    /// line each, like every toast.
    static func toast(for urls: [URL], needing tool: ExtraTool, tools: ExternalTools) -> Toast {
        let blocked = urls.filter { ConversionCatalog.formats(for: [$0], tools: tools) == [.zip] }
        let names = uniqueNames(of: blocked.isEmpty ? urls : blocked)
        var title = "These files need \(tool.name)"
        if !names.contains("") {
            if names.count == 1 {
                title = "\(names[0]) needs \(tool.name)"
            } else if names.count == 2 {
                title = "\(names[0]) and \(names[1]) need \(tool.name)"
            }
        }
        if title.count > 28 {
            title = "These files need \(tool.name)"
        }
        return Toast(systemImage: "puzzlepiece.extension", title: title, subtitle: installHint)
    }

    /// A one-time note for audio and video that convert natively but would get
    /// MP3, MKV, WebM and more with ffmpeg.
    static func moreFormatsHint(for urls: [URL], tools: ExternalTools) -> String? {
        guard tools.ffmpeg == nil, !urls.isEmpty else { return nil }
        let isNativeMedia = urls.allSatisfy { url in
            let kind = FileKind(url: url)
            return (kind == .audio || kind == .video) && MediaFormats.isAVFoundationReadable(url)
        }
        return isNativeMedia ? "More formats in Settings → Extra formats" : nil
    }

    /// How a format is known to people: "PowerPoint", "WebM", "MKV"; empty
    /// for a file without an extension.
    static func displayName(forExtension ext: String) -> String {
        switch ext.lowercased() {
        case "ppt", "pptx": "PowerPoint"
        case "webm": "WebM"
        case "opus": "Opus"
        case "oga": "OGG"
        default: ext.uppercased()
        }
    }

    private static func uniqueNames(of urls: [URL]) -> [String] {
        var names: [String] = []
        for url in urls {
            let name = displayName(forExtension: url.resolvingSymlinksInPath().pathExtension)
            if !names.contains(name) { names.append(name) }
        }
        return names
    }
}
