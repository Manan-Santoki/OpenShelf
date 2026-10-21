import Foundation

/// Runs ffmpeg the way every conversion and media tool does: quietly, with
/// progress read from its `-progress` output, and cancellable.
enum FFmpegJob {
    /// Runs `ffmpeg` with `arguments` (inputs, filters and codecs: everything
    /// before the output file) and writes `output`. `duration` is the length
    /// of the output in seconds, for progress; 0 when unknown.
    static func run(
        _ ffmpeg: URL,
        _ arguments: [String],
        output: URL,
        duration: Double,
        progress: @escaping @Sendable (Double) -> Void
    ) async throws {
        // A process started after a cancel would run to the end.
        try Task.checkCancellation()
        let all: [String] = ["-y", "-nostdin", "-loglevel", "error"] + arguments + ["-progress", "pipe:1", "-nostats", output.path]
        try await ProcessRunner.run(ffmpeg, all) { line in
            // ffmpeg reports "out_time_us=<microseconds>" (also as out_time_ms, in µs).
            guard duration > 0, line.hasPrefix("out_time_us="), let micros = Double(line.dropFirst(12)) else { return }
            progress(min(max(micros / 1_000_000 / duration, 0), 1))
        }
    }

    /// Like `run`, but writes a hidden file beside `destination` and moves it
    /// into place only once ffmpeg has finished, as AVFoundation exports do
    /// (see `MediaTools.export`): a cancel or failure leaves nothing behind.
    static func export(
        _ ffmpeg: URL,
        _ arguments: [String],
        to destination: URL,
        duration: Double,
        progress: @escaping @Sendable (Double) -> Void
    ) async throws {
        let partial = MediaTools.partialURL(for: destination)
        do {
            try await run(ffmpeg, arguments, output: partial, duration: duration, progress: progress)
        } catch {
            MediaTools.discardPartial(partial)
            throw error
        }
        try MediaTools.moveIntoPlace(partial, destination: destination)
        progress(1)
    }

    /// `export`, trying each set of arguments in turn until one works: a
    /// lossless copy first, say, then a re-encode for codecs the container
    /// can't hold. A cancel stops at once.
    static func export(
        _ ffmpeg: URL,
        attempts: [[String]],
        to destination: URL,
        duration: Double,
        progress: @escaping @Sendable (Double) -> Void
    ) async throws {
        var lastError: Error = MediaToolError.failed("ffmpeg had nothing to do.")
        for arguments in attempts {
            do {
                try await export(ffmpeg, arguments, to: destination, duration: duration, progress: progress)
                return
            } catch is CancellationError {
                throw CancellationError()
            } catch {
                lastError = error
                if Task.isCancelled { throw CancellationError() }
            }
        }
        throw lastError
    }
}
