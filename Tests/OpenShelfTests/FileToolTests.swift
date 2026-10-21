import Foundation
import Testing
@testable import OpenShelf

struct FileToolTests {
    private func urls(_ names: String...) -> [URL] {
        names.map { URL(fileURLWithPath: "/tmp/\($0)") }
    }

    @Test func imagesGetTheVideoToolOrderPlusReadQR() {
        #expect(FileTool.available(for: urls("a.jpg")) == [.compress, .metadata, .edit, .annotate, .addBackground, .crop, .resize, .removeBackground, .redact, .readQR])
    }

    @Test func severalImagesCanBeCombined() {
        #expect(FileTool.available(for: urls("a.jpg", "b.png")).last == .combinePDF)
    }

    @Test func singlePDFGetsPageTools() {
        #expect(FileTool.available(for: urls("a.pdf")) == [.organizePages, .splitPDF, .compressPDF, .pdfMetadata])
    }

    @Test func severalPDFsGetCombineButNotPageTools() {
        #expect(FileTool.available(for: urls("a.pdf", "b.pdf")) == [.combinePDF, .compressPDF, .pdfMetadata])
    }

    @Test func imagesAndPDFsTogetherOnlyCombine() {
        #expect(FileTool.available(for: urls("a.pdf", "b.jpg")) == [.combinePDF])
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
