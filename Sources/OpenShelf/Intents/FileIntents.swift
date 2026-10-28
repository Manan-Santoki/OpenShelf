import AppIntents
import Foundation

// Shortcuts actions for converting and the tools that need no window. They
// run inside the app, entirely on this Mac, and save results next to the
// original like the wheels do. The titles and descriptions are read when the
// app is built (see scripts/appintents-metadata.sh), so they stay literals.

// MARK: - Choices

/// The Convert wheel's formats, as a choice in Shortcuts. App Intents reads
/// these titles at build time, so they're spelled out rather than taken from
/// `OutputFormat.title`; tests check the two lists match.
enum IntentFormat: String, AppEnum, CaseIterable {
    case png, jpg, webp, heic, tiff, avif, bmp, gif
    case pdf, docx, txt, rtf, html, md, epub, pptx
    case xlsx, csv, tsv, ods
    case mp4, mov, m4a, mp3, wav, aiff, flac, ogg, opus, wma
    case mkv, webm, avi, wmv
    case srt, vtt
    case zip, tar, tgz, extract
    case pages, numbers, key

    static var typeDisplayRepresentation: TypeDisplayRepresentation { "Format" }

    static var caseDisplayRepresentations: [IntentFormat: DisplayRepresentation] {
        [
            .png: "PNG", .jpg: "JPG", .webp: "WEBP", .heic: "HEIC", .tiff: "TIFF", .avif: "AVIF", .bmp: "BMP", .gif: "GIF",
            .pdf: "PDF", .docx: "DOCX", .txt: "TXT", .rtf: "RTF", .html: "HTML", .md: "MD", .epub: "EPUB", .pptx: "PPTX",
            .xlsx: "XLSX", .csv: "CSV", .tsv: "TSV", .ods: "ODS",
            .mp4: "MP4", .mov: "MOV", .m4a: "M4A", .mp3: "MP3", .wav: "WAV", .aiff: "AIFF", .flac: "FLAC",
            .ogg: "OGG", .opus: "OPUS", .wma: "WMA",
            .mkv: "MKV", .webm: "WEBM", .avi: "AVI", .wmv: "WMV",
            .srt: "SRT", .vtt: "VTT",
            .zip: "ZIP", .tar: "TAR", .tgz: "TAR.GZ", .extract: "UNZIP",
            .pages: "PAGES", .numbers: "NUMBERS", .key: "KEY",
        ]
    }

    /// Every case has an `OutputFormat` of the same name; tests check it.
    var outputFormat: OutputFormat {
        OutputFormat(rawValue: rawValue)!
    }
}

/// How hard Compress squeezes, matching the PDF Compress tool's levels.
enum IntentCompression: String, AppEnum, CaseIterable {
    case light, balanced, strong

    static var typeDisplayRepresentation: TypeDisplayRepresentation { "Compression" }

    static var caseDisplayRepresentations: [IntentCompression: DisplayRepresentation] {
        [.light: "Light", .balanced: "Balanced", .strong: "Strong"]
    }

    var pdfLevel: PDFCompressionLevel {
        switch self {
        case .light: .light
        case .balanced: .balanced
        case .strong: .strong
        }
    }

    /// Image quality, 10–100, as on the image Compress tool's slider.
    var imageQuality: Double {
        switch self {
        case .light: 85
        case .balanced: 70
        case .strong: 45
        }
    }
}

/// What Extract Text saves, matching the OCR tool's choice.
enum IntentTextOutput: String, AppEnum, CaseIterable {
    case text, searchablePDF

    static var typeDisplayRepresentation: TypeDisplayRepresentation { "Text Output" }

    static var caseDisplayRepresentations: [IntentTextOutput: DisplayRepresentation] {
        [.text: "Text File", .searchablePDF: "Searchable PDF"]
    }

    var ocrOutput: OCR.Output {
        switch self {
        case .text: .text
        case .searchablePDF: .searchablePDF
        }
    }
}

// MARK: - Convert

struct ConvertFilesIntent: AppIntent {
    static var title: LocalizedStringResource { "Convert Files" }
    static var description: IntentDescription {
        IntentDescription("Converts files to another format on this Mac, saving each one next to the original. Files already in that format are passed on unchanged; ZIP, TAR and TAR.GZ put all the files in one archive.")
    }
    static var openAppWhenRun: Bool { false }

    @Parameter(title: "Files", description: "The files to convert.", supportedTypeIdentifiers: ["public.item"], inputConnectionBehavior: .connectToPreviousIntentResult)
    var files: [IntentFile]

    @Parameter(title: "Format", description: "The format to convert to.")
    var format: IntentFormat

    static var parameterSummary: some ParameterSummary {
        Summary("Convert \(\.$files) to \(\.$format)")
    }

