import SwiftUI

/// The PDF "Compress" tool: smaller copies of one or more PDFs.
struct PDFCompressEditor: View {
    static let windowSize = CGSize(width: 440, height: 380)

    let urls: [URL]
    let actions: EditorActions

    @State private var level = PDFCompressionLevel.balanced
    @State private var estimate: Int64?
    @State private var estimateFailed = false
    @State private var isWorking = false
    @State private var notice: String?
    private let originalSizes: [Int64]

    init(urls: [URL], actions: EditorActions) {
        self.urls = urls
        self.actions = actions
        originalSizes = urls.map(PDFTools.fileSize)
    }

    private var totalOriginal: Int64 { originalSizes.reduce(0, +) }

    /// For a single PDF the estimate is the real result, so a copy that
    /// wouldn't be smaller is known before anything is saved.
    private var isKnownNotSmaller: Bool {
        guard urls.count == 1, let estimate else { return false }
        return !ImageCompressor.isWorthSaving(Int(estimate), originalSize: originalSizes[0])
    }

    var body: some View {
        EditorChrome(
            title: "Compress PDF",
            primaryTitle: isWorking ? "Compressing…" : "Compress",
            isPrimaryEnabled: !isWorking && !isKnownNotSmaller,
            actions: actions,
            onReset: { level = .balanced },
            onPrimary: compress
        ) {
            VStack(alignment: .leading, spacing: 14) {
                PDFFileHeader(
                    url: urls[0],
                    detail: urls.count == 1 ? PDFTools.formatted(totalOriginal) : "\(PDFTools.formatted(totalOriginal)) in total",
                    count: urls.count
                )

                Picker("Level", selection: $level) {
                    ForEach(PDFCompressionLevel.allCases, id: \.self) { level in
                        Text(level.title).tag(level)
                    }
                }
                .pickerStyle(.segmented)
                .labelsHidden()

                Label(level.summary, systemImage: "info.circle")
                    .font(.system(size: 12))
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)

                sizeSummary
                EditorStatusLine(message: notice, systemImage: "checkmark.circle", tint: .orange)
                Spacer(minLength: 0)
            }
        }
        .task(id: level) {
            notice = nil
            await updateEstimate()
        }
    }

    private var sizeSummary: some View {
        HStack(spacing: 8) {
            Label {
                if let estimate {
                    let saving = totalOriginal > 0 ? 100 - Int(Double(estimate) / Double(totalOriginal) * 100) : 0
                    Text(urls.count == 1 ? "≈ \(PDFTools.formatted(estimate))" : "≈ \(PDFTools.formatted(estimate)) for \(urls.count) PDFs")
                        .font(.system(size: 12, weight: .semibold))
                    + Text(saving > 0 ? "  (\(saving)% smaller)" : "  (no smaller — nothing left to squeeze)")
                        .font(.system(size: 12))
                        .foregroundStyle(saving > 0 ? Color.secondary : Color.orange)
                } else if estimateFailed {
                    Text("Couldn’t estimate the size")
                        .font(.system(size: 12))
                        .foregroundStyle(.secondary)
                } else {
                    Text("Estimating…")
                        .font(.system(size: 12))
                        .foregroundStyle(.secondary)
                }
            } icon: {
                Image(systemName: "arrow.down.right.and.arrow.up.left")
                    .foregroundStyle(Color.accentColor)
            }
            Spacer(minLength: 0)
            if isWorking {
                ProgressView().controlSize(.small)
            }
        }
        .padding(10)
        .background(RoundedRectangle(cornerRadius: 10, style: .continuous).fill(.primary.opacity(0.05)))
    }

    /// Compresses the first file in the background and scales its result to the batch.
    private func updateEstimate() async {
        estimate = nil
        estimateFailed = false
        let first = urls[0]
        let level = level
        let size = await Task.detached(priority: .utility) {
            (try? PDFTools.compressedData(first, level: level)).map { Int64($0.count) }
        }.value
        guard !Task.isCancelled else { return }
        guard let size else {
            estimateFailed = true
            return
        }
        let firstOriginal = originalSizes[0]
        estimate = firstOriginal > 0 ? Int64(Double(size) * Double(totalOriginal) / Double(firstOriginal)) : size
    }

    private func compress() {
        isWorking = true
        notice = nil
        let urls = urls
        let level = level
        Task {
            let result = await Task.detached(priority: .userInitiated) {
                Result { try PDFTools.compress(urls, level: level) }
            }.value
            isWorking = false
            switch result {
            case .success(let outcome):
                if !outcome.savedAnything {
                    notice = PDFToolError.notSmaller(count: outcome.unchanged).localizedDescription
                }
                actions.finished(outcome)
            case .failure(let error):
                actions.failed(error)
            }
        }
    }
}
