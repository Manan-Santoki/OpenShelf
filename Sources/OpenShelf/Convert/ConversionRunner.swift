import AppKit

/// Runs file jobs one batch at a time, reporting progress in a toast next to
/// the pointer. Used by the Convert wheel and by one-click tools.
@MainActor
final class ConversionRunner {
    static let shared = ConversionRunner()

    /// Produces the files written for one job's inputs, reporting 0...1 progress.
    typealias Operation = @Sendable ([URL], @escaping @Sendable (Double) -> Void) async throws -> [URL]

    private var current: Task<Void, Never>?

    /// Called with the files a batch wrote, and the shelf the operation was
    /// started from, if any. Wired once at launch to add them back to that
    /// shelf when Settings → Shelves allows it.
    var onSaved: (([URL], Shelf.ID?) -> Void)?

    /// Called with each job's files as soon as they're written, before the
    /// rest of the batch finishes. Wired once at launch so watched folders
    /// can tell OpenShelf's own output from files that arrived.
    var onJobSaved: (@MainActor @Sendable ([URL]) -> Void)?

    /// - Parameters:
    ///   - settings: How to encode; nil for Settings → Output's, read now so
    ///     a change mid-batch doesn't mix two kinds of output.
    ///   - sourceShelfID: The shelf `urls` were dragged or selected from, if any.
    func convert(_ urls: [URL], to format: OutputFormat, settings: OutputSettings? = nil, near location: NSPoint = NSEvent.mouseLocation, sourceShelfID: Shelf.ID? = nil) {
        let tools = ExternalTools.current
        let settings = settings ?? Preferences.shared.output
        let isArchive = [.zip, .tar, .tgz].contains(format)
        let title = switch format {
        case .zip, .tar, .tgz: "Compressing to \(format.title)"
        case .extract: "Extracting"
        default: "Converting to \(format.title)"
        }
        // Several files go into one archive; everything else converts per file.
        let jobs: [[URL]] = isArchive ? [urls] : urls.map { [$0] }
        run(title: title, jobs: jobs, savedSubtitle: format == .extract ? nil : "as \(format.title)", near: location, sourceShelfID: sourceShelfID) { sources, progress in
            switch format {
            case .zip: return [try await ArchiveConversion.zip(sources)]
            case .tar: return [try await ArchiveConversion.tar(sources, gzip: false)]
            case .tgz: return [try await ArchiveConversion.tar(sources, gzip: true)]
            default: break
            }
            return try await Converter.convert(sources[0], to: format, tools: tools, settings: settings, progress: progress)
        }
    }

    /// Queues `jobs` behind any batch already running.
    func run(
        title: String,
        jobs: [[URL]],
        savedSubtitle: String? = nil,
        near location: NSPoint = NSEvent.mouseLocation,
        sourceShelfID: Shelf.ID? = nil,
        operation: @escaping Operation
    ) {
        guard !jobs.isEmpty else { return }
        let previous = current
        current = Task { [weak self] in
            // Batches queue behind each other so their toasts don't collide.
            await previous?.value
            await self?.execute(title: title, jobs: jobs, savedSubtitle: savedSubtitle, near: location, sourceShelfID: sourceShelfID, operation: operation)
        }
    }

    private func execute(
        title: String,
        jobs: [[URL]],
        savedSubtitle: String?,
        near location: NSPoint,
        sourceShelfID: Shelf.ID?,
        operation: @escaping Operation
    ) async {
        let toasts = ToastController.shared
        let total = jobs.count
        let onJobSaved = onJobSaved
        let job = Task {
            var saved: [URL] = []
            var failures: [String] = []
            for (index, sources) in jobs.enumerated() {
                if Task.isCancelled { break }
                let step = StepProgress(index: index, total: total) { toasts.updateProgress($0, subtitle: $1) }
                do {
                    let outputs = try await operation(sources) { fraction in
                        Task { @MainActor in step.report(fraction) }
                    }
                    saved += outputs
                    onJobSaved?(outputs)
                } catch is CancellationError {
                    step.cancel()
                    break
                } catch {
                    failures.append("\(sources[0].lastPathComponent): \(error.localizedDescription)")
                }
                step.finish(subtitle: "\(index + 1) of \(total) saved")
            }
            return (saved, failures)
        }

        toasts.showProgress(Toast(systemImage: "arrow.triangle.2.circlepath", title: title, subtitle: "0 of \(total) saved", progress: .determinate(0)), near: location) {
            job.cancel()
        }
        let (saved, failures) = await job.value
        if !saved.isEmpty { onSaved?(saved, sourceShelfID) }

        if job.isCancelled {
            toasts.show(Toast(systemImage: "xmark", title: "Cancelled", subtitle: saved.isEmpty ? nil : "\(saved.count) saved"))
        } else if let failure = failures.first {
            let title = failures.count == 1 ? "Something went wrong" : "\(failures.count) files failed"
            toasts.show(Toast(systemImage: "exclamationmark.triangle", title: title, subtitle: failure), for: .seconds(4))
        } else {
            // Worded like the editors' confirmations.
            toasts.show(SaveOutcome(saved: saved, detail: savedSubtitle).toast)
        }
    }
}

/// Tracks one file's share of the batch toast. Conversions that can't
/// measure their own progress (LibreOffice, Pandoc, AppKit printing…) simply
/// never call `report`; after a short wait with nothing to show, this swaps
/// the toast to a busy bar instead of leaving it looking stuck.
///
/// Internal, not private, for tests.
@MainActor
final class StepProgress {
    /// How long a file gets to report real progress before this gives up and
    /// shows a busy bar instead.
    static let indeterminateDelay: Duration = .milliseconds(350)

    private let index: Int
    private let total: Int
    /// Sets the toast's progress, and its subtitle when given.
    private let update: @MainActor (ToastProgress, String?) -> Void
    /// The late switch to the busy bar; readable so tests can wait for it.
    private(set) var pending: Task<Void, Never>?

    init(index: Int, total: Int, update: @escaping @MainActor (ToastProgress, String?) -> Void) {
        self.index = index
        self.total = total
        self.update = update
        pending = Task { [weak self] in
            try? await Task.sleep(for: Self.indeterminateDelay)
            guard let self, !Task.isCancelled else { return }
            self.update(.indeterminate, nil)
        }
    }

    /// `fraction` is this file's own 0...1 progress.
    func report(_ fraction: Double) {
        pending?.cancel()
        update(.determinate((Double(index) + min(max(fraction, 0), 1)) / Double(total)), nil)
    }

    func finish(subtitle: String) {
        pending?.cancel()
        update(.determinate(Double(index + 1) / Double(total)), subtitle)
    }

    func cancel() {
        pending?.cancel()
    }
}
