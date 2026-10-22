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
        settings: OutputSettings = .automatic,
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
                try await convertWithFFmpeg(source, to: format, destination: destination, tools: tools, settings: settings, progress: progress)
            } else {
                // A file named like one AVFoundation reads may not be one (an
                // MPEG-TS stream saved as .mp4); ffmpeg gets a go at those.
                try await MediaFallback.run(source, tools: tools) {
                    try await convertNatively(source, to: format, destination: destination, settings: settings, progress: progress)
                } fallback: { _ in
                    // AVFoundation may have started the file before giving up.
                    try? FileManager.default.removeItem(at: destination)
                    try await convertWithFFmpeg(source, to: format, destination: destination, tools: tools, settings: settings, progress: progress)
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
        settings: OutputSettings,
        progress: @escaping @Sendable (Double) -> Void
    ) async throws {
        switch format {
        case .mp4, .mov:
            let fileType: AVFileType = format == .mp4 ? .mp4 : .mov
            guard settings.reencodesVideo else {
                try await export(source, to: destination, preset: AVAssetExportPresetHighestQuality, fileType: fileType, progress: progress)
                return
            }
            // The preset chooses its own codec, size and bitrate, so settings
            // that choose any of them re-encode the way Compress does.
            let info = try await VideoSourceInfo.loadWithAVFoundation(source)
            let plan = VideoCompressionPlan(source: info, settings: settings)
            try await MediaTools.encode(source, source: info, plan: plan, fileType: fileType, to: destination, progress: progress)
        case .m4a:
            if let kbps = settings.audio.kbps {
                try await MediaTools.encodeAAC(source, kbps: kbps, to: destination, progress: progress)
            } else {
                try await export(source, to: destination, preset: AVAssetExportPresetAppleM4A, fileType: .m4a, progress: progress)
            }
        case .wav:
            try await ProcessRunner.run(afconvert, ["-f", "WAVE", "-d", "LEI16", source.path, destination.path])
        case .aiff:
            try await ProcessRunner.run(afconvert, ["-f", "AIFF", "-d", "BEI16", source.path, destination.path])
        case .flac:
            try await ProcessRunner.run(afconvert, ["-f", "flac", "-d", "flac", source.path, destination.path])
        case .gif:
            try await VideoGIF.write(source, to: destination, settings: settings.gif, progress: progress)
        default:
            throw ConversionError.unsupported("media to \(format.title)")
        }
    }

    private static func convertWithFFmpeg(
        _ source: URL,
        to format: OutputFormat,
        destination: URL,
        tools: ExternalTools,
        settings: OutputSettings,
        progress: @escaping @Sendable (Double) -> Void
    ) async throws {
        guard let ffmpeg = tools.ffmpeg else {
            throw MissingToolError(name: "ffmpeg")
        }
        let encoders = FFmpegEncoders.available(in: ffmpeg)
        let duration = await duration(of: source, tools: tools)
        let audio = await sourceAudio(of: source, tools: tools)
        let input: [String] = ["-i", source.path]

        // MP4 and MOV may be copied; MKV only needs looking at for its codec
        // setting, which HDR overrides (see `FFmpegArguments`).
        let needsProbe = format == .mp4 || format == .mov || (format == .mkv && settings.reencodesVideo)
        let probed: ProbedMedia? = needsProbe ? try? await ProbedMedia.load(source, ffprobe: tools.ffprobe) : nil

        var attempts: [[String]] = []
        // Copying streams would ignore settings that ask for a re-encode.
        if !settings.reencodesVideo {
            if format == .mkv {
                // Matroska holds almost any codec, so try copying every stream first.
                attempts.append(input + ["-map", "0", "-c", "copy"])
            } else if let probed, let copy = FFmpegArguments.streamCopy(to: format, from: probed) {
                // H.264 or HEVC with AAC, as in most MPEG-TS recordings, only needs a new container.
                attempts.append(input + copy)
            }
        }
        let isHDR = probed?.video?.isHDR ?? false
        let arguments = FFmpegArguments.arguments(for: format, encoders: encoders, audio: audio, settings: settings, isHDR: isHDR)
        attempts.append(input + arguments)
        if arguments.contains(FFmpegArguments.hevcToolbox) {
            // Only Apple silicon encodes to a constant quality; elsewhere fall
            // back to the next encoder (see `FFmpegArguments.hevc`).
            let others = encoders.subtracting([FFmpegArguments.hevcToolbox])
            attempts.append(input + FFmpegArguments.arguments(for: format, encoders: others, audio: audio, settings: settings, isHDR: isHDR))
        }
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
    static let hevcToolbox = "hevc_videotoolbox"

    /// Arguments between the input and the output path. `encoders` is what the
    /// installed ffmpeg offers, so OGG can fall back when libvorbis is missing.
    /// Lossy audio bitrates follow `audio`, the source's sound (see
    /// `AudioBitRate`), unless `settings` fix one; `isHDR` says the source is
    /// HDR, which keeps HEVC once video settings are chosen.
    static func arguments(
        for format: OutputFormat,
        encoders: Set<String>,
        audio: SourceAudio? = nil,
        settings: OutputSettings = .automatic,
        isHDR: Bool = false
    ) -> [String] {
        func bitRate(_ codec: AudioBitRate.Codec) -> [String] {
            ["-b:a", settings.audio.argument(for: codec, source: audio)]
        }
        let video = settings.video
        let scale: [String] = video.maxSize.bounds.map { ["-vf", scaleFilter(fitting: $0)] } ?? []
        let aac = ["-c:a", "aac"] + bitRate(.aac)
        switch format {
        case .mp4: return scale + videoEncoding(for: format, video, encoders: encoders, isHDR: isHDR) + aac + ["-movflags", "+faststart"]
        case .mov, .mkv: return scale + videoEncoding(for: format, video, encoders: encoders, isHDR: isHDR) + aac
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
            return scale + ["-c:v", "libvpx-vp9"] + vp9Rate(video) + ["-deadline", "good", "-cpu-used", "4", "-row-mt", "1", "-c:a", "libopus"] + bitRate(.opus)
        case .avi:
            return scale + ["-c:v", "mpeg4", "-vtag", "xvid"] + quantizer(video) + ["-c:a", "libmp3lame"] + bitRate(.mp3)
        case .wmv:
            return scale + ["-c:v", "wmv2"] + quantizer(video) + ["-c:a", "wmav2"] + bitRate(.wma)
        case .gif:
            // Two-pass palette for clean colours. By default capped at 720 px
            // wide and 12 fps to keep files small.
            let gif = settings.gif
            return ["-vf", "fps=\(gif.frameRate),scale='min(\(gif.width.maxWidth),iw)':-2:flags=lanczos,split[a][b];[a]palettegen[p];[b][p]paletteuse", "-loop", "0"]
        default:
            return []
        }
    }

    /// The picture in MP4, MOV and MKV: H.264 with x264 unless HEVC is
    /// chosen, or the source is HDR and any video setting is (H.264 can't
    /// hold HDR's colours). Automatic settings give x264 at CRF 20, as always.
    static func videoEncoding(for format: OutputFormat, _ video: OutputSettings.Video, encoders: Set<String>, isHDR: Bool) -> [String] {
        let wantsHEVC = video.codec == .hevc || (isHDR && !video.isAutomatic)
        if wantsHEVC, let hevc = hevc(for: format, video, encoders: encoders, isHDR: isHDR) {
            return hevc
        }
        let rate: [String] = customRate(video) ?? ["-crf", "\(x264CRF(video.quality))"]
        return ["-c:v", "libx264", "-preset", "medium"] + rate + ["-pix_fmt", "yuv420p"]
    }

    /// HEVC with x265 when this ffmpeg has it, else VideoToolbox; nil with
    /// neither. HDR is kept 10-bit. QuickTime only plays HEVC tagged "hvc1".
    static func hevc(for format: OutputFormat, _ video: OutputSettings.Video, encoders: Set<String>, isHDR: Bool) -> [String]? {
        let tag: [String] = format == .mp4 || format == .mov ? ["-tag:v", "hvc1"] : []
        if encoders.contains("libx265") {
            // x265 needs about five more CRF for the same size as x264.
            let rate: [String] = customRate(video) ?? ["-crf", "\(x264CRF(video.quality) + 5)"]
            return ["-c:v", "libx265", "-preset", "medium"] + rate + ["-pix_fmt", isHDR ? "yuv420p10le" : "yuv420p"] + tag + ["-x265-params", "log-level=error"]
        }
        if encoders.contains(hevcToolbox) {
            let rate: [String] = customRate(video) ?? ["-q:v", "\(toolboxQuality(video.quality))"]
            let depth: [String] = isHDR ? ["-pix_fmt", "p010le", "-profile:v", "main10"] : ["-pix_fmt", "yuv420p"]
            return ["-c:v", hevcToolbox] + rate + depth + tag
        }
        return nil
    }

    /// x264's constant rate factor; lower is better and larger.
    static func x264CRF(_ quality: OutputSettings.Video.Quality) -> Int {
        switch quality {
        case .best: 18
        case .automatic, .custom: 20
        case .balanced: 23
        case .smaller: 28
        }
    }

    /// VideoToolbox's constant quality, 1–100; higher is better and larger.
    static func toolboxQuality(_ quality: OutputSettings.Video.Quality) -> Int {
        switch quality {
        case .best: 75
        case .automatic, .custom: 65
        case .balanced: 55
        case .smaller: 40
        }
    }

    /// VP9 in constant quality mode (CRF with no bitrate), or a target bitrate.
    static func vp9Rate(_ video: OutputSettings.Video) -> [String] {
        if video.quality == .custom { return ["-b:v", "\(video.customBitRate)"] }
        let crf: Int = switch video.quality {
        case .best: 28
        case .automatic, .custom: 32
        case .balanced: 33
        case .smaller: 38
        }
        return ["-crf", "\(crf)", "-b:v", "0"]
    }

    /// MPEG-4 (Xvid) and WMV's fixed quantizer, 2–31; lower is better and
    /// larger. Or a target bitrate.
    static func quantizer(_ video: OutputSettings.Video) -> [String] {
        if video.quality == .custom { return ["-b:v", "\(video.customBitRate)"] }
        let scale: Int = switch video.quality {
        case .best: 2
        case .automatic, .custom: 4
        case .balanced: 5
        case .smaller: 8
        }
        return ["-q:v", "\(scale)"]
    }

    /// A custom bitrate as x264, x265 and VideoToolbox take it, with room for
    /// busy scenes; nil for the other qualities.
    static func customRate(_ video: OutputSettings.Video) -> [String]? {
        guard video.quality == .custom else { return nil }
        let rate = video.customBitRate
        return ["-b:v", "\(rate)", "-maxrate", "\(rate * 3 / 2)", "-bufsize", "\(rate * 2)"]
    }

    /// Scales a video to fit inside `bounds` (long and short edges) in
    /// either orientation, keeping its shape, never enlarging, with even
    /// sides. Frames are already upright here, so portrait fits turned.
    static func scaleFilter(fitting bounds: (long: Double, short: Double)) -> String {
        let long = Int(bounds.long)
        let short = Int(bounds.short)
        return "scale=w='if(gte(iw,ih),min(\(long),iw),min(\(short),iw))':h='if(gte(iw,ih),min(\(short),ih),min(\(long),ih))'"
            + ":force_original_aspect_ratio=decrease:force_divisible_by=2"
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
