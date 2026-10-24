import Foundation
import Testing
@testable import OpenShelf

struct WheelOrderingTests {
    let noTools = ExternalTools(ffmpeg: nil, cwebp: nil)
    let allTools = ExternalTools(
        ffmpeg: URL(fileURLWithPath: "/usr/bin/true"),
        cwebp: URL(fileURLWithPath: "/usr/bin/true"),
        ffprobe: nil,
        libreOffice: URL(fileURLWithPath: "/usr/bin/true")
    )

    private func urls(_ names: String...) -> [URL] {
        names.map { URL(fileURLWithPath: "/tmp/\($0)") }
    }

    private func tools(_ urls: [URL]) -> [FileTool] {
        WheelOrdering.tools(FileTool.available(for: urls))
    }

    private func formats(_ urls: [URL], _ tools: ExternalTools) -> [OutputFormat] {
        WheelOrdering.formats(ConversionCatalog.formats(for: urls, tools: tools), for: urls)
    }

    /// The first page of a wheel with these items: the six before More.
    private func firstPage<Element>(_ list: [Element]) -> [Element] {
        Array(list.prefix(WheelPaging.itemsPerPage))
    }

    // MARK: Tools

    @Test func imageToolsLeadWithTheEverydayOnes() {
        #expect(tools(urls("a.jpg")) == [
            .compress, .crop, .resize, .annotate, .removeBackground, .edit,
            .metadata, .redact, .addBackground, .readQR, .ocr,
        ])
    }

    @Test func severalImagesAddCombineAtTheEnd() {
        let tools = tools(urls("a.jpg", "b.png"))
        #expect(tools.count == 12)
        #expect(firstPage(tools) == [.compress, .crop, .resize, .annotate, .removeBackground, .edit])
        #expect(tools.last == .combinePDF)
    }

    @Test func severalVideosPutMergeFirst() {
        #expect(tools(urls("a.mov", "b.mp4")) == [.mergeMedia, .trim, .compressVideo, .cropVideo, .rotateVideo, .splitMedia, .speed, .mute])
    }

    @Test func toolWheelsThatFitOnOnePageKeepTheirOrder() {
        #expect(tools(urls("a.mov")) == FileTool.available(for: urls("a.mov")))
        #expect(tools(urls("a.pdf")) == [.organizePages, .splitPDF, .compressPDF, .pdfMetadata, .ocr])
        #expect(tools(urls("a.pdf", "b.pdf")) == [.combinePDF, .organizePages, .splitPDF, .compressPDF, .pdfMetadata, .ocr])
        #expect(tools(urls("a.mp3", "b.m4a")) == [.mergeMedia, .trim, .speed, .splitMedia])
    }

    // MARK: Convert

    @Test func imageFormatsLeaveOutTheSourceFormat() {
        #expect(formats(urls("a.png"), allTools) == [.jpg, .heic, .webp, .pdf, .avif, .tiff, .bmp, .docx, .zip])
        #expect(formats(urls("a.heic"), allTools) == [.png, .jpg, .webp, .pdf, .avif, .tiff, .bmp, .docx, .zip])
    }

    @Test func imageFormatsWithoutAWebPEncoder() {
        #expect(formats(urls("a.gif"), noTools) == [.png, .jpg, .heic, .pdf, .avif, .tiff, .bmp, .docx, .zip])
    }

    @Test func pdfFormats() {
        #expect(formats(urls("a.pdf"), noTools) == [.docx, .jpg, .png, .txt, .pptx, .xlsx, .md, .epub, .zip])
    }

    @Test func videoFormatsWithFFmpeg() {
        #expect(formats(urls("a.mov"), allTools) == [.mp4, .gif, .mp3, .m4a, .webm, .mkv, .avi, .wmv, .zip])
        #expect(formats(urls("a.mkv"), allTools) == [.mp4, .mov, .gif, .mp3, .m4a, .webm, .avi, .wmv, .zip])
    }

    @Test func videoFormatsWithoutFFmpegFitOnOnePage() {
        // GIF is made natively, so it stays without ffmpeg.
        #expect(formats(urls("a.mov"), noTools) == [.mp4, .m4a, .gif, .zip])
    }

    @Test func audioFormatsWithFFmpeg() {
        #expect(formats(urls("a.m4a"), allTools) == [.mp3, .wav, .flac, .aiff, .ogg, .opus, .wma, .zip])
        #expect(formats(urls("a.ogg"), allTools) == [.mp3, .m4a, .wav, .flac, .aiff, .opus, .wma, .zip])
    }

    @Test func audioFormatsWithoutFFmpegKeepTheCatalogOrder() {
        #expect(formats(urls("a.mp3"), noTools) == [.m4a, .wav, .aiff, .flac, .zip])
    }

    @Test func otherKindsKeepTheCatalogOrder() {
        let catalog = ConversionCatalog.formats(for: urls("a.docx"), tools: allTools)
        #expect(formats(urls("a.docx"), allTools) == catalog)
    }

    @Test func zipStaysLastAndNothingIsLost() {
        for name in ["a.png", "a.jpg", "a.pdf", "a.mov", "a.mp4", "a.webm", "a.mp3", "a.wav", "a.flac"] {
            let catalog = ConversionCatalog.formats(for: urls(name), tools: allTools)
            let ordered = formats(urls(name), allTools)
            #expect(Set(ordered) == Set(catalog) && ordered.count == catalog.count, "\(name)")
            #expect(ordered.last == .zip, "\(name)")
        }
    }

    @Test func everyCatalogFormatHasAPriority() {
        let sources: [(String, FileKind)] = [
            ("a.png", .image), ("a.gif", .image), ("a.pdf", .pdf),
            ("a.mov", .video), ("a.mkv", .video), ("a.mp3", .audio), ("a.ogg", .audio),
        ]
        for (name, kind) in sources {
            let priority = Set(WheelOrdering.formatsBySource[kind] ?? [])
            for format in ConversionCatalog.formats(for: urls(name), tools: allTools) {
                #expect(priority.contains(format), "\(format) for \(name)")
            }
        }
    }

    @Test func unlistedElementsFollowInTheirOriginalOrder() {
        #expect(WheelOrdering.ordered([5, 1, 9, 2, 7], by: [2, 1]) == [2, 1, 5, 9, 7])
    }
}
