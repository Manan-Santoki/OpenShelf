@preconcurrency import AVFoundation
import CoreGraphics
import Foundation

// MARK: - Tools

/// ffmpeg versions of the media tools, for files AVFoundation turns out not to
/// read (see `MediaFallback`). Each writes what its AVFoundation version would,
/// under the same name and in the same container, through a hidden partial
/// file and with progress (see `FFmpegJob`).
extension MediaTools {
    static func muteWithFFmpeg(_ url: URL, ffmpeg: URL, tools: ExternalTools, progress: @escaping @Sendable (Double) -> Void) async throws -> URL {
        let probed = try await ProbedMedia.load(url, ffprobe: tools.ffprobe)
        guard let video = probed.video else { throw MediaToolError.noVideo(url) }
        let destination = OutputNaming.destination(for: url, suffix: "Muted", pathExtension: videoContainer(for: url).pathExtension)
        try SaveError.checkWritable(destination)

        // Every picture stream, copied, unless the container can't hold its codec.
        let input: [String] = ["-i", url.path, "-map", "0:V"]
        let attempts: [[String]] = [input + FFmpegEdits.videoCopy(for: video), input + FFmpegEdits.videoEncoding(for: video)]
        try await FFmpegJob.export(ffmpeg, attempts: attempts, to: destination, duration: probed.duration, progress: progress)
        return destination
    }

    static func exportRangeWithFFmpeg(
        _ url: URL,
        range: CMTimeRange,
        suffix: String,
        ffmpeg: URL,
        tools: ExternalTools,
        progress: @escaping @Sendable (Double) -> Void
    ) async throws -> URL {
        let probed = try await ProbedMedia.load(url, ffprobe: tools.ffprobe)
        let start: Double = max(range.start.seconds, 0)
        let length: Double = range.duration.seconds
        let pathExtension: String
        let streams: [String]
        if let video = probed.video {
            pathExtension = videoContainer(for: url).pathExtension
            // A cut rarely falls on a keyframe, so the picture is re-encoded to
            // start exactly there; AAC sound is copied.
            streams = ["-map", "0:V", "-map", "0:a?"] + FFmpegEdits.videoEncoding(for: video) + FFmpegEdits.audioCopyOrAAC(probed.audio)
        } else {
            guard !probed.audio.isEmpty else { throw MediaToolError.noTracks(url) }
            pathExtension = audioPassthroughContainer(for: url)?.pathExtension ?? "m4a"
            streams = ["-map", "0:a", "-vn"] + FFmpegEdits.audioCodecs(forContainer: pathExtension, tracks: probed.audio)
        }
        let destination = OutputNaming.destination(for: url, suffix: suffix, pathExtension: pathExtension)
        try SaveError.checkWritable(destination)
        let arguments: [String] = FFmpegEdits.input(url, from: start, length: length) + streams
        try await FFmpegJob.export(ffmpeg, arguments, to: destination, duration: length, progress: progress)
        return destination
    }

    static func changeSpeedWithFFmpeg(
        _ url: URL,
        rate: Double,
        keepsPitch: Bool,
        ffmpeg: URL,
        tools: ExternalTools,
        progress: @escaping @Sendable (Double) -> Void
    ) async throws -> URL {
        let probed = try await ProbedMedia.load(url, ffprobe: tools.ffprobe)
        guard probed.video != nil || !probed.audio.isEmpty else { throw MediaToolError.noTracks(url) }
        let pathExtension: String = probed.video != nil ? videoContainer(for: url).pathExtension : "m4a"
        let destination = OutputNaming.destination(for: url, suffix: speedSuffix(rate), pathExtension: pathExtension)
        try SaveError.checkWritable(destination)
        let arguments: [String] = ["-i", url.path] + FFmpegEdits.speed(probed, rate: rate, keepsPitch: keepsPitch)
        try await FFmpegJob.export(ffmpeg, arguments, to: destination, duration: probed.duration / rate, progress: progress)
        return destination
    }

