import Foundation
import Testing
@testable import OpenShelf

/// ODT out and EPUB in, through Pandoc. Only the last tests need Pandoc
/// itself; the rest use a fake runner.
struct PandocFormatsTests {
    static let pandoc = URL(fileURLWithPath: "/opt/homebrew/bin/pandoc")
    static let tools = ExternalTools(ffmpeg: nil, cwebp: nil, pandoc: pandoc)
    static let none = ExternalTools(ffmpeg: nil, cwebp: nil)

    // MARK: Detection and the wheel

    @Test func epubIsABook() {
        #expect(FileKind(url: URL(fileURLWithPath: "/tmp/Novel.epub")) == .ebook)
        #expect(FileKind(url: URL(fileURLWithPath: "/tmp/Novel.EPUB")) == .ebook)
    }

    @Test func withoutPandocABookIsJustAFile() {
        #expect(ConversionCatalog.formats(for: URL(fileURLWithPath: "/tmp/Novel.epub"), tools: Self.none) == [.zip, .tar, .tgz])
        // No "needs Pandoc" toast for a book: its wheel isn't ZIP alone.
        #expect(MissingToolAdvice.blockingTool(for: [URL(fileURLWithPath: "/tmp/Novel.epub")], formats: [.zip, .tar, .tgz], tools: Self.none) == nil)
    }

    @Test func withPandocABookConverts() {
        let book = URL(fileURLWithPath: "/tmp/Novel.epub")
        #expect(ConversionCatalog.formats(for: book, tools: Self.tools) == [.docx, .md, .html, .odt, .txt, .zip])
        var withTeX = Self.tools
        withTeX.pdfEngine = URL(fileURLWithPath: "/Library/TeX/texbin/xelatex")
        #expect(ConversionCatalog.formats(for: book, tools: withTeX) == [.docx, .md, .html, .odt, .txt, .pdf, .zip])
    }

    @Test func odtIsOfferedOnlyWithPandoc() {
        for name in ["Notes.md", "Letter.docx", "Page.html", "Memo.rtf"] {
            let url = URL(fileURLWithPath: "/tmp/\(name)")
            #expect(!ConversionCatalog.formats(for: url, tools: Self.none).contains(.odt), "\(name)")
            #expect(ConversionCatalog.formats(for: url, tools: Self.tools).contains(.odt), "\(name)")
        }
        // Never to itself.
        #expect(!ConversionCatalog.formats(for: URL(fileURLWithPath: "/tmp/Letter.odt"), tools: Self.tools).contains(.odt))
    }

    @Test func odtGoesThroughPandoc() {
        for name in ["Notes.md", "Letter.docx", "Page.html", "Memo.rtf"] {
            #expect(DocumentEngine.choose(for: URL(fileURLWithPath: "/tmp/\(name)"), to: .odt, tools: Self.tools) == .pandoc, "\(name)")
        }
    }

    @Test func detectsPandocInEitherHomebrew() throws {
        for prefix in ["opt/homebrew/bin", "usr/local/bin"] {
            let folder = try ToolFolder("pandoc-\(prefix.hasPrefix("opt") ? "arm" : "intel")")
            defer { folder.remove() }
            try folder.executable("\(prefix)/pandoc")
            let locations = ToolLocations(
                commandDirectories: ["opt/homebrew/bin", "usr/local/bin"].map { folder.root.appendingPathComponent($0) },
                libreOfficeApps: []
            )
            #expect(ExternalTools.detect(in: locations, ignoring: []).pandoc?.path == folder.root.appendingPathComponent("\(prefix)/pandoc").path)
        }
        let empty = try ToolFolder("pandoc-none")
        defer { empty.remove() }
        #expect(ExternalTools.detect(in: ToolLocations(commandDirectories: [empty.root], libreOfficeApps: []), ignoring: []).pandoc == nil)
    }

    // MARK: Arguments

    @Test func bookArguments() throws {
        let book = URL(fileURLWithPath: "/Users/me/-Odd Bøok.epub")
        let out = URL(fileURLWithPath: "/tmp/w/converted.html")
        let html = try #require(PandocConversion.arguments(source: book, format: .html, output: out, pdfEngine: nil))
        #expect(html.first == "/Users/me/-Odd Bøok.epub")
        #expect(FakeToolRunner.value(after: "--from", in: html) == "epub")
        #expect(FakeToolRunner.value(after: "--to", in: html) == "html5")
        #expect(FakeToolRunner.value(after: "--output", in: html) == "/tmp/w/converted.html")
        #expect(html.contains("--embed-resources"))
        #expect(html.contains("--standalone"))

        let text = try #require(PandocConversion.arguments(source: book, format: .txt, output: out, pdfEngine: nil))
        #expect(FakeToolRunner.value(after: "--to", in: text) == "plain")
        #expect(!text.contains("--standalone"))
        #expect(!text.contains("--embed-resources"))

        let markdown = try #require(PandocConversion.arguments(source: book, format: .md, output: out, pdfEngine: nil))
        #expect(FakeToolRunner.value(after: "--to", in: markdown) == "gfm-raw_html")
        // Word to Markdown keeps its writer as it was.
        let word = try #require(PandocConversion.arguments(source: URL(fileURLWithPath: "/tmp/a.docx"), format: .md, output: out, pdfEngine: nil))
        #expect(FakeToolRunner.value(after: "--to", in: word) == "gfm")

        // A PDF needs a typesetter.
        #expect(PandocConversion.arguments(source: book, format: .pdf, output: out, pdfEngine: nil) == nil)
    }

