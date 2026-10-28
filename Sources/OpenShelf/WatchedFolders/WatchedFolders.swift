import AppKit
import Observation

/// The folders OpenShelf watches for new files, saved between launches.
/// Nothing is watched until a folder is added in Settings → Shelves.
@MainActor
@Observable
final class WatchedFolders {
    static let shared = WatchedFolders()

    private(set) var folders: [WatchedFolder]
    /// Switched-on folders that couldn't be watched: deleted, moved to the
    /// Trash, or on a disk that isn't connected.
    private(set) var unavailable: Set<WatchedFolder.ID> = []

    /// Called with new, finished files and the folder they arrived in.
    @ObservationIgnored var onArrival: (([URL], WatchedFolder) -> Void)?

    @ObservationIgnored private var monitors: [WatchedFolder.ID: FolderMonitor] = [:]
    /// Files OpenShelf itself just wrote (conversion and tool results). They
    /// land next to the original, so in the watched folder too, and must not
    /// count as arrivals: they'd run the folder's action again on its own
    /// output, and they go back on the shelf only when Settings allows.
    @ObservationIgnored private var recentOutputs: [String] = []
    @ObservationIgnored private let storeURL: URL
    @ObservationIgnored private var isStarted = false

    private static let recentOutputLimit = 200

    init(storeURL: URL = WatchedFolderStore.defaultURL) {
        self.storeURL = storeURL
        folders = WatchedFolderStore.load(from: storeURL)
    }

    /// Starts watching every switched-on folder; called once at launch.
    func start() {
        isStarted = true
        updateMonitors()
    }

    /// Whether `url` is already watched (the screenshot folder counts as
    /// wherever screenshots go now).
    func isWatching(_ url: URL) -> Bool {
        let path = url.standardizedFileURL.path
        return folders.contains { folder in
            folder.path == path || (folder.isScreenshots && ScreenshotLocation.current().standardizedFileURL.path == path)
        }
    }

    var isWatchingScreenshots: Bool {
        folders.contains(where: \.isScreenshots) || isWatching(ScreenshotLocation.current())
    }

    /// Adds a folder, switched on, unless it's already watched.
    func add(_ url: URL, isScreenshots: Bool = false) {
        guard !isWatching(url) else { return }
        folders.append(WatchedFolder(url: url, isScreenshots: isScreenshots))
        didChange()
    }

    func addScreenshots() {
        guard !isWatchingScreenshots else { return }
        add(ScreenshotLocation.current(), isScreenshots: true)
    }

    func remove(_ id: WatchedFolder.ID) {
        folders.removeAll { $0.id == id }
        didChange()
    }

    func setEnabled(_ isEnabled: Bool, for id: WatchedFolder.ID) {
        guard let index = folders.firstIndex(where: { $0.id == id }), folders[index].isEnabled != isEnabled else { return }
        folders[index].isEnabled = isEnabled
        didChange()
    }

    func setAction(_ action: WatchedFolderAction, for id: WatchedFolder.ID) {
        guard let index = folders.firstIndex(where: { $0.id == id }), folders[index].action != action else { return }
        folders[index].action = action
        didChange()
    }

    /// Records files OpenShelf wrote, so they aren't taken for arrivals.
    func noteOutputs(_ urls: [URL]) {
        recentOutputs += urls.map { $0.standardizedFileURL.path }
        if recentOutputs.count > Self.recentOutputLimit {
            recentOutputs.removeFirst(recentOutputs.count - Self.recentOutputLimit)
        }
    }

    /// Drops files OpenShelf wrote itself from a batch of arrivals.
    func arrivals(from urls: [URL]) -> [URL] {
        urls.filter { !recentOutputs.contains($0.standardizedFileURL.path) }
    }

    private func didChange() {
        try? WatchedFolderStore.save(folders, to: storeURL)
        updateMonitors()
    }

    /// Starts and stops monitors to match the switched-on folders.
    private func updateMonitors() {
        let wanted = isStarted ? Set(folders.filter(\.isEnabled).map(\.id)) : []
        for (id, monitor) in monitors where !wanted.contains(id) {
            monitor.stop()
            monitors[id] = nil
        }
        unavailable.formIntersection(wanted)
        for folder in folders where wanted.contains(folder.id) && monitors[folder.id] == nil {
            startMonitor(for: folder)
        }
    }

    private func startMonitor(for folder: WatchedFolder) {
        let id = folder.id
        guard let url = folder.resolve() else {
            unavailable.insert(id)
            return
        }
        let monitor = FolderMonitor(url: url)
        monitor.onArrival = { [weak self] urls in
            self?.deliver(urls, from: id)
        }
        monitor.onUnavailable = { [weak self] in
            self?.monitors[id] = nil
            self?.unavailable.insert(id)
        }
        guard monitor.start() else {
            unavailable.insert(id)
            return
        }
        monitors[id] = monitor
        unavailable.remove(id)
    }

    private func deliver(_ urls: [URL], from id: WatchedFolder.ID) {
        let urls = arrivals(from: urls)
        guard !urls.isEmpty, let folder = folders.first(where: { $0.id == id }), folder.isEnabled else { return }
        onArrival?(urls, folder)
    }
}

extension WatchedFolderAction {
    /// Runs this action on the files it applies to, through the same paths
    /// as the wheels, so results are added back to `sourceShelfID` when
    /// Settings → Shelves allows it.
    @MainActor
    func run(on urls: [URL], sourceShelfID: Shelf.ID?) {
        switch self {
        case .addOnly:
            return
        case .convert(let format):
            let tools = ExternalTools.current
            let targets = urls.filter { ConversionCatalog.formats(for: $0, tools: tools).contains(format) }
            guard !targets.isEmpty else { return }
            ConversionRunner.shared.convert(targets, to: format, sourceShelfID: sourceShelfID)
        case .tool(let tool):
            let targets = urls.filter { FileTool.available(for: [$0]).contains(tool) }
            guard !targets.isEmpty else { return }
            ToolLauncher.open(tool, with: targets, sourceShelfID: sourceShelfID)
        }
    }
}
