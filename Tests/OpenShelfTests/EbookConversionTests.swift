import Foundation
import Testing
@testable import OpenShelf

/// Books through calibre's ebook-convert. Only the last test needs calibre
/// itself; the rest use a fake runner.
struct EbookConversionTests {
    static let ebookConvert = URL(fileURLWithPath: "/Applications/calibre.app/Contents/MacOS/ebook-convert")
    static let calibre = ExternalTools(ffmpeg: nil, cwebp: nil, ebookConvert: ebookConvert)
    static let none = ExternalTools(ffmpeg: nil, cwebp: nil)

    // MARK: Detection

    @Test func findsCalibreInTheAppOrOnThePath() throws {
        let folder = try ToolFolder("calibre")
        defer { folder.remove() }
        try folder.executable("Applications/calibre.app/Contents/MacOS/ebook-convert")
        try folder.executable("bin/ebook-convert")
        let app = folder.root.appendingPathComponent("Applications/calibre.app")
        let bin = folder.root.appendingPathComponent("bin")

        // The app's own copy first.
        var locations = ToolLocations(commandDirectories: [bin], libreOfficeApps: [], calibreApps: [app])
        #expect(ExternalTools.detect(in: locations, ignoring: []).ebookConvert?.path == app.appendingPathComponent("Contents/MacOS/ebook-convert").path)

        // Homebrew's link (Apple silicon or Intel) when there's no app.
        locations.calibreApps = [folder.root.appendingPathComponent("Nowhere/calibre.app")]
        #expect(ExternalTools.detect(in: locations, ignoring: []).ebookConvert?.path == bin.appendingPathComponent("ebook-convert").path)

        #expect(ExternalTools.detect(in: locations, ignoring: ["calibre"]).ebookConvert == nil)
    }

    @Test func missingCalibreIsMissing() throws {
        let folder = try ToolFolder("calibre-none")
        defer { folder.remove() }
        let tools = ExternalTools.detect(in: ToolLocations(commandDirectories: [folder.root], libreOfficeApps: [], calibreApps: [folder.root.appendingPathComponent("calibre.app")]), ignoring: [])
        #expect(tools.ebookConvert == nil)
        #expect(OptionalTool.calibre.state(in: tools) == .missing)
        #expect(OptionalTool.calibre.state(in: Self.calibre) == .installed(Self.ebookConvert))
    }

    // MARK: The wheel

    @Test func booksAreBooks() {
        for name in ["a.epub", "b.mobi", "c.azw3", "d.AZW"] {
            #expect(FileKind(url: URL(fileURLWithPath: "/tmp/\(name)")) == .ebook, "\(name)")
        }
    }

    @Test func withoutCalibreKindleBooksAreJustFiles() {
        for name in ["b.mobi", "c.azw3"] {
            #expect(ConversionCatalog.formats(for: URL(fileURLWithPath: "/tmp/\(name)"), tools: Self.none) == [.zip, .tar, .tgz])
        }
        #expect(!ConversionCatalog.formats(for: URL(fileURLWithPath: "/tmp/a.pdf"), tools: Self.none).contains(.mobi))
    }

    @Test func withCalibreBooksConvert() {
        #expect(ConversionCatalog.formats(for: URL(fileURLWithPath: "/tmp/b.mobi"), tools: Self.calibre) == [.epub, .azw3, .pdf, .zip])
        #expect(ConversionCatalog.formats(for: URL(fileURLWithPath: "/tmp/c.azw3"), tools: Self.calibre) == [.epub, .mobi, .pdf, .zip])
        #expect(ConversionCatalog.formats(for: URL(fileURLWithPath: "/tmp/a.epub"), tools: Self.calibre) == [.mobi, .azw3, .pdf, .zip])
        let pdf = ConversionCatalog.formats(for: URL(fileURLWithPath: "/tmp/a.pdf"), tools: Self.calibre)
        #expect(pdf.suffix(3) == [.mobi, .azw3, .zip])
        #expect(pdf.count <= ConversionCatalog.maxWedges)
    }

