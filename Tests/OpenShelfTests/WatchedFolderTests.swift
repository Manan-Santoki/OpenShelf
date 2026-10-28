import AppKit
import Foundation
import Testing
@testable import OpenShelf

struct ArrivalFilterTests {
    @Test func ignoresHiddenAndTemporaryFiles() {
        for name in [
            ".DS_Store", ".Screenshot 2026-10-28 at 09.41.12.png", ".localized",
            "Report.pdf.download", "movie.mp4.crdownload", "archive.zip.part", "data.partial",
            "setup.dmg.opdownload", "notes.tmp", "Shot.screenshot", "~$Budget.xlsx", "Icon\r",
        ] {
            #expect(ArrivalFilter.isIgnored(name), "\(name)")
        }
    }

    @Test func keepsOrdinaryFiles() {
        for name in ["Screenshot 2026-10-28 at 09.41.12.png", "Report.pdf", "movie.MP4", "partial notes.txt", "Download list.md"] {
            #expect(!ArrivalFilter.isIgnored(name), "\(name)")
        }
    }
}

struct ArrivalTrackerTests {
    private typealias Entry = ArrivalTracker.Entry
    private let start = Date(timeIntervalSinceReferenceDate: 800_000_000)

    private func file(_ name: String, _ number: UInt64, size: Int64 = 100, busy: Bool = false) -> Entry {
        Entry(name: name, fileNumber: number, size: size, isRegularFile: true, isBusy: busy)
    }

    private func at(_ seconds: TimeInterval) -> Date {
        start.addingTimeInterval(seconds)
    }

    @Test func neverReportsFilesThatWereAlreadyThere() {
        var tracker = ArrivalTracker(existing: [file("old.pdf", 1)])
        #expect(tracker.observe([file("old.pdf", 1)], now: at(0)).isEmpty)
        #expect(tracker.observe([file("old.pdf", 1)], now: at(10)).isEmpty)
        #expect(!tracker.hasPending)
    }

    @Test func reportsANewFileOnceItsSizeHasSettled() {
        var tracker = ArrivalTracker(existing: [])
        #expect(tracker.observe([file("new.png", 2)], now: at(0)).isEmpty)
        #expect(tracker.hasPending)
        #expect(tracker.observe([file("new.png", 2)], now: at(0.5)).isEmpty)
        #expect(tracker.observe([file("new.png", 2)], now: at(1)) == ["new.png"])
        #expect(!tracker.hasPending)
        // Only once.
        #expect(tracker.observe([file("new.png", 2)], now: at(5)).isEmpty)
    }

    @Test func waitsWhileAFileIsStillGrowing() {
        var tracker = ArrivalTracker(existing: [])
        _ = tracker.observe([file("big.mov", 3, size: 10)], now: at(0))
        #expect(tracker.observe([file("big.mov", 3, size: 500)], now: at(1)).isEmpty)
        #expect(tracker.observe([file("big.mov", 3, size: 900)], now: at(2)).isEmpty)
        #expect(tracker.observe([file("big.mov", 3, size: 900)], now: at(2.5)).isEmpty)
        #expect(tracker.observe([file("big.mov", 3, size: 900)], now: at(3)) == ["big.mov"])
    }

    @Test func waitsLongerForAnEmptyFile() {
        var tracker = ArrivalTracker(existing: [])
        _ = tracker.observe([file("empty.txt", 4, size: 0)], now: at(0))
        #expect(tracker.observe([file("empty.txt", 4, size: 0)], now: at(2)).isEmpty)
        #expect(tracker.observe([file("empty.txt", 4, size: 0)], now: at(ArrivalTracker.emptySettleInterval)) == ["empty.txt"])
    }

    @Test func waitsWhileFinderIsStillCopying() {
        var tracker = ArrivalTracker(existing: [])
        _ = tracker.observe([file("copy.zip", 5, busy: true)], now: at(0))
        #expect(tracker.observe([file("copy.zip", 5, busy: true)], now: at(5)).isEmpty)
        #expect(tracker.observe([file("copy.zip", 5)], now: at(6)).isEmpty)
        #expect(tracker.observe([file("copy.zip", 5)], now: at(7)) == ["copy.zip"])
    }

