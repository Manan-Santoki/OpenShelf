@preconcurrency import AVFoundation
import Foundation

enum MediaToolError: LocalizedError {
    case noVideo(URL)
    case noTracks(URL)
    case cannotExport(String)
    case failed(String)
    /// Compressing didn't make the videos smaller, so nothing was saved.
    case notSmaller(count: Int)

    var errorDescription: String? {
        switch self {
        case .noVideo(let url): "“\(url.lastPathComponent)” has no video track."
        case .noTracks(let url): "“\(url.lastPathComponent)” has no audio or video."
        case .cannotExport(let detail): "This file can’t be exported \(detail)."
        case .failed(let detail): detail
        case .notSmaller(let count):
            count == 1
                ? "Already as small as it gets — nothing saved."
                : "These \(count) videos are already as small as they get — nothing saved."
        }
    }
}

/// Video and audio edits built on AVFoundation compositions and export sessions.
/// Every function writes a new file beside the source and never overwrites.
///
/// Files AVFoundation turns out not to read, like an MPEG-TS stream named
/// .mp4, are edited with ffmpeg instead when it's installed (see
/// `MediaFallback` and MediaToolsFFmpeg.swift).
enum MediaTools {
    // MARK: Mute

    /// A copy of a video with its audio tracks removed.
    static func mute(_ url: URL, tools: ExternalTools = .current, progress: @escaping @Sendable (Double) -> Void) async throws -> URL {
        try await MediaFallback.run(url, tools: tools) {
            try await muteWithAVFoundation(url, progress: progress)
        } fallback: { ffmpeg in
            try await muteWithFFmpeg(url, ffmpeg: ffmpeg, tools: tools, progress: progress)
        }
    }

    private static func muteWithAVFoundation(_ url: URL, progress: @escaping @Sendable (Double) -> Void) async throws -> URL {
        let asset = AVURLAsset(url: url)
        let videoTracks = try await asset.loadTracks(withMediaType: .video)
        guard !videoTracks.isEmpty else { throw MediaToolError.noVideo(url) }
        let duration = try await asset.load(.duration)

        let composition = AVMutableComposition()
        for track in videoTracks {
            guard let compositionTrack = composition.addMutableTrack(withMediaType: .video, preferredTrackID: kCMPersistentTrackID_Invalid) else { continue }
            try compositionTrack.insertTimeRange(CMTimeRange(start: .zero, duration: duration), of: track, at: .zero)
            compositionTrack.preferredTransform = try await track.load(.preferredTransform)
        }

        let output = videoContainer(for: url)
        let destination = OutputNaming.destination(for: url, suffix: "Muted", pathExtension: output.pathExtension)
        let session = try await makeSession(
            for: composition,
            presets: [AVAssetExportPresetPassthrough, AVAssetExportPresetHighestQuality],
            fileType: output.fileType
        )
        try await export(session, to: destination, fileType: output.fileType, progress: progress)
        return destination
    }

    // MARK: Trim

    /// The part of a video or audio file within `range`.
    static func trim(
        _ url: URL,
        range: CMTimeRange,
        tools: ExternalTools = .current,
        progress: @escaping @Sendable (Double) -> Void
    ) async throws -> URL {
        try await exportRange(url, range: range, suffix: "Trimmed", tools: tools, progress: progress)
    }

    /// Writes `range` of a video or audio file as "<name> <suffix>", without
    /// re-encoding when the container allows it.
    static func exportRange(
        _ url: URL,
        range: CMTimeRange,
        suffix: String,
        tools: ExternalTools = .current,
        progress: @escaping @Sendable (Double) -> Void
    ) async throws -> URL {
        try await MediaFallback.run(url, tools: tools) {
            try await exportRangeWithAVFoundation(url, range: range, suffix: suffix, progress: progress)
        } fallback: { ffmpeg in
            try await exportRangeWithFFmpeg(url, range: range, suffix: suffix, ffmpeg: ffmpeg, tools: tools, progress: progress)
        }
    }

    private static func exportRangeWithAVFoundation(
        _ url: URL,
        range: CMTimeRange,
        suffix: String,
        progress: @escaping @Sendable (Double) -> Void
    ) async throws -> URL {
        let asset = AVURLAsset(url: url)
        let isVideo = try await !asset.loadTracks(withMediaType: .video).isEmpty
        let session: AVAssetExportSession
        let output: (fileType: AVFileType, pathExtension: String)

        if isVideo {
            output = videoContainer(for: url)
            session = try await makeSession(for: asset, presets: [AVAssetExportPresetPassthrough, AVAssetExportPresetHighestQuality], fileType: output.fileType)
        } else if let passthrough = audioPassthroughContainer(for: url),
                  let candidate = try? await makeSession(for: asset, presets: [AVAssetExportPresetPassthrough], fileType: passthrough.fileType) {
            output = passthrough
            session = candidate
        } else {
            // Formats AVFoundation can't write back (e.g. MP3) become AAC.
            output = (.m4a, "m4a")
            session = try await makeSession(for: asset, presets: [AVAssetExportPresetAppleM4A], fileType: .m4a)
        }

        session.timeRange = range
        let destination = OutputNaming.destination(for: url, suffix: suffix, pathExtension: output.pathExtension)
        try await export(session, to: destination, fileType: output.fileType, progress: progress)
        return destination
    }

