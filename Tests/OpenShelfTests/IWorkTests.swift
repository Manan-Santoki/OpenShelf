import CoreGraphics
import Foundation
import PDFKit
import Testing
import UniformTypeIdentifiers
@testable import OpenShelf

/// Pages, Numbers and Keynote files: which kind they are, the preview inside
/// them (in zip files and in folder packages), what the wheel offers for them,
/// and when PAGES, NUMBERS and KEY are offered as outputs.
struct IWorkTests {
    let none = ExternalTools()
    let office = ExternalTools(ffmpeg: nil, cwebp: nil, libreOffice: URL(fileURLWithPath: "/usr/bin/true"))
    private func url(_ name: String) -> URL { URL(fileURLWithPath: "/tmp/\(name)") }

    private func makeDirectory(_ name: String) throws -> URL {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("openshelf-\(name)-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        return directory
    }

    /// A PDF of drawn pages, like the one iWork '09 saves for Quick Look.
    private func pdfData(pageCount: Int) throws -> Data {
        let data = NSMutableData()
        let consumer = try #require(CGDataConsumer(data: data as CFMutableData))
        var box = CGRect(x: 0, y: 0, width: 200, height: 260)
        let context = try #require(CGContext(consumer: consumer, mediaBox: &box, nil))
        for _ in 0..<pageCount {
            context.beginPDFPage(nil)
            context.setFillColor(CGColor(red: 1, green: 1, blue: 1, alpha: 1))
            context.fill(box)
            context.endPDFPage()
        }
        context.closePDF()
        return data as Data
    }

    /// A small JPEG, like the "preview.jpg" newer iWork files carry.
    private func jpegData(in directory: URL) throws -> Data {
        let context = try #require(CGContext(
            data: nil, width: 40, height: 30, bitsPerComponent: 8, bytesPerRow: 0,
            space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGImageAlphaInfo.noneSkipLast.rawValue
        ))
        context.setFillColor(CGColor(red: 0.2, green: 0.4, blue: 0.8, alpha: 1))
        context.fill(CGRect(x: 0, y: 0, width: 40, height: 30))
        let image = try #require(context.makeImage())
        let url = directory.appendingPathComponent("preview-\(UUID().uuidString).jpg")
        try EditableImage.write(image, to: url, type: .jpeg)
        defer { try? FileManager.default.removeItem(at: url) }
        return try Data(contentsOf: url)
    }

    /// A package folder named `name` holding `files` (path → contents) and a
    /// stand-in for the document's own data.
    private func makePackage(_ name: String, files: [String: Data], in directory: URL) throws -> URL {
        let package = directory.appendingPathComponent(name)
        try PackageZip.write("stand-in", to: "Index/Document.iwa", in: package)
        for (path, data) in files {
            try PackageZip.write(data, to: path, in: package)
        }
        return package
    }

    /// The same package as a single zip file, the way current iWork saves.
    private func makeZipPackage(_ name: String, files: [String: Data], in directory: URL) throws -> URL {
        let parts = try makeDirectory("iwork-parts")
        defer { try? FileManager.default.removeItem(at: parts) }
        let folder = try makePackage("contents", files: files, in: parts)
        let zip = directory.appendingPathComponent(name)
        try PackageZip.zip(folder, to: zip)
        return zip
    }

    // MARK: Detection

    @Test(arguments: [
        ("a.pages", FileKind.document, IWorkFormat.pages),
        ("a.numbers", .spreadsheet, .numbers),
        ("a.key", .presentation, .keynote),
        ("A.KEY", .presentation, .keynote),
    ])
    func classifiesIWorkFiles(name: String, kind: FileKind, format: IWorkFormat) {
        #expect(FileKind(url: url(name)) == kind)
        #expect(IWorkFormat(url: url(name)) == format)
    }

    @Test func otherFilesAreNotIWork() {
        for name in ["a.docx", "a.pptx", "a.xlsx", "a.pdf", "a.keynote", "pages"] {
            #expect(IWorkFormat(url: url(name)) == nil, "\(name)")
        }
    }

    @Test func packageFoldersAreDocumentsNotFolders() throws {
        let directory = try makeDirectory("iwork-kind")
        defer { try? FileManager.default.removeItem(at: directory) }
        let pages = try makePackage("Report.pages", files: [:], in: directory)
        let keynote = try makePackage("Deck.key", files: [:], in: directory)
        #expect(FileKind(url: pages) == .document)
        #expect(FileKind(url: keynote) == .presentation)
    }

    // MARK: Previews

    @Test func readsAZipsEntryNames() throws {
        let directory = try makeDirectory("iwork-zip")
        defer { try? FileManager.default.removeItem(at: directory) }
        let zip = try makeZipPackage("Report.pages", files: ["preview.jpg": Data([1, 2, 3])], in: directory)
        let names = try #require(ZipDirectory.entryNames(of: zip))
        #expect(names.contains("preview.jpg"))
        #expect(names.contains("Index/Document.iwa"))

        let notZip = directory.appendingPathComponent("plain.pages")
        try Data("not a zip at all".utf8).write(to: notZip)
        #expect(ZipDirectory.entryNames(of: notZip) == nil)
    }

    @Test func prefersThePDFPreviewThenTheLargestPicture() {
        #expect(IWorkPreview.find(inEntries: ["Index/Document.iwa"]) == nil)
        #expect(IWorkPreview.find(inEntries: ["QuickLook/Thumbnail.jpg", "preview.jpg"]) == IWorkPreview(path: "preview.jpg", isPDF: false))
        #expect(IWorkPreview.find(inEntries: ["preview.jpg", "QuickLook/Preview.pdf"]) == IWorkPreview(path: "QuickLook/Preview.pdf", isPDF: true))
        #expect(IWorkPreview.find(inEntries: ["QuickLook/Thumbnail.jpg"]) == IWorkPreview(path: "QuickLook/Thumbnail.jpg", isPDF: false))
        // Zipped in Finder, the package's contents sit in a folder of its name.
        #expect(IWorkPreview.find(inEntries: ["Report.pages/preview.jpg"]) == IWorkPreview(path: "Report.pages/preview.jpg", isPDF: false))
        #expect(IWorkPreview.find(inEntries: ["Data/Images/preview.jpg"]) == nil)
        #expect(IWorkPreview.find(inEntries: ["preview-micro.jpg"]) == nil)
    }

    @Test func findsAndReadsThePreviewInAZip() throws {
        let directory = try makeDirectory("iwork-zip-preview")
        defer { try? FileManager.default.removeItem(at: directory) }
        let pdf = try pdfData(pageCount: 2)
        let zip = try makeZipPackage("Old.pages", files: ["QuickLook/Preview.pdf": pdf, "QuickLook/Thumbnail.jpg": Data([0xFF])], in: directory)
        let preview = try #require(IWorkPreview.find(in: zip))
        #expect(preview == IWorkPreview(path: "QuickLook/Preview.pdf", isPDF: true))
        #expect(preview.data(in: zip) == pdf)
    }

    @Test func findsAndReadsThePreviewInAPackageFolder() throws {
        let directory = try makeDirectory("iwork-folder-preview")
        defer { try? FileManager.default.removeItem(at: directory) }
        let jpeg = try jpegData(in: directory)
        let package = try makePackage("Deck.key", files: ["preview.jpg": jpeg], in: directory)
        let preview = try #require(IWorkPreview.find(in: package))
        #expect(preview == IWorkPreview(path: "preview.jpg", isPDF: false))
        #expect(preview.data(in: package) == jpeg)
        let empty = try makePackage("Empty.key", files: [:], in: directory)
        #expect(IWorkPreview.find(in: empty) == nil)
    }

    @Test func convertsThePDFPreviewWithoutLibreOffice() async throws {
        let directory = try makeDirectory("iwork-convert-pdf")
        defer { try? FileManager.default.removeItem(at: directory) }
        let preview = try pdfData(pageCount: 2)
        let zip = try makeZipPackage("Report.pages", files: ["QuickLook/Preview.pdf": preview], in: directory)

        let pdf = try await Converter.convert(zip, to: .pdf, tools: none) { _ in }
        #expect(pdf.map(\.lastPathComponent) == ["Report.pdf"])
        #expect(PDFDocument(url: pdf[0])?.pageCount == 2)

        let images = try await Converter.convert(zip, to: .png, tools: none) { _ in }
        #expect(images.count == 2)
        #expect(images.allSatisfy { $0.pathExtension == "png" && $0.deletingLastPathComponent().path == directory.path })
    }

    @Test func convertsTheJPEGPreviewWithoutLibreOffice() async throws {
        let directory = try makeDirectory("iwork-convert-jpg")
        defer { try? FileManager.default.removeItem(at: directory) }
        let jpeg = try jpegData(in: directory)
        let package = try makePackage("Deck.key", files: ["preview.jpg": jpeg], in: directory)

        let png = try await Converter.convert(package, to: .png, tools: none) { _ in }
        #expect(png.map(\.lastPathComponent) == ["Deck.png"])
        #expect(FileSniffer.type(of: png[0]) == .png)
        let jpg = try await Converter.convert(package, to: .jpg, tools: none) { _ in }
        #expect(jpg.map(\.lastPathComponent) == ["Deck.jpg"])
    }

    @Test func withoutAPreviewOrLibreOfficeTheMessageSaysWhatToInstall() async throws {
        let directory = try makeDirectory("iwork-no-preview")
        defer { try? FileManager.default.removeItem(at: directory) }
        let package = try makePackage("Sheet.numbers", files: [:], in: directory)
        do {
            _ = try await Converter.convert(package, to: .pdf, tools: none) { _ in }
            Issue.record("Expected the conversion to fail")
        } catch {
            #expect(error.localizedDescription.contains("Settings → Extra Formats"))
        }
    }

    // MARK: What the wheel offers

    @Test func libreOfficeConvertsAllThree() {
        #expect(ConversionCatalog.formats(for: [url("a.pages")], tools: office) == [.pdf, .docx, .rtf, .txt, .jpg, .png, .zip])
        #expect(ConversionCatalog.formats(for: [url("a.numbers")], tools: office) == [.pdf, .xlsx, .csv, .ods, .jpg, .png, .zip])
        #expect(ConversionCatalog.formats(for: [url("a.key")], tools: office) == [.pdf, .pptx, .jpg, .png, .zip])
        #expect(OfficeConversion.filter(for: .pptx) == "pptx")
        #expect(OfficeConversion.filter(for: .rtf) == "rtf")
        #expect(OfficeConversion.filter(for: .txt)?.hasPrefix("txt") == true)
    }

    @Test func withoutLibreOfficeThePreviewDecides() throws {
        let directory = try makeDirectory("iwork-catalog")
        defer { try? FileManager.default.removeItem(at: directory) }
        let pdf = try pdfData(pageCount: 1)
        let jpeg = try jpegData(in: directory)
        let withPDF = try makeZipPackage("Old.pages", files: ["QuickLook/Preview.pdf": pdf], in: directory)
        let withJPEG = try makePackage("New.key", files: ["preview.jpg": jpeg], in: directory)
        let withNothing = try makePackage("Bare.numbers", files: [:], in: directory)

        #expect(ConversionCatalog.formats(for: [withPDF], tools: none) == [.pdf, .jpg, .png, .zip])
        #expect(ConversionCatalog.formats(for: [withJPEG], tools: none) == [.jpg, .png, .zip])
        #expect(ConversionCatalog.formats(for: [withNothing], tools: none) == [.zip])
        // LibreOffice's list wins once it's installed.
        #expect(ConversionCatalog.formats(for: [withJPEG], tools: office) == [.pdf, .pptx, .jpg, .png, .zip])
    }

    @Test func aMissingPreviewPointsToLibreOffice() {
        let blocking = MissingToolAdvice.blockingTool(for: [url("a.key")], formats: ConversionCatalog.formats(for: [url("a.key")], tools: none), tools: none)
        #expect(blocking == .libreOffice)
        #expect(MissingToolAdvice.toast(for: [url("a.key")], needing: .libreOffice, tools: none).title == "Keynote needs LibreOffice")
        #expect(MissingToolAdvice.toast(for: [url("a.pages")], needing: .libreOffice, tools: none).title == "Pages needs LibreOffice")
        #expect(MissingToolAdvice.moreFormatsHint(for: [url("a.pages")], tools: none) != nil)
        #expect(MissingToolAdvice.moreFormatsHint(for: [url("a.pages")], tools: office) == nil)
    }

    // MARK: Saving as Pages, Numbers and Keynote

    @Test func iWorkOutputsNeedTheirApp() {
        let pages = ExternalTools(ffmpeg: nil, cwebp: nil, iWorkApps: [.pages])
        let all = ExternalTools(ffmpeg: nil, cwebp: nil, iWorkApps: Set(IWorkFormat.allCases))

        #expect(!ConversionCatalog.formats(for: [url("a.docx")], tools: none).contains(.pages))
        #expect(ConversionCatalog.formats(for: [url("a.docx")], tools: pages).suffix(2) == [.pages, .zip])
        for name in ["a.rtf", "a.txt", "a.doc"] {
            #expect(ConversionCatalog.formats(for: [url(name)], tools: pages).contains(.pages), "\(name)")
        }
        // Pages can't open Markdown, HTML or EPUB.
        #expect(!ConversionCatalog.formats(for: [url("a.md")], tools: all).contains(.pages))
        #expect(!ConversionCatalog.formats(for: [url("a.html")], tools: all).contains(.pages))

        #expect(!ConversionCatalog.formats(for: [url("a.csv")], tools: pages).contains(.numbers))
        #expect(ConversionCatalog.formats(for: [url("a.csv")], tools: all).contains(.numbers))
        #expect(ConversionCatalog.formats(for: [url("a.xlsx")], tools: all).contains(.numbers))
        // Keynote saves PowerPoint decks even without LibreOffice.
        #expect(ConversionCatalog.formats(for: [url("a.pptx")], tools: all) == [.key, .zip])
        // An iWork file is never offered its own format, or another app's.
        #expect(ConversionCatalog.formats(for: [url("a.key")], tools: all) == [.zip])
    }

    @Test func iWorkOutputFormatsNameTheirApp() {
        #expect(OutputFormat.pages.iWorkFormat == .pages)
        #expect(OutputFormat.numbers.iWorkFormat == .numbers)
        #expect(OutputFormat.key.iWorkFormat == .keynote)
        #expect(OutputFormat.docx.iWorkFormat == nil)
        #expect(OutputFormat.key.fileExtension == "key")
        #expect(OutputFormat.pages.title == "PAGES")
        #expect(IWorkFormat.allCases.allSatisfy { $0.outputFormat.iWorkFormat == $0 })
    }

    @Test func findsInstalledAppsByBundleIdentifier() throws {
        let applications = try makeDirectory("iwork-apps")
        defer { try? FileManager.default.removeItem(at: applications) }
        func install(_ name: String, identifier: String) throws {
            let plist: [String: Any] = ["CFBundleIdentifier": identifier, "CFBundleName": name, "CFBundlePackageType": "APPL"]
            let data = try PropertyListSerialization.data(fromPropertyList: plist, format: .xml, options: 0)
            try PackageZip.write(data, to: "\(name).app/Contents/Info.plist", in: applications)
        }
        try install("Pages", identifier: "com.apple.iWork.Pages")
        // Something else that happens to be called Keynote doesn't count.
        try install("Keynote", identifier: "com.example.keynote")

        let locations = ToolLocations(commandDirectories: [], libreOfficeApps: [], applicationDirectories: [applications])
        #expect(ExternalTools.detect(in: locations, ignoring: []).iWorkApps == [.pages])
        #expect(ExternalTools.detect(in: locations, ignoring: ["iwork"]).iWorkApps.isEmpty)
        #expect(ExternalTools.detect(in: ToolLocations(commandDirectories: [], libreOfficeApps: []), ignoring: []).iWorkApps.isEmpty)
    }

    @Test func exportScriptTakesPathsAsArguments() {
        let source = URL(fileURLWithPath: "/tmp/My \"Report\".docx")
        let destination = URL(fileURLWithPath: "/tmp/My \"Report\".pages")
        let arguments = IWorkExport.arguments(source: source, destination: destination, app: .pages)
        #expect(arguments.first == "-e")
        #expect(Array(arguments.suffix(2)) == [source.path, destination.path])
        let script = IWorkExport.script(for: .pages)
        #expect(script.contains("application id \"com.apple.iWork.Pages\""))
        #expect(!script.contains("Report"))
        #expect(IWorkExport.script(for: .keynote).contains("com.apple.iWork.Keynote"))
    }

    @Test func explainsAutomationRefusals() {
        let source = URL(fileURLWithPath: "/tmp/Report.docx")
        let denied = IWorkExport.message(
            forScriptError: "0:312: execution error: Not authorized to send Apple events to Pages. (-1743)",
            source: source, app: .pages
        )
        #expect(denied.contains("Privacy & Security → Automation"))
        #expect(IWorkExport.message(forScriptError: "execution error: Can’t get application id \"com.apple.iWork.Pages\". (-10814)", source: source, app: .pages)
            == "Pages isn’t installed any more.")
        #expect(IWorkExport.message(forScriptError: "execution error: Pages got an error: The document can’t be opened. (-1)", source: source, app: .pages)
            .hasPrefix("Pages couldn’t save “Report.docx”"))
    }
}
