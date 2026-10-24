// Adapted from David NeSmith’s a2c6d6f; see docs/FORK_REVIEW.md.
import AppKit
import Testing
@testable import OpenShelf

struct ShelfWheelSelectionTests {
    @Test func shortcutPicksTheSelectionInFront() {
        let a = [URL(fileURLWithPath: "/tmp/a.txt")], b = [URL(fileURLWithPath: "/tmp/b.txt")]
        let cases: [([URL]?, [URL], Bool, WheelSelectionSource)] = [
            (a, b, false, .shelf(a)), (a, b, true, .shelf(a)),
            ([], b, false, .finder), (nil, b, false, .finder),
            (nil, b, true, .shelf(b)), (nil, [], true, .finder),
        ]
        for (key, recent, frontmost, expected) in cases {
            #expect(WheelSelectionSource.choose(keyShelfSelection: key, recentShelfSelection: recent, isOpenShelfFrontmost: frontmost) == expected)
        }
    }

    @MainActor @Test func managerReportsFocusedAndRecentSelections() throws {
        _ = NSApplication.shared
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("openshelf-wheel-selection-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let manager = ShelfManager(storeURL: folder.appendingPathComponent("shelves.json"))
        defer {
            manager.closeAll()
            manager.saveNow()
            try? FileManager.default.removeItem(at: folder)
        }
        let a = folder.appendingPathComponent("a.txt")
        let b = folder.appendingPathComponent("b.txt")
        try Data("a".utf8).write(to: a)
        try Data("b".utf8).write(to: b)
        let first = manager.newShelf(with: [a])
        let second = manager.newShelf(with: [b])
        first.selectAll()
        second.selectAll()
        #expect(manager.recentShelfSelection == [b])
        manager.show(first)
        #expect(manager.keyShelfSelection == [a])
        #expect(manager.recentShelfSelection == [a])
        first.selection = []
        #expect(manager.keyShelfSelection == [])
    }
}
