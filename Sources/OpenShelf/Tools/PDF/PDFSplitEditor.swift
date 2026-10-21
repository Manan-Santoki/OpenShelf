import PDFKit
import SwiftUI

/// The "Split" tool: one PDF into several, by page, by chunk or by ranges.
struct PDFSplitEditor: View {
    static let windowSize = CGSize(width: 440, height: 380)

    private enum Mode: Hashable {
        case everyPage
        case chunks
        case ranges
    }

    let url: URL
    let actions: EditorActions

    @State private var mode = Mode.everyPage
    @State private var chunkSize = 2
    @State private var rangesText = ""
    @State private var isWorking = false
    private let pageCount: Int
    private let isLocked: Bool

    init(url: URL, actions: EditorActions) {
        self.url = url
        self.actions = actions
        let document = PDFDocument(url: url)
        pageCount = document?.pageCount ?? 0
        isLocked = document?.isLocked ?? false
    }

    private var groups: Result<[[Int]], PDFTools.RangeError> {
        switch mode {
        case .everyPage: .success(PDFTools.everyPage(pageCount: pageCount))
        case .chunks: .success(PDFTools.chunks(pageCount: pageCount, size: chunkSize))
        case .ranges: PDFTools.ranges(rangesText, pageCount: pageCount)
        }
    }

    private var fileCount: Int {
        (try? groups.get().count) ?? 0
    }

    var body: some View {
        EditorChrome(
            title: "Split PDF",
            primaryTitle: isWorking ? "Splitting…" : "Split",
            isPrimaryEnabled: !isWorking && !isLocked && fileCount > 0,
            actions: actions,
            onReset: {
                mode = .everyPage
                chunkSize = 2
                rangesText = ""
            },
            onPrimary: split
        ) {
            VStack(alignment: .leading, spacing: 14) {
                PDFFileHeader(url: url, detail: pageCount == 1 ? "1 page" : "\(pageCount) pages")

                Picker("Split", selection: $mode) {
                    Text("Every page").tag(Mode.everyPage)
                    Text("Every N pages").tag(Mode.chunks)
                    Text("Ranges").tag(Mode.ranges)
                }
                .pickerStyle(.segmented)
                .labelsHidden()

                switch mode {
                case .everyPage:
                    Text("Each page becomes its own PDF.")
                        .font(.system(size: 12))
                        .foregroundStyle(.secondary)
                case .chunks:
                    EditorRow(label: "Pages each") {
                        Stepper(value: $chunkSize, in: 1...max(pageCount, 1)) {
                            Text("\(chunkSize)")
                                .font(.system(size: 12).monospacedDigit())
                        }
                        Spacer(minLength: 0)
                    }
                case .ranges:
                    EditorRow(label: "Ranges") {
                        TextField("e.g. 1-3, 4, 5-\(max(pageCount, 5))", text: $rangesText)
                            .textFieldStyle(.roundedBorder)
                            .accessibilityLabel("Page ranges")
                    }
                }

                summary
                Spacer(minLength: 0)
            }
        }
    }

    @ViewBuilder private var summary: some View {
        HStack(spacing: 8) {
            if isLocked {
                Label("This PDF is password-protected.", systemImage: "lock")
                    .foregroundStyle(.orange)
            } else {
                switch groups {
                case .success(let groups):
                    Label(groups.count == 1 ? "Creates 1 PDF" : "Creates \(groups.count) PDFs", systemImage: "doc.on.doc")
                        .foregroundStyle(.primary)
                case .failure(let error):
                    Label(rangesText.isEmpty ? "Enter the pages for each new PDF, separated by commas." : error.message,
                          systemImage: rangesText.isEmpty ? "info.circle" : "exclamationmark.triangle")
                        .foregroundStyle(rangesText.isEmpty ? Color.secondary : Color.orange)
                }
            }
            Spacer(minLength: 0)
            if isWorking {
                ProgressView().controlSize(.small)
            }
        }
        .font(.system(size: 12))
        .padding(10)
        .background(RoundedRectangle(cornerRadius: 10, style: .continuous).fill(.primary.opacity(0.05)))
    }

    private func split() {
        guard let groups = try? groups.get(), !groups.isEmpty else { return }
        isWorking = true
        let url = url
        Task {
            let result = await Task.detached(priority: .userInitiated) {
                Result { try PDFTools.split(url, into: groups) }
            }.value
            isWorking = false
            switch result {
            case .success(let urls): actions.saved(urls)
            case .failure(let error): actions.failed(error)
            }
        }
    }
}

/// File name, a short detail line and a page icon, shared by the PDF editors.
struct PDFFileHeader: View {
    let url: URL
    let detail: String
    var count = 1

    var body: some View {
        HStack(spacing: 12) {
            Image(nsImage: NSWorkspace.shared.icon(forFile: url.path))
                .resizable()
                .frame(width: 44, height: 44)
                .overlay(alignment: .bottomTrailing) {
                    if count > 1 {
                        Text("\(count)")
                            .font(.system(size: 10, weight: .bold).monospacedDigit())
                            .foregroundStyle(.white)
                            .padding(.horizontal, 5)
                            .frame(minWidth: 18, minHeight: 18)
                            .background(Capsule().fill(Color.accentColor))
                            .offset(x: 5, y: 3)
                    }
                }
                .accessibilityHidden(true)
            VStack(alignment: .leading, spacing: 2) {
                Text(count == 1 ? url.lastPathComponent : "\(count) PDFs")
                    .font(.system(size: 13, weight: .semibold))
                    .lineLimit(1)
                    .truncationMode(.middle)
                Text(detail)
                    .font(.system(size: 11))
                    .foregroundStyle(.secondary)
            }
            Spacer(minLength: 0)
        }
    }
}
