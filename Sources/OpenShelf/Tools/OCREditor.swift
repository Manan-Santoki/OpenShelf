// Adapted from David NeSmith’s d0b71b4 and 207bc44; see docs/FORK_REVIEW.md.
import Observation
import SwiftUI

/// Reads the text in images and scanned PDFs, saved as a searchable PDF or text.
struct OCREditor: View {
    static let windowSize = CGSize(width: 420, height: 250)

    let actions: EditorActions
    @State private var model: OCRModel

    init(urls: [URL], actions: EditorActions) {
        self.actions = actions
        _model = State(initialValue: OCRModel(urls: urls))
    }

    var body: some View {
        EditorChrome(
            title: "Recognise Text",
            primaryTitle: "Save",
            isPrimaryEnabled: model.progress == nil,
            actions: actions,
            onPrimary: { model.save(actions: actions) }
        ) {
            VStack(alignment: .leading, spacing: 14) {
                Text(model.title)
                    .font(.system(size: 13, weight: .semibold))
                    .lineLimit(1)
                    .truncationMode(.middle)
                EditorRow(label: "Save as") {
                    Picker("Save as", selection: $model.output) {
                        Text("Searchable PDF").tag(OCR.Output.searchablePDF)
                        Text("Text").tag(OCR.Output.text)
                    }
                    .pickerStyle(.segmented)
                    .labelsHidden()
                }
                Text("Read on this Mac in your system languages. PDF pages that already have text are kept.")
                    .font(.system(size: 11))
                    .foregroundStyle(.secondary)
                if let progress = model.progress {
                    MediaExportProgress(progress: progress, detail: model.detail)
                }
                Spacer(minLength: 0)
            }
        }
        .onDisappear(perform: model.cancel)
    }
}

@MainActor
@Observable
final class OCRModel {
    let urls: [URL]
    var output = OCR.Output.searchablePDF
    private(set) var progress: Double?
    private(set) var detail: String?
    @ObservationIgnored private var task: Task<Void, Never>?

    init(urls: [URL]) {
        self.urls = urls
    }

    var title: String {
        urls.count == 1 ? urls[0].lastPathComponent : "\(urls.count) files"
    }

    func cancel() {
        task?.cancel()
    }

    func save(actions: EditorActions) {
        guard progress == nil, !urls.isEmpty else { return }
        progress = 0
        let urls = urls
        let output = output
        task = Task { [weak self] in
            var saved: [URL] = []
            do {
                for (index, url) in urls.enumerated() {
                    try Task.checkCancellation()
                    self?.detail = urls.count > 1 ? "Reading \(index + 1) of \(urls.count)…" : nil
                    saved.append(try await OCR.run(url, output: output) { fraction in
                        Task { @MainActor in self?.progress = (Double(index) + fraction) / Double(urls.count) }
                    })
                }
                try Task.checkCancellation()
                actions.saved(saved)
            } catch is CancellationError {
                self?.progress = nil
            } catch {
                self?.progress = nil
                self?.detail = nil
                actions.failed(error)
            }
        }
    }
}
