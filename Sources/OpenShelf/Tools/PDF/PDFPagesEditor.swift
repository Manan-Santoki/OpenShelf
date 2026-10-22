import PDFKit
import SwiftUI
import UniformTypeIdentifiers

/// The "Pages" tool: reorder, rotate and delete pages of a PDF. Given several
/// PDFs, a filmstrip moves between them, and each keeps its own pages.
struct PDFPagesEditor: View {
    static func windowSize(for count: Int) -> CGSize {
        CGSize(width: 620, height: count > 1 ? 640 + EditorFilmstrip.height : 640)
    }

    let urls: [URL]
    let actions: EditorActions

    @State private var index = 0
    /// Each PDF's pages as edited and as it was, once it has been shown.
    @State private var edits: [[PDFPageEdit]?]
    @State private var originals: [[PDFPageEdit]?]
    @State private var thumbnails: [[Int: CGImage]]
    @State private var loadErrors: [String?]
    @State private var selection: Set<PDFPageEdit.ID> = []
    @State private var dragged: PDFPageEdit.ID?
    @State private var isWorking = false
    private let firstPages: [CGImage]

    init(urls: [URL], actions: EditorActions) {
        self.urls = urls
        self.actions = actions
        _edits = State(initialValue: Array(repeating: nil, count: urls.count))
        _originals = State(initialValue: Array(repeating: nil, count: urls.count))
        _thumbnails = State(initialValue: Array(repeating: [:], count: urls.count))
        _loadErrors = State(initialValue: Array(repeating: nil, count: urls.count))
        firstPages = urls.count > 1 ? urls.map { PDFTools.firstPageThumbnail(of: $0) } : []
    }

    private var isBatch: Bool { urls.count > 1 }

    /// The shown PDF's pages.
    private var pages: [PDFPageEdit] {
        get { edits[index] ?? [] }
        nonmutating set { edits[index] = newValue }
    }

    private var pagesBinding: Binding<[PDFPageEdit]> {
        Binding(get: { pages }, set: { pages = $0 })
    }

    private var original: [PDFPageEdit] { originals[index] ?? [] }

    /// PDFs with page changes waiting to be saved.
    private var edited: [Bool] { urls.indices.map { edits[$0] != originals[$0] } }
    private var editedCount: Int { edited.filter { $0 }.count }

    private var canSave: Bool {
        guard !isWorking else { return false }
        guard isBatch else { return !pages.isEmpty && pages != original }
        // A PDF with every page deleted has nothing to save.
        return editedCount > 0 && !urls.indices.contains { edited[$0] && edits[$0]?.isEmpty == true }
    }

    var body: some View {
        EditorChrome(
            title: "Pages",
            primaryTitle: primaryTitle,
            isPrimaryEnabled: canSave,
            actions: actions,
            onReset: {
                withAnimation(.snappy) { pages = original }
                selection = []
            },
            onPrimary: save
        ) {
            VStack(spacing: 12) {
                grid
                toolbar
                if isBatch {
                    EditorFilmstrip(
                        thumbnails: firstPages,
                        names: urls.map(\.lastPathComponent),
                        selection: $index,
                        edited: edited,
                        sameForAll: nil
                    )
                    .onChange(of: index) { _, _ in
                        selection = []
                        dragged = nil
                    }
                }
            }
        }
        .task(id: index) { await load(index) }
    }

    private var primaryTitle: String {
        if isWorking { return "Saving…" }
        guard isBatch else { return "Save" }
        switch editedCount {
        case 0: return "Save PDFs"
        case 1: return "Save 1 PDF"
        default: return "Save \(editedCount) PDFs"
        }
    }

    private var grid: some View {
        ScrollView {
            LazyVGrid(columns: [GridItem(.adaptive(minimum: 116), spacing: 14)], spacing: 16) {
                ForEach(Array(pages.enumerated()), id: \.element.id) { position, page in
                    PageCell(
                        image: thumbnails[index][page.sourceIndex],
                        number: position + 1,
                        rotation: page.rotation,
                        isSelected: selection.contains(page.id),
                        isDragged: dragged == page.id
                    )
                    .contentShape(Rectangle())
                    .onTapGesture { select(page) }
                    .onDrag {
                        dragged = page.id
                        return NSItemProvider(object: page.id.uuidString as NSString)
                    }
                    .onDrop(of: [.text], delegate: PageReorderDelegate(target: page.id, pages: pagesBinding, dragged: $dragged))
                }
            }
            .padding(14)
            .animation(.snappy, value: pages)
        }
        // Each PDF starts at its first page, without animating from the last one.
        .id(index)
        .frame(maxHeight: .infinity)
        .background(RoundedRectangle(cornerRadius: 14, style: .continuous).fill(.primary.opacity(0.04)))
        .overlay {
            if let loadError = loadErrors[index] {
                Label(loadError, systemImage: "exclamationmark.triangle")
                    .font(.system(size: 12))
                    .foregroundStyle(.secondary)
            } else if pages.isEmpty {
                ProgressView().controlSize(.small)
            }
        }
        .onTapGesture { selection = [] }
    }

    private var toolbar: some View {
        HStack(spacing: 8) {
            Text(summary)
                .font(.system(size: 11))
                .foregroundStyle(.secondary)
            Spacer()
            Button { rotateSelection(by: -90) } label: {
                Label("Rotate Left", systemImage: "rotate.left")
            }
            .help("Rotate selected pages left")
            Button { rotateSelection(by: 90) } label: {
                Label("Rotate Right", systemImage: "rotate.right")
            }
            .help("Rotate selected pages right")
            Button(role: .destructive, action: deleteSelection) {
                Label("Delete", systemImage: "trash")
            }
            .help("Delete selected pages")
            .keyboardShortcut(.delete, modifiers: [])
        }
        .labelStyle(.iconOnly)
        .controlSize(.large)
        .disabled(selection.isEmpty || isWorking)
    }

