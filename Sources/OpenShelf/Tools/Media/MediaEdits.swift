@preconcurrency import AVFoundation
import Foundation

// MARK: - Split plan

/// How a file is cut into parts.
enum SplitMode: Hashable, CaseIterable, Identifiable {
    case equalParts
    case everyInterval
    case atTimes

    var id: Self { self }

    var title: String {
        switch self {
        case .equalParts: "Equal parts"
        case .everyInterval: "Every…"
        case .atTimes: "At times"
        }
    }
}

/// One part of a split, in seconds.
struct SplitSegment: Equatable {
    var start: Double
    var end: Double
    var duration: Double { end - start }
}

enum SplitPlanError: LocalizedError, Equatable {
    case unreadable(String)
    case outOfRange(String)
    case noCuts
    case tooMany(Int)
    case intervalTooShort

    var errorDescription: String? {
        switch self {
        case .unreadable(let text): "“\(text)” isn’t a time. Use minutes and seconds, like 1:30."
        case .outOfRange(let text): "\(text) is past the end of the file."
        case .noCuts: "Add at least one time to split at, like 0:30, 1:15."
        case .tooMany(let count): "That would make \(count) parts; split into 100 or fewer."
        case .intervalTooShort: "Parts must be at least 1 second long."
        }
    }
}

/// Works out the parts of a split, before anything is exported.
enum SplitPlan {
    static let maximumParts = 100
    /// Leftover tails shorter than this are joined to the previous part.
    static let minimumTail = 0.25

    static func equalParts(duration: Double, count: Int) -> [SplitSegment] {
        let count = min(max(count, 2), 20)
        guard duration > 0 else { return [] }
        let length = duration / Double(count)
        var segments: [SplitSegment] = []
        for index in 0..<count {
            let start = Double(index) * length
            // The last part ends exactly at the end, whatever rounding did.
            let end: Double = index == count - 1 ? duration : Double(index + 1) * length
            segments.append(SplitSegment(start: start, end: end))
        }
        return segments
    }

    static func every(_ interval: Double, duration: Double) throws -> [SplitSegment] {
        guard interval >= 1 else { throw SplitPlanError.intervalTooShort }
        guard duration > 0 else { return [] }
        let count = Int((duration / interval).rounded(.up))
        guard count <= maximumParts else { throw SplitPlanError.tooMany(count) }
        var cuts = stride(from: interval, to: duration, by: interval).map { $0 }
        if let last = cuts.last, duration - last < minimumTail { cuts.removeLast() }
        return segments(cuts: cuts, duration: duration)
    }

    /// Cuts at comma-separated times such as "0:30, 1:15, 2:00".
    static func at(_ text: String, duration: Double) throws -> [SplitSegment] {
        let parts = text.split(whereSeparator: { $0 == "," || $0 == ";" || $0 == "\n" })
            .map { $0.trimmingCharacters(in: .whitespaces) }
            .filter { !$0.isEmpty }
        var cuts: [Double] = []
        for part in parts {
            guard let seconds = MediaTime.parse(part), seconds >= 0 else { throw SplitPlanError.unreadable(part) }
            guard seconds < duration else { throw SplitPlanError.outOfRange(part) }
            if seconds > 0 { cuts.append(seconds) }
        }
        cuts = Array(Set(cuts)).sorted()
        guard !cuts.isEmpty else { throw SplitPlanError.noCuts }
        guard cuts.count + 1 <= maximumParts else { throw SplitPlanError.tooMany(cuts.count + 1) }
        return segments(cuts: cuts, duration: duration)
    }

    private static func segments(cuts: [Double], duration: Double) -> [SplitSegment] {
        let edges = [0] + cuts + [duration]
        return zip(edges, edges.dropFirst()).map { SplitSegment(start: $0, end: $1) }
    }
}

// MARK: - Rotation