    @Test func aRenameInsideTheFolderIsNotNew() {
        var tracker = ArrivalTracker(existing: [file("draft.txt", 6)])
        #expect(tracker.observe([file("final.txt", 6)], now: at(0)).isEmpty)
        #expect(tracker.observe([file("final.txt", 6)], now: at(5)).isEmpty)
        #expect(!tracker.hasPending)
    }

    /// Apps often save by writing a new file and swapping it in under the
    /// same name, which gives it a new file number.
    @Test func aFileSavedOverInPlaceIsNotNew() {
        var tracker = ArrivalTracker(existing: [file("notes.md", 7)])
        #expect(tracker.observe([file("notes.md", 70)], now: at(0)).isEmpty)
        #expect(tracker.observe([file("notes.md", 70)], now: at(5)).isEmpty)
    }

    /// Screenshot writes ".Screenshot …png" and then renames it; browsers
    /// rename "….crdownload" when the download finishes. Those names were
    /// never counted, so the finished file is new.
    @Test func aFileRenamedFromATemporaryNameIsNew() {
        var tracker = ArrivalTracker(existing: [])
        _ = tracker.observe([file(".Screenshot 1.png", 8), file("movie.mp4.crdownload", 9, size: 50)], now: at(0))
        #expect(!tracker.hasPending)
        _ = tracker.observe([file("Screenshot 1.png", 8), file("movie.mp4", 9, size: 50)], now: at(1))
        #expect(tracker.observe([file("Screenshot 1.png", 8), file("movie.mp4", 9, size: 50)], now: at(2)) == ["movie.mp4", "Screenshot 1.png"])
    }

    @Test func ignoresFoldersAndHiddenFiles() {
        var tracker = ArrivalTracker(existing: [])
        let folder = Entry(name: "Unzipped", fileNumber: 10, size: 0, isRegularFile: false)
        _ = tracker.observe([folder, file(".DS_Store", 11)], now: at(0))
        #expect(!tracker.hasPending)
        #expect(tracker.observe([folder, file(".DS_Store", 11)], now: at(9)).isEmpty)
    }

    @Test func forgetsAFileThatDisappearsBeforeSettling() {
        var tracker = ArrivalTracker(existing: [])
        _ = tracker.observe([file("scratch.dat", 12)], now: at(0))
        #expect(tracker.observe([], now: at(0.5)).isEmpty)
        #expect(!tracker.hasPending)
    }

    /// Deleting a file and later getting another with the same name (a second
    /// download, the next screenshot) counts as new.
    @Test func aNameReusedAfterDeletionIsNew() {
        var tracker = ArrivalTracker(existing: [file("report.pdf", 13)])
        _ = tracker.observe([], now: at(0))
        _ = tracker.observe([file("report.pdf", 14)], now: at(1))
        #expect(tracker.observe([file("report.pdf", 14)], now: at(2)) == ["report.pdf"])
    }

    /// Without file numbers, renames can't be told apart, but the file
    /// already there still isn't reported.
    @Test func copesWithoutFileNumbers() {
        let old = Entry(name: "old.txt", fileNumber: nil, size: 5, isRegularFile: true)
        var tracker = ArrivalTracker(existing: [old])
        let new = Entry(name: "new.txt", fileNumber: nil, size: 5, isRegularFile: true)
        _ = tracker.observe([old, new], now: at(0))
        #expect(tracker.observe([old, new], now: at(1)) == ["new.txt"])
        #expect(tracker.observe([old, new], now: at(2)).isEmpty)
    }
}

