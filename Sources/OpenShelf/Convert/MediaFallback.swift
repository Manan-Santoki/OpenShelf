@preconcurrency import AVFoundation
import Foundation

/// When AVFoundation can't read a file that looks native (an MPEG-TS stream
/// named .mp4, a codec macOS doesn't decode), conversions and media tools try
/// again with ffmpeg, if it's installed.
///
/// Only errors that mean "AVFoundation can't open, parse or decode this"
/// switch over. A cancel never does, and neither do failures ffmpeg would hit
/// as well, such as a full disk or a folder that can't be written to.
enum MediaFallback {
    /// Runs `native`, and when it fails because AVFoundation can't read
    /// `source`, `fallback` with ffmpeg instead. Without ffmpeg, such a failure
    /// becomes an `UnreadableMediaError` that says ffmpeg might help.
    /// - Parameter source: The file, or nil when there are several (a merge).
    static func run<T>(
        _ source: URL?,
        tools: ExternalTools,
        native: () async throws -> T,
        fallback: (_ ffmpeg: URL) async throws -> T
    ) async throws -> T {
        do {
            return try await native()
        } catch {
            guard shouldUseFFmpeg(after: error, tools: tools), let ffmpeg = tools.ffmpeg else {
                if tools.ffmpeg == nil, !Task.isCancelled, isUnopenable(error) {
                    throw UnreadableMediaError(url: source)
                }
                throw error
            }
            return try await fallback(ffmpeg)
        }
    }

    /// Whether to retry with ffmpeg after `error`: never once the task is
    /// cancelled, never without ffmpeg, and only for errors that mean the
    /// source couldn't be read.
    static func shouldUseFFmpeg(after error: Error, tools: ExternalTools, isCancelled: Bool = Task.isCancelled) -> Bool {
        guard tools.ffmpeg != nil, !isCancelled else { return false }
        return isUnreadable(error)
    }

    /// Whether `error` says AVFoundation or Core Audio couldn't read or
    /// decode the source, so ffmpeg might.
    static func isUnreadable(_ error: Error) -> Bool {
        if error is CancellationError { return false }
        if isUnopenable(error) { return true }
        // No export preset accepts the source: its tracks are in codecs
        // AVFoundation can't decode.
        if case MediaToolError.cannotExport = error { return true }
        // afconvert (WAV, AIFF and FLAC) says "Couldn't open input file ('typ?')".
        if let failure = error as? ProcessFailure, failure.tool == "afconvert" {
            return coreAudioCodes.keys.contains { failure.message.contains("('\($0)')") }
        }

        let nsError = error as NSError
        switch nsError.domain {
        case AVFoundationErrorDomain:
            if unreadableCodes.contains(nsError.code) { return true }
            // "Unknown" and "export failed" are also how a reader gives up,
            // unless they wrap a file-system error, which ffmpeg would hit too.
            return genericCodes.contains(nsError.code) && !wrapsFileSystemError(nsError)
        case coreAudioDomain:
            return coreAudioCodes.values.contains(nsError.code)
        default:
            return false
        }
    }

    /// Errors that mean the file couldn't be opened or parsed at all, as
    /// opposed to failing partway through.
    static func isUnopenable(_ error: Error) -> Bool {
        let nsError = error as NSError
        return nsError.domain == AVFoundationErrorDomain && openCodes.contains(nsError.code)
    }

    // MARK: Codes

    /// AVErrorFileFormatNotRecognized (-11828), AVErrorFileFailedToParse
    /// (-11829, "Cannot Open") and AVErrorFailedToParse (-11853).
    private static let openCodes: Set<Int> = [-11828, -11829, -11853]

    /// The source can be opened but not decoded or exported: decode failed
    /// (-11821), invalid source media (-11822), no decoder (-11833), not
    /// supported for this asset (-11838), incompatible asset (-11848), media
    /// data or samples that won't load (-11849, -11881), undecodable data
    /// (-11855) and unsupported format (-11864).
    private static let unreadableCodes: Set<Int> = openCodes.union([-11821, -11822, -11833, -11838, -11848, -11849, -11855, -11864, -11881])

    /// AVErrorUnknown (-11800) and AVErrorExportFailed (-11820). An export
    /// session that can't read its source fails with -11800.
    private static let genericCodes: Set<Int> = [-11800, -11820]

    /// AVAudioFile reports Core Audio's file errors in this domain.
    private static let coreAudioDomain = "com.apple.coreaudio.avfaudio"

    /// kAudioFileUnsupportedFileTypeError, kAudioFileUnsupportedDataFormatError
    /// and kAudioFileInvalidFileError, by their four-character codes.
    private static let coreAudioCodes: [String: Int] = ["typ?": 1_954_115_647, "fmt?": 1_718_449_215, "dta?": 1_685_348_671]

    private static func wrapsFileSystemError(_ error: NSError) -> Bool {
        var current: NSError? = error.userInfo[NSUnderlyingErrorKey] as? NSError
        while let underlying = current {
            if underlying.domain == NSPOSIXErrorDomain || underlying.domain == NSCocoaErrorDomain { return true }
            current = underlying.userInfo[NSUnderlyingErrorKey] as? NSError
        }
        return false
    }
}

/// AVFoundation couldn't open a file and there's no ffmpeg to try instead.
struct UnreadableMediaError: LocalizedError, Equatable {
    /// The file, or nil when it was one of several.
    let url: URL?

    var errorDescription: String? {
        let file = url.map { "“\($0.lastPathComponent)”" } ?? "one of these files"
        return "macOS can’t read \(file). Installing ffmpeg in Settings → Extra formats may help."
    }
}
