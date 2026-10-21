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
    static func convert(
        _ source: URL,
        to format: OutputFormat,
        tools: ExternalTools,
        progress: @escaping @Sendable (Double) -> Void
    ) async throws -> [URL] {
        do {
            return try await run(source, to: format, tools: tools, progress: progress)
        } catch {
            throw FailureMessage.describe(error, source: source)
        }
    }

    private static func run(
        _ source: URL,
        to format: OutputFormat,
        tools: ExternalTools,
        progress: @escaping @Sendable (Double) -> Void
    ) async throws -> [URL] {
        switch format {
        case .zip:
            return [try await ArchiveConversion.zip([source])]
        case .extract:
            return [try await ArchiveConversion.extract(source)]
        default:
            break
        }

        switch FileKind(url: source) {
        case .image:
            return [try await ImageConversion.convert(source, to: format, tools: tools)]
        case .pdf:
            return try PDFConversion.convert(source, to: format)
        case .document:
            return [try await DocumentConversion.convert(source, to: format)]
        case .audio, .video:
            return [try await MediaConversion.convert(source, to: format, tools: tools, progress: progress)]
        case .subtitle:
            return [try SubtitleConversion.convert(source, to: format)]
        case .archive, .folder, .other:
            throw ConversionError.unsupported("\(source.pathExtension.uppercased()) to \(format.title)")
        }
    }
}
