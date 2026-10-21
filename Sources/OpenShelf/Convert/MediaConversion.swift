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
        // GIFs can be made natively, but ffmpeg's two-pass palette looks better.
        let usesFFmpeg = !isNative || format.needsFFmpeg || (format == .gif && tools.ffmpeg != nil)
        do {
            if usesFFmpeg {
                try await convertWithFFmpeg(source, to: format, destination: destination, tools: tools, progress: progress)
            } else {
                // A file named like one AVFoundation reads may not be one (an
                // MPEG-TS stream saved as .mp4); ffmpeg gets a go at those.
                try await MediaFallback.run(source, tools: tools) {
                    try await convertNatively(source, to: format, destination: destination, progress: progress)
                } fallback: { _ in
                    // AVFoundation may have started the file before giving up.
                    try? FileManager.default.removeItem(at: destination)
                    try await convertWithFFmpeg(source, to: format, destination: destination, tools: tools, progress: progress)
                }
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
        case .gif:
            try await VideoGIF.write(source, to: destination, progress: progress)
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
            throw MissingToolError(name: "ffmpeg")
        }
        let encoders = FFmpegEncoders.available(in: ffmpeg)
        let duration = await duration(of: source, tools: tools)
        let audio = await sourceAudio(of: source, tools: tools)
        let input: [String] = ["-i", source.path]

        var attempts: [[String]] = []
        if format == .mkv {
            // Matroska holds almost any codec, so try copying every stream first.
            attempts.append(input + ["-map", "0", "-c", "copy"])
        } else if format == .mp4 || format == .mov,
                  let probed = try? await ProbedMedia.load(source, ffprobe: tools.ffprobe),
                  let copy = FFmpegArguments.streamCopy(to: format, from: probed) {
            // H.264 or HEVC with AAC, as in most MPEG-TS recordings, only needs a new container.
            attempts.append(input + copy)
        }
        attempts.append(input + FFmpegArguments.arguments(for: format, encoders: encoders, audio: audio))
        try await FFmpegJob.export(ffmpeg, attempts: attempts, to: destination, duration: duration, progress: progress)
    }

    private static func duration(of source: URL, tools: ExternalTools) async -> Double {
        if MediaFormats.isAVFoundationReadable(source),
           let seconds = try? await AVURLAsset(url: source).load(.duration).seconds,
           seconds.isFinite {
            return seconds
        }
        return MediaProbe.duration(of: source, ffprobe: tools.ffprobe) ?? 0
    }

    private static func sourceAudio(of source: URL, tools: ExternalTools) async -> SourceAudio? {
        if MediaFormats.isAVFoundationReadable(source), let audio = await SourceAudio.load(from: source) {
            return audio
        }
        return SourceAudio.probe(source, ffprobe: tools.ffprobe)
    }

    private static func requireAudio(in source: URL, isNative: Bool, tools: ExternalTools) async throws {
        var hasAudio: Bool?
        if isNative, let tracks = try? await AVURLAsset(url: source).loadTracks(withMediaType: .audio) {
            hasAudio = !tracks.isEmpty
        }
        // ffprobe answers for the rest, including "native" files AVFoundation
        // can't open. When it can't tell either, let the conversion try and report.
        guard hasAudio ?? MediaProbe.hasAudioTrack(source, ffprobe: tools.ffprobe) ?? true else {
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
        // Opening the file first fails with a clear "can't open" (see
        // `MediaFallback`) rather than the export's catch-all error.
        _ = try await asset.load(.duration)
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
    /// Lossy audio bitrates follow `audio`, the source's sound (see `AudioBitRate`).
    static func arguments(for format: OutputFormat, encoders: Set<String>, audio: SourceAudio? = nil) -> [String] {
        func bitRate(_ codec: AudioBitRate.Codec) -> [String] {
            ["-b:a", AudioBitRate.argument(for: codec, source: audio)]
        }
        let h264 = ["-c:v", "libx264", "-preset", "medium", "-crf", "20", "-pix_fmt", "yuv420p"]
        let aac = ["-c:a", "aac"] + bitRate(.aac)
        switch format {
        case .mp4: return h264 + aac + ["-movflags", "+faststart"]
        case .mov, .mkv: return h264 + aac
        case .m4a: return ["-vn"] + aac
        case .mp3: return ["-vn", "-c:a", "libmp3lame"] + bitRate(.mp3)
        case .wav: return ["-vn", "-c:a", "pcm_s16le"]
        case .aiff: return ["-vn", "-c:a", "pcm_s16be"]
        case .flac: return ["-vn", "-c:a", "flac"]
        case .opus: return ["-vn", "-c:a", "libopus"] + bitRate(.opus)
        case .wma: return ["-vn", "-c:a", "wmav2"] + bitRate(.wma)
        case .ogg:
            if encoders.contains("libvorbis") { return ["-vn", "-c:a", "libvorbis"] + bitRate(.vorbis) }
            // Opus in an Ogg container is a valid .ogg file.
            if encoders.contains("libopus") { return ["-vn", "-c:a", "libopus"] + bitRate(.opus) }
            // ffmpeg's own Vorbis encoder is experimental, stereo-only and ignores bitrates.
            return ["-vn", "-c:a", "vorbis", "-strict", "experimental", "-ac", "2"]
        case .webm:
            return ["-c:v", "libvpx-vp9", "-crf", "32", "-b:v", "0", "-deadline", "good", "-cpu-used", "4", "-row-mt", "1", "-c:a", "libopus"] + bitRate(.opus)
        case .avi:
            return ["-c:v", "mpeg4", "-vtag", "xvid", "-q:v", "4", "-c:a", "libmp3lame"] + bitRate(.mp3)
        case .wmv:
            return ["-c:v", "wmv2", "-q:v", "4", "-c:a", "wmav2"] + bitRate(.wma)
        case .gif:
            // Two-pass palette for clean colours; capped at 720 px wide and 12 fps to keep files small.
            return ["-vf", "fps=12,scale='min(720,iw)':-2:flags=lanczos,split[a][b];[a]palettegen[p];[b][p]paletteuse", "-loop", "0"]
        default:
            return []
        }
    }

    /// Arguments that copy `probed`'s picture and sound into an MP4 or MOV
    /// untouched, when they're in codecs Apple's players handle: 8-bit H.264,
    /// HEVC and AAC. Nil when they need re-encoding.
    static func streamCopy(to format: OutputFormat, from probed: ProbedMedia) -> [String]? {
        guard format == .mp4 || format == .mov, let video = probed.video else { return nil }
        let pixelFormat: String = video.pixelFormat ?? ""
        let playable: Bool
        switch video.codec {
        case "h264": playable = ["yuv420p", "yuvj420p"].contains(pixelFormat)
        case "hevc": playable = ["yuv420p", "yuvj420p", "yuv420p10le"].contains(pixelFormat)
        default: playable = false
        }
        guard playable, probed.audio.allSatisfy({ $0.codec == "aac" }) else { return nil }

        // Every sound track comes along, not just the first.
        var arguments: [String] = ["-map", "0:V:0", "-map", "0:a?", "-c", "copy"]
        if video.codec == "hevc" {
            // QuickTime only plays HEVC tagged "hvc1"; ffmpeg's default is "hev1".
            arguments += ["-tag:v", "hvc1"]
        }
        if format == .mp4 {
            arguments += ["-movflags", "+faststart"]
        }
        return arguments
    }
}

private struct ExportSessionBox: @unchecked Sendable {
    let session: AVAssetExportSession
}
