@preconcurrency import AVFoundation
import Foundation

/// Audio and video, using AVFoundation and the built-in `afconvert` where they
/// can, and ffmpeg (when installed) for formats Apple doesn't read or write.
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
        let isNative = MediaFormats.isAVFoundationReadable(source)

        if format.isAudioOnly {
            try await requireAudio(in: source, isNative: isNative, tools: tools)
        }
        do {
            if isNative && !format.needsFFmpeg {
                try await convertNatively(source, to: format, destination: destination, progress: progress)
            } else {
                try await convertWithFFmpeg(source, to: format, destination: destination, tools: tools, progress: progress)
            }
        } catch {
            // Don't leave a half-written file behind.
            try? FileManager.default.removeItem(at: destination)
            throw error
        }
        return destination
    }

    private static func convertNatively(
        _ source: URL,
        to format: OutputFormat,
        destination: URL,
        progress: @escaping @Sendable (Double) -> Void
    ) async throws {
        switch format {
        case .mp4:
            try await export(source, to: destination, preset: AVAssetExportPresetHighestQuality, fileType: .mp4, progress: progress)
        case .mov:
            try await export(source, to: destination, preset: AVAssetExportPresetHighestQuality, fileType: .mov, progress: progress)
        case .m4a:
            try await export(source, to: destination, preset: AVAssetExportPresetAppleM4A, fileType: .m4a, progress: progress)
        case .wav:
            try await ProcessRunner.run(afconvert, ["-f", "WAVE", "-d", "LEI16", source.path, destination.path])
        case .aiff:
            try await ProcessRunner.run(afconvert, ["-f", "AIFF", "-d", "BEI16", source.path, destination.path])
        case .flac:
            try await ProcessRunner.run(afconvert, ["-f", "flac", "-d", "flac", source.path, destination.path])
        default:
            throw ConversionError.unsupported("media to \(format.title)")
        }
    }

    private static func convertWithFFmpeg(
        _ source: URL,
        to format: OutputFormat,
        destination: URL,
        tools: ExternalTools,
        progress: @escaping @Sendable (Double) -> Void
    ) async throws {
        guard let ffmpeg = tools.ffmpeg else {
            throw ConversionError.unsupported("\(format.title) needs ffmpeg (brew install ffmpeg)")
        }
        let encoders = FFmpegEncoders.available(in: ffmpeg)
        let duration = await duration(of: source, tools: tools)

        if format == .mkv {
            // Matroska holds almost any codec, so try copying the streams first.
            do {
                try await run(ffmpeg, source: source, destination: destination, arguments: ["-map", "0", "-c", "copy"], duration: duration, progress: progress)
                return
            } catch is CancellationError {
                throw CancellationError()
            } catch {
                try? FileManager.default.removeItem(at: destination)
            }
        }
        try await run(
            ffmpeg,
            source: source,
            destination: destination,
            arguments: FFmpegArguments.arguments(for: format, encoders: encoders),
            duration: duration,
            progress: progress
        )
    }

    private static func run(
        _ ffmpeg: URL,
        source: URL,
        destination: URL,
        arguments: [String],
        duration: Double,
        progress: @escaping @Sendable (Double) -> Void
    ) async throws {
        try await ProcessRunner.run(
            ffmpeg,
            ["-y", "-loglevel", "error", "-i", source.path] + arguments + ["-progress", "pipe:1", "-nostats", destination.path]
        ) { line in
            // ffmpeg reports "out_time_us=<microseconds>" (also as out_time_ms, in µs).
            guard duration > 0, line.hasPrefix("out_time_us="), let micros = Double(line.dropFirst(12)) else { return }
            progress(min(micros / 1_000_000 / duration, 1))
        }
    }

    private static func duration(of source: URL, tools: ExternalTools) async -> Double {
        if MediaFormats.isAVFoundationReadable(source),
           let seconds = try? await AVURLAsset(url: source).load(.duration).seconds,
           seconds.isFinite {
            return seconds
        }
        return MediaProbe.duration(of: source, ffprobe: tools.ffprobe) ?? 0
    }

    private static func requireAudio(in source: URL, isNative: Bool, tools: ExternalTools) async throws {
        let hasAudio: Bool
        if isNative {
            hasAudio = try await !AVURLAsset(url: source).loadTracks(withMediaType: .audio).isEmpty
        } else {
            // When ffprobe can't tell, let ffmpeg try and report.
            hasAudio = MediaProbe.hasAudioTrack(source, ffprobe: tools.ffprobe) ?? true
        }
        guard hasAudio else {
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
}

/// ffmpeg codec settings for each output format.
enum FFmpegArguments {
    /// Arguments between the input and the output path. `encoders` is what the
    /// installed ffmpeg offers, so OGG can fall back when libvorbis is missing.
    static func arguments(for format: OutputFormat, encoders: Set<String>) -> [String] {
        let h264 = ["-c:v", "libx264", "-preset", "medium", "-crf", "20", "-pix_fmt", "yuv420p"]
        let aac = ["-c:a", "aac", "-b:a", "192k"]
        switch format {
        case .mp4: return h264 + aac + ["-movflags", "+faststart"]
        case .mov, .mkv: return h264 + aac
        case .m4a: return ["-vn"] + aac
        case .mp3: return ["-vn", "-c:a", "libmp3lame", "-q:a", "2"]
        case .wav: return ["-vn", "-c:a", "pcm_s16le"]
        case .aiff: return ["-vn", "-c:a", "pcm_s16be"]
        case .flac: return ["-vn", "-c:a", "flac"]
        case .opus: return ["-vn", "-c:a", "libopus", "-b:a", "128k"]
        case .wma: return ["-vn", "-c:a", "wmav2", "-b:a", "192k"]
        case .ogg:
            if encoders.contains("libvorbis") { return ["-vn", "-c:a", "libvorbis", "-q:a", "5"] }
            // Opus in an Ogg container is a valid .ogg file.
            if encoders.contains("libopus") { return ["-vn", "-c:a", "libopus", "-b:a", "160k"] }
            // ffmpeg's own Vorbis encoder is experimental and stereo-only.
            return ["-vn", "-c:a", "vorbis", "-strict", "experimental", "-ac", "2"]
        case .webm:
            return ["-c:v", "libvpx-vp9", "-crf", "32", "-b:v", "0", "-deadline", "good", "-cpu-used", "4", "-row-mt", "1", "-c:a", "libopus", "-b:a", "128k"]
        case .avi:
            return ["-c:v", "mpeg4", "-vtag", "xvid", "-q:v", "4", "-c:a", "libmp3lame", "-q:a", "3"]
        case .wmv:
            return ["-c:v", "wmv2", "-q:v", "4", "-c:a", "wmav2", "-b:a", "192k"]
        case .gif:
            // Two-pass palette for clean colours; capped at 720 px wide and 12 fps to keep files small.
            return ["-vf", "fps=12,scale='min(720,iw)':-2:flags=lanczos,split[a][b];[a]palettegen[p];[b][p]paletteuse", "-loop", "0"]
        default:
            return []
        }
    }
}

private struct ExportSessionBox: @unchecked Sendable {
    let session: AVAssetExportSession
}