    private var summary: String {
        let count = pages.count == 1 ? "1 page" : "\(pages.count) pages"
        return selection.isEmpty ? "\(count) · Drag to reorder, click to select" : "\(count) · \(selection.count) selected"
    }

    private func select(_ page: PDFPageEdit) {
        if NSEvent.modifierFlags.contains(.command) || NSEvent.modifierFlags.contains(.shift) {
            if selection.remove(page.id) == nil { selection.insert(page.id) }
        } else {
            selection = [page.id]
        }
    }

    private func rotateSelection(by degrees: Int) {
        withAnimation(.snappy) {
            for index in pages.indices where selection.contains(pages[index].id) {
                pages[index].rotation = ((pages[index].rotation + degrees) % 360 + 360) % 360
            }
        }
    }

    private func deleteSelection() {
        withAnimation(.snappy) { pages.removeAll { selection.contains($0.id) } }
        selection = []
    }

    /// Reads the PDF at `index` the first time it's shown.
    private func load(_ index: Int) async {
        guard originals[index] == nil, loadErrors[index] == nil else { return }
        let url = urls[index]
        let result = await Task.detached(priority: .userInitiated) { () -> Result<[Int: ThumbnailImage], PDFToolError> in
            guard let document = PDFDocument(url: url) else { return .failure(.unreadable(url)) }
            guard !document.isLocked else { return .failure(.locked(url)) }
            var images: [Int: ThumbnailImage] = [:]
            for index in 0..<document.pageCount {
                guard let page = document.page(at: index) else { continue }
                var rect = CGRect(x: 0, y: 0, width: 220, height: 220)
                if let image = page.thumbnail(of: rect.size, for: .cropBox).cgImage(forProposedRect: &rect, context: nil, hints: nil) {
                    images[index] = ThumbnailImage(image: image)
                }
            }
            return .success(images)
        }.value

        // Shown again while loading, it may have been read already.
        guard originals[index] == nil else { return }
        switch result {
        case .success(let images):
            let pages = images.keys.sorted().map { PDFPageEdit(sourceIndex: $0) }
            thumbnails[index] = images.mapValues(\.image)
            originals[index] = pages
            edits[index] = pages
        case .failure(let error):
            loadErrors[index] = error.localizedDescription
        }
    }

    private func save() {
        isWorking = true
        let isBatch = isBatch
        // PDFs never shown, or put back as they were, are left alone.
        let jobs = urls.indices.map { (url: urls[$0], pages: edited[$0] ? edits[$0] : nil) }
        Task {
            let result = await Task.detached(priority: .userInitiated) {
                Result {
                    isBatch
                        ? try PDFTools.saveEditedPages(jobs)
                        : SaveOutcome(saved: [try PDFTools.saveEditedPages(of: jobs[0].url, pages: jobs[0].pages ?? [])])
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

/// Carries a rendered thumbnail out of a detached task.
private struct ThumbnailImage: @unchecked Sendable {
    let image: CGImage
}

private struct PageCell: View {
    let image: CGImage?
    let number: Int
    let rotation: Int
    let isSelected: Bool
    let isDragged: Bool

    var body: some View {
        VStack(spacing: 6) {
            ZStack {
                if let image {
                    Image(decorative: image, scale: 1)
                        .resizable()
                        .scaledToFit()
                        .background(Color.white)
                        .shadow(color: .black.opacity(0.15), radius: 2, y: 1)
                        .rotationEffect(.degrees(Double(rotation)))
                } else {
                    RoundedRectangle(cornerRadius: 4).fill(.primary.opacity(0.08))
                }
            }
            // A square box keeps rotated pages inside the cell.
            .frame(width: 100, height: 100)
            .padding(6)
            .background(
                RoundedRectangle(cornerRadius: 12, style: .continuous)
                    .fill(isSelected ? Color.accentColor.opacity(0.18) : .clear)
            )
            .overlay(
                RoundedRectangle(cornerRadius: 12, style: .continuous)
                    .strokeBorder(isSelected ? Color.accentColor : .clear, lineWidth: 2)
            )

            Text("\(number)")
                .font(.system(size: 11, weight: .medium).monospacedDigit())
                .foregroundStyle(isSelected ? Color.white : Color.secondary)
                .padding(.horizontal, 7)
                .padding(.vertical, 1)
                .background(Capsule().fill(isSelected ? Color.accentColor : .clear))
        }
        .opacity(isDragged ? 0.4 : 1)
        .accessibilityElement(children: .ignore)
        .accessibilityLabel("Page \(number)\(rotation == 0 ? "" : ", rotated \(rotation) degrees")")
        .accessibilityAddTraits(isSelected ? .isSelected : [])
    }
}

/// Moves the dragged page to the hovered page's position as the drag passes over it.
private struct PageReorderDelegate: DropDelegate {
    let target: PDFPageEdit.ID
    @Binding var pages: [PDFPageEdit]
    @Binding var dragged: PDFPageEdit.ID?

    func dropEntered(info: DropInfo) {
        guard let dragged, dragged != target,
              let from = pages.firstIndex(where: { $0.id == dragged }),
              let to = pages.firstIndex(where: { $0.id == target })
        else { return }
        withAnimation(.snappy) {
            pages.move(fromOffsets: IndexSet(integer: from), toOffset: to > from ? to + 1 : to)
        }
    }

    func dropUpdated(info: DropInfo) -> DropProposal? {
        DropProposal(operation: .move)
    }

    func performDrop(info: DropInfo) -> Bool {
        dragged = nil
        return true
    }
}