    @MainActor
    func perform() async throws -> some IntentResult & ReturnsValue<[IntentFile]> {
        let format = format.outputFormat
        let settings = Preferences.shared.output
        let outputs = try await IntentFiles.withURLs(files) { urls in
            try await Self.convert(urls, to: format, settings: settings)
        }
        return .result(value: IntentFiles.results(outputs))
    }

    private static func convert(_ urls: [URL], to format: OutputFormat, settings: OutputSettings) async throws -> [URL] {
        switch format {
        case .zip: return [try await ArchiveConversion.zip(urls)]
        case .tar: return [try await ArchiveConversion.tar(urls, gzip: false)]
        case .tgz: return [try await ArchiveConversion.tar(urls, gzip: true)]
        default: break
        }
        let tools = ExternalTools.current
        return try await IntentFiles.each(urls) { url in
            let available = ConversionCatalog.formats(for: url, tools: tools)
            switch IntentRouting.convertStep(pathExtension: url.pathExtension, to: format, available: available) {
            case .keep:
                return [url]
            case .convert:
                return try await Converter.convert(url, to: format, tools: tools, settings: settings) { _ in }
            case .unsupported:
                throw IntentFailure(IntentRouting.unsupportedMessage(name: url.lastPathComponent, format: format, available: available))
            }
        }
    }
}

// MARK: - Tools

struct CompressFilesIntent: AppIntent {
    static var title: LocalizedStringResource { "Compress Images and PDFs" }
    static var description: IntentDescription {
        IntentDescription("Saves smaller copies of images and PDFs next to the originals. A file that can’t get any smaller is passed on unchanged.")
    }
    static var openAppWhenRun: Bool { false }

    @Parameter(title: "Files", description: "Images or PDFs.", supportedTypeIdentifiers: ["public.image", "com.adobe.pdf"], inputConnectionBehavior: .connectToPreviousIntentResult)
    var files: [IntentFile]

    @Parameter(title: "Compression", description: "How hard to squeeze.", default: .balanced)
    var compression: IntentCompression

    static var parameterSummary: some ParameterSummary {
        Summary("Compress \(\.$files) (\(\.$compression))")
    }

    @MainActor
    func perform() async throws -> some IntentResult & ReturnsValue<[IntentFile]> {
        let compression = compression
        let outputs = try await IntentFiles.withURLs(files, tool: .compress) { urls in
            try await IntentFiles.each(urls) { url in
                if FileKind(url: url) == .pdf {
                    return [try PDFTools.compress(url, level: compression.pdfLevel) ?? url]
                }
                let image = try EditableImage.load(url)
                return [try ImageCompressor.save(image, options: CompressOptions(quality: compression.imageQuality)) ?? url]
            }
        }
        return .result(value: IntentFiles.results(outputs))
    }
}

struct ExtractTextIntent: AppIntent {
    static var title: LocalizedStringResource { "Extract Text (OCR)" }
    static var description: IntentDescription {
        IntentDescription("Reads the text in images and scanned PDFs on this Mac, and saves it as a text file or a searchable PDF next to each one.")
    }
    static var openAppWhenRun: Bool { false }

    @Parameter(title: "Files", description: "Images or PDFs.", supportedTypeIdentifiers: ["public.image", "com.adobe.pdf"], inputConnectionBehavior: .connectToPreviousIntentResult)
    var files: [IntentFile]

    @Parameter(title: "Save As", description: "A text file, or a PDF whose text can be searched and selected.", default: .text)
    var output: IntentTextOutput

    static var parameterSummary: some ParameterSummary {
        Summary("Extract text from \(\.$files) as \(\.$output)")
    }

    @MainActor
    func perform() async throws -> some IntentResult & ReturnsValue<[IntentFile]> {
        let output = output.ocrOutput
        let outputs = try await IntentFiles.withURLs(files, tool: .extractText) { urls in
            try await IntentFiles.each(urls) { url in
                [try await OCR.run(url, output: output)]
            }
        }
        return .result(value: IntentFiles.results(outputs))
    }
}

struct RemoveBackgroundIntent: AppIntent {
    static var title: LocalizedStringResource { "Remove Background" }
    static var description: IntentDescription {
        IntentDescription("Cuts the subject out of each photo on this Mac and saves it as a transparent PNG next to the original.")
    }
    static var openAppWhenRun: Bool { false }

    @Parameter(title: "Images", description: "Photos of people, animals or objects.", supportedTypeIdentifiers: ["public.image"], inputConnectionBehavior: .connectToPreviousIntentResult)
    var files: [IntentFile]

    static var parameterSummary: some ParameterSummary {
        Summary("Remove the background from \(\.$files)")
    }

