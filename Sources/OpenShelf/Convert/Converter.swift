import Foundation

enum ConversionError: LocalizedError {
    case unsupported(String)
    case unreadable(URL)
    case failed(String)

    var errorDescription: String? {
        switch self {
        case .unsupported(let detail): "This conversion isn’t supported: \(detail)."
        case .unreadable(let url): "“\(url.lastPathComponent)” couldn’t be read."
        case .failed(let detail): detail
        }
    }
}

/// Converts one file into another format, saving the result next to it.
enum Converter {
    /// Returns the files written (several when, e.g., a multi-page PDF becomes images).
    /// - Parameter settings: How audio, video and images are encoded.
    static func convert(
        _ source: URL,
        to format: OutputFormat,
        tools: ExternalTools,
        settings: OutputSettings = .automatic,
        progress: @escaping @Sendable (Double) -> Void
    ) async throws -> [URL] {
        do {
            return try await run(source, to: format, tools: tools, settings: settings, progress: progress)
        } catch {
            throw FailureMessage.describe(error, source: source)
        }
    }

    private static func run(
        _ source: URL,
        to format: OutputFormat,
        tools: ExternalTools,
        settings: OutputSettings,
        progress: @escaping @Sendable (Double) -> Void
    ) async throws -> [URL] {
        switch format {
        case .zip:
            return [try await ArchiveConversion.zip([source])]
        case .tar:
            return [try await ArchiveConversion.tar([source], gzip: false)]
        case .tgz:
            return [try await ArchiveConversion.tar([source], gzip: true)]
        case .extract:
            return [try await ArchiveConversion.extract(source)]
        default:
            break
        }

        switch FileKind(url: source) {
        case .image:
            return [try await ImageConversion.convert(source, to: format, tools: tools, settings: settings.image)]
        case .pdf:
            return try PDFConversion.convert(source, to: format, imageQuality: settings.image.quality, progress: progress)
        case .document:
            return [try await DocumentConversion.convert(source, to: format, tools: tools)]
        case .spreadsheet:
            return try await SpreadsheetConversion.convert(source, to: format, tools: tools)
        case .presentation:
            return try await OfficeConversion.convert(source, to: format, tools: tools, progress: progress)
        case .audio, .video:
            return [try await MediaConversion.convert(source, to: format, tools: tools, settings: settings, progress: progress)]
        case .subtitle:
            return [try SubtitleConversion.convert(source, to: format)]
        case .archive, .folder, .other:
            throw ConversionError.unsupported("\(source.pathExtension.uppercased()) to \(format.title)")
        }
    }
}