struct FolderScanTests {
    @Test func listsRegularFilesWithTheirSizeAndFileNumber() throws {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("openshelf-scan-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: folder.appendingPathComponent("Sub"), withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: folder) }
        try Data("hello".utf8).write(to: folder.appendingPathComponent("a.txt"))
        try Data("x".utf8).write(to: folder.appendingPathComponent(".hidden"))
        try Data("x".utf8).write(to: folder.appendingPathComponent("b.pdf.crdownload"))

        let entries = try #require(FolderMonitor.scan(folder))
        #expect(Set(entries.map(\.name)) == ["a.txt", "Sub"])
        let file = try #require(entries.first { $0.name == "a.txt" })
        #expect(file.isRegularFile)
        #expect(file.size == 5)
        #expect(file.fileNumber != nil)
        #expect(!file.isBusy)
        #expect(entries.first { $0.name == "Sub" }?.isRegularFile == false)
    }

    @Test func returnsNilForAMissingFolder() {
        #expect(FolderMonitor.scan(URL(fileURLWithPath: "/nonexistent-\(UUID().uuidString)")) == nil)
    }
}

struct WatchedFolderActionTests {
    @Test func roundTripsThroughItsStoredText() {
        let actions = [WatchedFolderAction.addOnly] + WatchedFolderAction.conversions + WatchedFolderAction.tools
        for action in actions {
            #expect(WatchedFolderAction(stored: action.stored) == action, "\(action.stored)")
        }
        #expect(WatchedFolderAction.convert(.jpg).stored == "convert:jpg")
        #expect(WatchedFolderAction.tool(.compress).stored == "tool:compress")
    }

    @Test func unreadableTextIsNil() {
        for stored in ["", "convert", "convert:nope", "tool:nope", "zap:jpg"] {
            #expect(WatchedFolderAction(stored: stored) == nil, "\(stored)")
        }
    }