    // MARK: Speed

    /// Plays the whole file `rate` times faster (or slower, below 1).
    static func changeSpeed(
        _ url: URL,
        rate: Double,
        keepsPitch: Bool,
        tools: ExternalTools = .current,
        progress: @escaping @Sendable (Double) -> Void
    ) async throws -> URL {
        try await MediaFallback.run(url, tools: tools) {
            try await changeSpeedWithAVFoundation(url, rate: rate, keepsPitch: keepsPitch, progress: progress)
        } fallback: { ffmpeg in
            try await changeSpeedWithFFmpeg(url, rate: rate, keepsPitch: keepsPitch, ffmpeg: ffmpeg, tools: tools, progress: progress)
        }
    }

    private static func changeSpeedWithAVFoundation(
        _ url: URL,
        rate: Double,
        keepsPitch: Bool,
        progress: @escaping @Sendable (Double) -> Void
    ) async throws -> URL {
        let asset = AVURLAsset(url: url)
        let duration = try await asset.load(.duration)
        let videoTracks = try await asset.loadTracks(withMediaType: .video)
        let audioTracks = try await asset.loadTracks(withMediaType: .audio)
        guard !videoTracks.isEmpty || !audioTracks.isEmpty else { throw MediaToolError.noTracks(url) }
        if videoTracks.isEmpty && !keepsPitch {
            // Audio-only exports with the varispeed algorithm never finish (macOS 26),
            // so resample the samples directly instead.
            return try await varispeedAudio(url, rate: rate, progress: progress)
        }

        let composition = AVMutableComposition()
        let fullRange = CMTimeRange(start: .zero, duration: duration)
        for track in videoTracks + audioTracks {
            guard let compositionTrack = composition.addMutableTrack(withMediaType: track.mediaType, preferredTrackID: kCMPersistentTrackID_Invalid) else { continue }
            try compositionTrack.insertTimeRange(fullRange, of: track, at: .zero)
            if track.mediaType == .video {
                compositionTrack.preferredTransform = try await track.load(.preferredTransform)
            }
        }
        composition.scaleTimeRange(fullRange, toDuration: CMTimeMultiplyByFloat64(duration, multiplier: 1 / rate))

        let isVideo = !videoTracks.isEmpty
        let output: (fileType: AVFileType, pathExtension: String) = isVideo ? videoContainer(for: url) : (.m4a, "m4a")
        let session = try await makeSession(
            for: composition,
            presets: isVideo ? [AVAssetExportPresetHighestQuality] : [AVAssetExportPresetAppleM4A],
            fileType: output.fileType
        )
        session.audioTimePitchAlgorithm = keepsPitch ? .spectral : .varispeed
        let destination = OutputNaming.destination(for: url, suffix: speedSuffix(rate), pathExtension: output.pathExtension)
        try await export(session, to: destination, fileType: output.fileType, progress: progress)
        return destination
    }

