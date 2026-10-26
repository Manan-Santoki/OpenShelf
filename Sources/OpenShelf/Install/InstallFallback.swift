import Foundation

/// Where a tool is installed from.
enum InstallSource: Equatable, Sendable {
    /// The publisher's own server: ffmpeg.martin-riedl.de, libreoffice.org.
    case vendor
    /// OpenShelf's copy of the vendor's signed ffmpeg zips (`FFmpegSource.mirrorBase`).
    case mirror
    /// `brew install`, run for someone who has Homebrew.
    case homebrew

    /// The status line under the progress bar: honest about where it's
    /// coming from. "Downloading from OpenShelf’s mirror", "From libreoffice.org".
    func statusLine(for tool: ExtraTool, step: InstallProgress.Step) -> String {
        let name: String
        switch self {
        case .vendor: name = tool.downloadHost
        case .mirror: name = "OpenShelf’s mirror"
        case .homebrew: return "Installing with Homebrew"
        }
        return step == .downloading ? "Downloading from \(name)" : "From \(name)"
    }
}

/// Every source was down or answered with an error.
struct SourcesUnavailableError: LocalizedError, Equatable {
    let tool: ExtraTool
    /// What the last source said, for the tooltip: "ffmpeg.martin-riedl.de answered with an error (521)…"
    let detail: String

    var errorDescription: String? {
        "The \(tool.name) download server isn’t responding right now."
    }
}

/// Why an install failed, as far as what to offer next is concerned.
struct InstallFailure: Equatable, Sendable {
    enum Kind: Equatable, Sendable {
        /// Every download source was down: Homebrew may still work.
        case sourcesUnavailable
        /// `brew install` itself failed.
        case homebrew
        /// Anything else, including a download that failed its checks.
        case other
    }

    var message: String
    var kind: Kind
    /// More about it, for a tooltip.
    var detail: String? = nil
}

/// What Settings offers after a failed install, the first one most prominently.
enum InstallOffer: Equatable, Sendable {
    case retry
    case installWithHomebrew
    case copyHomebrewCommand
    /// "Try again later, or install Homebrew from brew.sh."
    case getHomebrew
}

/// The order sources are tried in, and what happens when one fails. Pure
/// decisions, tested without a network.
///
/// ffmpeg comes from the build server, then OpenShelf's mirror of it; then,
/// once both are down, Homebrew is offered (never started on its own, since
/// it changes someone's Homebrew). LibreOffice has no mirror. A download
/// that fails its checksum or signature stops everything: it means
/// something is wrong with the file, not the server, and is never retried
/// elsewhere.
enum InstallFallback {
    /// The sources tried one after another when Install is clicked.
    static func automaticSources(for tool: ExtraTool) -> [InstallSource] {
        switch tool {
        case .ffmpeg: [.vendor, .mirror]
        case .libreOffice, .pandoc: [.vendor]
        }
    }

    /// Whether `error` means the source couldn't be reached or served an
    /// error (HTTP 5xx such as 521, a 404, a timeout), so another source may
    /// work. Verification failures, redirects elsewhere and cancelling never do.
    static func isSourceUnavailable(_ error: Error) -> Bool {
        switch error as? DownloadError {
        case .unreachable, .httpStatus: true
        case .untrustedLocation, nil: false
        }
    }

    /// Tries `sources` in order until one installs. Moves on only while
    /// sources are unavailable; any other error ends it there. Throws
    /// `SourcesUnavailableError` when none could be reached.
    static func install<Value: Sendable>(
        _ tool: ExtraTool,
        from sources: [InstallSource],
        attempt: (InstallSource) async throws -> Value
    ) async throws -> Value {
        var lastError: Error?
        for source in sources {
            try Task.checkCancellation()
            do {
                return try await attempt(source)
            } catch let error where isSourceUnavailable(error) {
                lastError = error
            }
        }
        throw SourcesUnavailableError(tool: tool, detail: lastError?.localizedDescription ?? "")
    }

    /// How a failed install is described and what can be done next.
    static func failure(after error: Error, from source: InstallSource) -> InstallFailure {
        if let error = error as? SourcesUnavailableError {
            return InstallFailure(message: error.localizedDescription, kind: .sourcesUnavailable, detail: error.detail)
        }
        return InstallFailure(message: error.localizedDescription, kind: source == .homebrew ? .homebrew : .other)
    }

    /// What to offer after `failure`. Homebrew is only offered when the
    /// servers are down and it's on this Mac; without it, a calm pointer to
    /// brew.sh. After any other failure: Retry, and the Terminal command.
    static func offers(after failure: InstallFailure.Kind, homebrewInstalled: Bool) -> [InstallOffer] {
        switch failure {
        case .sourcesUnavailable:
            homebrewInstalled ? [.installWithHomebrew, .retry] : [.retry, .getHomebrew]
        case .homebrew, .other:
            [.retry, .copyHomebrewCommand]
        }
    }
}
