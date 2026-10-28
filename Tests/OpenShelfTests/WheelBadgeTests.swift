// Adapted from David NeSmith’s e543433; see docs/FORK_REVIEW.md.
import AppKit
import Carbon.HIToolbox
import Testing
@testable import OpenShelf

struct WheelBadgeTests {
    private func items(_ count: Int) -> [WheelItem] {
        Array(OutputFormat.allCases.prefix(count)).map(WheelItem.convert)
    }

    @Test func numbersFollowWheelOrder() {
        let slots = WheelPaging.slots(for: items(5), page: 0)
        #expect(WheelBadges.keys(for: slots) == [0: "1", 1: "2", 2: "3", 3: "4", 4: "5"])
        #expect(WheelBadges.slot(forKey: "3", in: slots) == 2)
        #expect(WheelBadges.slot(forKey: "6", in: slots) == nil)
    }

    @Test func pagesNumberTheirOwnWedgesAndMoreIsZero() {
        let first = WheelPaging.slots(for: items(8), page: 0)
        #expect(WheelBadges.keys(for: first) == [0: "1", 1: "2", 2: "3", 3: "4", 4: "5", 5: "6", 6: "0"])
        // Page 2: two items, four empty wedges with no number, then Back.
        let second = WheelPaging.slots(for: items(8), page: 1)
        #expect(WheelBadges.keys(for: second) == [0: "1", 1: "2", 6: "0"])
        #expect(second[WheelBadges.slot(forKey: "2", in: second)!].item == items(8)[7])
    }

    private func key(_ character: String, keyCode: Int, command: Bool = true) -> NSEvent {
        NSEvent.keyEvent(
            with: .keyDown, location: .zero, modifierFlags: command ? .command : [], timestamp: 0, windowNumber: 0, context: nil,
            characters: character, charactersIgnoringModifiers: character, isARepeat: false, keyCode: UInt16(keyCode)
        )!
    }

    private func flags(_ flags: NSEvent.ModifierFlags) -> NSEvent {
        NSEvent.keyEvent(
            with: .flagsChanged, location: .zero, modifierFlags: flags, timestamp: 0, windowNumber: 0, context: nil,
            characters: "", charactersIgnoringModifiers: "", isARepeat: false, keyCode: UInt16(kVK_Command)
        )!
    }

    @MainActor
    @Test func commandNumberRunsThatWedge() {
        let controller = WheelController()
        let all = items(8)
        controller.itemsProvider = { kind, _ in kind == .convert ? all : [] }
        var ran: [WheelItem] = []
        controller.onDrop = { item, _, _, _ in ran.append(item) }
        #expect(controller.presentForKeyboard(urls: [URL(fileURLWithPath: "/tmp/a.png")], at: NSPoint(x: 400, y: 400)))
        defer { controller.endKeyboardMode(hidingWheel: true) }

        #expect(controller.handleKey(flags(.command)))
        #expect(controller.model.showsBadges)
        #expect(controller.handleKey(flags([])))
        #expect(!controller.model.showsBadges)

        // ⌘0 turns to page 2, where ⌘2 is the eighth item.
        #expect(controller.handleKey(key("0", keyCode: kVK_ANSI_0)))
        #expect(controller.model.page == 1)
        #expect(controller.handleKey(key("2", keyCode: kVK_ANSI_2)))
        #expect(ran == [all[7]])
        // A number without ⌘, or ⌘ with a key that isn't a wedge, is left alone.
        #expect(!controller.handleKey(key("q", keyCode: kVK_ANSI_Q)))
    }
}

@MainActor
struct WheelBadgeIntegrationTests {
    private func key(_ text: String, flags: NSEvent.ModifierFlags) -> NSEvent {
        NSEvent.keyEvent(with: .keyDown, location: .zero, modifierFlags: flags, timestamp: 0, windowNumber: 0, context: nil, characters: text, charactersIgnoringModifiers: text, isARepeat: false, keyCode: UInt16(kVK_ANSI_1))!
    }

    @Test func optionCommandNumberKeepsConversionOptionsAndShelfURLs() {
        let controller = WheelController()
        let selected = [URL(fileURLWithPath: "/tmp/from-shelf.jpg")]
        controller.itemsProvider = { kind, _ in kind == .convert ? [.convert(.png)] : [] }
        var received: [URL] = []
        var modifiers: NSEvent.ModifierFlags = []
        controller.onDrop = { _, urls, flags, _ in received = urls; modifiers = flags }
        #expect(controller.presentForKeyboard(urls: selected, at: NSPoint(x: 400, y: 400)))
        defer { controller.endKeyboardMode(hidingWheel: true) }
        #expect(controller.handleKey(key("1", flags: [.command, .option])))
        #expect(received == selected)
        #expect(modifiers.contains(.option))
        #expect(!controller.model.showsBadges)
    }

    @Test func unrelatedCommandsAndPlainNumbersDoNotRunWedges() {
        let controller = WheelController()
        controller.itemsProvider = { kind, _ in kind == .convert ? [.convert(.png)] : [] }
        var runs = 0
        controller.onDrop = { _, _, _, _ in runs += 1 }
        #expect(controller.presentForKeyboard(urls: [URL(fileURLWithPath: "/tmp/a.jpg")], at: NSPoint(x: 400, y: 400)))
        defer { controller.endKeyboardMode(hidingWheel: true) }
        #expect(!controller.handleKey(key("1", flags: [])))
        #expect(!controller.handleKey(key("q", flags: .command)))
        #expect(!controller.handleKey(key("9", flags: .command)))
        #expect(runs == 0)
    }
}
