import SwiftUI

struct ShelfView: View {
    let shelf: Shelf
    let close: () -> Void
    @State private var isShown = false
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        content
            .scaleEffect(isShown || reduceMotion ? 1 : 0.86)
            .opacity(isShown ? 1 : 0)
            .onAppear {
                withAnimation(reduceMotion ? .easeOut(duration: 0.15) : .spring(response: 0.36, dampingFraction: 0.72)) {
                    isShown = true
                }
            }
    }

    private var content: some View {
        VStack(spacing: 0) {
            ShelfHeader(shelf: shelf, close: close)
            Group {
                if shelf.items.isEmpty {
                    EmptyShelfView()
                } else if shelf.isExpanded {
                    ExpandedShelfView(shelf: shelf)
                } else {
                    CollapsedShelfView(shelf: shelf)
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
        }
        .animation(.spring(response: 0.3, dampingFraction: 0.8), value: shelf.items.map(\.id))
        // Drops are taken by the window's ShelfDropView, which adds them at once.
        .background { ShelfBackground(dockedEdge: shelf.dockedEdge, isDropTargeted: shelf.isDropTargeted) }
    }
}

private struct ShelfHeader: View {
    let shelf: Shelf
    let close: () -> Void

    var body: some View {
        HStack(spacing: 4) {
            HeaderButton(systemImage: "xmark", help: "Close shelf (files are not touched)", action: close)
            Spacer(minLength: 0)
            Text(title)
                .font(.system(size: 11, weight: .semibold))
                .foregroundStyle(.secondary)
                .lineLimit(1)
            Spacer(minLength: 0)
            if shelf.isExpanded {
                HeaderButton(
                    systemImage: shelf.viewMode == .grid ? "list.bullet" : "square.grid.2x2",
                    help: shelf.viewMode == .grid ? "Show as list" : "Show as grid"
                ) { shelf.viewMode.toggle() }
            }
            if !shelf.items.isEmpty {
                HeaderButton(
                    systemImage: shelf.isExpanded ? "chevron.up" : "chevron.down",
                    help: shelf.isExpanded ? "Collapse" : "Show all files"
                ) { withAnimation(.snappy) { shelf.isExpanded.toggle() } }
            }
        }
        .padding(.horizontal, 8)
        .frame(height: ShelfLayout.headerHeight)
        // Drag the top bar to move the shelf (and dock it at a screen edge).
        .background(WindowDragArea())
    }

    private var title: String {
        switch shelf.items.count {
        case 0: "Shelf"
        case 1: "1 file"
        case let count: "\(count) files"
        }
    }
}

private struct HeaderButton: View {
    let systemImage: String
    let help: String
    let action: () -> Void
    @State private var isHovered = false

    var body: some View {
        Button(action: action) {
            Image(systemName: systemImage)
                .font(.system(size: 10, weight: .bold))
                .frame(width: 20, height: 20)
                .background(Circle().fill(.primary.opacity(isHovered ? 0.12 : 0)))
        }
        .buttonStyle(.plain)
        .foregroundStyle(.secondary)
        .help(help)
        .accessibilityLabel(help)
        .onHover { isHovered = $0 }
    }
}

private struct EmptyShelfView: View {
    var body: some View {
        VStack(spacing: 6) {
            Image(systemName: "tray.and.arrow.down")
                .font(.system(size: 22, weight: .light))
            Text("Drop files here")
                .font(.system(size: 11, weight: .medium))
        }
        .foregroundStyle(.secondary)
        .padding(.bottom, 8)
    }
}

/// The stacked preview shown when a shelf is collapsed. Dragging it drags every file.
private struct CollapsedShelfView: View {
    let shelf: Shelf
    @State private var isHovered = false

    var body: some View {
        StackPreview(items: Array(shelf.items.suffix(3)), totalCount: shelf.items.count, isSpread: isHovered)
            .frame(width: 100, height: 100)
            .onHover { hovering in
                withAnimation(.spring(response: 0.3, dampingFraction: 0.65)) { isHovered = hovering }
            }
            .overlay {
                DragSourceArea(
                    isSelected: { true },
                    dragItems: { shelf.items },
                    onSelect: { _ in },
                    onDoubleClick: { withAnimation(.snappy) { shelf.isExpanded = true } },
                    menu: { anchor in ShelfMenu.make(for: shelf.items, in: shelf, anchor: anchor) },
                    onDragEnded: { dragged, operation in shelf.didDragOut(dragged, operation: operation, removeDragged: Preferences.shared.removeAfterDragOut) }
                )
            }
            .padding(.bottom, 14)
            .accessibilityElement(children: .ignore)
            .accessibilityLabel("\(shelf.items.count) files. Drag to move them, double-click to show all.")
    }
}

private struct StackPreview: View {
    let items: [ShelfItem]
    let totalCount: Int
    /// Fans the stack out further, e.g. while hovered.
    var isSpread = false

    var body: some View {
        ZStack {
            ForEach(Array(items.enumerated()), id: \.element.id) { index, item in
                // The newest item sits on top, older ones fan out behind it.
                let depth = CGFloat(items.count - 1 - index)
                let spread: CGFloat = isSpread ? 1.8 : 1
                Thumbnail(item: item)
                    .frame(width: 76, height: 76)
                    .rotationEffect(.degrees(Double(depth * spread) * -7))
                    .offset(x: depth * spread * -5, y: depth * spread * -3)
                    .shadow(color: .black.opacity(0.2), radius: 3, y: 1)
                    .transition(.scale(scale: 0.6).combined(with: .opacity))
            }
        }
        .overlay(alignment: .bottomTrailing) {
            if totalCount > 1 {
                Text("\(totalCount)")
                    .font(.system(size: 11, weight: .bold).monospacedDigit())
                    .foregroundStyle(.white)
                    .padding(.horizontal, 6)
                    .frame(minWidth: 20, minHeight: 20)
                    .background(Capsule().fill(Color.accentColor))
                    .offset(x: 6, y: 4)
            }
        }
    }
}

private struct ExpandedShelfView: View {
    let shelf: Shelf
    /// Item frames in the content's coordinate space, for rubber-band hit testing.
    @State private var frames = ItemFrames()
    @State private var band: CGRect?
    @State private var selectionBeforeBand: Set<ShelfItem.ID>?

    private static let space = "shelfContent"

    var body: some View {
        GeometryReader { viewport in
            ScrollViewReader { proxy in
                ScrollView {
                    items
                        .padding(ShelfLayout.contentPadding)
                        // Fill the viewport so empty space below the items can start a rubber band.
                        .frame(maxWidth: .infinity, minHeight: viewport.size.height, alignment: .top)
                        .background { RubberBandArea(onChange: updateBand, onClick: { shelf.selection.removeAll() }) }
                        .overlay(alignment: .topLeading) { bandOverlay }
                        .coordinateSpace(.named(Self.space))
                }
                .onChange(of: shelf.scrollTarget) { _, target in
                    guard let target else { return }
                    withAnimation(.snappy) { proxy.scrollTo(target, anchor: .center) }
                    shelf.scrollTarget = nil
                }
            }
        }
    }

    @ViewBuilder
    private var items: some View {
        switch shelf.viewMode {
        case .grid:
            LazyVGrid(
                columns: Array(repeating: GridItem(.fixed(ShelfLayout.tileSize.width), spacing: ShelfLayout.tileSpacing), count: ShelfLayout.gridColumns),
                spacing: ShelfLayout.tileSpacing
            ) {
                ForEach(shelf.items) { item in
                    ItemTile(item: item, isSelected: shelf.selection.contains(item.id))
                        .overlay { ItemDragArea(shelf: shelf, item: item) }
                        .modifier(TracksFrame(id: item.id, frames: frames, space: Self.space))
                        .id(item.id)
                        .transition(.scale(scale: 0.6).combined(with: .opacity))
                }
            }
        case .list:
            LazyVStack(spacing: 0) {
                ForEach(shelf.items) { item in
                    ItemRow(item: item, isSelected: shelf.selection.contains(item.id))
                        .overlay { ItemDragArea(shelf: shelf, item: item) }
                        .modifier(TracksFrame(id: item.id, frames: frames, space: Self.space))
                        .id(item.id)
                        .transition(.move(edge: .top).combined(with: .opacity))
                }
            }
        }
    }

    @ViewBuilder
    private var bandOverlay: some View {
        if let band {
            RoundedRectangle(cornerRadius: 3, style: .continuous)
                .fill(Color.accentColor.opacity(0.15))
                .overlay(RoundedRectangle(cornerRadius: 3, style: .continuous).strokeBorder(Color.accentColor.opacity(0.8), lineWidth: 1))
                .frame(width: band.width, height: band.height)
                .offset(x: band.minX, y: band.minY)
                .allowsHitTesting(false)
        }
    }

    private func updateBand(_ rect: CGRect?, modifiers: NSEvent.ModifierFlags) {
        guard let rect else {
            band = nil
            selectionBeforeBand = nil
            return
        }
        let initial = selectionBeforeBand ?? shelf.selection
        selectionBeforeBand = initial
        band = rect
        let visible = frames.byID.filter { id, _ in shelf.items.contains { $0.id == id } }
        shelf.selection = RubberBand.selection(itemFrames: visible, rect: rect, initial: initial, modifiers: modifiers)
    }
}

/// Item frames kept outside SwiftUI state, so measuring doesn't trigger re-renders.
private final class ItemFrames {
    var byID: [ShelfItem.ID: CGRect] = [:]
}

private struct TracksFrame: ViewModifier {
    let id: ShelfItem.ID
    let frames: ItemFrames
    let space: String

    func body(content: Content) -> some View {
        content.onGeometryChange(for: CGRect.self) { proxy in
            proxy.frame(in: .named(space))
        } action: { frame in
            frames.byID[id] = frame
        }
    }
}

private struct ItemTile: View {
    let item: ShelfItem
    let isSelected: Bool
    @State private var isHovered = false

    var body: some View {
        VStack(spacing: 4) {
            Thumbnail(item: item)
                .frame(width: 56, height: 56)
                .padding(4)
                .background(
                    RoundedRectangle(cornerRadius: 12, style: .continuous)
                        .fill(Color.primary.opacity(isSelected ? 0.14 : isHovered ? 0.07 : 0))
                )
            Text(item.name)
                .font(.system(size: 10))
                .lineLimit(2)
                .truncationMode(.middle)
                .multilineTextAlignment(.center)
                .padding(.horizontal, 5)
                .padding(.vertical, 1)
                .foregroundStyle(isSelected ? Color.white : Color.primary)
                .background(Capsule().fill(isSelected ? Color.accentColor : .clear))
        }
        .frame(width: ShelfLayout.tileSize.width, height: ShelfLayout.tileSize.height, alignment: .top)
        .onHover { hovering in
            withAnimation(.spring(response: 0.25, dampingFraction: 0.7)) { isHovered = hovering }
        }
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(item.name)
        .accessibilityAddTraits(isSelected ? .isSelected : [])
    }
}

private struct ItemRow: View {
    let item: ShelfItem
    let isSelected: Bool

    var body: some View {
        HStack(spacing: 8) {
            Thumbnail(item: item)
                .frame(width: 24, height: 24)
            Text(item.name)
                .font(.system(size: 12))
                .lineLimit(1)
                .truncationMode(.middle)
            Spacer(minLength: 0)
        }
        .padding(.horizontal, 8)
        .frame(height: ShelfLayout.rowHeight)
        .foregroundStyle(isSelected ? Color.white : Color.primary)
        .background(RoundedRectangle(cornerRadius: 10, style: .continuous).fill(isSelected ? Color.accentColor : .clear))
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(item.name)
        .accessibilityAddTraits(isSelected ? .isSelected : [])
    }
}

/// Click, drag and context-menu handling for one item in an expanded shelf.
private struct ItemDragArea: View {
    let shelf: Shelf
    let item: ShelfItem

    var body: some View {
        DragSourceArea(
            isSelected: { shelf.selection.contains(item.id) },
            dragItems: { shelf.targets(for: item) },
            onSelect: { modifiers in shelf.select(item, modifiers: modifiers) },
            onDoubleClick: { FileActions.open(shelf.targets(for: item).map(\.url)) },
            menu: { anchor in ShelfMenu.make(for: shelf.targets(for: item), in: shelf, anchor: anchor) },
            onDragEnded: { dragged, operation in shelf.didDragOut(dragged, operation: operation, removeDragged: Preferences.shared.removeAfterDragOut) }
        )
    }
}

private struct Thumbnail: View {
    let item: ShelfItem
    @Environment(\.displayScale) private var displayScale

    var body: some View {
        Image(nsImage: item.thumbnail)
            .resizable()
            .interpolation(.high)
            .scaledToFit()
            .clipShape(RoundedRectangle(cornerRadius: 6, style: .continuous))
            .onAppear { item.loadThumbnail(side: 128, scale: displayScale) }
    }
}

private struct ShelfBackground: View {
    let dockedEdge: ShelfEdge?
    let isDropTargeted: Bool

    var body: some View {
        let shape = UnevenRoundedRectangle(cornerRadii: cornerRadii, style: .continuous)
        shape
            .fill(.regularMaterial)
            .overlay { shape.fill(Color.accentColor.opacity(isDropTargeted ? 0.15 : 0)) }
            .overlay {
                shape.strokeBorder(
                    isDropTargeted ? Color.accentColor : Color.primary.opacity(0.12),
                    lineWidth: isDropTargeted ? 2 : 1
                )
            }
            .animation(.easeOut(duration: 0.15), value: isDropTargeted)
    }

    /// Square off the corners that touch the screen edge when docked.
    private var cornerRadii: RectangleCornerRadii {
        let radius = ShelfLayout.cornerRadius
        return switch dockedEdge {
        case .left: RectangleCornerRadii(topLeading: 0, bottomLeading: 0, bottomTrailing: radius, topTrailing: radius)
        case .right: RectangleCornerRadii(topLeading: radius, bottomLeading: radius, bottomTrailing: 0, topTrailing: 0)
        case nil: RectangleCornerRadii(topLeading: radius, bottomLeading: radius, bottomTrailing: radius, topTrailing: radius)
        }
    }
}
