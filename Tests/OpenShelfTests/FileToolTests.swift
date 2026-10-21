import Foundation
import Testing
@testable import OpenShelf

struct FileToolTests {
    private func urls(_ names: String...) -> [URL] {
        names.map { URL(fileURLWithPath: "/tmp/\($0)") }
    }

    @Test func imagesGetTheVideoToolOrderPlusReadQR() {
        #expect(FileTool.available(for: urls("a.jpg")) == [.compress, .metadata, .edit, .annotate, .addBackground, .crop, .redact, .readQR])
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
        #expect(FileTool.available(for: urls("a.mov")) == [.trim, .speed, .cropVideo, .compressVideo, .mute])
        #expect(FileTool.available(for: urls("a.mp3")) == [.trim, .speed])
    }

    @Test func otherFilesHaveNoTools() {
        #expect(FileTool.available(for: urls("a.txt")).isEmpty)
        #expect(FileTool.available(for: []).isEmpty)
    }
}
