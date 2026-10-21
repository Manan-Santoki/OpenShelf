@preconcurrency import AVFoundation
import CoreGraphics
import Foundation
import ImageIO

/// What the media editors show before an edit: a file's length, its picture
/// size and preview frames. AVFoundation answers for the files it reads;
/// ffprobe and ffmpeg for those it turns out not to (see `MediaFallback`),
/// which can then be edited, though not played, in the editor.
enum MediaLoading {
    struct Basics: Equatable, Sendable {
        var duration: Double
        var hasVideo: Bool
        /// The picture size players show, after rotation; nil without video.
        var displaySize: CGSize?
        /// Whether AVFoundation read the file, so the editor can play it.
        var isPlayable: Bool
    }

    static func basics(of url: URL, tools: ExternalTools = .current) async throws -> Basics {
        try await MediaFallback.run(url, tools: tools) {
            let asset = AVURLAsset(url: url)
            let duration = try await asset.load(.duration).seconds
            var size: CGSize?
            if let track = try await asset.loadTracks(withMediaType: .video).first {
                let (naturalSize, transform) = try await track.load(.naturalSize, .preferredTransform)
                let rect = CGRect(origin: .zero, size: naturalSize).applying(transform)
                size = CGSize(width: abs(rect.width), height: abs(rect.height))
            }
            return Basics(duration: duration.isFinite ? duration : 0, hasVideo: size != nil, displaySize: size, isPlayable: true)
        } fallback: { _ in
            let probed = try await ProbedMedia.load(url, ffprobe: tools.ffprobe)
            return Basics(duration: probed.duration, hasVideo: probed.video != nil, displaySize: probed.video?.displaySize, isPlayable: false)
        }
    }

    /// A frame to show a video by, upright and at most `maximumSize` on its
    /// longer side: a second in, or halfway through shorter clips.
    static func poster(of url: URL, maximumSize: CGFloat, tools: ExternalTools = .current) async throws -> CGImage {
        try await MediaFallback.run(url, tools: tools) {
            let asset = AVURLAsset(url: url)
            let duration = try await asset.load(.duration).seconds
            let generator = AVAssetImageGenerator(asset: asset)
            generator.appliesPreferredTrackTransform = true
            generator.maximumSize = CGSize(width: maximumSize, height: maximumSize)
            return try await generator.image(at: MediaTime.cmTime(posterTime(duration))).image
        } fallback: { ffmpeg in
            let probed = try await ProbedMedia.load(url, ffprobe: tools.ffprobe)
            return try await frame(of: url, at: posterTime(probed.duration), maximumSize: maximumSize, ffmpeg: ffmpeg)
        }
    }

    static func posterTime(_ duration: Double) -> Double {
        duration > 2 ? 1 : duration / 2
    }

    /// What an editor says when a file won't load: `fallback`, unless
    /// installing ffmpeg might be the answer.
    static func loadError(_ error: Error, otherwise fallback: String) -> String {
        error is UnreadableMediaError ? error.localizedDescription : fallback
    }

    /// One frame decoded by ffmpeg, upright and fitted inside `maximumSize`
    /// without being enlarged.
    static func frame(of url: URL, at seconds: Double, maximumSize: CGFloat, ffmpeg: URL) async throws -> CGImage {
        let output = FileManager.default.temporaryDirectory.appendingPathComponent("openshelf-frame-\(UUID().uuidString).png")
        defer { try? FileManager.default.removeItem(at: output) }
        let side = Int(maximumSize)
        let fit = "scale=w='min(iw,\(side))':h='min(ih,\(side))':force_original_aspect_ratio=decrease"
        var arguments: [String] = ["-y", "-nostdin", "-loglevel", "error"] + FFmpegEdits.input(url, from: seconds, length: nil)
        arguments += ["-map", "0:V:0", "-frames:v", "1", "-vf", fit, "-update", "1", output.path]
        try await ProcessRunner.run(ffmpeg, arguments)
        guard let source = CGImageSourceCreateWithURL(output as CFURL, nil),
              let image = CGImageSourceCreateImageAtIndex(source, 0, nil)
        else { throw ConversionError.unreadable(url) }
        return image
    }

    /// `count` frames spread evenly over the video, decoded by ffmpeg, for a
    /// trimming filmstrip. Frames that won't decode are left out.
    static func thumbnails(of url: URL, count: Int, duration: Double, tools: ExternalTools = .current) async -> [CGImage] {
        guard let ffmpeg = tools.ffmpeg, count > 0 else { return [] }
        var images: [CGImage] = []
        for index in 0..<count {
            guard !Task.isCancelled else { break }
            let seconds: Double = duration * (Double(index) + 0.5) / Double(count)
            if let image = try? await frame(of: url, at: seconds, maximumSize: 200, ffmpeg: ffmpeg) {
                images.append(image)
            }
        }
        return images
    }

    /// Peak levels for `bars` equal slices of the first sound track, 0...1,
    /// from ffmpeg decoding it as mono at a rate that gives each bar plenty
    /// of samples without decoding long files at full rate.
    static func waveform(of url: URL, bars: Int, duration: Double, tools: ExternalTools = .current) async -> [Float] {
        guard let ffmpeg = tools.ffmpeg, bars > 0 else { return [] }
        let output = FileManager.default.temporaryDirectory.appendingPathComponent("openshelf-waveform-\(UUID().uuidString).raw")
        defer { try? FileManager.default.removeItem(at: output) }
        let wanted: Double = Double(bars * 64) / max(duration, 0.1)
        let sampleRate = Int(min(max(wanted, 100), 8_000))
        do {
            try await ProcessRunner.run(ffmpeg, [
                "-y", "-nostdin", "-loglevel", "error", "-i", url.path,
                "-map", "0:a:0", "-ac", "1", "-ar", "\(sampleRate)", "-f", "s16le", "-c:a", "pcm_s16le", output.path,
            ])
        } catch {
            return []
        }
        guard let data = try? Data(contentsOf: output), data.count >= 2 else { return [] }
        let samples: [Int16] = data.withUnsafeBytes { raw in
            (0..<raw.count / 2).map { raw.loadUnaligned(fromByteOffset: $0 * 2, as: Int16.self) }
        }
        return peaks(of: samples, bars: bars)
    }

    /// The loudest sample in each of `bars` equal slices, 0...1.
    static func peaks(of samples: [Int16], bars: Int) -> [Float] {
        guard !samples.isEmpty, bars > 0 else { return [] }
        let perBar: Double = Double(samples.count) / Double(bars)
        let count: Int = perBar >= 1 ? bars : samples.count
        let size: Double = max(perBar, 1)
        return (0..<count).map { bar in
            let start = Int(Double(bar) * size)
            let end = min(max(Int(Double(bar + 1) * size), start + 1), samples.count)
            let peak: Int32 = samples[start..<end].reduce(0) { max($0, abs(Int32($1))) }
            return Float(peak) / Float(Int16.max)
        }
    }
}
