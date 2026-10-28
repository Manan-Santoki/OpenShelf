import Foundation

/// Decides which shelf, if any, a conversion or tool's output should be added
/// to: the shelf the operation was started from, when the setting allows it
/// and that shelf is still open. Kept separate from `ShelfManager` so the
/// decision is a plain, testable function.
@MainActor
enum ShelfConversionTarget {
    /// - Parameters:
    ///   - sourceShelfID: The shelf the files being converted or edited came
    ///     from, or nil when they weren't dragged or selected from a shelf.
    ///   - isEnabled: Settings → Shelves' toggle for this feature.
    ///   - openShelves: Every shelf currently open.
    /// - Returns: The shelf to add results to, or nil to add nothing — which
    ///   is also what happens when the source shelf was closed or removed
    ///   while the operation ran; a closed shelf is never reopened for this.
    static func resolve(sourceShelfID: Shelf.ID?, isEnabled: Bool, openShelves: [Shelf]) -> Shelf? {
        guard isEnabled, let sourceShelfID else { return nil }
        return openShelves.first { $0.id == sourceShelfID }
    }
}
