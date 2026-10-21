import AppKit
import Testing
@testable import OpenShelf

struct RubberBandTests {
    let frames: [Int: CGRect] = [
        1: CGRect(x: 0, y: 0, width: 80, height: 90),
        2: CGRect(x: 90, y: 0, width: 80, height: 90),
        3: CGRect(x: 0, y: 100, width: 80, height: 90),
    ]

    @Test func rectWorksInAnyDragDirection() {
        #expect(RubberBand.rect(from: CGPoint(x: 50, y: 60), to: CGPoint(x: 10, y: 20)) == CGRect(x: 10, y: 20, width: 40, height: 40))
    }

    @Test func plainDragSelectsTouchedItems() {
        let rect = CGRect(x: 70, y: 10, width: 30, height: 10)  // spans items 1 and 2
        #expect(RubberBand.selection(itemFrames: frames, rect: rect, initial: [3], modifiers: []) == [1, 2])
    }

    @Test func shiftAddsToExistingSelection() {
        let rect = CGRect(x: 100, y: 10, width: 10, height: 10)
        #expect(RubberBand.selection(itemFrames: frames, rect: rect, initial: [3], modifiers: .shift) == [2, 3])
    }

    @Test func commandTogglesTouchedItems() {
        let rect = CGRect(x: 70, y: 10, width: 30, height: 10)
        #expect(RubberBand.selection(itemFrames: frames, rect: rect, initial: [1, 3], modifiers: .command) == [2, 3])
    }

    @Test func emptySpaceSelectsNothing() {
        let rect = CGRect(x: 200, y: 200, width: 20, height: 20)
        #expect(RubberBand.selection(itemFrames: frames, rect: rect, initial: [1], modifiers: []).isEmpty)
    }
}

struct TypeSelectTests {
    let names = ["Budget.xlsx", "budget notes.txt", "Café menu.pdf", "Invoice 2.pdf"]

    @Test func matchesPrefixIgnoringCaseAndAccents() {
        #expect(TypeSelect.match("bud", in: names) == 0)
        #expect(TypeSelect.match("cafe", in: names) == 2)
        #expect(TypeSelect.match("inv", in: names) == 3)
        #expect(TypeSelect.match("zz", in: names) == nil)
        #expect(TypeSelect.match("", in: names) == nil)
    }

    @Test func prefixBuildsUntilAPause() {
        var typeSelect = TypeSelect()
        #expect(typeSelect.append("i", at: 10) == "i")
        #expect(typeSelect.append("n", at: 10.4) == "in")
        #expect(typeSelect.append("c", at: 12) == "c")
    }

    @Test func onlyPrintableCharactersTypeSelect() {
        #expect(TypeSelect.isTypingCharacter("a"))
        #expect(TypeSelect.isTypingCharacter("7"))
        #expect(!TypeSelect.isTypingCharacter(" "))
        #expect(!TypeSelect.isTypingCharacter("\u{1B}"))
        #expect(!TypeSelect.isTypingCharacter(String(UnicodeScalar(NSUpArrowFunctionKey)!)))
    }

    @MainActor
    @Test func shelfSelectsAndScrollsToMatch() {
        let shelf = Shelf()
        shelf.add(["alpha.txt", "beta.txt", "gamma.txt"].map { URL(fileURLWithPath: "/tmp/openshelf-tests/\($0)") })
        #expect(shelf.typeSelect("g"))
        #expect(shelf.selectedItems.map(\.name) == ["gamma.txt"])
        #expect(shelf.scrollTarget == shelf.items[2].id)
        #expect(!shelf.typeSelect("q"))
    }
}

struct ShelfStoreTests {
    private func makeDirectory() throws -> URL {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("openshelf-store-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        return directory
    }

    @Test func snapshotsRoundTrip() throws {
        let directory = try makeDirectory()
        let file = directory.appendingPathComponent("shelves.json")
        let snapshot = ShelfSnapshot(
            items: [.init(path: "/tmp/a.txt", bookmark: Data([1, 2, 3]))],
            frame: CGRect(x: 10, y: 20, width: 284, height: 352),
            dockedEdge: .right,
            isExpanded: true,
            viewMode: .list
        )
        try ShelfStore.save([snapshot], to: file)
        #expect(ShelfStore.load(from: file) == [snapshot])
    }

    @Test func missingOrCorruptFileLoadsNothing() throws {
        let directory = try makeDirectory()
        #expect(ShelfStore.load(from: directory.appendingPathComponent("none.json")).isEmpty)
        let corrupt = directory.appendingPathComponent("bad.json")
        try Data("not json".utf8).write(to: corrupt)
        #expect(ShelfStore.load(from: corrupt).isEmpty)
    }

    @Test func bookmarkFindsMovedFile() throws {
        let directory = try makeDirectory()
        let original = directory.appendingPathComponent("report.txt")
        try Data("hi".utf8).write(to: original)
        let bookmark = try original.bookmarkData()
        let moved = directory.appendingPathComponent("report renamed.txt")
        try FileManager.default.moveItem(at: original, to: moved)

        let resolved = ShelfStore.resolve(.init(path: original.path, bookmark: bookmark))
        #expect(resolved?.lastPathComponent == "report renamed.txt")
    }

    @Test func fallsBackToPathAndSkipsMissingFiles() throws {
        let directory = try makeDirectory()
        let file = directory.appendingPathComponent("plain.txt")
        try Data("hi".utf8).write(to: file)
        #expect(ShelfStore.resolve(.init(path: file.path, bookmark: nil))?.path == file.path)
        #expect(ShelfStore.resolve(.init(path: directory.appendingPathComponent("gone.txt").path, bookmark: nil)) == nil)
    }
}

struct EdgeHidingTests {
    let screen = CGRect(x: 0, y: 0, width: 1440, height: 900)
    let shown = CGRect(x: 1156, y: 300, width: 284, height: 352)  // docked right

    @Test func hiddenFrameSitsJustPastTheEdge() {
        #expect(EdgeHiding.hiddenFrame(for: shown, edge: .right, in: screen).minX == 1440)
        let left = CGRect(x: 0, y: 300, width: 284, height: 352)
        #expect(EdgeHiding.hiddenFrame(for: left, edge: .left, in: screen).maxX == 0)
    }

    @Test func tabHugsTheEdgeCentredOnTheShelf() {
        let tab = EdgeHiding.tabFrame(for: shown, edge: .right, in: screen)
        #expect(tab.maxX == 1440)
        #expect(tab.width == EdgeHiding.tabSize.width)
        #expect(tab.midY == shown.midY)
        #expect(EdgeHiding.tabFrame(for: shown, edge: .left, in: screen).minX == 0)
    }

    @Test func tabStaysOnScreen() {
        let low = CGRect(x: 1156, y: -40, width: 284, height: 60)
        #expect(EdgeHiding.tabFrame(for: low, edge: .right, in: screen).minY == 0)
    }

    @Test func pointerNearIncludesAMargin() {
        #expect(EdgeHiding.isPointerNear(CGPoint(x: 1150, y: 400), shelf: shown))
        #expect(!EdgeHiding.isPointerNear(CGPoint(x: 1100, y: 400), shelf: shown))
    }
}
