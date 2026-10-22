import PDFKit
import SwiftUI

/// The "Split" tool: one PDF into several, by page, by chunk or by ranges.
/// Given several PDFs, a filmstrip moves between them, and one rule can split
/// all of them or each can have its own.
struct PDFSplitEditor: View {
    static func windowSize(for count: Int) -> CGSize {
        // The filmstrip takes the place of the file header.
        CGSize(width: 440, height: count > 1 ? 380 + EditorFilmstrip.height - 44 : 380)
    }

    let urls: [URL]
    let actions: EditorActions

    @State private var index = 0
    /// Each PDF's rule.
    @State private var rules: [PDFSplitRule]
    /// One rule for every PDF, each split as far as its pages go.
    @State private var sameForAll = true
    @State private var isWorking = false
    private let pageCounts: [Int]
    private let locked: [Bool]
    private let thumbnails: [CGImage]

    init(urls: [URL], actions: EditorActions) {
        self.urls = urls
        self.actions = actions
        let documents = urls.map(PDFDocument.init(url:))
        pageCounts = documents.map { $0?.pageCount ?? 0 }
        locked = documents.map { $0?.isLocked ?? false }
        thumbnails = urls.count > 1 ? urls.map { PDFTools.firstPageThumbnail(of: $0) } : []
        _rules = State(initialValue: Array(repeating: PDFSplitRule(), count: urls.count))
    }

    private var isBatch: Bool { urls.count > 1 }
    private var pageCount: Int { pageCounts[index] }
    private var isLocked: Bool { locked[index] }

    /// The shown PDF's rule. With Same for All on, changing it changes every PDF's.
    private var rule: PDFSplitRule {
        get { rules[index] }
        nonmutating set {
            guard isBatch, sameForAll else {
                rules[index] = newValue
                return
            }
            rules = rules.map { _ in newValue }
        }
    }

    private var ruleBinding: Binding<PDFSplitRule> {
        Binding(get: { rule }, set: { rule = $0 })
    }

    /// While one rule splits every PDF, ranges and chunk sizes go up to the
    /// longest one.
    private var pageLimit: Int {
        isBatch && sameForAll ? pageCounts.max() ?? 0 : pageCount
    }

    /// With several PDFs, ranges are cut to each one's pages, so a rule kept
    /// from Same for All doesn't stop working when it's turned off.
    private func groups(_ index: Int) -> Result<[[Int]], PDFTools.RangeError> {
        rules[index].groups(pageCount: pageCounts[index], rangeLimit: isBatch ? pageCounts.max() : nil)
    }

    private var groups: Result<[[Int]], PDFTools.RangeError> { groups(index) }

    private var fileCount: Int {
        (try? groups.get().count) ?? 0
    }

    /// PDFs their rule would split; the rest are left as they are.
    private var edited: [Bool] {
        urls.indices.map { index in
            guard !locked[index], let groups = try? groups(index).get() else { return false }
            return PDFTools.splits(groups, pageCount: pageCounts[index])
        }
    }

    private var editedCount: Int { edited.filter { $0 }.count }

    /// New PDFs made from every PDF that will be split.
    private var totalFileCount: Int {
        urls.indices.filter { edited[$0] }.reduce(0) { $0 + ((try? groups($1).get().count) ?? 0) }
    }

    var body: some View {
        EditorChrome(
            title: isBatch ? "Split PDFs" : "Split PDF",
            primaryTitle: primaryTitle,
            isPrimaryEnabled: !isWorking && (isBatch ? editedCount > 0 : !isLocked && fileCount > 0),
            actions: actions,
            onReset: { rule = PDFSplitRule() },
            onPrimary: split
        ) {
            VStack(alignment: .leading, spacing: 14) {
                if isBatch {
                    EditorFilmstrip(
                        thumbnails: thumbnails,
                        names: urls.map(\.lastPathComponent),
                        selection: $index,
                        edited: edited,
                        sameForAll: $sameForAll
                    )
                    .onChange(of: sameForAll) { _, isOn in
                        // Turning it on spreads the shown PDF's rule to the rest.
                        if isOn { rule = rules[index] }
                    }
                } else {
                    PDFFileHeader(url: urls[0], detail: pageCount == 1 ? "1 page" : "\(pageCount) pages")
                }

                Picker("Split", selection: ruleBinding.mode) {
                    Text("Every page").tag(PDFSplitRule.Mode.everyPage)
                    Text("Every N pages").tag(PDFSplitRule.Mode.chunks)
                    Text("Ranges").tag(PDFSplitRule.Mode.ranges)
                }
                .pickerStyle(.segmented)
                .labelsHidden()

                switch rule.mode {
                case .everyPage:
                    Text("Each page becomes its own PDF.")
                        .font(.system(size: 12))
                        .foregroundStyle(.secondary)
                case .chunks:
                    EditorRow(label: "Pages each") {
                        Stepper(value: ruleBinding.chunkSize, in: 1...max(pageLimit, 1)) {
                            Text("\(rule.chunkSize)")
                                .font(.system(size: 12).monospacedDigit())
                        }
                        Spacer(minLength: 0)
                    }
                case .ranges:
                    EditorRow(label: "Ranges") {
                        TextField("e.g. 1-3, 4, 5-\(max(pageLimit, 5))", text: ruleBinding.rangesText)
                            .textFieldStyle(.roundedBorder)
                            .accessibilityLabel("Page ranges")
                    }
                }

                summary
                Spacer(minLength: 0)
            }
        }
    }

