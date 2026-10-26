import Foundation

/// Which program converts a document. The built-in conversions always work;
/// Pandoc and LibreOffice, when installed, do some of them better.
enum DocumentEngine: Equatable {
    case builtIn
    case pandoc
    case libreOffice

    static func choose(for source: URL, to format: OutputFormat, tools: ExternalTools) -> DocumentEngine {
        let ext = source.pathExtension.lowercased()
        // Pandoc makes PDFs only through a typesetter.
        let pandocCanWrite = tools.pandoc != nil && (format != .pdf || tools.pdfEngine != nil)
        if MarkdownConversion.isMarkdown(source) {
            return pandocCanWrite && [.docx, .html, .epub, .rtf, .pdf].contains(format) ? .pandoc : .builtIn
        }
        switch format {
        case .pdf:
            // LibreOffice keeps a Word document's own page layout; Pandoc retypesets it.
            if tools.libreOffice != nil, ["docx", "doc", "odt", "rtf"].contains(ext) { return .libreOffice }
            if pandocCanWrite, PandocConversion.readers[ext] != nil, ext != "rtf" { return .pandoc }
        case .md, .epub:
            if pandocCanWrite, PandocConversion.readers[ext] != nil { return .pandoc }
        default:
            break
        }
        return .builtIn
    }
}

/// Converts documents with Pandoc (https://pandoc.org), when it's installed:
/// `brew install pandoc`. PDFs also need a typesetter such as MacTeX,
/// Tectonic or Typst.
enum PandocConversion {
    /// How long Pandoc gets before the conversion is abandoned. A LaTeX PDF
    /// can take a while the first time fonts are set up.
    static let timeout: Duration = .seconds(180)

    /// The PDF typesetters Pandoc can use, best first.
    static let pdfEngines = ["xelatex", "lualatex", "tectonic", "typst", "pdflatex", "weasyprint", "wkhtmltopdf"]

    /// Pandoc's reader for each kind of file it's given.
    static let readers: [String: String] = [
        "md": "markdown", "markdown": "markdown", "mdown": "markdown", "mkd": "markdown", "mkdn": "markdown",
        "docx": "docx", "odt": "odt", "html": "html", "htm": "html", "rtf": "rtf",
    ]

    static func writer(for format: OutputFormat) -> String? {
        switch format {
        case .docx: "docx"
        case .html: "html5"
        case .epub: "epub3"
        case .md: "gfm"
        case .rtf: "rtf"
        // Pandoc picks the writer that suits the typesetter.
        case .pdf: ""
        default: nil
        }
    }

    /// The command line for one conversion, or nil when Pandoc can't do it.
    static func arguments(source: URL, format: OutputFormat, output: URL, pdfEngine: URL?) -> [String]? {
        guard let reader = readers[source.pathExtension.lowercased()], let writer = writer(for: format) else { return nil }
        var arguments = [source.path, "--from", reader]
        if !writer.isEmpty { arguments += ["--to", writer] }
        // Pictures next to the document are found wherever Pandoc runs.
        arguments += ["--output", output.path, "--resource-path", source.deletingLastPathComponent().path]
        // A whole document rather than a fragment; Markdown has no such thing.
        if format != .md { arguments.append("--standalone") }
        if format == .html || format == .epub {
            // The page's title, without adding a heading to it.
            arguments += ["--metadata", "pagetitle=\(source.deletingPathExtension().lastPathComponent)"]
        }
        switch format {
        case .pdf:
            guard let pdfEngine else { return nil }
            arguments += ["--pdf-engine", pdfEngine.path]
            if pdfEngine.lastPathComponent.hasSuffix("latex") || pdfEngine.lastPathComponent == "tectonic" {
                // LaTeX's default margins are very wide; links are coloured instead of boxed.
                arguments += ["--variable", "geometry:margin=1in", "--variable", "colorlinks=true"]
            }
        case .md:
            arguments += ["--wrap", "none"]
        default:
            break
        }
        return arguments
    }

    static func convert(_ source: URL, to format: OutputFormat, tools: ExternalTools) async throws -> URL {
        guard let pandoc = tools.pandoc else { throw MissingToolError(name: "Pandoc") }
        let workspace = try PackageZip.makeWorkspace("pandoc")
        defer { try? FileManager.default.removeItem(at: workspace) }
        let output = workspace.appendingPathComponent("converted.\(format.fileExtension ?? "out")")
        guard let pathExtension = format.fileExtension,
              let arguments = arguments(source: source, format: format, output: output, pdfEngine: tools.pdfEngine)
        else {
            throw ConversionError.unsupported("\(source.pathExtension.uppercased()) to \(format.title)")
        }
        // A typesetter starts its own helpers, which it finds next to itself.
        let engineFolder = format == .pdf ? tools.pdfEngine?.deletingLastPathComponent().path : nil
        let environment = engineFolder.map { ["PATH": $0 + ":" + ChildEnvironment.searchPath] } ?? [:]

        try await withThrowingTaskGroup(of: Void.self) { group in
            group.addTask {
                try await ProcessRunner.run(pandoc, arguments, currentDirectory: workspace, extraEnvironment: environment)
            }
            group.addTask {
                try await Task.sleep(for: timeout)
                throw ConversionError.failed("Pandoc took too long to convert “\(source.lastPathComponent)”.")
            }
            // Whichever finishes first wins; the other is cancelled (which stops Pandoc).
            defer { group.cancelAll() }
            try await group.next()
        }

        guard FileManager.default.fileExists(atPath: output.path) else {
            throw ConversionError.failed("Pandoc couldn’t convert “\(source.lastPathComponent)”.")
        }
        let destination = OutputNaming.destination(for: source, pathExtension: pathExtension)
        try FileManager.default.moveItem(at: output, to: destination)
        return destination
    }
}
