import Foundation

/// `brew install` failed; the message says why in plain words.
struct HomebrewError: LocalizedError, Equatable {
    let message: String

    var errorDescription: String? { message }
}

/// Installs a tool with Homebrew, offered when someone has it and the
/// download servers are down. Runs `brew install` as Terminal would, except
/// that it never waits for input, doesn't update Homebrew first, never
/// upgrades what's already installed, and never uses sudo.
enum Homebrew {
    /// Apple silicon's prefix, then Intel's.
    static let standardLocations: [URL] = ["/opt/homebrew/bin/brew", "/usr/local/bin/brew"].map { URL(fileURLWithPath: $0) }

    /// The `brew` on this Mac, if there is one.
    static func find(in candidates: [URL] = standardLocations) -> URL? {
        candidates.first { FileManager.default.isExecutableFile(atPath: $0.path) }
    }

    static func arguments(for tool: ExtraTool) -> [String] {
        switch tool {
        case .ffmpeg: ["install", "ffmpeg"]
        case .libreOffice: ["install", "--cask", "libreoffice"]
        case .pandoc: ["install", "pandoc"]
        }
    }

    /// The minimal environment (see `ChildEnvironment`) plus the user's own
    /// HOMEBREW_* settings, with this brew's folders and the system's as the
    /// PATH, adjusted for running brew from a GUI app.
    static func environment(for brew: URL, base: [String: String] = ProcessInfo.processInfo.environment) -> [String: String] {
        let settings = base.filter { $0.key.hasPrefix("HOMEBREW_") && !ChildEnvironment.isCredential($0.key) }
        var environment = ChildEnvironment.minimal(from: base, extra: settings)
        let prefix = brew.deletingLastPathComponent().deletingLastPathComponent().path
        environment["PATH"] = ["\(prefix)/bin", "\(prefix)/sbin", "/usr/bin", "/bin", "/usr/sbin", "/sbin"].joined(separator: ":")
        // Updating Homebrew first can take minutes; installing doesn't need it.
        environment["HOMEBREW_NO_AUTO_UPDATE"] = "1"
        // Fail instead of waiting for input nobody can type.
        environment["NONINTERACTIVE"] = "1"
        environment["HOMEBREW_NO_INSTALL_UPGRADE"] = "1"
        environment["HOMEBREW_NO_INSTALL_CLEANUP"] = "1"
        environment["HOMEBREW_NO_ENV_HINTS"] = "1"
        environment["HOMEBREW_NO_COLOR"] = "1"
        return environment
    }

    /// Runs `brew install` for `tool`, reporting its latest meaningful line.
    /// Throws `HomebrewError` when it fails; whether the tool then works is
    /// for the caller to check by detecting it.
    static func install(_ tool: ExtraTool, using brew: URL, report: @escaping ToolInstallation.Report) async throws {
        report(InstallProgress(step: .installing, fraction: nil, detail: "Starting Homebrew…", source: .homebrew))
        let transcript = Transcript()
        let status = try await GroupedProcess.run(brew, arguments(for: tool), environment: environment(for: brew)) { line in
            transcript.append(line)
            if let detail = progressDetail(for: line) {
                report(InstallProgress(step: detail.hasPrefix("Download") ? .downloading : .installing, fraction: nil, detail: detail, source: .homebrew))
            }
        }
        guard status == 0 else {
            throw HomebrewError(message: failureMessage(for: tool, output: transcript.lines))
        }
    }

    // MARK: Reading its output

    /// What to show for a line of brew's output, or nil to keep showing the
    /// previous one: "==> Pouring ffmpeg--8.0.bottle.tar.gz" → "Pouring ffmpeg…".
    static func progressDetail(for line: String) -> String? {
        let text = plain(line)
        if text.hasPrefix("==> ") {
            return heading(String(text.dropFirst(4)))
        }
        if text.hasPrefix("✔︎ ") || text.hasPrefix("✔ ") {
            // "✔︎ Bottle Manifest x265 (4.1)": one download of several finished.
            let words = text.split(separator: " ").dropFirst().filter { $0 != "Bottle" && $0 != "Manifest" && $0 != "Cask" }
            return words.first.map { "Downloaded \($0)" }
        }
        if text.hasPrefix("Warning: "), text.contains("already installed") {
            return "Already installed"
        }
        if text.hasPrefix("🍺") {
            return "Finishing…"
        }
        return nil
    }