    @MainActor
    func perform() async throws -> some IntentResult & ReturnsValue<[IntentFile]> {
        let outputs = try await IntentFiles.withURLs(files, tool: .removeBackground) { urls in
            try await IntentFiles.each(urls) { url in
                let image = try EditableImage.load(url)
                return [try BackgroundRemover.save(try BackgroundRemover.removeBackground(from: image.cgImage), of: image)]
            }
        }
        return .result(value: IntentFiles.results(outputs))
    }
}

struct CombinePDFIntent: AppIntent {
    static var title: LocalizedStringResource { "Combine into PDF" }
    static var description: IntentDescription {
        IntentDescription("Combines PDFs and images, in order, into one PDF named “Combined” next to the first file.")
    }
    static var openAppWhenRun: Bool { false }

    @Parameter(title: "Files", description: "PDFs and images, in order.", supportedTypeIdentifiers: ["public.image", "com.adobe.pdf"], inputConnectionBehavior: .connectToPreviousIntentResult)
    var files: [IntentFile]

    static var parameterSummary: some ParameterSummary {
        Summary("Combine \(\.$files) into a PDF")
    }

    @MainActor
    func perform() async throws -> some IntentResult & ReturnsValue<IntentFile> {
        let output = try await IntentFiles.withURLs(files, tool: .combinePDF) { urls in
            try await PDFTools.combine(urls)
        }
        return .result(value: IntentFile(fileURL: output))
    }
}

struct RemoveAudioIntent: AppIntent {
    static var title: LocalizedStringResource { "Remove Audio from Videos" }
    static var description: IntentDescription {
        IntentDescription("Saves a silent copy of each video next to the original.")
    }
    static var openAppWhenRun: Bool { false }

    @Parameter(title: "Videos", description: "The videos to mute.", supportedTypeIdentifiers: ["public.movie"], inputConnectionBehavior: .connectToPreviousIntentResult)
    var files: [IntentFile]

    static var parameterSummary: some ParameterSummary {
        Summary("Remove audio from \(\.$files)")
    }

    @MainActor
    func perform() async throws -> some IntentResult & ReturnsValue<[IntentFile]> {
        let outputs = try await IntentFiles.withURLs(files, tool: .removeAudio) { urls in
            try await IntentFiles.each(urls) { url in
                [try await MediaTools.mute(url) { _ in }]
            }
        }
        return .result(value: IntentFiles.results(outputs))
    }
}

struct ReadQRCodeIntent: AppIntent {
    static var title: LocalizedStringResource { "Read QR Codes" }
    static var description: IntentDescription {
        IntentDescription("Reads the QR, Aztec, Data Matrix and PDF417 codes in images, returning each one’s text. Returns nothing when there are none.")
    }
    static var openAppWhenRun: Bool { false }

    @Parameter(title: "Images", description: "Images that show a code.", supportedTypeIdentifiers: ["public.image"], inputConnectionBehavior: .connectToPreviousIntentResult)
    var files: [IntentFile]

    static var parameterSummary: some ParameterSummary {
        Summary("Read the codes in \(\.$files)")
    }

    @MainActor
    func perform() async throws -> some IntentResult & ReturnsValue<[String]> {
        // Nothing is saved, so files don't need a folder OpenShelf can write to.
        let codes = try await IntentFiles.withURLs(files, tool: .readQR, needsWritableFolder: false) { urls in
            try await QRReader.read(urls)
        }
        return .result(value: codes)
    }
}

// MARK: - Shelves

struct AddToShelfIntent: AppIntent {
    static var title: LocalizedStringResource { "Add to Shelf" }
    static var description: IntentDescription {
        IntentDescription("Puts files on the shelf you used most recently, or on a new shelf, and passes them on.")
    }
    static var openAppWhenRun: Bool { false }

    @Parameter(title: "Files", description: "The files to put on a shelf.", supportedTypeIdentifiers: ["public.item"], inputConnectionBehavior: .connectToPreviousIntentResult)
    var files: [IntentFile]

    @Parameter(title: "New Shelf", description: "Put the files on a new shelf instead of the most recent one.", default: false)
    var newShelf: Bool

    static var parameterSummary: some ParameterSummary {
        Summary("Add \(\.$files) to a shelf") {
            \.$newShelf
        }
    }

    @MainActor
    func perform() async throws -> some IntentResult & ReturnsValue<[IntentFile]> {
        // A shelf holds references, so files without a place on disk are
        // kept somewhere that outlasts the shortcut.
        let urls = try await IntentFiles.withURLs(files, needsWritableFolder: false, stagingFolder: IntentFiles.keptFolder) { $0 }
        guard let shelves = await IntentShelves.ready() else {
            throw IntentFailure("OpenShelf is still starting up. Try again in a moment.")
        }
        if newShelf {
            shelves.newShelf(with: urls)
        } else {
            shelves.addToRecentShelf(urls)
        }
        return .result(value: IntentFiles.results(urls))
    }
}
