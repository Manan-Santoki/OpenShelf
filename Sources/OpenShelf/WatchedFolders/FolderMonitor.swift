import Foundation

/// Watches one folder's top level and reports files that finish arriving in
/// it after watching starts (see `ArrivalTracker` for the rules).
///
/// A dispatch source on the folder says when its list of files changes; the
/// folder is then listed off the main thread. While a new file is still
/// growing it's listed again every `pollInterval` until its size settles,
/// since writing into a file doesn't change the folder itself.
@MainActor
final class FolderMonitor {
    static let pollInterval: Duration = .milliseconds(500)
    /// Gathers a burst of changes (a download being renamed into place,
    /// several screenshots at once) into one listing.
    private static let coalesceDelay: Duration = .milliseconds(150)
    /// The creation date Finder gives a file while it's still copying it.
    private nonisolated static let busyCreationDate = Date(timeIntervalSince1970: 443_779_200)

    let url: URL
    /// New, finished files, in name order.
    var onArrival: (([URL]) -> Void)?
    /// The folder was deleted, moved away or its disk ejected; watching stopped.
    var onUnavailable: (() -> Void)?

    private var source: (any DispatchSourceFileSystemObject)?
    private var tracker: ArrivalTracker?
    private var scanTask: Task<Void, Never>?
    /// The folder changed while a listing was under way.
    private var needsRescan = false

    init(url: URL) {
        self.url = url
    }

    /// Starts watching; false when the folder can't be opened.
    func start() -> Bool {
        guard source == nil else { return true }
        let descriptor = open(url.path, O_EVTONLY)
        guard descriptor >= 0 else { return false }
        let source = DispatchSource.makeFileSystemObjectSource(
            fileDescriptor: descriptor,
            eventMask: [.write, .delete, .rename, .revoke],
            queue: .main
        )
        source.setEventHandler { [weak self] in
            MainActor.assumeIsolated {
                self?.folderDidChange()
            }
        }
        source.setCancelHandler {
            close(descriptor)
        }
        self.source = source
        source.resume()
        // The first listing records what's already there.
        scheduleScan(after: .zero)
        return true
    }

    func stop() {
        scanTask?.cancel()
        scanTask = nil
        source?.cancel()
        source = nil
        tracker = nil
        needsRescan = false
    }

    private func folderDidChange() {
        guard let source else { return }
        if !source.data.isDisjoint(with: [.delete, .rename, .revoke]) {
            // Moved to the Trash, renamed or ejected: this path no longer is the folder.
            stop()
            onUnavailable?()
            return
        }
        if scanTask == nil {
            scheduleScan(after: Self.coalesceDelay)
        } else {
            needsRescan = true
        }
    }

    private func scheduleScan(after delay: Duration) {
        guard scanTask == nil else { return }
        needsRescan = false
        let directory = url
        scanTask = Task { [weak self] in
            if delay > .zero {
                try? await Task.sleep(for: delay)
            }
            guard !Task.isCancelled else { return }
            let entries = await Task.detached { FolderMonitor.scan(directory) }.value
            guard let self, !Task.isCancelled else { return }
            self.scanTask = nil
            self.apply(entries)
        }
    }

    private func apply(_ entries: [ArrivalTracker.Entry]?) {
        guard let entries else {
            stop()
            onUnavailable?()
            return
        }
        guard var tracker else {
            tracker = ArrivalTracker(existing: entries)
            if needsRescan { scheduleScan(after: Self.coalesceDelay) }
            return
        }
        let ready = tracker.observe(entries, now: .now)
        self.tracker = tracker
        if !ready.isEmpty {
            onArrival?(ready.map { url.appendingPathComponent($0) })
        }
        if needsRescan {
            scheduleScan(after: Self.coalesceDelay)
        } else if tracker.hasPending {
            scheduleScan(after: Self.pollInterval)
        }
    }

    /// The folder's top-level entries, or nil when it can't be read.
    /// Ignored names aren't looked at any further.
    nonisolated static func scan(_ directory: URL) -> [ArrivalTracker.Entry]? {
        let fileManager = FileManager.default
        guard let names = try? fileManager.contentsOfDirectory(atPath: directory.path) else { return nil }
        return names.compactMap { name in
            guard !ArrivalFilter.isIgnored(name),
                  let attributes = try? fileManager.attributesOfItem(atPath: directory.appendingPathComponent(name).path)
            else { return nil }
            let created = attributes[.creationDate] as? Date
            return ArrivalTracker.Entry(
                name: name,
                fileNumber: (attributes[.systemFileNumber] as? NSNumber)?.uint64Value,
                size: (attributes[.size] as? NSNumber)?.int64Value ?? 0,
                isRegularFile: attributes[.type] as? FileAttributeType == .typeRegular,
                isBusy: created == busyCreationDate
            )
        }
    }
}