    /// Changes speed and pitch together, like playing a record faster: the
    /// samples are written unchanged with a sample rate scaled by `rate`, then
    /// resampled to 44.1 kHz AAC.
    private static func varispeedAudio(_ url: URL, rate: Double, progress: @escaping @Sendable (Double) -> Void) async throws -> URL {
        let source = try AVAudioFile(forReading: url)
        let format = source.processingFormat
        guard let scaledFormat = AVAudioFormat(
            commonFormat: .pcmFormatFloat32,
            sampleRate: format.sampleRate * rate,
            channels: format.channelCount,
            interleaved: false
        ) else { throw MediaToolError.cannotExport("at this speed") }

        let temporary = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString + ".caf")
        defer { try? FileManager.default.removeItem(at: temporary) }
        do {
            let target = try AVAudioFile(forWriting: temporary, settings: scaledFormat.settings, commonFormat: .pcmFormatFloat32, interleaved: false)
            let frameCount: AVAudioFrameCount = 32_768
            guard let input = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: frameCount),
                  let output = AVAudioPCMBuffer(pcmFormat: scaledFormat, frameCapacity: frameCount)
            else { throw MediaToolError.cannotExport("at this speed") }
            while source.framePosition < source.length {
                try Task.checkCancellation()
                try source.read(into: input, frameCount: frameCount)
                guard input.frameLength > 0, let from = input.floatChannelData, let to = output.floatChannelData else { break }
                for channel in 0..<Int(format.channelCount) {
                    to[channel].update(from: from[channel], count: Int(input.frameLength))
                }
                output.frameLength = input.frameLength
                try target.write(from: output)
                progress(0.8 * Double(source.framePosition) / Double(max(source.length, 1)))
            }
        }

        let destination = OutputNaming.destination(for: url, suffix: speedSuffix(rate), pathExtension: "m4a")
        try await ProcessRunner.run(
            URL(fileURLWithPath: "/usr/bin/afconvert"),
            [temporary.path, destination.path, "-f", "m4af", "-d", "aac@44100"]
        )
        progress(1)
        return destination
    }

    /// "2x", "0.5x", "1.25x".
    static func speedSuffix(_ rate: Double) -> String {
        String(format: "%g", (rate * 100).rounded() / 100) + "x"
    }

    // MARK: Crop

    /// Crops a video to `rect`, given in pixels of the upright frame with the
    /// origin at the top left. Audio is kept.
    static func crop(
        _ url: URL,
        to rect: CGRect,
        tools: ExternalTools = .current,
        progress: @escaping @Sendable (Double) -> Void
    ) async throws -> URL {
        try await MediaFallback.run(url, tools: tools) {
            try await cropWithAVFoundation(url, to: rect, progress: progress)
        } fallback: { ffmpeg in
            try await cropWithFFmpeg(url, to: rect, ffmpeg: ffmpeg, tools: tools, progress: progress)
        }
    }

    private static func cropWithAVFoundation(_ url: URL, to rect: CGRect, progress: @escaping @Sendable (Double) -> Void) async throws -> URL {
        let asset = AVURLAsset(url: url)
        guard let videoTrack = try await asset.loadTracks(withMediaType: .video).first else { throw MediaToolError.noVideo(url) }
        let audioTracks = try await asset.loadTracks(withMediaType: .audio)
        let duration = try await asset.load(.duration)
        let (naturalSize, transform, frameRate) = try await videoTrack.load(.naturalSize, .preferredTransform, .nominalFrameRate)
        let fullRange = CMTimeRange(start: .zero, duration: duration)

        let composition = AVMutableComposition()
        guard let compositionVideo = composition.addMutableTrack(withMediaType: .video, preferredTrackID: kCMPersistentTrackID_Invalid) else {
            throw MediaToolError.cannotExport("with a crop")
        }
        try compositionVideo.insertTimeRange(fullRange, of: videoTrack, at: .zero)
        for track in audioTracks {
            let compositionAudio = composition.addMutableTrack(withMediaType: .audio, preferredTrackID: kCMPersistentTrackID_Invalid)
            try compositionAudio?.insertTimeRange(fullRange, of: track, at: .zero)
        }

        // Rotate into the upright frame, move it to the origin, then shift the crop there.
        let orientedBounds = CGRect(origin: .zero, size: naturalSize).applying(transform)
        let upright = transform.concatenating(CGAffineTransform(translationX: -orientedBounds.minX, y: -orientedBounds.minY))
        let crop = evenRect(rect.integral.intersection(CGRect(origin: .zero, size: orientedBounds.size)))

        let layer = AVMutableVideoCompositionLayerInstruction(assetTrack: compositionVideo)
        layer.setTransform(upright.concatenating(CGAffineTransform(translationX: -crop.minX, y: -crop.minY)), at: .zero)
        let instruction = AVMutableVideoCompositionInstruction()
        instruction.timeRange = fullRange
        instruction.layerInstructions = [layer]
        let videoComposition = AVMutableVideoComposition()
        videoComposition.renderSize = crop.size
        videoComposition.frameDuration = CMTime(value: 1, timescale: CMTimeScale(frameRate > 0 ? frameRate.rounded() : 30))
        videoComposition.instructions = [instruction]

        let output = videoContainer(for: url)
        let session = try await makeSession(for: composition, presets: [AVAssetExportPresetHighestQuality], fileType: output.fileType)
        session.videoComposition = videoComposition
        let destination = OutputNaming.destination(for: url, suffix: "Cropped", pathExtension: output.pathExtension)
        try await export(session, to: destination, fileType: output.fileType, progress: progress)
        return destination
    }

    /// Size of the video as displayed (after rotation), in pixels.
    static func displaySize(of url: URL) async throws -> CGSize {
        guard let track = try await AVURLAsset(url: url).loadTracks(withMediaType: .video).first else { throw MediaToolError.noVideo(url) }
        let (naturalSize, transform) = try await track.load(.naturalSize, .preferredTransform)
        let rect = CGRect(origin: .zero, size: naturalSize).applying(transform)
        return CGSize(width: abs(rect.width), height: abs(rect.height))
    }

    // MARK: Export plumbing

    /// The first preset that can write `fileType` from `asset`.
    static func makeSession(for asset: AVAsset, presets: [String], fileType: AVFileType) async throws -> AVAssetExportSession {
        for preset in presets {
            guard await AVAssetExportSession.compatibility(ofExportPreset: preset, with: asset, outputFileType: fileType),
                  let session = AVAssetExportSession(asset: asset, presetName: preset)
            else { continue }
            return session
        }
        throw MediaToolError.cannotExport("as \(fileType.rawValue.split(separator: ".").last.map(String.init)?.uppercased() ?? "this format")")
    }

    /// Runs `session` with progress reporting; cancelling the task cancels the export.
    static func export(
        _ session: AVAssetExportSession,
        to destination: URL,
        fileType: AVFileType,
        progress: @escaping @Sendable (Double) -> Void
    ) async throws {
        // Write to a hidden file first and move it into place only when it's
        // complete, so a cancelled or failed export never leaves a partial file
        // where the user can see it (sessions can finish writing after a cancel).
        let partial = partialURL(for: destination)
        session.outputURL = partial
        session.outputFileType = fileType
        // The session is thread-safe for these calls but isn't marked Sendable.
        let box = MediaExportBox(session: session)

        let reporter = Task {
            while !Task.isCancelled {
                progress(Double(box.session.progress))
                try? await Task.sleep(for: .milliseconds(150))
            }
        }
        defer { reporter.cancel() }

        await withTaskCancellationHandler {
            await withCheckedContinuation { (continuation: CheckedContinuation<Void, Never>) in
                box.session.exportAsynchronously { continuation.resume() }
            }
        } onCancel: {
            box.session.cancelExport()
        }
        if Task.isCancelled {
            discardPartial(partial)
            throw CancellationError()
        }
        guard box.session.status == .completed else {
            discardPartial(partial)
            throw box.session.error ?? MediaToolError.failed("Export failed.")
        }
        try moveIntoPlace(partial, destination: destination)
        progress(1)
    }

    /// A hidden file beside `destination` to write into before it's complete.
    static func partialURL(for destination: URL) -> URL {
        destination.deletingLastPathComponent()
            .appendingPathComponent(".openshelf-\(UUID().uuidString.prefix(8))-\(destination.lastPathComponent)")
    }

    /// Moves a finished partial file to where the user will see it.
    static func moveIntoPlace(_ partial: URL, destination: URL) throws {
        do {
            try FileManager.default.moveItem(at: partial, to: destination)
        } catch {
            discardPartial(partial)
            throw SaveError.from(error, writing: destination)
        }
        // Tidy up anything AVFoundation left beside it.
        discardPartial(partial)
    }

    /// Removes a partial export now and again shortly after, in case the
    /// session was still finishing the file.
    static func discardPartial(_ url: URL) {
        removePartialFiles(url)
        Task.detached {
            try? await Task.sleep(for: .seconds(2))
            removePartialFiles(url)
        }
    }

    /// The partial file and its companions: a writer optimizing for streaming
    /// works in "<partial>.sb-…" files that can outlive it.
    private static func removePartialFiles(_ url: URL) {
        let folder = url.deletingLastPathComponent()
        let names = (try? FileManager.default.contentsOfDirectory(atPath: folder.path)) ?? []
        for name in names where name.hasPrefix(url.lastPathComponent) {
            try? FileManager.default.removeItem(at: folder.appendingPathComponent(name))
        }
    }

    /// Keeps MP4, M4V and MOV; anything else becomes MOV.
    static func videoContainer(for url: URL) -> (fileType: AVFileType, pathExtension: String) {
        switch url.pathExtension.lowercased() {
        case "mp4": (.mp4, "mp4")
        case "m4v": (.m4v, "m4v")
        default: (.mov, "mov")
        }
    }

    /// Audio formats a passthrough export can write back unchanged.
    static func audioPassthroughContainer(for url: URL) -> (fileType: AVFileType, pathExtension: String)? {
        switch url.pathExtension.lowercased() {
        case "m4a": (.m4a, "m4a")
        case "wav", "wave": (.wav, "wav")
        case "aif", "aiff": (.aiff, url.pathExtension.lowercased())
        case "caf": (.caf, "caf")
        default: nil
        }
    }

    /// Video encoders need even dimensions.
    static func evenRect(_ rect: CGRect) -> CGRect {
        CGRect(
            x: rect.minX,
            y: rect.minY,
            width: max(2, (rect.width / 2).rounded(.down) * 2),
            height: max(2, (rect.height / 2).rounded(.down) * 2)
        )
    }
}

/// Lets an export session cross into progress and cancellation closures.
struct MediaExportBox: @unchecked Sendable {
    let session: AVAssetExportSession
}