    static func cropWithFFmpeg(_ url: URL, to rect: CGRect, ffmpeg: URL, tools: ExternalTools, progress: @escaping @Sendable (Double) -> Void) async throws -> URL {
        let probed = try await ProbedMedia.load(url, ffprobe: tools.ffprobe)
        guard let video = probed.video else { throw MediaToolError.noVideo(url) }
        let crop = evenRect(rect.integral.intersection(CGRect(origin: .zero, size: video.displaySize)))
        let destination = OutputNaming.destination(for: url, suffix: "Cropped", pathExtension: videoContainer(for: url).pathExtension)
        try SaveError.checkWritable(destination)

        // ffmpeg turns frames upright before filtering them, so the crop
        // applies to the picture as the editor showed it.
        var arguments: [String] = ["-i", url.path, "-map", "0:V:0", "-map", "0:a?", "-vf", FFmpegEdits.cropFilter(crop)]
        arguments += FFmpegEdits.videoEncoding(for: video) + FFmpegEdits.audioCopyOrAAC(probed.audio)
        try await FFmpegJob.export(ffmpeg, arguments, to: destination, duration: probed.duration, progress: progress)
        return destination
    }

    static func rotateWithFFmpeg(
        _ url: URL,
        by rotation: VideoRotation,
        ffmpeg: URL,
        tools: ExternalTools,
        progress: @escaping @Sendable (Double) -> Void
    ) async throws -> URL {
        let probed = try await ProbedMedia.load(url, ffprobe: tools.ffprobe)
        guard let video = probed.video else { throw MediaToolError.noVideo(url) }
        let destination = OutputNaming.destination(for: url, suffix: "Rotated", pathExtension: videoContainer(for: url).pathExtension)
        try SaveError.checkWritable(destination)

        let streams: [String] = ["-map", "0:V:0", "-map", "0:a?"]
        let audio: [String] = FFmpegEdits.audioCopyOrAAC(probed.audio)
        var attempts: [[String]] = []
        if rotation.isLossless {
            // As with AVFoundation, a turn only changes the angle players show
            // the frames at. ffmpeg before 6.0 can't set it, and re-encodes.
            let angle: Int = FFmpegEdits.displayRotation(turning: video, by: rotation)
            attempts.append(["-display_rotation:v:0", "\(angle)", "-i", url.path] + streams + FFmpegEdits.videoCopy(for: video) + audio)
        }
        let turned: [String] = ["-i", url.path] + streams + ["-vf", FFmpegEdits.rotationFilter(rotation)]
        attempts.append(turned + FFmpegEdits.videoEncoding(for: video) + audio)
        try await FFmpegJob.export(ffmpeg, attempts: attempts, to: destination, duration: probed.duration, progress: progress)
        return destination
    }

    static func mergeWithFFmpeg(_ urls: [URL], ffmpeg: URL, tools: ExternalTools, progress: @escaping @Sendable (Double) -> Void) async throws -> URL {
        guard let first = urls.first else { throw MediaToolError.failed("Choose at least two files to merge.") }
        var clips: [ProbedMedia] = []
        for url in urls {
            clips.append(try await ProbedMedia.load(url, ffprobe: tools.ffprobe))
        }
        let folder = first.deletingLastPathComponent()
        let isVideo: Bool = clips.allSatisfy { $0.video != nil }
        var arguments: [String] = urls.flatMap { ["-i", $0.path] }
        let destination: URL
        if isVideo {
            destination = OutputNaming.uniqueURL(in: folder, baseName: "Merged", pathExtension: "mp4")
            arguments += FFmpegEdits.mergeVideos(clips)
        } else {
            for (url, clip) in zip(urls, clips) where clip.audio.isEmpty {
                throw MediaToolError.noTracks(url)
            }
            destination = OutputNaming.uniqueURL(in: folder, baseName: "Merged", pathExtension: "m4a")
            arguments += FFmpegEdits.mergeAudio(clips)
        }
        try SaveError.checkWritable(destination)
        let duration: Double = clips.reduce(0) { $0 + $1.duration }
        try await FFmpegJob.export(ffmpeg, arguments, to: destination, duration: duration, progress: progress)
        return destination
    }
}