    @Test func calibreAndPandocTogether() {
        var both = Self.calibre
        both.pandoc = URL(fileURLWithPath: "/opt/homebrew/bin/pandoc")
        let formats = ConversionCatalog.formats(for: URL(fileURLWithPath: "/tmp/a.epub"), tools: both)
        #expect(formats == [.mobi, .azw3, .pdf, .docx, .md, .html, .odt, .txt, .zip])
        // calibre makes the PDF (no typesetter needed); Pandoc the rest.
        #expect(EbookConversion.handles(URL(fileURLWithPath: "/tmp/a.epub"), to: .pdf, tools: both))
        #expect(!EbookConversion.handles(URL(fileURLWithPath: "/tmp/a.epub"), to: .docx, tools: both))
        // PDF → EPUB stays OpenShelf's own.
        #expect(!EbookConversion.handles(URL(fileURLWithPath: "/tmp/a.pdf"), to: .epub, tools: both))
    }

    // MARK: Arguments and progress

    @Test func argumentsAreTwoAbsolutePaths() {
        let source = URL(fileURLWithPath: "/Users/me/-Mÿ Book.epub")
        let output = URL(fileURLWithPath: "/tmp/w/converted.mobi")
        #expect(EbookConversion.arguments(source: source, output: output) == ["/Users/me/-Mÿ Book.epub", "/tmp/w/converted.mobi"])
        #expect(EbookConversion.environment == ["QTWEBENGINE_CHROMIUM_FLAGS": "--disable-gpu"])
    }

    @Test func readsProgress() {
        #expect(EbookConversion.fraction(fromProgressLine: "34% Running transforms on e-book...") == 0.34)
        #expect(EbookConversion.fraction(fromProgressLine: "100% Output saved") == 1)
        #expect(EbookConversion.fraction(fromProgressLine: "Python function terminated") == nil)
        #expect(EbookConversion.fraction(fromProgressLine: "") == nil)
    }

    // MARK: Converting, with a fake calibre

    @Test func savesNextToTheBookAndCleansUp() async throws {
        let folder = try ToolFolder("calibre-fake")
        defer { folder.remove() }
        let book = try folder.file("-Mÿ Book.epub")
        try folder.file("-Mÿ Book.mobi", contents: "taken")
        let workspace = Box<URL?>(nil)
        let fake = FakeToolRunner { invocation in
            workspace.value = invocation.currentDirectory
            try "book".write(to: URL(fileURLWithPath: invocation.arguments[1]), atomically: true, encoding: .utf8)
            return ["1% Converting input to HTML...", "34% Running transforms on e-book...", "67% Running MOBI Output plugin"]
        }
        let reported = Box<[Double]>([])
        let result = try await EbookConversion.convert(book, to: .mobi, tools: Self.calibre, runner: fake.runner) { reported.value.append($0) }

        #expect(result.lastPathComponent == "-Mÿ Book 2.mobi")
        #expect(reported.value == [0.01, 0.34, 0.67, 1])
        #expect(fake.invocations.count == 1)
        #expect(fake.invocations[0].executable == Self.ebookConvert)
        #expect(fake.invocations[0].arguments[0] == book.path)
        #expect(fake.invocations[0].extraEnvironment["QTWEBENGINE_CHROMIUM_FLAGS"] == "--disable-gpu")
        let dir = try #require(workspace.value)
        #expect(!FileManager.default.fileExists(atPath: dir.path))
    }

