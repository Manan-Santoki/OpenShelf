import AppKit
import Testing
@testable import OpenShelf

/// Adapted from David NeSmith's a2c6d6f, with selection-anchor coverage.
@MainActor
struct ShelfRelocationTests {
    @Test func followsSuccessfulMovesAndKeepsFailedOnes() throws {
        let fm = FileManager.default
        let folder = fm.temporaryDirectory.appendingPathComponent("openshelf-relocate-\(UUID().uuidString)")
        defer { try? fm.removeItem(at: folder) }
        let source = folder.appendingPathComponent("source")
        let target = folder.appendingPathComponent("target")
        try fm.createDirectory(at: source, withIntermediateDirectories: true)
        try fm.createDirectory(at: target, withIntermediateDirectories: true)
        let moved = source.appendingPathComponent("Move.txt")
        let blocked = source.appendingPathComponent("Existing.txt")
        try Data("moved".utf8).write(to: moved)
        try Data("original".utf8).write(to: blocked)
        try Data("keep".utf8).write(to: target.appendingPathComponent("Existing.txt"))
        let shelf = Shelf()
        shelf.add([moved, blocked])
        shelf.selectAll()
        let second = Shelf()
        second.add([moved])
        let result = FileActions.move([moved, blocked], into: target)
        #expect(result.moves == [moved.standardizedFileURL: target.appendingPathComponent("Move.txt").standardizedFileURL])
        #expect(result.failures.count == 1)
        shelf.relocate(result.moves)
        second.relocate(result.moves)
        #expect(shelf.items.allSatisfy { $0.exists })
        #expect(shelf.selectedItems.count == 2)
        #expect(shelf.items[0].url == second.items[0].url)
        #expect(shelf.items[1].url == blocked.standardizedFileURL)
        #expect(try String(contentsOf: target.appendingPathComponent("Existing.txt"), encoding: .utf8) == "keep")
    }

    @Test func selectionAnchorAndScrollTargetFollowTheMovedItem() throws {
        let shelf = Shelf()
        let a = URL(fileURLWithPath: "/tmp/openshelf-anchor-a.txt")
        let b = URL(fileURLWithPath: "/tmp/openshelf-anchor-b.txt")
        let c = URL(fileURLWithPath: "/tmp/openshelf-anchor-c.txt")
        shelf.add([a, b, c])
        shelf.select(shelf.items[0], modifiers: [])
        shelf.scrollTarget = shelf.items[0].id
        shelf.relocate([a: URL(fileURLWithPath: "/tmp/openshelf-moved-a.txt")])
        #expect(shelf.scrollTarget == shelf.items[0].id)
        shelf.select(shelf.items[2], modifiers: .shift)
        #expect(shelf.selectedItems.count == 3)
        #expect(shelf.items.map(\.name) == ["openshelf-moved-a.txt", "openshelf-anchor-b.txt", "openshelf-anchor-c.txt"])
    }
}