// MARK: - Arguments

/// ffmpeg arguments for the media tools, apart from running them so they can
/// be checked without encoding anything.
enum FFmpegEdits {
    /// How far before a cut ffmpeg jumps to directly; from there it decodes
    /// up to the cut. Jumping straight to the cut isn't safe: in MPEG-TS it
    /// can land past the last keyframe and lose the picture until the next.
    static let seekMargin: Double = 30

    /// Input arguments for `length` seconds of `url` from `start` (to the end
    /// when `length` is nil), exact to the frame.
    static func input(_ url: URL, from start: Double, length: Double?) -> [String] {
        let coarse: Double = max(start - seekMargin, 0)
        var arguments: [String] = []
        if coarse > 0 { arguments += ["-ss", seconds(coarse)] }
        arguments += ["-i", url.path]
        let fine: Double = start - coarse
        if fine > 0 { arguments += ["-ss", seconds(fine)] }
        if let length { arguments += ["-t", seconds(length)] }
        return arguments
    }

    // MARK: Codecs

    /// The picture re-encoded about as well as AVFoundation's highest-quality
    /// preset does: H.264, or 10-bit HEVC for HDR so it keeps its colours.
    static func videoEncoding(for video: ProbedMedia.Video?) -> [String] {
        if video?.isHDR == true {
            return ["-c:v", "libx265", "-preset", "medium", "-crf", "20", "-pix_fmt", "yuv420p10le", "-tag:v", "hvc1", "-x265-params", "log-level=error"]
        }
        return ["-c:v", "libx264", "-preset", "medium", "-crf", "18", "-pix_fmt", "yuv420p"]
    }

    /// The picture copied as it is.
    static func videoCopy(for video: ProbedMedia.Video) -> [String] {
        // QuickTime only plays HEVC tagged "hvc1"; ffmpeg's default is "hev1".
        video.codec == "hevc" ? ["-c:v", "copy", "-tag:v", "hvc1"] : ["-c:v", "copy"]
    }

    /// For sound tracks mapped in order: AAC is copied, anything else becomes
    /// AAC at the rate `AudioBitRate` picks for it.
    static func audioCopyOrAAC(_ tracks: [ProbedMedia.Audio]) -> [String] {
        var arguments: [String] = []
        for (index, track) in tracks.enumerated() {
            arguments += track.codec == "aac" ? ["-c:a:\(index)", "copy"] : aac(track, index: index)
        }
        return arguments
    }

    /// The `index`th output sound track as AAC at `AudioBitRate`'s rate for `track`.
    static func aac(_ track: ProbedMedia.Audio, index: Int) -> [String] {
        ["-c:a:\(index)", "aac", "-b:a:\(index)", AudioBitRate.argument(for: .aac, source: track.source)]
    }

    /// Sound for an audio-only file written as `pathExtension`, as a
    /// passthrough export would: PCM stays PCM, M4A is AAC.
    static func audioCodecs(forContainer pathExtension: String, tracks: [ProbedMedia.Audio]) -> [String] {
        switch pathExtension {
        case "wav": return ["-c:a", "pcm_s16le"]
        case "aif", "aiff": return ["-c:a", "pcm_s16be"]
        case "caf": return ["-c:a", "copy"]
        default: return audioCopyOrAAC(tracks)
        }
    }

    // MARK: Speed

    /// Plays `probed` `rate` times as fast, whether it has a picture, sound or
    /// both. Every frame is kept, shown for less (or more) time, as in the
    /// AVFoundation version; every sound track keeps its pitch or follows
    /// the speed, like a record.
    static func speed(_ probed: ProbedMedia, rate: Double, keepsPitch: Bool) -> [String] {
        var graph: [String] = []
        var arguments: [String] = []
        if let video = probed.video {
            graph.append("[0:V:0]setpts=PTS/\(factor(rate))[v]")
            arguments += ["-map", "[v]"] + videoEncoding(for: video)
            if let fps = video.framesPerSecond {
                arguments += ["-r", factor(fps * rate)]
            }
        }
        for (index, track) in probed.audio.enumerated() {
            let filter: String = keepsPitch ? atempoChain(rate) : varispeed(rate, sampleRate: track.sampleRate ?? 48_000)
            graph.append("[0:a:\(index)]\(filter)[a\(index)]")
            arguments += ["-map", "[a\(index)]"] + aac(track, index: index)
        }
        return ["-filter_complex", graph.joined(separator: ";")] + arguments
    }

