import AppKit

/// Runs file jobs one batch at a time, reporting progress in a toast next to
/// the pointer. Used by the Convert wheel and by one-click tools.
@MainActor
final class ConversionRunner {
    static let shared = ConversionRunner()

    /// Produces the files written for one job's inputs, reporting 0...1 progress.
    typealias Operation = @Sendable ([URL], @escaping @Sendable (Double) -> Void) async throws -> [URL]

    private var current: Task<Void, Never>?

    /// - Parameter settings: How to encode; nil for Settings → Output's,
    ///   read now so a change mid-batch doesn't mix two kinds of output.
    func convert(_ urls: [URL], to format: OutputFormat, settings: OutputSettings? = nil, near location: NSPoint = NSEvent.mouseLocation) {
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
        run(title: title, jobs: jobs, savedSubtitle: format == .extract ? nil : "as \(format.title)", near: location) { sources, progress in
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
        operation: @escaping Operation
    ) {
        guard !jobs.isEmpty else { return }
        let previous = current
        current = Task { [weak self] in
            // Batches queue behind each other so their toasts don't collide.
            await previous?.value
            await self?.execute(title: title, jobs: jobs, savedSubtitle: savedSubtitle, near: location, operation: operation)
        }
    }

    private func execute(
        title: String,
        jobs: [[URL]],
        savedSubtitle: String?,
        near location: NSPoint,
        operation: @escaping Operation
    ) async {
        let toasts = ToastController.shared
        let total = jobs.count
        let job = Task {
            var saved: [URL] = []
            var failures: [String] = []
            for (index, sources) in jobs.enumerated() {
                if Task.isCancelled { break }
                do {
                    saved += try await operation(sources) { fraction in
                        Task { @MainActor in toasts.updateProgress((Double(index) + fraction) / Double(total)) }
                    }
                } catch is CancellationError {
                    break
                } catch {
                    failures.append("\(sources[0].lastPathComponent): \(error.localizedDescription)")
                }
                toasts.updateProgress(Double(index + 1) / Double(total), subtitle: "\(index + 1) of \(total) saved")
            }
            return (saved, failures)
        }

        toasts.showProgress(Toast(systemImage: "arrow.triangle.2.circlepath", title: title, subtitle: "0 of \(total) saved", progress: 0), near: location) {
            job.cancel()
        }
        let (saved, failures) = await job.value

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
