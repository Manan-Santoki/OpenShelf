@preconcurrency import AVFoundation
import Foundation

/// Audio and video, using AVFoundation and the built-in `afconvert` where they
/// can, and ffmpeg (when installed) for formats Apple doesn't encode.
enum MediaConversion {
    private static let afconvert = URL(fileURLWithPath: "/usr/bin/afconvert")

    static func convert(
        _ source: URL,
        to format: OutputFormat,
        tools: ExternalTools,
        progress: @escaping @Sendable (Double) -> Void
    ) async throws -> URL {
        guard let pathExtension = format.fileExtension else {
            throw ConversionError.unsupported("media to \(format.title)")
        }
        let destination = OutputNaming.destination(for: source, pathExtension: pathExtension)

        switch format {
        case .mp4:
            try await export(source, to: destination, preset: AVAssetExportPresetHighestQuality, fileType: .mp4, progress: progress)
        case .mov:
            try await export(source, to: destination, preset: AVAssetExportPresetHighestQuality, fileType: .mov, progress: progress)
        case .m4a:
            try await requireAudio(in: source)
            try await export(source, to: destination, preset: AVAssetExportPresetAppleM4A, fileType: .m4a, progress: progress)
        case .wav:
            try await ProcessRunner.run(afconvert, ["-f", "WAVE", "-d", "LEI16", source.path, destination.path])
        case .aiff:
            try await ProcessRunner.run(afconvert, ["-f", "AIFF", "-d", "BEI16", source.path, destination.path])
        case .flac:
            try await ProcessRunner.run(afconvert, ["-f", "flac", "-d", "flac", source.path, destination.path])
        case .mp3:
            try await requireAudio(in: source)
            try await ffmpeg(tools, source: source, destination: destination, arguments: ["-vn", "-codec:a", "libmp3lame", "-q:a", "2"], progress: progress)
        case .gif:
            // Two-pass palette for clean colours; capped at 720 px wide and 12 fps to keep files small.
            let filter = "fps=12,scale='min(720,iw)':-2:flags=lanczos,split[a][b];[a]palettegen[p];[b][p]paletteuse"
            try await ffmpeg(tools, source: source, destination: destination, arguments: ["-vf", filter, "-loop", "0"], progress: progress)
        default:
            throw ConversionError.unsupported("media to \(format.title)")
        }
        return destination
    }

    private static func requireAudio(in source: URL) async throws {
        guard try await !AVURLAsset(url: source).loadTracks(withMediaType: .audio).isEmpty else {
            throw ConversionError.failed("“\(source.lastPathComponent)” has no audio track.")
        }
    }

    private static func export(
        _ source: URL,
        to destination: URL,
        preset: String,
        fileType: AVFileType,
        progress: @escaping @Sendable (Double) -> Void
    ) async throws {
        let asset = AVURLAsset(url: source)
        guard let session = AVAssetExportSession(asset: asset, presetName: preset) else {
            throw ConversionError.failed("This file can’t be exported as \(destination.pathExtension.uppercased()).")
        }
        session.outputURL = destination
        session.outputFileType = fileType
        // The session is thread-safe for these calls but isn't marked Sendable.
        let box = ExportSessionBox(session: session)

        let reporter = Task {
            while !Task.isCancelled {
                progress(Double(box.session.progress))
                try? await Task.sleep(for: .milliseconds(200))
            }
        }
        defer { reporter.cancel() }

        await withTaskCancellationHandler {
            await box.session.export()
        } onCancel: {
            box.session.cancelExport()
        }
        try Task.checkCancellation()
        guard session.status == .completed else {
            throw session.error ?? ConversionError.failed("Export failed.")
        }
    }

    private static func ffmpeg(
        _ tools: ExternalTools,
        source: URL,
        destination: URL,
        arguments: [String],
        progress: @escaping @Sendable (Double) -> Void
    ) async throws {
        guard let ffmpeg = tools.ffmpeg else {
            throw ConversionError.unsupported("\(destination.pathExtension.uppercased()) needs ffmpeg (brew install ffmpeg)")
        }
        let duration = (try? await AVURLAsset(url: source).load(.duration).seconds) ?? 0
        try await ProcessRunner.run(
            ffmpeg,
            ["-y", "-loglevel", "error", "-i", source.path] + arguments + ["-progress", "pipe:1", "-nostats", destination.path]
        ) { line in
            // ffmpeg reports "out_time_us=<microseconds>" (also as out_time_ms, in µs).
            guard duration > 0, line.hasPrefix("out_time_us="), let micros = Double(line.dropFirst(12)) else { return }
            progress(min(micros / 1_000_000 / duration, 1))
        }
    }
}

private struct ExportSessionBox: @unchecked Sendable {
    let session: AVAssetExportSession
}
