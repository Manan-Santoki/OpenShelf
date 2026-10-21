import Foundation

/// Office formats macOS can't read or write itself (PowerPoint, XLS, ODS…),
/// converted by LibreOffice when it's installed (`brew install --cask libreoffice`).
enum OfficeConversion {
    /// How long LibreOffice gets before the conversion is abandoned.
    static let timeout: Duration = .seconds(120)

    static func convert(_ source: URL, to format: OutputFormat, tools: ExternalTools) async throws -> [URL] {
        guard let soffice = tools.libreOffice else {
            throw ConversionError.unsupported("\(source.pathExtension.uppercased()) files need LibreOffice (brew install --cask libreoffice)")
        }
        let workspace = try PackageZip.makeWorkspace("office")
        defer { try? FileManager.default.removeItem(at: workspace) }
        let output = workspace.appendingPathComponent("out")
        try FileManager.default.createDirectory(at: output, withIntermediateDirectories: true)

        // Slides become images by way of a PDF, one picture per slide.
        if format == .jpg || format == .png {
            let pdf = try await run(soffice, source: source, filter: "pdf", extension: "pdf", workspace: workspace, output: output)
            let named = workspace.appendingPathComponent(source.deletingPathExtension().lastPathComponent + ".pdf")
            try FileManager.default.moveItem(at: pdf, to: named)
            return try PDFConversion.convert(named, to: format, savingNextTo: source, pageLabel: "Slide")
        }

        guard let pathExtension = format.fileExtension, let filter = filter(for: format) else {
            throw ConversionError.unsupported("\(source.pathExtension.uppercased()) to \(format.title)")
        }
        let converted = try await run(soffice, source: source, filter: filter, extension: pathExtension, workspace: workspace, output: output)
        let destination = OutputNaming.destination(for: source, pathExtension: pathExtension)
        try FileManager.default.moveItem(at: converted, to: destination)
        return [destination]
    }

    /// LibreOffice's `--convert-to` value for a format.
    static func filter(for format: OutputFormat) -> String? {
        switch format {
        case .pdf: "pdf"
        case .docx: "docx"
        case .xlsx: "xlsx"
        case .ods: "ods"
        // Comma-separated, double quotes, UTF-8 (76), first sheet.
        case .csv: "csv:Text - txt - csv (StarCalc):44,34,76,1"
        default: nil
        }
    }

    /// The command line for one conversion. A private profile keeps it from
    /// clashing with a LibreOffice the user already has open.
    static func arguments(source: URL, filter: String, profile: URL, output: URL) -> [String] {
        [
            "-env:UserInstallation=\(profile.absoluteString)",
            "--headless", "--norestore", "--nologo", "--nodefault", "--nolockcheck",
            "--convert-to", filter,
            "--outdir", output.path,
            source.path,
        ]
    }

    private static func run(_ soffice: URL, source: URL, filter: String, extension pathExtension: String, workspace: URL, output: URL) async throws -> URL {
        let profile = workspace.appendingPathComponent("profile", isDirectory: true)
        let arguments = arguments(source: source, filter: filter, profile: profile, output: output)

        try await withThrowingTaskGroup(of: Void.self) { group in
            group.addTask { try await ProcessRunner.run(soffice, arguments) }
            group.addTask {
                try await Task.sleep(for: timeout)
                throw ConversionError.failed("LibreOffice took too long to convert “\(source.lastPathComponent)”.")
            }
            // Whichever finishes first wins; the other is cancelled (which stops LibreOffice).
            defer { group.cancelAll() }
            try await group.next()
        }

        let expected = output.appendingPathComponent(source.deletingPathExtension().lastPathComponent + "." + pathExtension)
        if FileManager.default.fileExists(atPath: expected.path) { return expected }
        // LibreOffice exits successfully even when it couldn't read the file.
        if let produced = try FileManager.default.contentsOfDirectory(at: output, includingPropertiesForKeys: nil).first {
            return produced
        }
        throw ConversionError.failed("LibreOffice couldn’t convert “\(source.lastPathComponent)”.")
    }
}
