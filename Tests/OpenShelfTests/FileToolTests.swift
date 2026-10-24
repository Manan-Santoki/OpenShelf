import Foundation
import Testing
@testable import OpenShelf

struct FileToolTests {
    private let ffmpeg = ExternalTools(ffmpeg: URL(fileURLWithPath: "/usr/bin/true"))

    private func urls(_ names: String...) -> [URL] {
        names.map { URL(fileURLWithPath: "/tmp/\($0)") }
    }

    @Test func imagesOfferImageToolsAndOCR() {
        #expect(FileTool.available(for: urls("a.jpg")) == [.compress, .metadata, .edit, .annotate, .addBackground, .crop, .resize, .removeBackground, .redact, .readQR, .ocr])
    }

    @Test func severalImagesCanBeCombined() {
        #expect(FileTool.available(for: urls("a.jpg", "b.png")).last == .combinePDF)
    }

    @Test func singlePDFGetsPageTools() {
        #expect(FileTool.available(for: urls("a.pdf")) == [.organizePages, .splitPDF, .compressPDF, .pdfMetadata, .ocr])
    }

    @Test func severalPDFsGetCombineAndThePageTools() {
        #expect(FileTool.available(for: urls("a.pdf", "b.pdf")) == [.combinePDF, .organizePages, .splitPDF, .compressPDF, .pdfMetadata, .ocr])
    }

    @Test func imagesAndPDFsTogetherOfferCombineAndOCR() {
        #expect(FileTool.available(for: urls("a.pdf", "b.jpg")) == [.combinePDF, .ocr])
    }

    @Test func mediaTools() {
        #expect(FileTool.available(for: urls("a.mov")) == [.trim, .speed, .cropVideo, .rotateVideo, .splitMedia, .compressVideo, .mute])
        #expect(FileTool.available(for: urls("a.mp3")) == [.trim, .speed, .splitMedia])
    }

    @Test func severalMediaFilesCanBeMerged() {
        #expect(FileTool.available(for: urls("a.mov", "b.mp4")).first == .mergeMedia)
        #expect(FileTool.available(for: urls("a.mp3", "b.m4a")) == [.mergeMedia, .trim, .speed, .splitMedia])
        #expect(!FileTool.available(for: urls("a.mov", "b.mp3")).contains(.mergeMedia))
    }

    @Test func convertFirstToastNamesTheFormatToConvertTo() {
        let video = FileTool.convertFirstToast(for: urls("a.mkv"), convertKeys: "⇧", tools: ffmpeg)
        #expect(video.title == "Convert to MP4 first")
        #expect(video.subtitle == "Tap ⇧ while dragging to convert")
        #expect(FileTool.convertFirstToast(for: urls("a.wma"), convertKeys: "⇧", tools: ffmpeg).title == "Convert to M4A first")
        #expect(FileTool.convertFirstToast(for: urls("a.webm", "b.wma"), convertKeys: "⇧", tools: ffmpeg).title == "Convert to MP4 or M4A first")
        // The Convert wheel's trigger can be changed in Settings.
        #expect(FileTool.convertFirstToast(for: urls("a.avi"), convertKeys: "⌃⌥", tools: ffmpeg).subtitle == "Tap ⌃⌥ while dragging to convert")
    }

    @Test func convertFirstToastFitsOnOneLine() {
        // The toast shows one line each: about 30 characters of subtitle fit.
        let toast = FileTool.convertFirstToast(for: urls("a.webm", "b.wma"), convertKeys: "⇧⌥", tools: ffmpeg)
        #expect(toast.title.count <= 28)
        #expect((toast.subtitle ?? "").count <= 34)
    }

    @Test func mediaToolsOnlyForFormatsTheMediaFrameworkEdits() {
        for name in ["a.mkv", "a.webm", "a.avi", "a.wmv", "a.flv", "a.wma"] {
            #expect(FileTool.available(for: urls(name)).isEmpty, "\(name) should not offer tools")
            #expect(FileTool.needsConversionForTools(urls(name)))
        }
        #expect(FileTool.available(for: urls("a.mp4", "b.mkv")).isEmpty)
        #expect(!FileTool.available(for: urls("a.ogg")).isEmpty)
        #expect(!FileTool.needsConversionForTools(urls("a.mp4")))
        #expect(!FileTool.needsConversionForTools(urls("a.jpg")))
    }

    @Test func otherFilesHaveNoTools() {
        #expect(FileTool.available(for: urls("a.txt")).isEmpty)
        #expect(FileTool.available(for: []).isEmpty)
    }
}
