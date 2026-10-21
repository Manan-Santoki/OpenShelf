import AppKit

/// Runs conversions one batch at a time, reporting progress in a toast next to
/// the pointer.
@MainActor
final class ConversionRunner {
    static let shared = ConversionRunner()

    private var current: Task<Void, Never>?

    func convert(_ urls: [URL], to format: OutputFormat, near location: NSPoint = NSEvent.mouseLocation) {
        guard !urls.isEmpty else { return }
        let previous = current
        current = Task { [weak self] in
            // Batches queue behind each other so their toasts don't collide.
            await previous?.value
            await self?.run(urls, to: format, near: location)
        }
    }

    private func run(_ urls: [URL], to format: OutputFormat, near location: NSPoint) async {
        let toasts = ToastController.shared
        let tools = ExternalTools.detect()
        // Several files zip into one archive; everything else converts per file.
        let jobs: [[URL]] = format == .zip ? [urls] : urls.map { [$0] }
        let total = jobs.count

        let title = switch format {
        case .zip: "Compressing to ZIP"
        case .extract: "Extracting"
        default: "Converting to \(format.title)"
        }
        let job = Task {
            var saved: [URL] = []
            var failures: [String] = []
            for (index, sources) in jobs.enumerated() {
                if Task.isCancelled { break }
                do {
                    let outputs = try await format == .zip
                        ? [ArchiveConversion.zip(sources)]
                        : Converter.convert(sources[0], to: format, tools: tools) { fraction in
                            Task { @MainActor in toasts.updateProgress((Double(index) + fraction) / Double(total)) }
                        }
                    saved += outputs
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
            let title = failures.count == 1 ? "Couldn’t convert" : "\(failures.count) files couldn’t be converted"
            toasts.show(Toast(systemImage: "exclamationmark.triangle", title: title, subtitle: failure), for: .seconds(4))
        } else if saved.count == 1 {
            toasts.show(Toast(systemImage: "checkmark", title: "Saved", subtitle: saved[0].lastPathComponent))
        } else {
            toasts.show(Toast(systemImage: "checkmark", title: "Saved \(saved.count) files", subtitle: format == .extract ? nil : "as \(format.title)"))
        }
    }
}
