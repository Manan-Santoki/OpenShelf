import AppKit
import Carbon.HIToolbox
import Testing
@testable import OpenShelf

/// Whether a conversion or tool's results come back to a shelf depends on
/// the shelf ID `WheelController.onDrop` is handed; these cover how that ID
/// is threaded through both a real drag and a keyboard-driven session.
@MainActor
struct WheelShelfOriginTests {
    private func items(_ count: Int) -> [WheelItem] {
        Array(OutputFormat.allCases.prefix(count)).map(WheelItem.convert)
    }

    private func returnKeyEvent() -> NSEvent {
        NSEvent.keyEvent(
            with: .keyDown, location: .zero, modifierFlags: [], timestamp: 0, windowNumber: 0, context: nil,
            characters: "\r", charactersIgnoringModifiers: "\r", isARepeat: false, keyCode: UInt16(kVK_Return)
        )!
    }

    @Test func dragDropReportsTheShelfTheDragStartedFrom() {
        let shelf = Shelf()
        Shelf.activeDragSource = shelf
        defer { Shelf.activeDragSource = nil }

        let controller = WheelController()
        controller.model.show(items(3))
        controller.model.isPresented = true
        controller.model.highlightedSlot = 0
        var receivedShelfID: Shelf.ID?
        controller.onDrop = { _, _, _, sourceShelfID in receivedShelfID = sourceShelfID }

        #expect(controller.confirmHighlighted(with: [URL(fileURLWithPath: "/tmp/a.png")]))
        #expect(receivedShelfID == shelf.id)
    }

    /// A drag that didn't start on a shelf (Finder, another app) must behave
    /// exactly as before: no shelf ID at all.
    @Test func dragDropHasNoShelfWhenNotDraggedFromOne() {
        Shelf.activeDragSource = nil
        let controller = WheelController()
        controller.model.show(items(3))
        controller.model.isPresented = true
        controller.model.highlightedSlot = 0
        var ran = false
        var receivedShelfID: Shelf.ID?
        controller.onDrop = { _, _, _, sourceShelfID in
            ran = true
            receivedShelfID = sourceShelfID
        }

        #expect(controller.confirmHighlighted(with: [URL(fileURLWithPath: "/tmp/a.png")]))
        #expect(ran)
        #expect(receivedShelfID == nil)
    }

    @Test func keyboardSessionCarriesItsSourceShelfToOnDrop() {
        let shelf = Shelf()
        let controller = WheelController()
        controller.itemsProvider = { kind, _ in kind == .convert ? self.items(1) : [] }
        var receivedShelfID: Shelf.ID?
        controller.onDrop = { _, _, _, sourceShelfID in receivedShelfID = sourceShelfID }

        #expect(controller.presentForKeyboard(urls: [URL(fileURLWithPath: "/tmp/a.png")], at: NSPoint(x: 400, y: 400), sourceShelfID: shelf.id))
        defer { controller.endKeyboardMode(hidingWheel: true) }

        #expect(controller.handleKey(returnKeyEvent()))
        #expect(receivedShelfID == shelf.id)
    }

    /// Opening a keyboard wheel for Finder's selection (no shelf involved)
    /// passes no shelf ID through, same as before this feature existed.
    @Test func keyboardSessionWithoutASourceShelfPassesNone() {
        let controller = WheelController()
        controller.itemsProvider = { kind, _ in kind == .convert ? self.items(1) : [] }
        var ran = false
        var receivedShelfID: Shelf.ID?
        controller.onDrop = { _, _, _, sourceShelfID in
            ran = true
            receivedShelfID = sourceShelfID
        }

        #expect(controller.presentForKeyboard(urls: [URL(fileURLWithPath: "/tmp/a.png")], at: NSPoint(x: 400, y: 400)))
        defer { controller.endKeyboardMode(hidingWheel: true) }

        #expect(controller.handleKey(returnKeyEvent()))
        #expect(ran)
        #expect(receivedShelfID == nil)
    }
}