    private var primaryTitle: String {
        if isWorking { return "Splitting…" }
        guard isBatch else { return "Split" }
        switch editedCount {
        case 0: return "Split PDFs"
        case 1: return "Split 1 PDF"
        default: return "Split \(editedCount) PDFs"
        }
    }

    @ViewBuilder private var summary: some View {
        HStack(spacing: 8) {
            if isLocked {
                Label("This PDF is password-protected.", systemImage: "lock")
                    .foregroundStyle(.orange)
            } else {
                switch groups {
                case .success(let groups) where isBatch && !PDFTools.splits(groups, pageCount: pageCount):
                    // The rule asks for more pages than it has, so it stays whole.
                    Label(pageCount == 1 ? "Not split: it has 1 page" : "Not split: it has \(pageCount) pages", systemImage: "equal.circle")
                        .foregroundStyle(.secondary)
                case .success(let groups):
                    Label(groups.count == 1 ? "Creates 1 PDF" : "Creates \(groups.count) PDFs", systemImage: "doc.on.doc")
                        .foregroundStyle(.primary)
                case .failure(let error):
                    Label(rule.rangesText.isEmpty ? "Enter the pages for each new PDF, separated by commas." : error.message,
                          systemImage: rule.rangesText.isEmpty ? "info.circle" : "exclamationmark.triangle")
                        .foregroundStyle(rule.rangesText.isEmpty ? Color.secondary : Color.orange)
                }
            }
            Spacer(minLength: 0)
            if isWorking {
                ProgressView().controlSize(.small)
            } else if isBatch {
                Text(totalFileCount == 1 ? "1 PDF in all" : "\(totalFileCount) PDFs in all")
                    .foregroundStyle(.secondary)
            }
        }
        .font(.system(size: 12))
        .padding(10)
        .background(RoundedRectangle(cornerRadius: 10, style: .continuous).fill(.primary.opacity(0.05)))
    }

    private func split() {
        let isBatch = isBatch
        let jobs: [(url: URL, groups: [[Int]])]
        if isBatch {
            // PDFs that are password-protected, too short for the rule or
            // given ranges with a problem are left as they are.
            jobs = urls.indices.map { (urls[$0], edited[$0] ? (try? groups($0).get()) ?? [] : []) }
        } else {
            guard let groups = try? groups.get(), !groups.isEmpty else { return }
            jobs = [(urls[0], groups)]
        }
        isWorking = true
        Task {
            let result = await Task.detached(priority: .userInitiated) {
                Result {
                    isBatch ? try PDFTools.split(jobs) : SaveOutcome(saved: try PDFTools.split(jobs[0].url, into: jobs[0].groups))
                }
            }.value
            isWorking = false
            switch result {
            case .success(let outcome): actions.finished(outcome)
            case .failure(let error): actions.failed(error)
            }
        }
    }
}

/// How Split divides a PDF: a file per page, per run of pages, or per range.
struct PDFSplitRule: Equatable, Sendable {
    enum Mode: Hashable, Sendable {
        case everyPage
        case chunks
        case ranges
    }

    var mode = Mode.everyPage
    var chunkSize = 2
    var rangesText = ""

    /// Page groups (0-based) for a PDF of `pageCount` pages. With a
    /// `rangeLimit` (the longest of several PDFs), ranges are read up to that
    /// page and cut to this PDF: "4-9" is pages 4-6 of a 6-page PDF and
    /// nothing of a 3-page one.
    func groups(pageCount: Int, rangeLimit: Int? = nil) -> Result<[[Int]], PDFTools.RangeError> {
        switch mode {
        case .everyPage: .success(PDFTools.everyPage(pageCount: pageCount))
        case .chunks: .success(PDFTools.chunks(pageCount: pageCount, size: chunkSize))
        case .ranges:
            PDFTools.ranges(rangesText, pageCount: max(rangeLimit ?? pageCount, pageCount)).map { groups in
                groups.map { $0.filter { $0 < pageCount } }.filter { !$0.isEmpty }
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