    @Test func decodesSomethingUnknownAsJustAdding() throws {
        let decoded = try JSONDecoder().decode([WatchedFolderAction].self, from: Data(#"["tool:gone", "convert:png"]"#.utf8))
        #expect(decoded == [.addOnly, .convert(.png)])
    }

    /// Every tool offered works on a single file.
    @Test func offersOnlySingleFileTools() {
        for case .tool(let tool) in WatchedFolderAction.tools {
            #expect(![.combinePDF, .mergeMedia].contains(tool))
        }
        #expect(Set(WatchedFolderAction.tools.map(\.title)).count == WatchedFolderAction.tools.count)
    }
}

struct WatchedFolderStoreTests {
    @Test func roundTripsFolders() throws {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("openshelf-watched-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: folder) }
        var downloads = WatchedFolder(url: folder)
        downloads.action = .convert(.jpg)
        downloads.isEnabled = false
        let screenshots = WatchedFolder(url: folder, isScreenshots: true)
        let store = folder.appendingPathComponent("watched-folders.json")

        try WatchedFolderStore.save([downloads, screenshots], to: store)
        #expect(WatchedFolderStore.load(from: store) == [downloads, screenshots])
    }

    @Test func loadsNothingFromAMissingOrUnreadableFile() throws {
        let store = FileManager.default.temporaryDirectory.appendingPathComponent("openshelf-watched-\(UUID().uuidString).json")
        defer { try? FileManager.default.removeItem(at: store) }
        #expect(WatchedFolderStore.load(from: store).isEmpty)
        try Data("not json".utf8).write(to: store)
        #expect(WatchedFolderStore.load(from: store).isEmpty)
    }

    @Test func findsAFolderByItsPathOrNotAtAll() throws {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("openshelf-watched-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        var watched = WatchedFolder(url: folder)
        watched.bookmark = nil
        #expect(watched.resolve()?.standardizedFileURL.path == folder.standardizedFileURL.path)
        try FileManager.default.removeItem(at: folder)
        #expect(watched.resolve() == nil)
    }
}

@MainActor
struct WatchedFoldersTests {
    private func makeFolders() throws -> (store: URL, folder: URL) {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("openshelf-watched-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: folder.appendingPathComponent("Inbox"), withIntermediateDirectories: true)
        return (folder.appendingPathComponent("watched-folders.json"), folder)
    }

    @Test func startsWithNothingWatched() throws {
        let (store, folder) = try makeFolders()
        defer { try? FileManager.default.removeItem(at: folder) }
        #expect(WatchedFolders(storeURL: store).folders.isEmpty)
    }

    @Test func savesChangesForTheNextLaunch() throws {
        let (store, folder) = try makeFolders()
        defer { try? FileManager.default.removeItem(at: folder) }
        let inbox = folder.appendingPathComponent("Inbox")
        let watched = WatchedFolders(storeURL: store)
        watched.add(inbox)
        watched.add(inbox)
        #expect(watched.folders.count == 1)
        let id = try #require(watched.folders.first?.id)
        watched.setAction(.tool(.compress), for: id)
        watched.setEnabled(false, for: id)

        let reloaded = WatchedFolders(storeURL: store)
        #expect(reloaded.folders == watched.folders)
        #expect(reloaded.folders.first?.action == .tool(.compress))
        #expect(reloaded.folders.first?.isEnabled == false)
        #expect(reloaded.folders.first?.name == "Inbox")
        #expect(reloaded.isWatching(inbox))

        reloaded.remove(id)
        #expect(WatchedFolders(storeURL: store).folders.isEmpty)
    }

    @Test func leavesOutFilesOpenShelfWroteItself() throws {
        let (store, folder) = try makeFolders()
        defer { try? FileManager.default.removeItem(at: folder) }
        let watched = WatchedFolders(storeURL: store)
        let shot = folder.appendingPathComponent("Shot.png")
        let converted = folder.appendingPathComponent("Shot.jpg")
        watched.noteOutputs([converted])
        #expect(watched.arrivals(from: [shot, converted]) == [shot])
    }
}

struct ScreenshotLocationTests {
    private let home = URL(fileURLWithPath: "/Users/someone", isDirectory: true)

    @Test func fallsBackToTheDesktop() {
        let desktop = home.appendingPathComponent("Desktop").path
        #expect(ScreenshotLocation.resolve(stored: nil, home: home) { _ in true }.path == desktop)
        #expect(ScreenshotLocation.resolve(stored: "  ", home: home) { _ in true }.path == desktop)
        #expect(ScreenshotLocation.resolve(stored: "relative/path", home: home) { _ in true }.path == desktop)
        // The chosen folder was deleted.
        #expect(ScreenshotLocation.resolve(stored: "/Volumes/Gone/Shots", home: home) { _ in false }.path == desktop)
    }

    @Test func readsTheChosenFolder() {
        #expect(ScreenshotLocation.resolve(stored: "~/Pictures/Screenshots", home: home) { _ in true }.path == "/Users/someone/Pictures/Screenshots")
        #expect(ScreenshotLocation.resolve(stored: "/Users/someone/Shots/", home: home) { _ in true }.path == "/Users/someone/Shots")
        #expect(ScreenshotLocation.resolve(stored: "file:///Users/someone/Shots/", home: home) { _ in true }.path == "/Users/someone/Shots")
    }
}

/// New files go on their watched folder's shelf, through `ShelfManager`.
@MainActor
struct WatchedFolderShelfTests {
    private func makeManager() throws -> (manager: ShelfManager, folder: URL) {
        _ = NSApplication.shared
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("openshelf-arrivals-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        return (ShelfManager(storeURL: folder.appendingPathComponent("shelves.json")), folder)
    }

    private func touch(_ name: String, in folder: URL) throws -> URL {
        let url = folder.appendingPathComponent(name)
        try Data(name.utf8).write(to: url)
        return url
    }

    @Test func keepsEachFoldersFilesOnOneShelf() throws {
        let (manager, folder) = try makeManager()
        defer { manager.closeAll(); manager.saveNow(); try? FileManager.default.removeItem(at: folder) }
        let downloads = UUID(), screenshots = UUID()

        let a = try touch("a.pdf", in: folder), b = try touch("b.pdf", in: folder), shot = try touch("Shot.png", in: folder)

        let first = manager.addArrivals([a], fromWatchedFolder: downloads)
        let second = manager.addArrivals([b], fromWatchedFolder: downloads)
        let other = manager.addArrivals([shot], fromWatchedFolder: screenshots)

        #expect(first === second)
        #expect(first.items.map(\.name) == ["a.pdf", "b.pdf"])
        #expect(other !== first)
        #expect(manager.openShelves.count == 2)
    }

    @Test func opensANewShelfOnceTheOldOneIsClosed() throws {
        let (manager, folder) = try makeManager()
        defer { manager.closeAll(); manager.saveNow(); try? FileManager.default.removeItem(at: folder) }
        let downloads = UUID()

        let a = try touch("a.pdf", in: folder), b = try touch("b.pdf", in: folder)

        let first = manager.addArrivals([a], fromWatchedFolder: downloads)
        manager.closeAll()
        let second = manager.addArrivals([b], fromWatchedFolder: downloads)

        #expect(second !== first)
        #expect(second.items.map(\.name) == ["b.pdf"])
    }
}
