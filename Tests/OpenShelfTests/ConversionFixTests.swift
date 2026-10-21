import Foundation
import Testing
import UniformTypeIdentifiers
@testable import OpenShelf

struct OutputNamingLimitTests {
    @Test func truncatesOnCharacterBoundaries() {
        #expect(OutputNaming.truncated("abcdef", toBytes: 4) == "abcd")
        // Each emoji is 4 bytes; a partial one must not be cut in half.
        #expect(OutputNaming.truncated("😀😀😀", toBytes: 6) == "😀")
        #expect(OutputNaming.truncated("short", toBytes: 100) == "short")
    }

    @Test func longNamesKeepTheirSuffixAndFit() {
        let source = URL(fileURLWithPath: "/tmp/\(String(repeating: "L", count: 236)).jpg")
        let destination = OutputNaming.destination(for: source, suffix: "Redacted", pathExtension: "jpg")
        let name = destination.lastPathComponent
        #expect(name.hasSuffix(" Redacted.jpg"))
        #expect(name.utf8.count <= OutputNaming.maxNameBytes)
    }

    @Test func dotFilesProduceVisibleOutputs() {
        let source = URL(fileURLWithPath: "/tmp/openshelf-naming-\(UUID().uuidString)/.hidden.jpg")
        #expect(OutputNaming.destination(for: source, pathExtension: "png").lastPathComponent == "hidden.png")
        #expect(OutputNaming.destination(for: source, suffix: "Compressed", pathExtension: "jpg").lastPathComponent == "hidden Compressed.jpg")
        #expect(OutputNaming.visibleName("...") == "Untitled")
    }
}

struct TextDecodingTests {
    @Test func decodesUTF8AndEmpty() {
        #expect(TextDecoding.decode(Data("Hello 👋".utf8)) == "Hello 👋")
        #expect(TextDecoding.decode(Data()) == "")
    }

    @Test func decodesUTF16WithByteOrderMark() {
        let data = "Grüße".data(using: .utf16)!  // includes a BOM
        #expect(TextDecoding.decode(data) == "Grüße")
    }

    @Test func fallsBackToWindowsLatin() {
        let data = Data([0x63, 0x61, 0x66, 0xE9])  // "café" in Latin-1 / Windows-1252
        #expect(TextDecoding.decode(data) == "café")
    }
}

struct FileSnifferTests {
    @Test(arguments: [
        ([UInt8]([0xFF, 0xD8, 0xFF, 0xE0]), UTType.jpeg),
        ([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A], .png),
        (Array("%PDF-1.7".utf8), .pdf),
        ([0x50, 0x4B, 0x03, 0x04], .zip),
        ([0x1F, 0x8B, 0x08], .gzip),
        ([0, 0, 0, 0x18] + Array("ftypheic".utf8), .heic),
        ([0, 0, 0, 0x14] + Array("ftypqt  ".utf8), .quickTimeMovie),
    ])
    func recognisesHeaders(bytes: [UInt8], type: UTType) {
        #expect(FileSniffer.type(ofHeader: Data(bytes)) == type)
    }

    @Test func ignoresText() {
        #expect(FileSniffer.type(ofHeader: Data("BMW service notes".utf8)) == nil)
        #expect(FileSniffer.type(ofHeader: Data("hello".utf8)) == nil)
    }

    @Test func classifiesFilesWithoutExtensionsAndSymlinks() throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("openshelf-sniff-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        let photo = directory.appendingPathComponent("NoExtension")
        try Data([0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10]).write(to: photo)
        #expect(FileKind(url: photo) == .image)

        let real = directory.appendingPathComponent("photo.jpg")
        try Data([0xFF, 0xD8, 0xFF]).write(to: real)
        let link = directory.appendingPathComponent("Link to photo")
        try FileManager.default.createSymbolicLink(at: link, withDestinationURL: real)
        #expect(FileKind(url: link) == .image)
    }
}

struct FailureMessageTests {
    let folder = URL(fileURLWithPath: "/Users/me/Read Only")

    @Test func permissionProblemsNameTheFolder() {
        let source = folder.appendingPathComponent("Photo.jpg")
        let message = FailureMessage.message(forToolOutput: "ditto: /x/Photo.zip: Permission denied", source: source, kind: .image)
        #expect(message == "You don’t have permission to save in “Read Only”.")
    }

    @Test func archivesExplainPasswordsAndDamage() {
        let zip = folder.appendingPathComponent("Secret.zip")
        #expect(FailureMessage.message(
            forToolOutput: "ditto: No password was provided and the client did not provide a callback for retrieving the PKZip encryption key",
            source: zip, kind: .archive
        ) == "“Secret.zip” is password-protected; OpenShelf can’t open encrypted archives.")
        #expect(FailureMessage.message(forToolOutput: "ditto: Couldn't read pkzip signature.", source: zip, kind: .archive)
            == "“Secret.zip” is damaged or isn’t an archive OpenShelf can open.")
    }

    @Test func damagedMediaIsExplained() {
        let clip = folder.appendingPathComponent("Clip.mp4")
        #expect(FailureMessage.message(forToolOutput: "clip.mp4: Invalid data found when processing input", source: clip, kind: .video)
            == "“Clip.mp4” is damaged or in a format OpenShelf can’t read.")
        #expect(FailureMessage.message(forToolOutput: "something unexpected", source: clip, kind: .video) == nil)
    }

    @Test func sanitizingRemovesTemporaryPaths() {
        let text = "Error! Cannot open output file '/private/var/folders/dy/abc/T/1234.webp'"
        let sanitized = FailureMessage.sanitized(text)
        #expect(!sanitized.contains("/var/folders"))
        #expect(sanitized.contains("a temporary file"))
    }
}

struct MediaProbeTests {
    /// A minimal moov payload: one trak whose mdia declares the given handler.
    private func movieBox(handlers: [String]) -> Data {
        var data = Data()
        for handler in handlers {
            data += Data([0, 0, 0, 33]) + Data("hdlr".utf8) + Data(count: 4) + Data("mhlr".utf8) + Data(handler.utf8) + Data(count: 13)
        }
        return data
    }

    @Test func findsTrackHandlers() {
        #expect(MediaProbe.handlerTypes(inMovieBox: movieBox(handlers: ["vide", "soun"])) == ["vide", "soun"])
        #expect(!MediaProbe.handlerTypes(inMovieBox: movieBox(handlers: ["vide"])).contains("soun"))
    }

    @Test func readsRealContainers() throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("openshelf-probe-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        // ftyp, then a moov whose only track is video.
        var file = Data([0, 0, 0, 16]) + Data("ftypisom".utf8) + Data(count: 4)
        let moov = movieBox(handlers: ["vide"])
        file += Data([0, 0, 0, UInt8(8 + moov.count)]) + Data("moov".utf8) + moov
        let silent = directory.appendingPathComponent("silent.mp4")
        try file.write(to: silent)
        #expect(MediaProbe.hasAudioTrack(silent) == false)

        let notMovie = directory.appendingPathComponent("notes.txt")
        try Data("hello".utf8).write(to: notMovie)
        #expect(MediaProbe.hasAudioTrack(notMovie) == nil)
    }
}
