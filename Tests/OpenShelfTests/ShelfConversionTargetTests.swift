import AppKit
import Testing
@testable import OpenShelf

@MainActor
struct ShelfConversionTargetTests {
    @Test func returnsNilWhenTheSettingIsOff() {
        let shelf = Shelf()
        #expect(ShelfConversionTarget.resolve(sourceShelfID: shelf.id, isEnabled: false, openShelves: [shelf]) == nil)
    }

    @Test func returnsNilWhenTheFilesDidNotComeFromAShelf() {
        let shelf = Shelf()
        #expect(ShelfConversionTarget.resolve(sourceShelfID: nil, isEnabled: true, openShelves: [shelf]) == nil)
    }

    /// The shelf closed (or was removed) before the operation finished: it's
    /// no longer among the open ones, so results go nowhere, and the closed
    /// shelf is never reopened to receive them.
    @Test func returnsNilWhenTheSourceShelfIsNoLongerOpen() {
        let shelf = Shelf()
        #expect(ShelfConversionTarget.resolve(sourceShelfID: shelf.id, isEnabled: true, openShelves: []) == nil)
        #expect(ShelfConversionTarget.resolve(sourceShelfID: shelf.id, isEnabled: true, openShelves: [Shelf()]) == nil)
    }

    @Test func picksTheMatchingShelfAmongSeveralOpenOnes() {
        let a = Shelf(), b = Shelf(), c = Shelf()
        #expect(ShelfConversionTarget.resolve(sourceShelfID: b.id, isEnabled: true, openShelves: [a, b, c]) === b)
    }
}

/// `ShelfManager.addConversionResults` wires `ShelfConversionTarget.resolve`
/// to a real shelf's `add`, including persistence.
@MainActor
struct ShelfConversionResultsTests {
    private func makeManager() throws -> (manager: ShelfManager, folder: URL) {
        _ = NSApplication.shared
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("openshelf-conversion-results-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        return (ShelfManager(storeURL: folder.appendingPathComponent("shelves.json")), folder)
    }

    @Test func addsTheOutputsToTheShelfTheyCameFrom() throws {
        let (manager, folder) = try makeManager()
        defer { manager.closeAll(); manager.saveNow(); try? FileManager.default.removeItem(at: folder) }
        let original = folder.appendingPathComponent("a.jpg")
        try Data("a".utf8).write(to: original)
        let shelf = manager.newShelf(with: [original])

        let output = folder.appendingPathComponent("a.png")
        try Data("b".utf8).write(to: output)
        manager.addConversionResults([output], sourceShelfID: shelf.id, isEnabled: true)

        #expect(shelf.items.map(\.name) == ["a.jpg", "a.png"])
    }

    /// Several outputs from one batch all land on the shelf, in order, and
    /// one already on the shelf isn't duplicated.
    @Test func addsABatchWithoutDuplicatesInOrder() throws {
        let (manager, folder) = try makeManager()
        defer { manager.closeAll(); manager.saveNow(); try? FileManager.default.removeItem(at: folder) }
        let existing = folder.appendingPathComponent("b.png")
        try Data("b".utf8).write(to: existing)
        let shelf = manager.newShelf(with: [existing])

        let a = folder.appendingPathComponent("a.png")
        let b = folder.appendingPathComponent("b.png")
        let c = folder.appendingPathComponent("c.png")
        try Data("a".utf8).write(to: a)
        try Data("c".utf8).write(to: c)
        manager.addConversionResults([a, b, c], sourceShelfID: shelf.id, isEnabled: true)

        #expect(shelf.items.map(\.name) == ["b.png", "a.png", "c.png"])
    }

    @Test func doesNothingWhenTheSettingIsOff() throws {
        let (manager, folder) = try makeManager()
        defer { manager.closeAll(); manager.saveNow(); try? FileManager.default.removeItem(at: folder) }
        let original = folder.appendingPathComponent("a.jpg")
        try Data("a".utf8).write(to: original)
        let shelf = manager.newShelf(with: [original])

        let output = folder.appendingPathComponent("a.png")
        try Data("b".utf8).write(to: output)
        manager.addConversionResults([output], sourceShelfID: shelf.id, isEnabled: false)

        #expect(shelf.items.map(\.name) == ["a.jpg"])
    }

    /// The shelf closed while the conversion was running: nothing crashes,
    /// nothing is added, and the shelf doesn't come back.
    @Test func doesNothingWhenTheSourceShelfHasClosed() throws {
        let (manager, folder) = try makeManager()
        defer { manager.closeAll(); manager.saveNow(); try? FileManager.default.removeItem(at: folder) }
        let original = folder.appendingPathComponent("a.jpg")
        try Data("a".utf8).write(to: original)
        let shelf = manager.newShelf(with: [original])
        let shelfID = shelf.id
        manager.closeAll()
        #expect(!manager.hasShelves)

        let output = folder.appendingPathComponent("a.png")
        try Data("b".utf8).write(to: output)
        manager.addConversionResults([output], sourceShelfID: shelfID, isEnabled: true)

        #expect(shelf.items.map(\.name) == ["a.jpg"])
        #expect(!manager.hasShelves)
    }

    @Test func doesNothingWhenTheFilesDidNotComeFromAShelf() throws {
        let (manager, folder) = try makeManager()
        defer { manager.closeAll(); manager.saveNow(); try? FileManager.default.removeItem(at: folder) }
        let original = folder.appendingPathComponent("a.jpg")
        try Data("a".utf8).write(to: original)
        let shelf = manager.newShelf(with: [original])

        let output = folder.appendingPathComponent("a.png")
        try Data("b".utf8).write(to: output)
        manager.addConversionResults([output], sourceShelfID: nil, isEnabled: true)

        #expect(shelf.items.map(\.name) == ["a.jpg"])
    }
}

struct PreferencesConversionResultsTests {
    @MainActor
    @Test func defaultsOnAndPersists() throws {
        let suite = "OpenShelfTests.addsConversionResultsToShelf.\(UUID().uuidString)"
        let defaults = try #require(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        #expect(Preferences(defaults: defaults).addsConversionResultsToShelf)
        Preferences(defaults: defaults).addsConversionResultsToShelf = false
        #expect(!Preferences(defaults: defaults).addsConversionResultsToShelf)
    }
}