/// A turn or mirror applied to a whole video.
enum VideoRotation: Hashable, CaseIterable, Identifiable {
    case left
    case right
    case upsideDown
    case flipHorizontal

    var id: Self { self }

    var title: String {
        switch self {
        case .left: "Left"
        case .right: "Right"
        case .upsideDown: "180°"
        case .flipHorizontal: "Flip"
        }
    }

    var systemImage: String {
        switch self {
        case .left: "rotate.left"
        case .right: "rotate.right"
        case .upsideDown: "arrow.clockwise"
        case .flipHorizontal: "arrow.left.and.right"
        }
    }

    /// Degrees clockwise on screen; 0 for a mirror.
    var degrees: Double {
        switch self {
        case .left: -90
        case .right: 90
        case .upsideDown: 180
        case .flipHorizontal: 0
        }
    }

    /// Turns only change how players orient the frames, so they need no
    /// re-encoding; a mirror rewrites every frame.
    var isLossless: Bool { self != .flipHorizontal }

    /// `transform` (a track's preferred transform) followed by this change,
    /// moved back so the frame starts at the origin.
    func applied(to transform: CGAffineTransform, naturalSize: CGSize) -> CGAffineTransform {
        let extra: CGAffineTransform = switch self {
        case .flipHorizontal: CGAffineTransform(scaleX: -1, y: 1)
        default: CGAffineTransform(rotationAngle: degrees * .pi / 180)
        }
        let combined = transform.concatenating(extra)
        let rect = CGRect(origin: .zero, size: naturalSize).applying(combined)
        return combined.concatenating(CGAffineTransform(translationX: -rect.minX, y: -rect.minY))
    }
}

// MARK: - Edits

extension MediaTools {
    // MARK: Merge

    /// Joins videos (into "Merged.mp4") or audio files (into "Merged.m4a") in
    /// the given order, saved next to the first one.
    static func merge(_ urls: [URL], progress: @escaping @Sendable (Double) -> Void) async throws -> URL {
        guard let first = urls.first, urls.count >= 2 else { throw MediaToolError.failed("Choose at least two files to merge.") }
        var allVideo = true
        for url in urls where try await AVURLAsset(url: url).loadTracks(withMediaType: .video).isEmpty {
            allVideo = false
        }
        return allVideo
            ? try await mergeVideos(urls, nextTo: first, progress: progress)
            : try await mergeAudio(urls, nextTo: first, progress: progress)
    }

    private static func mergeAudio(_ urls: [URL], nextTo first: URL, progress: @escaping @Sendable (Double) -> Void) async throws -> URL {
        let composition = AVMutableComposition()
        guard let track = composition.addMutableTrack(withMediaType: .audio, preferredTrackID: kCMPersistentTrackID_Invalid) else {
            throw MediaToolError.cannotExport("as one file")
        }
        var cursor = CMTime.zero
        for url in urls {
            let asset = AVURLAsset(url: url)
            guard let source = try await asset.loadTracks(withMediaType: .audio).first else { throw MediaToolError.noTracks(url) }
            let duration = try await asset.load(.duration)
            try track.insertTimeRange(CMTimeRange(start: .zero, duration: duration), of: source, at: cursor)
            cursor = cursor + duration
        }
        let destination = OutputNaming.uniqueURL(in: first.deletingLastPathComponent(), baseName: "Merged", pathExtension: "m4a")
        try SaveError.checkWritable(destination)
        let session = try await makeSession(for: composition, presets: [AVAssetExportPresetAppleM4A], fileType: .m4a)
        try await export(session, to: destination, fileType: .m4a, progress: progress)
        return destination
    }