    /// `atempo` changes tempo and keeps pitch. Before ffmpeg 4.1 one filter
    /// took 0.5–2 only, so larger changes are a chain of them.
    static func atempoChain(_ rate: Double) -> String {
        guard rate > 0 else { return "anull" }
        var remaining: Double = rate
        var factors: [Double] = []
        while remaining > 2 {
            factors.append(2)
            remaining /= 2
        }
        while remaining < 0.5 {
            factors.append(0.5)
            remaining /= 0.5
        }
        factors.append(remaining)
        return factors.map { "atempo=\(factor($0))" }.joined(separator: ",")
    }

    /// Pitch follows speed: the same samples played at a scaled rate, then
    /// resampled back to `sampleRate`.
    static func varispeed(_ rate: Double, sampleRate: Double) -> String {
        let scaled = Int((sampleRate * rate).rounded())
        return "asetrate=\(scaled),aresample=\(Int(sampleRate.rounded()))"
    }

    // MARK: Rotate and crop

    /// Re-encoding filters for each change, applied to the upright picture.
    static func rotationFilter(_ rotation: VideoRotation) -> String {
        switch rotation {
        case .right: "transpose=clock"
        case .left: "transpose=cclock"
        case .upsideDown: "hflip,vflip"
        case .flipHorizontal: "hflip"
        }
    }

    /// The `-display_rotation` for `video` turned by `rotation` on top of its
    /// own rotation: degrees counter-clockwise, 0 to 270.
    static func displayRotation(turning video: ProbedMedia.Video, by rotation: VideoRotation) -> Int {
        let clockwise: Int = video.rotation + Int(rotation.degrees)
        let normalized: Int = ((clockwise % 360) + 360) % 360
        return (360 - normalized) % 360
    }

    static func cropFilter(_ rect: CGRect) -> String {
        "crop=\(Int(rect.width)):\(Int(rect.height)):\(Int(rect.minX)):\(Int(rect.minY))"
    }

    // MARK: Compress

    /// Encodes as `plan` lays out, like the AVAssetWriter version: the same
    /// size, bitrates and sound, a keyframe every two seconds, and an MP4
    /// ready for streaming.
    static func compress(_ url: URL, plan: VideoCompressionPlan, source: VideoSourceInfo) -> [String] {
        // ffmpeg turns frames upright first, so they're scaled straight to
        // the size players show and need no rotation afterwards.
        let width = Int(plan.displaySize.width)
        let height = Int(plan.displaySize.height)
        let fps: Double = source.frameRate > 0 ? source.frameRate : 30
        let keyframeInterval = max(Int((fps * 2).rounded()), 1)
        var arguments: [String] = ["-i", url.path, "-map", "0:V:0", "-map", "0:a?", "-vf", "scale=\(width):\(height),setsar=1"]
        switch plan.codec {
        case .h264:
            arguments += ["-c:v", "libx264", "-preset", "medium", "-profile:v", "high", "-pix_fmt", "yuv420p"]
        case .hevc:
            arguments += ["-c:v", "libx265", "-preset", "medium", "-pix_fmt", "yuv420p10le", "-tag:v", "hvc1", "-x265-params", "log-level=error"]
        }
        arguments += ["-b:v", "\(plan.videoBitRate)", "-g", "\(keyframeInterval)"]
        for (index, audio) in plan.audio.enumerated() {
            switch audio {
            case .passthrough:
                arguments += ["-c:a:\(index)", "copy"]
            case .aac(let bitRate, let channels, let sampleRate):
                arguments += ["-c:a:\(index)", "aac", "-b:a:\(index)", "\(bitRate)", "-ac:a:\(index)", "\(channels)", "-ar:a:\(index)", "\(Int(sampleRate))"]
            }
        }
        return arguments + ["-movflags", "+faststart"]
    }

