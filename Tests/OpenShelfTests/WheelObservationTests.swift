import AppKit
import Observation
import Testing
@testable import OpenShelf

/// `@Observable`'s generated setters notify observers on every assignment,
/// even one that writes the value already there, so the wheel's per-move
/// hover handling (`setHighlight`, `updateDwell`, `cancelDwell`) has to guard
/// against reassigning the same value itself; this checks that guard holds.
@MainActor
struct WheelObservationTests {
    private func items(_ count: Int) -> [WheelItem] {
        Array(OutputFormat.allCases.prefix(count)).map(WheelItem.convert)
    }

    @Test func reassigningTheSameHighlightDoesNotNotifyObservers() {
        let controller = WheelController()
        let only = items(1)
        controller.itemsProvider = { kind, _ in kind == .convert ? only : [] }
        #expect(controller.presentForKeyboard(urls: [URL(fileURLWithPath: "/tmp/a.png")], at: NSPoint(x: 400, y: 400)))
        defer { controller.endKeyboardMode(hidingWheel: true) }
        #expect(controller.model.highlightedSlot == 0)

        nonisolated(unsafe) var notified = false
        withObservationTracking({ _ = controller.model.highlightedSlot }, onChange: { notified = true })
        // A one-wedge wheel's only wedge wraps back to itself: the highlight
        // doesn't actually move, so observers must not be notified.
        controller.step(.clockwise)
        #expect(!notified)
        #expect(controller.model.highlightedSlot == 0)
    }

    @Test func movingToADifferentWedgeStillNotifiesObservers() {
        let controller = WheelController()
        let several = items(3)
        controller.itemsProvider = { kind, _ in kind == .convert ? several : [] }
        #expect(controller.presentForKeyboard(urls: [URL(fileURLWithPath: "/tmp/a.png")], at: NSPoint(x: 400, y: 400)))
        defer { controller.endKeyboardMode(hidingWheel: true) }
        #expect(controller.model.highlightedSlot == 0)

        nonisolated(unsafe) var notified = false
        withObservationTracking({ _ = controller.model.highlightedSlot }, onChange: { notified = true })
        controller.step(.clockwise)
        #expect(notified)
        #expect(controller.model.highlightedSlot == 1)
    }
}
