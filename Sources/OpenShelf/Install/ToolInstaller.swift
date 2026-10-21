import AppKit
import Observation

/// Where an installed tool came from, as shown in Settings.
enum ToolSource: String, Equatable, Sendable {
    case homebrew = "Homebrew"
    case macPorts = "MacPorts"
    case openShelf = "OpenShelf"
    case applications = "Applications"
    case other = "Installed"
}

/// A tool that's on this Mac.
struct InstalledToolInfo: Equatable {
    var path: URL
    var source: ToolSource
    var version: String?
    /// Only copies OpenShelf installed itself can be removed from Settings.
    var canRemove: Bool
}

/// What Settings shows for a tool.
enum ToolStatus: Equatable {
    case notInstalled
    case installed(InstalledToolInfo)
    case installing(InstallProgress)
    case failed(String)
}

/// Installs and removes ffmpeg and LibreOffice when asked from Settings, and
/// keeps going if the window closes; a toast says when it's done. Nothing here
/// touches the network until someone clicks Install.
@MainActor
@Observable
final class ToolInstaller {
    static let shared = ToolInstaller()

    private(set) var tools: ExternalTools
    private(set) var installed: InstalledTools
    private var progress: [ExtraTool: InstallProgress] = [:]
    private var failures: [ExtraTool: String] = [:]

    @ObservationIgnored let locations: InstallLocations
    @ObservationIgnored private var jobs: [ExtraTool: Task<Void, Never>] = [:]

    init(locations: InstallLocations = .standard) {
        self.locations = locations
        tools = ExternalTools.current
        installed = InstalledTools.load(from: locations.record)
    }

    func status(of tool: ExtraTool) -> ToolStatus {
        Self.status(of: tool, tools: tools, installed: installed, progress: progress[tool], failure: failures[tool], locations: locations)
    }

    /// Detects the tools again, e.g. when Settings comes forward after an
    /// install from Terminal.
    func refresh() {
        tools = ExternalTools.refresh()
        installed = InstalledTools.load(from: locations.record)
    }

    func install(_ tool: ExtraTool) {
        guard jobs[tool] == nil else { return }
        failures[tool] = nil
        progress[tool] = InstallProgress(step: .downloading, fraction: 0, detail: "Starting…")
        let locations = locations
        let report: ToolInstallation.Report = { [weak self] update in
            Task { @MainActor in
                // Late updates from a finished or cancelled install are dropped.
                guard let self, self.jobs[tool] != nil else { return }
                self.progress[tool] = update
            }
        }
        jobs[tool] = Task { [weak self] in
            let outcome: Result<InstalledTools.Entry, Error>
            do {
                switch tool {
                case .ffmpeg:
                    outcome = .success(try await ToolInstallation.installFFmpeg(into: locations, report: report))
                case .libreOffice:
                    outcome = .success(try await ToolInstallation.installLibreOffice(into: locations, report: report))
                }
            } catch {
                outcome = .failure(error)
            }
            self?.finish(tool, with: outcome)
        }
    }

    func cancel(_ tool: ExtraTool) {
        jobs[tool]?.cancel()
    }

    func remove(_ tool: ExtraTool) {
        guard let entry = installed[tool] else { return }
        do {
            try ToolInstallation.remove(entry, from: locations)
            installed[tool] = nil
            try installed.save(to: locations.record)
            failures[tool] = nil
        } catch {
            failures[tool] = "Couldn’t remove \(tool.name): \(error.localizedDescription)"
        }
        refresh()
    }

    private func finish(_ tool: ExtraTool, with outcome: Result<InstalledTools.Entry, Error>) {
        jobs[tool] = nil
        progress[tool] = nil
        switch outcome {
        case .success(let entry):
            installed[tool] = entry
            try? installed.save(to: locations.record)
            refresh()
            ToastController.shared.show(
                Toast(systemImage: "checkmark", title: "\(tool.name) installed", subtitle: tool.installedConfirmation),
                for: .seconds(4)
            )
        case .failure(let error) where error is CancellationError:
            break
        case .failure(let error):
            let message = error.localizedDescription
            failures[tool] = message
            ToastController.shared.show(
                Toast(systemImage: "exclamationmark.triangle", title: "Couldn’t install \(tool.name)", subtitle: message),
                for: .seconds(5)
            )
        }
    }

    // MARK: Decisions

    /// What Settings shows: an install in progress or a failure take
    /// precedence over whether the tool is there.
    nonisolated static func status(
        of tool: ExtraTool,
        tools: ExternalTools,
        installed: InstalledTools,
        progress: InstallProgress?,
        failure: String?,
        locations: InstallLocations
    ) -> ToolStatus {
        if let progress { return .installing(progress) }
        if let failure { return .failed(failure) }
        guard let path = tool.path(in: tools) else { return .notInstalled }
        let entry = installed[tool]
        let toolPath = path.standardizedFileURL.path
        let isOwn = entry.map { record in
            record.paths.contains { owned in
                let ownedPath = URL(fileURLWithPath: owned).standardizedFileURL.path
                return toolPath == ownedPath || toolPath.hasPrefix(ownedPath + "/")
            }
        } ?? false
        let source = isOwn ? .openShelf : source(of: path)
        let version = isOwn ? entry?.version : version(of: path)
        return .installed(InstalledToolInfo(path: path, source: source, version: version, canRemove: isOwn))
    }

    /// Where a tool someone installed themselves came from, judged by its path.
    nonisolated static func source(of path: URL) -> ToolSource {
        let resolved = path.resolvingSymlinksInPath().path
        if resolved.contains("/Cellar/") || resolved.contains("/Caskroom/") || path.path.hasPrefix("/opt/homebrew/") {
            return .homebrew
        }
        if path.path.hasPrefix("/opt/local/") { return .macPorts }
        if path.path.contains(".app/") { return .applications }
        return .other
    }

    /// The version, when it can be read without running the tool: from a
    /// Homebrew folder name ("…/Cellar/ffmpeg/9.0.2_1/bin/ffmpeg" → "9.0.2"),
    /// or from an app's Info.plist.
    nonisolated static func version(of path: URL) -> String? {
        let components = path.resolvingSymlinksInPath().pathComponents
        if let cellar = components.firstIndex(of: "Cellar"), cellar + 2 < components.count {
            let folder = components[cellar + 2]
            return String(folder.split(separator: "_").first ?? Substring(folder))
        }
        if let app = components.firstIndex(where: { $0.hasSuffix(".app") }) {
            let bundle = NSString.path(withComponents: Array(components[...app]))
            let info = URL(fileURLWithPath: bundle).appendingPathComponent("Contents/Info.plist")
            if let plist = NSDictionary(contentsOf: info), let version = plist["CFBundleShortVersionString"] as? String {
                return version
            }
        }
        return nil
    }
}

extension ExtraTool {
    /// For the Install button, since LibreOffice is a large download.
    var approximateDownloadSize: String {
        switch self {
        case .ffmpeg: FFmpegSource.approximateSize
        case .libreOffice: LibreOfficeSource.approximateSize
        }
    }

    /// Where OpenShelf downloads it from.
    var downloadHost: String {
        switch self {
        case .ffmpeg: FFmpegSource.host
        case .libreOffice: "libreoffice.org"
        }
    }
}
