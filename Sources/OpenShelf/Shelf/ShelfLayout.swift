import CoreGraphics

/// Window sizes for each shelf state.
enum ShelfLayout {
    static let headerHeight: CGFloat = 32
    static let cornerRadius: CGFloat = 22
    static let gridColumns = 3
    static let tileSize = CGSize(width: 84, height: 96)
    static let tileSpacing: CGFloat = 8
    static let rowHeight: CGFloat = 34
    static let contentPadding: CGFloat = 8
    static let maxHeight: CGFloat = 460

    static let emptySize = CGSize(width: 180, height: 132)
    static let collapsedSize = CGSize(width: 140, height: 160)

    static var expandedWidth: CGFloat {
        CGFloat(gridColumns) * tileSize.width + CGFloat(gridColumns - 1) * tileSpacing + contentPadding * 2
    }

    static func size(itemCount: Int, isExpanded: Bool, viewMode: ShelfViewMode) -> CGSize {
        guard itemCount > 0 else { return emptySize }
        guard isExpanded else { return collapsedSize }

        let contentHeight: CGFloat
        switch viewMode {
        case .grid:
            let rows = (itemCount + gridColumns - 1) / gridColumns
            contentHeight = CGFloat(rows) * tileSize.height + CGFloat(rows - 1) * tileSpacing
        case .list:
            contentHeight = CGFloat(itemCount) * rowHeight
        }
        let height = headerHeight + contentHeight + contentPadding * 2
        return CGSize(width: expandedWidth, height: min(height, maxHeight))
    }
}
