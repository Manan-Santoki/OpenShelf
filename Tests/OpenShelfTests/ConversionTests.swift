import Foundation
import Testing
@testable import OpenShelf

struct OutputNamingTests {
    private func makeDirectory() throws -> URL {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("openshelf-naming-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        return directory
    }

    @Test func savesBesideOriginalWithNewExtension() throws {
        let directory = try makeDirectory()
        let source = directory.appendingPathComponent("Page 001.pdf")
        #expect(OutputNaming.destination(for: source, pathExtension: "jpg").lastPathComponent == "Page 001.jpg")
    }

    @Test func appendsSuffixAndAvoidsOverwriting() throws {
        let directory = try makeDirectory()
        let source = directory.appendingPathComponent("Photo.jpg")
        FileManager.default.createFile(atPath: directory.appendingPathComponent("Photo Cropped.jpg").path, contents: Data())
        let destination = OutputNaming.destination(for: source, suffix: "Cropped", pathExtension: "jpg")
        #expect(destination.lastPathComponent == "Photo Cropped 2.jpg")
    }

    @Test func foldersHaveNoExtension() throws {
        let directory = try makeDirectory()
        let source = directory.appendingPathComponent("Backup.zip")
        #expect(OutputNaming.destination(for: source, pathExtension: nil).lastPathComponent == "Backup")
    }
}

struct SubtitleConversionTests {
    let srt = """
    1
    00:00:01,000 --> 00:00:02,500
    Hello <i>there</i>

    2
    00:00:03,000 --> 00:00:04,000
    General Kenobi
    """

    @Test func parsesSRTCues() {
        let cues = SubtitleConversion.parse(srt)
        #expect(cues.count == 2)
        #expect(cues[0] == .init(start: "00:00:01,000", end: "00:00:02,500", lines: ["Hello <i>there</i>"]))
    }

    @Test func convertsSRTToVTT() {
        let vtt = SubtitleConversion.vtt(SubtitleConversion.parse(srt))
        #expect(vtt.hasPrefix("WEBVTT\n\n00:00:01.000 --> 00:00:02.500\nHello <i>there</i>"))
    }

    @Test func convertsVTTToSRTWithShortTimesAndCueSettings() {
        let vtt = "WEBVTT\n\n01:02.5 --> 01:04.000 align:start\nLine one\n"
        #expect(SubtitleConversion.srt(SubtitleConversion.parse(vtt)) == "1\n00:01:02,500 --> 00:01:04,000\nLine one\n")
    }

    @Test func plainTextDropsTimingsAndTags() {
        #expect(SubtitleConversion.plainText(SubtitleConversion.parse(srt)) == "Hello there\nGeneral Kenobi\n")
    }
}

struct ConversionCatalogTests {
    let noTools = ExternalTools(ffmpeg: nil, cwebp: nil)
    let allTools = ExternalTools(ffmpeg: URL(fileURLWithPath: "/usr/bin/true"), cwebp: URL(fileURLWithPath: "/usr/bin/true"))

    @Test func pdfOffersDocumentAndImageFormats() {
        let formats = ConversionCatalog.formats(for: [URL(fileURLWithPath: "/tmp/Page 001.pdf")], tools: noTools)
        #expect(formats.prefix(4) == [.docx, .jpg, .png, .txt])
    }

    @Test func imageOmitsItsOwnFormatAndWebPWithoutEncoder() {
        let formats = ConversionCatalog.formats(for: [URL(fileURLWithPath: "/tmp/Page 001.jpg")], tools: noTools)
        #expect(!formats.contains(.jpg))
        #expect(!formats.contains(.webp))
        #expect(formats.contains(.heic) && formats.contains(.avif) && formats.contains(.docx))
    }

    @Test func webPAppearsWhenAnEncoderIsInstalled() {
        #expect(ConversionCatalog.formats(for: [URL(fileURLWithPath: "/tmp/a.png")], tools: allTools).contains(.webp))
    }

    @Test func mixedSelectionsOnlyOfferSharedFormats() {
        let formats = ConversionCatalog.formats(
            for: [URL(fileURLWithPath: "/tmp/a.png"), URL(fileURLWithPath: "/tmp/b.pdf")],
            tools: noTools
        )
        #expect(Set(formats) == [.jpg, .docx, .zip])
    }

    @Test func archivesOfferExtraction() {
        #expect(ConversionCatalog.formats(for: [URL(fileURLWithPath: "/tmp/files.zip")], tools: noTools) == [.extract])
    }
}

struct FileKindTests {
    @Test(arguments: [
        ("a.jpg", FileKind.image), ("a.HEIC", .image), ("a.pdf", .pdf), ("a.docx", .document),
        ("a.md", .document), ("a.mp3", .audio), ("a.mov", .video), ("a.srt", .subtitle),
        ("a.zip", .archive), ("a.tar.gz", .archive), ("a.xyz123", .other),
    ])
    func classifiesByExtension(name: String, kind: FileKind) {
        #expect(FileKind(url: URL(fileURLWithPath: "/tmp/\(name)")) == kind)
    }
}