    @Test func odtArguments() throws {
        let doc = URL(fileURLWithPath: "/Users/me/Notes ünï.md")
        let args = try #require(PandocConversion.arguments(source: doc, format: .odt, output: URL(fileURLWithPath: "/tmp/w/converted.odt"), pdfEngine: nil))
        #expect(args.first == "/Users/me/Notes ünï.md")
        #expect(FakeToolRunner.value(after: "--to", in: args) == "odt")
        #expect(args.contains("--standalone"))
        #expect(FakeToolRunner.value(after: "--resource-path", in: args) == "/Users/me")
    }

    // MARK: Converting, with a fake Pandoc

    @Test func savesNextToTheBookAndCleansUp() async throws {
        let folder = try ToolFolder("pandoc-fake")
        defer { folder.remove() }
        let book = try folder.file("-Odd Bøok.epub")
        try folder.file("-Odd Bøok.docx", contents: "taken")
        let workspace = Box<URL?>(nil)
        let fake = FakeToolRunner { invocation in
            let output = URL(fileURLWithPath: FakeToolRunner.value(after: "--output", in: invocation.arguments)!)
            workspace.value = invocation.currentDirectory
            try "converted".write(to: output, atomically: true, encoding: .utf8)
            return []
        }
        let result = try await PandocConversion.convert(book, to: .docx, tools: Self.tools, runner: fake.runner)
        #expect(result.lastPathComponent == "-Odd Bøok 2.docx")
        #expect(fake.invocations.count == 1)
        #expect(fake.invocations[0].executable == Self.pandoc)
        let dir = try #require(workspace.value)
        #expect(!FileManager.default.fileExists(atPath: dir.path))
    }

    @Test func aFailedRunLeavesNothing() async throws {
        let folder = try ToolFolder("pandoc-fail")
        defer { folder.remove() }
        let book = try folder.file("Book.epub")
        let fake = FakeToolRunner { _ in throw ProcessFailure(tool: "pandoc", message: "bad epub") }
        await #expect(throws: ProcessFailure.self) {
            _ = try await PandocConversion.convert(book, to: .md, tools: Self.tools, runner: fake.runner)
        }
        #expect(try FileManager.default.contentsOfDirectory(atPath: folder.root.path) == ["Book.epub"])
    }

    @Test func missingPandocRunsNothing() async throws {
        let fake = FakeToolRunner()
        await #expect(throws: MissingToolError(name: "Pandoc")) {
            _ = try await PandocConversion.convert(URL(fileURLWithPath: "/tmp/a.epub"), to: .md, tools: Self.none, runner: fake.runner)
        }
        #expect(fake.invocations.isEmpty)
    }

    // MARK: The real thing, where Pandoc is installed

    static var real: ExternalTools { ExternalTools.detect(ignoring: []) }

    @Test(.enabled(if: RealToolTests.enabled && real.pandoc != nil))
    func convertsForRealOnThisMac() async throws {
        let folder = try ToolFolder("pandoc-real")
        defer { folder.remove() }
        let markdown = try folder.file("-Notes é.md", contents: "# Chapter One\n\nSome *emphasis* and a list:\n\n- alpha\n- beta\n")

        // Markdown → ODT, then → EPUB, then the book → every format.
        let odt = try await Converter.convert(markdown, to: .odt, tools: Self.real) { _ in }
        #expect(odt[0].lastPathComponent == "-Notes é.odt")
        #expect(try Data(contentsOf: odt[0]).starts(with: Array("PK".utf8)))

        let epub = try await Converter.convert(markdown, to: .epub, tools: Self.real) { _ in }
        #expect(FileKind(url: epub[0]) == .ebook)
        for format in PandocConversion.formats(forBook: epub[0], tools: Self.real) where format != .pdf {
            let out = try await Converter.convert(epub[0], to: format, tools: Self.real) { _ in }
            #expect(out[0].pathExtension == format.rawValue)
            if [.md, .html, .txt].contains(format) {
                #expect(try String(contentsOf: out[0], encoding: .utf8).contains("alpha"), "\(format)")
            }
            if format == .md {
                #expect(!(try String(contentsOf: out[0], encoding: .utf8).contains("<span")))
            }
        }
        // Word → ODT too. The book already made "-Notes é.docx", so this
        // Word file is "-Notes é 2.docx", and nothing is overwritten.
        let docx = try await Converter.convert(markdown, to: .docx, tools: Self.real) { _ in }
        #expect(docx[0].lastPathComponent == "-Notes é 2.docx")
        let existing = Set(try FileManager.default.contentsOfDirectory(atPath: folder.root.path))
        let odt2 = try await Converter.convert(docx[0], to: .odt, tools: Self.real) { _ in }
        // "-Notes é 2.odt" came from the book, so this one gets another number.
        #expect(odt2[0].lastPathComponent == "-Notes é 2 2.odt")
        #expect(!existing.contains(odt2[0].lastPathComponent))
        #expect(try Data(contentsOf: odt2[0]).starts(with: Array("PK".utf8)))
    }
}
