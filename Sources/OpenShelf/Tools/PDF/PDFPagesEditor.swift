import PDFKit
import SwiftUI
import UniformTypeIdentifiers

/// The "Pages" tool: reorder, rotate and delete pages of one PDF.
struct PDFPagesEditor: View {
    static let windowSize = CGSize(width: 620, height: 640)

    let url: URL
    let actions: EditorActions

    @State private var pages: [PDFPageEdit] = []
    @State private var original: [PDFPageEdit] = []
    @State private var thumbnails: [Int: CGImage] = [:]
    @State private var selection: Set<PDFPageEdit.ID> = []
    @State private var dragged: PDFPageEdit.ID?
    @State private var loadError: String?
    @State private var isWorking = false

    init(url: URL, actions: EditorActions) {
        self.url = url
        self.actions = actions
    }

    var body: some View {
        EditorChrome(
            title: "Pages",
            primaryTitle: isWorking ? "Saving…" : "Save",
            isPrimaryEnabled: !isWorking && !pages.isEmpty && pages != original,
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
            }
        }
        .task { await load() }
    }

    private var grid: some View {
        ScrollView {
            LazyVGrid(columns: [GridItem(.adaptive(minimum: 116), spacing: 14)], spacing: 16) {
                ForEach(Array(pages.enumerated()), id: \.element.id) { position, page in
                    PageCell(
                        image: thumbnails[page.sourceIndex],
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
                    .onDrop(of: [.text], delegate: PageReorderDelegate(target: page.id, pages: $pages, dragged: $dragged))
                }
            }
            .padding(14)
            .animation(.snappy, value: pages)
        }
        .frame(maxHeight: .infinity)
        .background(RoundedRectangle(cornerRadius: 14, style: .continuous).fill(.primary.opacity(0.04)))
        .overlay {
            if let loadError {
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

    private func load() async {
        let url = url
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

        switch result {
        case .success(let images):
            let edits = images.keys.sorted().map { PDFPageEdit(sourceIndex: $0) }
            thumbnails = images.mapValues(\.image)
            original = edits
            pages = edits
        case .failure(let error):
            loadError = error.localizedDescription
        }
    }

    private func save() {
        isWorking = true
        let url = url
        let pages = pages
        Task {
            let result = await Task.detached(priority: .userInitiated) {
                Result { try PDFTools.saveEditedPages(of: url, pages: pages) }
            }.value
            isWorking = false
            switch result {
            case .success(let saved): actions.saved([saved])
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