    private static func mergeVideos(_ urls: [URL], nextTo first: URL, progress: @escaping @Sendable (Double) -> Void) async throws -> URL {
        let composition = AVMutableComposition()
        guard let videoTrack = composition.addMutableTrack(withMediaType: .video, preferredTrackID: kCMPersistentTrackID_Invalid),
              let audioTrack = composition.addMutableTrack(withMediaType: .audio, preferredTrackID: kCMPersistentTrackID_Invalid)
        else { throw MediaToolError.cannotExport("as one file") }

        let layer = AVMutableVideoCompositionLayerInstruction(assetTrack: videoTrack)
        var renderSize: CGSize?
        var frameRate: Float = 30
        var cursor = CMTime.zero
        for url in urls {
            let asset = AVURLAsset(url: url)
            guard let source = try await asset.loadTracks(withMediaType: .video).first else { throw MediaToolError.noVideo(url) }
            let duration = try await asset.load(.duration)
            let range = CMTimeRange(start: .zero, duration: duration)
            let (naturalSize, transform, rate) = try await source.load(.naturalSize, .preferredTransform, .nominalFrameRate)
            let upright = CGRect(origin: .zero, size: naturalSize).applying(transform).size
            if renderSize == nil {
                // The first clip sets the size; the others are fitted inside it.
                renderSize = evenSize(CGSize(width: abs(upright.width), height: abs(upright.height)))
                if rate > 0 { frameRate = rate }
            }
            try videoTrack.insertTimeRange(range, of: source, at: cursor)
            if let audio = try await asset.loadTracks(withMediaType: .audio).first {
                try audioTrack.insertTimeRange(range, of: audio, at: cursor)
            }
            layer.setTransform(fitTransform(naturalSize: naturalSize, preferredTransform: transform, renderSize: renderSize!), at: cursor)
            cursor = cursor + duration
        }
        if audioTrack.segments.isEmpty {
            composition.removeTrack(audioTrack)
        }

        let instruction = AVMutableVideoCompositionInstruction()
        instruction.timeRange = CMTimeRange(start: .zero, duration: cursor)
        instruction.layerInstructions = [layer]
        instruction.backgroundColor = CGColor(gray: 0, alpha: 1)
        let videoComposition = AVMutableVideoComposition()
        videoComposition.renderSize = renderSize ?? CGSize(width: 1280, height: 720)
        videoComposition.frameDuration = CMTime(value: 1, timescale: CMTimeScale(frameRate.rounded()))
        videoComposition.instructions = [instruction]

        let destination = OutputNaming.uniqueURL(in: first.deletingLastPathComponent(), baseName: "Merged", pathExtension: "mp4")
        try SaveError.checkWritable(destination)
        let session = try await makeSession(for: composition, presets: [AVAssetExportPresetHighestQuality], fileType: .mp4)
        session.videoComposition = videoComposition
        try await export(session, to: destination, fileType: .mp4, progress: progress)
        return destination
    }

    /// Turns a clip upright and scales it to fit `renderSize`, centred, with
    /// letterbox or pillarbox bars where the shapes differ.
    static func fitTransform(naturalSize: CGSize, preferredTransform: CGAffineTransform, renderSize: CGSize) -> CGAffineTransform {
        let oriented = CGRect(origin: .zero, size: naturalSize).applying(preferredTransform)
        let upright = preferredTransform.concatenating(CGAffineTransform(translationX: -oriented.minX, y: -oriented.minY))
        guard oriented.width > 0, oriented.height > 0 else { return upright }
        let scale = min(renderSize.width / oriented.width, renderSize.height / oriented.height)
        let offset = CGPoint(
            x: (renderSize.width - oriented.width * scale) / 2,
            y: (renderSize.height - oriented.height * scale) / 2
        )
        return upright
            .concatenating(CGAffineTransform(scaleX: scale, y: scale))
            .concatenating(CGAffineTransform(translationX: offset.x, y: offset.y))
    }

    // MARK: Split

