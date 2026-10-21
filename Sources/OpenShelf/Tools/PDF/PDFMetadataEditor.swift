import SwiftUI

/// The PDF "Metadata" tool: edit document info for one PDF, or strip it from several.
struct PDFMetadataEditor: View {
    static let windowSize = CGSize(width: 460, height: 520)

    let urls: [URL]
    let actions: EditorActions

    @State private var fields = PDFMetadataFields()
    @State private var isWorking = false
    private let infos: [PDFInfo?]

    init(urls: [URL], actions: EditorActions) {
        self.urls = urls
        self.actions = actions
        infos = urls.map(PDFTools.info(for:))
        _fields = State(initialValue: infos.first??.fields ?? PDFMetadataFields())
    }

    private var isSingle: Bool { urls.count == 1 }
    private var singleInfo: PDFInfo? { infos.first ?? nil }
    private var originalFields: PDFMetadataFields { singleInfo?.fields ?? PDFMetadataFields() }
    private var hasLockedFile: Bool { infos.contains { $0?.isLocked ?? true } }

    /// Saving is only offered when the copy would differ from the original.
    private var canSave: Bool {
        guard !isWorking, !hasLockedFile else { return false }
        if isSingle {
            return fields != originalFields || (fields.isEmpty && singleInfo?.hasMetadata == true)
        }
        return infos.contains { $0?.hasMetadata == true }
    }

    var body: some View {
        EditorChrome(
            title: "PDF Metadata",
            primaryTitle: isWorking ? "Saving…" : (isSingle ? "Save" : "Remove Metadata"),
            isPrimaryEnabled: canSave,
            actions: actions,
            onReset: isSingle ? { fields = originalFields } : nil,
            onPrimary: save
        ) {
            VStack(alignment: .leading, spacing: 14) {
                PDFFileHeader(url: urls[0], detail: headerDetail, count: urls.count)
                if hasLockedFile {
                    Label("Password-protected PDFs can’t be changed.", systemImage: "lock")
                        .font(.system(size: 12))
                        .foregroundStyle(.orange)
                }
                if isSingle {
                    singleFileForm
                } else {
                    batchSummary
                }
                Spacer(minLength: 0)
            }
        }
    }

    private var headerDetail: String {
        if isSingle, let info = singleInfo {
            let pages = info.pageCount == 1 ? "1 page" : "\(info.pageCount) pages"
            return "\(pages) · \(PDFTools.formatted(info.fileSize))"
        }
        let withMetadata = infos.compactMap { $0 }.filter(\.hasMetadata).count
        return "\(withMetadata) of \(urls.count) have document info"
    }

    private var singleFileForm: some View {
        VStack(alignment: .leading, spacing: 10) {
            field("Title", text: $fields.title)
            field("Author", text: $fields.author)
            field("Subject", text: $fields.subject)
            field("Keywords", text: $fields.keywords, prompt: "Comma separated")
            field("Creator", text: $fields.creator)
            field("Producer", text: $fields.producer)

            Divider().padding(.vertical, 2)

            if let info = singleInfo {
                readOnly("Created", info.created.map(Self.format) ?? "—")
                readOnly("Modified", info.modified.map(Self.format) ?? "—")
                readOnly("Encrypted", info.isEncrypted ? "Yes" : "No")
            }

            HStack {
                Spacer()
                Button("Remove All") { fields = PDFMetadataFields() }
                    .disabled(fields.isEmpty)
                    .help("Clear every field; saving then writes a clean copy")
            }
        }
    }

    private var batchSummary: some View {
        ScrollView {
            VStack(spacing: 6) {
                ForEach(Array(urls.enumerated()), id: \.offset) { index, url in
                    HStack {
                        Text(url.lastPathComponent)
                            .font(.system(size: 12))
                            .lineLimit(1)
                            .truncationMode(.middle)
                        Spacer()
                        if let info = infos[index] {
                            Text(info.isLocked ? "Locked" : info.hasMetadata ? "Has document info" : "Clean")
                                .font(.system(size: 11))
                                .foregroundStyle(info.isLocked ? Color.orange : .secondary)
                        } else {
                            Text("Unreadable")
                                .font(.system(size: 11))
                                .foregroundStyle(.orange)
                        }
                    }
                    .padding(.horizontal, 10)
                    .padding(.vertical, 7)
                    .background(RoundedRectangle(cornerRadius: 8, style: .continuous).fill(.primary.opacity(0.05)))
                }
            }
        }
        .frame(maxHeight: 260)
    }

    private func field(_ label: String, text: Binding<String>, prompt: String = "") -> some View {
        EditorRow(label: label) {
            TextField(prompt, text: text)
                .textFieldStyle(.roundedBorder)
                .accessibilityLabel(label)
        }
    }

    private func readOnly(_ label: String, _ value: String) -> some View {
        EditorRow(label: label) {
            Text(value)
                .font(.system(size: 12))
                .textSelection(.enabled)
            Spacer(minLength: 0)
        }
    }

    private func save() {
        isWorking = true
        let urls = urls
        // nil removes everything; used for a cleared single form.
        let fields: PDFMetadataFields? = isSingle && !fields.isEmpty ? fields : nil
        let isSingle = isSingle
        Task {
            let result = await Task.detached(priority: .userInitiated) {
                Result {
                    // A batch only strips, skipping files that are already clean.
                    isSingle
                        ? SaveOutcome(saved: [try PDFTools.saveMetadata(urls[0], fields: fields)])
                        : try PDFTools.removeMetadata(urls)
                }
            }.value
            isWorking = false
            switch result {
            case .success(let outcome): actions.finished(outcome)
            case .failure(let error): actions.failed(error)
            }
        }
    }

    private static func format(_ date: Date) -> String {
        date.formatted(date: .abbreviated, time: .shortened)
    }
}