    @Test func aFailureLeavesNothing() async throws {
        let folder = try ToolFolder("calibre-fail")
        defer { folder.remove() }
        let book = try folder.file("Book.azw3")
        let fake = FakeToolRunner { invocation in
            // A half-written file, then the tool fails.
            try "half".write(to: URL(fileURLWithPath: invocation.arguments[1]), atomically: true, encoding: .utf8)
            throw ProcessFailure(tool: "ebook-convert", message: "DRM")
        }
        await #expect(throws: ProcessFailure.self) {
            _ = try await EbookConversion.convert(book, to: .epub, tools: Self.calibre, runner: fake.runner) { _ in }
        }
        #expect(try FileManager.default.contentsOfDirectory(atPath: folder.root.path) == ["Book.azw3"])
    }

    @Test func aCrashIsRetriedOnceAndAnErrorIsNot() async throws {
        let folder = try ToolFolder("calibre-crash")
        defer { folder.remove() }
        let book = try folder.file("Book.epub")
        let attempts = Box(0)
        let crashesOnce = FakeToolRunner { invocation in
            attempts.value += 1
            if attempts.value == 1 { throw ProcessFailure(tool: "ebook-convert", message: "Rendering 2 HTML files", crashed: true) }
            try "pdf".write(to: URL(fileURLWithPath: invocation.arguments[1]), atomically: true, encoding: .utf8)
            return []
        }
        let pdf = try await EbookConversion.convert(book, to: .pdf, tools: Self.calibre, runner: crashesOnce.runner) { _ in }
        #expect(pdf.lastPathComponent == "Book.pdf")
        #expect(crashesOnce.invocations.count == 2)

        // Crashing every time: two tries, then a plain "crashed".
        let alwaysCrashes = FakeToolRunner { _ in throw ProcessFailure(tool: "ebook-convert", message: "Note", crashed: true) }
        await #expect(throws: ProcessFailure.self) {
            _ = try await EbookConversion.convert(book, to: .mobi, tools: Self.calibre, runner: alwaysCrashes.runner) { _ in }
        }
        #expect(alwaysCrashes.invocations.count == 2)
        #expect(ProcessFailure(tool: "ebook-convert", message: "Note", crashed: true).errorDescription == "ebook-convert crashed.")

        // An ordinary error (DRM, a damaged book) isn't tried again.
        let fails = FakeToolRunner { _ in throw ProcessFailure(tool: "ebook-convert", message: "DRM") }
        await #expect(throws: ProcessFailure.self) {
            _ = try await EbookConversion.convert(book, to: .mobi, tools: Self.calibre, runner: fails.runner) { _ in }
        }
        #expect(fails.invocations.count == 1)
    }

    @Test func aRealCrashIsReportedAsOne() async throws {
        // A child killed by a signal is a crash; exiting with 1 isn't.
        await #expect {
            try await ProcessRunner.run(URL(fileURLWithPath: "/bin/sh"), ["-c", "kill -KILL $$"])
        } throws: { ($0 as? ProcessFailure)?.crashed == true }
        await #expect {
            try await ProcessRunner.run(URL(fileURLWithPath: "/bin/sh"), ["-c", "exit 1"])
        } throws: { ($0 as? ProcessFailure)?.crashed == false }
    }

    @Test func cancellingLeavesNothing() async throws {
        let folder = try ToolFolder("calibre-cancel")
        defer { folder.remove() }
        let book = try folder.file("Book.epub")
        let fake = FakeToolRunner { _ in throw CancellationError() }
        await #expect(throws: CancellationError.self) {
            _ = try await EbookConversion.convert(book, to: .pdf, tools: Self.calibre, runner: fake.runner) { _ in }
        }
        #expect(try FileManager.default.contentsOfDirectory(atPath: folder.root.path) == ["Book.epub"])
    }

    @Test func missingCalibreRunsNothing() async throws {
        let fake = FakeToolRunner()
        await #expect(throws: MissingToolError(name: "calibre")) {
            _ = try await EbookConversion.convert(URL(fileURLWithPath: "/tmp/a.mobi"), to: .epub, tools: Self.none, runner: fake.runner) { _ in }
        }
        #expect(fake.invocations.isEmpty)
    }

    // MARK: The real thing, where calibre is installed

    static var real: ExternalTools { ExternalTools.detect(ignoring: []) }

    @Test(.enabled(if: RealToolTests.enabled && real.ebookConvert != nil), .timeLimit(.minutes(5)))
    func convertsBooksOnThisMac() async throws {
        let folder = try ToolFolder("calibre-real")
        defer { folder.remove() }
        // A tiny EPUB made by OpenShelf's own writer.
        let epub = folder.root.appendingPathComponent("-Tïny Book.epub")
        try EpubWriter.write(title: "Tiny Book", chapters: [EpubChapter(title: "One", bodyXHTML: "<p>Hello from the first chapter.</p>")], images: [:], to: epub)

        let mobi = try await Converter.convert(epub, to: .mobi, tools: Self.real) { _ in }
        #expect(mobi[0].lastPathComponent == "-Tïny Book.mobi")
        #expect(try Data(contentsOf: mobi[0]).count > 1000)

        let azw3 = try await Converter.convert(mobi[0], to: .azw3, tools: Self.real) { _ in }
        #expect(azw3[0].pathExtension == "azw3")

        let back = try await Converter.convert(azw3[0], to: .epub, tools: Self.real) { _ in }
        #expect(back[0].lastPathComponent == "-Tïny Book 2.epub")

        let pdf = try await Converter.convert(epub, to: .pdf, tools: Self.real) { _ in }
        #expect(try Data(contentsOf: pdf[0]).starts(with: Array("%PDF".utf8)))

        let fromPDF = try await Converter.convert(pdf[0], to: .mobi, tools: Self.real) { _ in }
        #expect(fromPDF[0].lastPathComponent == "-Tïny Book 2.mobi")
    }
}
