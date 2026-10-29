import AppKit
import Testing
@testable import OpenShelf

@MainActor
struct ShelfTests {
    private func makeShelf(_ names: [String]) -> Shelf {
        let shelf = Shelf()
        shelf.add(names.map { URL(fileURLWithPath: "/tmp/openshelf-tests/\($0)") })
        return shelf
    }

    @Test func addingSkipsDuplicatesAndNonFileURLs() {
        let shelf = makeShelf(["a.txt", "b.txt"])
        shelf.add([URL(fileURLWithPath: "/tmp/openshelf-tests/a.txt"), URL(string: "https://example.com")!])
        #expect(shelf.items.map(\.name) == ["a.txt", "b.txt"])
        #expect(shelf.hasHadItems)
    }

    @Test func plainClickSelectsOnlyThatItem() {
        let shelf = makeShelf(["a", "b", "c"])
        shelf.select(shelf.items[0], modifiers: [])
        shelf.select(shelf.items[2], modifiers: [])
        #expect(shelf.selection == [shelf.items[2].id])
    }

    @Test func commandClickToggles() {
        let shelf = makeShelf(["a", "b", "c"])
        shelf.select(shelf.items[0], modifiers: [])
        shelf.select(shelf.items[2], modifiers: .command)
        #expect(shelf.selection == [shelf.items[0].id, shelf.items[2].id])
        shelf.select(shelf.items[0], modifiers: .command)
        #expect(shelf.selection == [shelf.items[2].id])
    }

    @Test func shiftClickSelectsRangeFromAnchor() {
        let shelf = makeShelf(["a", "b", "c", "d"])
        shelf.select(shelf.items[3], modifiers: [])
        shelf.select(shelf.items[1], modifiers: .shift)
        #expect(shelf.selection == Set(shelf.items[1...3].map(\.id)))
    }

    @Test func actionTargetsFollowSelection() {
        let shelf = makeShelf(["a", "b", "c"])
        shelf.select(shelf.items[0], modifiers: [])
        shelf.select(shelf.items[1], modifiers: .command)
        #expect(shelf.targets(for: shelf.items[1]).map(\.name) == ["a", "b"])
        #expect(shelf.targets(for: shelf.items[2]).map(\.name) == ["c"])
    }

    @Test func removingClearsSelection() {
        let shelf = makeShelf(["a", "b"])
        shelf.selectAll()
        shelf.remove([shelf.items[0].id])
        #expect(shelf.items.map(\.name) == ["b"])
        #expect(shelf.selection == [shelf.items[0].id])
    }

    @Test func layoutGrowsWithItems() {
        let one = ShelfLayout.size(itemCount: 1, isExpanded: true, viewMode: .grid)
        let seven = ShelfLayout.size(itemCount: 7, isExpanded: true, viewMode: .grid)
        #expect(seven.height > one.height)
        #expect(ShelfLayout.size(itemCount: 500, isExpanded: true, viewMode: .list).height == ShelfLayout.maxHeight)
        #expect(ShelfLayout.size(itemCount: 3, isExpanded: false, viewMode: .grid) == ShelfLayout.collapsedSize)
    }

    @Test func onlyShelvesTallerThanTheCapScroll() {
        #expect(!ShelfLayout.needsScrolling(itemCount: 5, viewMode: .grid))
        #expect(ShelfLayout.needsScrolling(itemCount: 500, viewMode: .list))
        // The last count that fits shows everything at its full height.
        let fitting = (1...100).last { !ShelfLayout.needsScrolling(itemCount: $0, viewMode: .list) }!
        #expect(ShelfLayout.size(itemCount: fitting, isExpanded: true, viewMode: .list).height <= ShelfLayout.maxHeight)
        #expect(ShelfLayout.needsScrolling(itemCount: fitting + 1, viewMode: .list))
    }
}

struct TriggerMatcherTests {
    let matcher = TriggerMatcher(triggers: [.actions: .control])

    @Test func exactMatchTriggers() {
        #expect(matcher.wheel(for: .control) == .actions)
        #expect(matcher.wheel(for: [.control, .capsLock]) == .actions)
    }

    @Test func extraOrMissingModifiersDoNotTrigger() {
        #expect(matcher.wheel(for: [.control, .shift]) == nil)
        #expect(matcher.wheel(for: []) == nil)
    }
}