    // MARK: Merge

    /// Joins clips (each given with `-i`, in order) into one H.264 video. As
    /// in the AVFoundation version, the first clip sets the size and frame
    /// rate and the others are fitted inside it, with black bars where the
    /// shapes differ. Clips without sound get silence.
    static func mergeVideos(_ clips: [ProbedMedia]) -> [String] {
        guard let first = clips.first?.video else { return [] }
        let size: CGSize = MediaTools.evenSize(first.displaySize)
        let width = Int(size.width)
        let height = Int(size.height)
        let fps: String = first.frameRate ?? "30"
        let hasSound: Bool = clips.contains { !$0.audio.isEmpty }
        let layout: String = channelLayout(for: clips)

        var graph: [String] = []
        var segments: String = ""
        for (index, clip) in clips.enumerated() {
            let fit = "scale=\(width):\(height):force_original_aspect_ratio=decrease:force_divisible_by=2"
            let pad = "pad=\(width):\(height):(ow-iw)/2:(oh-ih)/2:color=black"
            graph.append("[\(index):V:0]\(fit),\(pad),setsar=1,fps=\(fps),format=yuv420p[v\(index)]")
            segments += "[v\(index)]"
            if hasSound {
                if clip.audio.isEmpty {
                    graph.append("anullsrc=r=48000:cl=\(layout),atrim=duration=\(seconds(clip.duration))[a\(index)]")
                } else {
                    graph.append("[\(index):a:0]\(soundFormat(layout))[a\(index)]")
                }
                segments += "[a\(index)]"
            }
        }
        let outputs: String = hasSound ? "[v][a]" : "[v]"
        graph.append("\(segments)concat=n=\(clips.count):v=1:a=\(hasSound ? 1 : 0)\(outputs)")

        var arguments: [String] = ["-filter_complex", graph.joined(separator: ";"), "-map", "[v]"] + videoEncoding(for: nil)
        if hasSound {
            arguments += ["-map", "[a]", "-c:a", "aac", "-b:a", mergedBitRate(clips)]
        }
        return arguments + ["-movflags", "+faststart"]
    }

    /// Joins the first sound track of each clip into one AAC track.
    static func mergeAudio(_ clips: [ProbedMedia]) -> [String] {
        let layout: String = channelLayout(for: clips)
        var graph: [String] = []
        var segments: String = ""
        for index in clips.indices {
            graph.append("[\(index):a:0]\(soundFormat(layout))[a\(index)]")
            segments += "[a\(index)]"
        }
        graph.append("\(segments)concat=n=\(clips.count):v=0:a=1[a]")
        return ["-filter_complex", graph.joined(separator: ";"), "-map", "[a]", "-c:a", "aac", "-b:a", mergedBitRate(clips)]
    }

    /// Joined sound needs one format: 48 kHz, in stereo unless every clip is mono.
    private static func soundFormat(_ layout: String) -> String {
        "aresample=48000,aformat=sample_fmts=fltp:channel_layouts=\(layout)"
    }

    private static func channelLayout(for clips: [ProbedMedia]) -> String {
        clips.contains { ($0.audio.first?.channels ?? 1) > 1 } ? "stereo" : "mono"
    }

    /// The richest clip's rate, so none sounds worse than it did.
    private static func mergedBitRate(_ clips: [ProbedMedia]) -> String {
        let rates: [Int] = clips.compactMap { $0.audio.first }.map { AudioBitRate.target(for: .aac, source: $0.source) }
        let best: Int = rates.max() ?? AudioBitRate.target(for: .aac, source: nil)
        return "\(best / 1000)k"
    }

    // MARK: Numbers

    /// Seconds to the millisecond, as ffmpeg reads them.
    static func seconds(_ value: Double) -> String {
        String(format: "%.3f", value)
    }

    /// A factor or rate without needless digits: "2", "0.5", "1.25".
    static func factor(_ value: Double) -> String {
        String(format: "%g", value)
    }
}