    /// Exports each segment as "<name> Part N", reporting overall progress.
    static func split(
        _ url: URL,
        into segments: [SplitSegment],
        progress: @escaping @Sendable (_ fraction: Double, _ part: Int) -> Void
    ) async throws -> [URL] {
        var outputs: [URL] = []
        do {
            for (index, segment) in segments.enumerated() {
                try Task.checkCancellation()
                let range = CMTimeRange(start: MediaTime.cmTime(segment.start), end: MediaTime.cmTime(segment.end))
                let output = try await exportRange(url, range: range, suffix: "Part \(index + 1)") { fraction in
                    progress((Double(index) + fraction) / Double(segments.count), index + 1)
                }
                outputs.append(output)
            }
        } catch {
            // A cancelled split leaves no partial set behind.
            if error is CancellationError {
                for output in outputs { try? FileManager.default.removeItem(at: output) }
            }
            throw error
        }
        return outputs
    }

    // MARK: Rotate

    /// Turns or mirrors a video. Turns keep the original frames and only change
    /// their orientation; a mirror re-encodes. Audio is kept.
    static func rotate(_ url: URL, by rotation: VideoRotation, progress: @escaping @Sendable (Double) -> Void) async throws -> URL {
        let asset = AVURLAsset(url: url)
        guard let source = try await asset.loadTracks(withMediaType: .video).first else { throw MediaToolError.noVideo(url) }
        let audioTracks = try await asset.loadTracks(withMediaType: .audio)
        let duration = try await asset.load(.duration)
        let (naturalSize, transform, rate) = try await source.load(.naturalSize, .preferredTransform, .nominalFrameRate)
        let fullRange = CMTimeRange(start: .zero, duration: duration)
        let newTransform = rotation.applied(to: transform, naturalSize: naturalSize)

        let composition = AVMutableComposition()
        guard let videoTrack = composition.addMutableTrack(withMediaType: .video, preferredTrackID: kCMPersistentTrackID_Invalid) else {
            throw MediaToolError.cannotExport("rotated")
        }
        try videoTrack.insertTimeRange(fullRange, of: source, at: .zero)
        for audio in audioTracks {
            let track = composition.addMutableTrack(withMediaType: .audio, preferredTrackID: kCMPersistentTrackID_Invalid)
            try track?.insertTimeRange(fullRange, of: audio, at: .zero)
        }

        let output = videoContainer(for: url)
        let destination = OutputNaming.destination(for: url, suffix: "Rotated", pathExtension: output.pathExtension)
        try SaveError.checkWritable(destination)
        let session: AVAssetExportSession
        if rotation.isLossless {
            videoTrack.preferredTransform = newTransform
            session = try await makeSession(
                for: composition,
                presets: [AVAssetExportPresetPassthrough, AVAssetExportPresetHighestQuality],
                fileType: output.fileType
            )
        } else {
            let size = CGRect(origin: .zero, size: naturalSize).applying(newTransform).size
            let layer = AVMutableVideoCompositionLayerInstruction(assetTrack: videoTrack)
            layer.setTransform(newTransform, at: .zero)
            let instruction = AVMutableVideoCompositionInstruction()
            instruction.timeRange = fullRange
            instruction.layerInstructions = [layer]
            let videoComposition = AVMutableVideoComposition()
            videoComposition.renderSize = evenSize(CGSize(width: abs(size.width), height: abs(size.height)))
            videoComposition.frameDuration = CMTime(value: 1, timescale: CMTimeScale(rate > 0 ? rate.rounded() : 30))
            videoComposition.instructions = [instruction]
            session = try await makeSession(for: composition, presets: [AVAssetExportPresetHighestQuality], fileType: output.fileType)
            session.videoComposition = videoComposition
        }
        try await export(session, to: destination, fileType: output.fileType, progress: progress)
        return destination
    }

    /// Video encoders need even dimensions.
    static func evenSize(_ size: CGSize) -> CGSize {
        CGSize(width: max(2, (size.width / 2).rounded(.down) * 2), height: max(2, (size.height / 2).rounded(.down) * 2))
    }
}