    private static func heading(_ text: String) -> String? {
        let words = text.split(separator: " ").map(String.init)
        guard let first = words.first else { return nil }
        switch first {
        case "Downloading", "Fetching":
            // "Downloading https://ghcr.io/v2/homebrew/core/x265/blobs/sha256:…",
            // "Fetching downloads for: ffmpeg", "Fetching ffmpeg"
            if words.count == 2, !text.contains("://") { return "Downloading \(words[1])…" }
            let names = text.split(separator: ":").dropFirst().first?
                .replacingOccurrences(of: " and ", with: ", ")
                .split(separator: ",")
                .map { $0.trimmingCharacters(in: .whitespaces) } ?? []
            return names.count == 1 && !text.contains("://") ? "Downloading \(names[0])…" : "Downloading…"
        case "Pouring":
            // "Pouring ffmpeg--8.0_1.arm64_sequoia.bottle.tar.gz"
            let bottle = words.count > 1 ? words[1] : ""
            let name = bottle.components(separatedBy: "--").first ?? bottle
            return name.isEmpty ? "Pouring…" : "Pouring \(name)…"
        case "Installing":
            // "Installing dependencies for ffmpeg: …", "Installing ffmpeg
            // dependency: x265", "Installing Cask libreoffice", "Installing ffmpeg"
            if words.count > 1, words[1].hasPrefix("dependencies") { return "Installing dependencies…" }
            if words.count > 3, words[2] == "dependency:" { return "Installing \(words[3])…" }
            if words.count > 2, words[1] == "Cask" { return "Installing \(words[2])…" }
            return words.count > 1 ? "Installing \(words[1])…" : "Installing…"
        case "Moving":
            // "Moving App 'LibreOffice.app' to '/Applications/LibreOffice.app'"
            let app = words.first { $0.hasSuffix(".app'") }?.trimmingCharacters(in: CharacterSet(charactersIn: "'"))
            return app.map { "Moving \(($0 as NSString).deletingPathExtension) to Applications…" } ?? "Moving into place…"
        case "Caveats", "Summary":
            return nil
        default:
            return String(text.trimmingCharacters(in: CharacterSet(charactersIn: ". ")).prefix(40)) + "…"
        }
    }

    /// Why brew failed, from its output: its last "Error:" line, or for a
    /// cask that wanted an administrator password, how to run it in Terminal.
    static func failureMessage(for tool: ExtraTool, output lines: [String]) -> String {
        let plainLines = lines.map(plain).filter { !$0.isEmpty }
        let wantsPassword = plainLines.contains { line in
            let lower = line.lowercased()
            return lower.contains("sudo") || lower.contains("password") || lower.contains("administrator")
        }
        if wantsPassword {
            return "Homebrew needs an administrator password for \(tool.name). Run “\(tool.homebrewCommand)” in Terminal."
        }
        let error = plainLines.last { $0.hasPrefix("Error:") }.map { String($0.dropFirst("Error:".count)).trimmingCharacters(in: .whitespaces) }
        guard let reason = error ?? plainLines.last else {
            return "Homebrew couldn’t install \(tool.name)."
        }
        return "Homebrew couldn’t install \(tool.name): \(FailureMessage.sanitized(String(reason.prefix(160))))"
    }

    /// `line` without terminal colour codes or surrounding spaces.
    private static func plain(_ line: String) -> String {
        line.replacingOccurrences(of: "\u{1B}\\[[0-9;]*[A-Za-z]", with: "", options: .regularExpression)
            .trimmingCharacters(in: .whitespaces)
    }
}

/// The lines brew printed, for the failure message.
private final class Transcript: @unchecked Sendable {
    private let lock = NSLock()
    private var recorded: [String] = []

    func append(_ line: String) {
        lock.withLock {
            recorded.append(line)
            if recorded.count > 200 { recorded.removeFirst(recorded.count - 200) }
        }
    }

    var lines: [String] { lock.withLock { recorded } }
}
