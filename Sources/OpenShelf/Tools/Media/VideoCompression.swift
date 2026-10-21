@preconcurrency import AVFoundation
import Foundation
import os
import VideoToolbox

enum VideoResolution: CaseIterable, Identifiable {
    case original, p1080, p720, p480

    var id: Self { self }

    var title: String {
        switch self {
        case .original: "Original"
        case .p1080: "1080p"
        case .p720: "720p"
        case .p480: "480p"
        }
    }

    /// The frame a video is fitted inside, by its long and short edges so
    /// portrait videos get the same treatment; nil keeps the size.
    var bounds: (long: Double, short: Double)? {
        switch self {
        case .original: nil
        case .p1080: (1920, 1080)
        case .p720: (1280, 720)
        case .p480: (854, 480)
        }
    }
}

enum VideoQuality: CaseIterable, Identifiable {
    case high, medium, low

    var id: Self { self }

    var title: String {
        switch self {
        case .high: "High"
        case .medium: "Medium"
        case .low: "Low"
        }
    }

    /// Share of the bitrate High gets.
    var videoFactor: Double {
        switch self {
        case .high: 1
        case .medium: 0.6
        case .low: 0.35
        }
    }

    /// Stereo AAC ceiling; High uses the codec's own.
    var audioLimit: Int? {
        switch self {
        case .high: nil
        case .medium: 128_000
        case .low: 96_000
        }
    }
}

/// What a video's tracks look like, read once so the estimate can follow the
/// settings instantly.
struct VideoSourceInfo: Equatable, Sendable {
    struct AudioTrack: Equatable, Sendable {
        var audio: SourceAudio
        var sampleRate: Double
    }

    /// Frame size as stored, before the track's rotation.
    var naturalSize: CGSize
    var preferredTransform: CGAffineTransform = .identity
    var frameRate: Double = 30
    /// Bits per second of the video track; 0 when unknown.
    var bitRate: Int = 0
    var isHEVC = false
    var isHDR = false
    /// AVVideoColorPropertiesKey values, when the source declares all three.
    var colorProperties: [String: String]?
    var audioTracks: [AudioTrack] = []
    var duration: Double
    var fileSize: Int64

    /// The size players show, after rotation.
    var displaySize: CGSize {
        let rect = CGRect(origin: .zero, size: naturalSize).applying(preferredTransform)
        return CGSize(width: abs(rect.width).rounded(), height: abs(rect.height).rounded())
    }

    /// Reads the tracks with AVFoundation, or with ffprobe for files it turns
    /// out not to read (see `MediaFallback`).
    static func load(_ url: URL, tools: ExternalTools = .current) async throws -> VideoSourceInfo {
        try await MediaFallback.run(url, tools: tools) {
            try await loadWithAVFoundation(url)
        } fallback: { _ in
            try await probe(url, ffprobe: tools.ffprobe)
        }
    }

    private static func loadWithAVFoundation(_ url: URL) async throws -> VideoSourceInfo {
        let asset = AVURLAsset(url: url)
        guard let video = try await asset.loadTracks(withMediaType: .video).first else { throw MediaToolError.noVideo(url) }
        let (naturalSize, transform, frameRate, dataRate) = try await video.load(.naturalSize, .preferredTransform, .nominalFrameRate, .estimatedDataRate)
        let (formats, characteristics) = try await video.load(.formatDescriptions, .mediaCharacteristics)
        let duration = try await asset.load(.duration).seconds

        var info = VideoSourceInfo(naturalSize: naturalSize, duration: duration.isFinite ? duration : 0, fileSize: MediaSize.fileSize(url))
        info.preferredTransform = transform
        info.frameRate = Double(frameRate)
        info.bitRate = Int(dataRate)
        info.isHDR = characteristics.contains(.containsHDRVideo)
        if let format = formats.first {
            info.isHEVC = CMFormatDescriptionGetMediaSubType(format) == kCMVideoCodecType_HEVC
            info.colorProperties = colorProperties(of: format)
        }

        // One entry per track, in order, so plans line up with the tracks.
        for track in try await asset.loadTracks(withMediaType: .audio) {
            let (rate, audioFormats) = try await track.load(.estimatedDataRate, .formatDescriptions)
            let description = audioFormats.first.flatMap { CMAudioFormatDescriptionGetStreamBasicDescription($0)?.pointee }
            let audio = description.map { SourceAudio($0, bitRate: rate) } ?? SourceAudio(channels: 2, bitRate: nil)
            info.audioTracks.append(AudioTrack(audio: audio, sampleRate: description?.mSampleRate ?? 44_100))
        }
        return info
    }

    private static func colorProperties(of format: CMFormatDescription) -> [String: String]? {
        func value(_ key: CFString) -> String? {
            CMFormatDescriptionGetExtension(format, extensionKey: key) as? String
        }
        guard let primaries = value(kCMFormatDescriptionExtension_ColorPrimaries),
              let transfer = value(kCMFormatDescriptionExtension_TransferFunction),
              let matrix = value(kCMFormatDescriptionExtension_YCbCrMatrix)
        else { return nil }
        // Core Media and AVFoundation spell these values the same way.
        return [AVVideoColorPrimariesKey: primaries, AVVideoTransferFunctionKey: transfer, AVVideoYCbCrMatrixKey: matrix]
    }
}

extension VideoSourceInfo {
    /// The same facts from ffprobe. Colours aren't needed: ffmpeg keeps the
    /// source's own tags when it re-encodes.
    static func probe(_ url: URL, ffprobe: URL?) async throws -> VideoSourceInfo {
        let probed = try await ProbedMedia.load(url, ffprobe: ffprobe)
        guard let video = probed.video else { throw MediaToolError.noVideo(url) }
        return VideoSourceInfo(probed: probed, video: video, fileSize: MediaSize.fileSize(url))
    }

    init(probed: ProbedMedia, video: ProbedMedia.Video, fileSize: Int64) {
        self.init(naturalSize: CGSize(width: video.width, height: video.height), duration: probed.duration, fileSize: fileSize)
        preferredTransform = Self.transform(rotatedClockwise: video.rotation)
        frameRate = video.framesPerSecond ?? 30
        isHEVC = video.codec == "hevc"
        isHDR = video.isHDR
        audioTracks = probed.audio.map { AudioTrack(audio: $0.source, sampleRate: $0.sampleRate ?? 44_100) }
        if let rate = video.bitRate {
            bitRate = rate
        } else if probed.duration > 0 {
            // MPEG-TS doesn't say what the picture alone takes; the whole
            // file's rate less the sound is close.
            let total: Double = Double(fileSize) * 8 / probed.duration
            let sound: Int = audioTracks.reduce(0) { $0 + ($1.audio.bitRate ?? 0) }
            bitRate = max(Int(total) - sound, 0)
        }
    }

    /// The preferred transform that turns a frame `degrees` clockwise, as
    /// AVFoundation reports it for phone videos.
    static func transform(rotatedClockwise degrees: Int) -> CGAffineTransform {
        switch degrees {
        case 90: CGAffineTransform(a: 0, b: 1, c: -1, d: 0, tx: 0, ty: 0)
        case 180: CGAffineTransform(a: -1, b: 0, c: 0, d: -1, tx: 0, ty: 0)
        case 270: CGAffineTransform(a: 0, b: -1, c: 1, d: 0, tx: 0, ty: 0)
        default: .identity
        }
    }
}

/// How a video is re-encoded for a resolution and quality. Plain numbers, so
/// the editor's estimate is exactly what the encoder is asked for.
struct VideoCompressionPlan: Equatable, Sendable {
    enum Codec: Sendable {
        /// Plays everywhere; used for everything but HDR.
        case h264
        /// 10-bit, which HDR needs to keep its colours.
        case hevc

        /// Bits needed relative to H.264 at the same quality.
        var efficiency: Double { self == .hevc ? 0.65 : 1 }
    }

    enum Audio: Equatable, Sendable {
        /// The AAC track is copied as it is.
        case passthrough(bitRate: Int)
        case aac(bitRate: Int, channels: Int, sampleRate: Double)

        var bitRate: Int {
            switch self {
            case .passthrough(let bitRate), .aac(let bitRate, _, _): bitRate
            }
        }
    }

    /// H.264 at 1080p and 30 fps on High; other sizes scale from this.
    static let referenceBitRate: Double = 6_000_000
    static let referencePixels: Double = 1920 * 1080
    /// A re-encode never gets more than this share of the source's bits for
    /// the same picture: matching them would only lose quality, not size.
    static let sourceShare: Double = 0.8
    /// MP4 boxes and indexes on top of the media data.
    static let containerOverhead: Double = 1.01

    let codec: Codec
    /// Encoded frame size, in the track's own (unrotated) orientation.
    let encodedSize: CGSize
    /// The size players show, after rotation.
    let displaySize: CGSize
    /// The source's rotation, moved to fit `encodedSize`.
    let transform: CGAffineTransform
    let videoBitRate: Int
    let audio: [Audio]
    let estimatedBytes: Int64

    init(source: VideoSourceInfo, resolution: VideoResolution, quality: VideoQuality) {
        codec = source.isHDR ? .hevc : .h264
        encodedSize = Self.encodedSize(for: source.naturalSize, resolution: resolution)
        transform = Self.transform(source.preferredTransform, fitting: encodedSize)
        let shown = CGRect(origin: .zero, size: encodedSize).applying(transform)
        displaySize = CGSize(width: abs(shown.width).rounded(), height: abs(shown.height).rounded())
        videoBitRate = Self.videoBitRate(for: encodedSize, quality: quality, codec: codec, source: source)
        audio = source.audioTracks.map { Self.audio(for: $0, quality: quality) }

        let bitsPerSecond: Double = Double(videoBitRate + audio.reduce(0) { $0 + $1.bitRate })
        let bytes: Double = bitsPerSecond / 8 * max(source.duration, 0) * Self.containerOverhead
        estimatedBytes = Int64(bytes)
    }

    /// Fits `naturalSize` inside the resolution's bounds, keeping its shape,
    /// never enlarging, with even sides as video encoders need.
    static func encodedSize(for naturalSize: CGSize, resolution: VideoResolution) -> CGSize {
        let width = Double(abs(naturalSize.width))
        let height = Double(abs(naturalSize.height))
        var scale: Double = 1
        if let bounds = resolution.bounds, width > 0, height > 0 {
            let long: Double = max(width, height)
            let short: Double = min(width, height)
            scale = min(1, bounds.long / long, bounds.short / short)
        }
        return CGSize(width: evenSide(width * scale, atMost: width), height: evenSide(height * scale, atMost: height))
    }

    /// `value` rounded to the nearest even number, but not above `limit`.
    private static func evenSide(_ value: Double, atMost limit: Double) -> Double {
        let nearest: Double = (value / 2).rounded() * 2
        let largest: Double = (limit / 2).rounded(.down) * 2
        return max(2, min(nearest, largest))
    }

    /// The rotation part of `transform`, shifted so a frame of `size` lands at the origin.
    static func transform(_ transform: CGAffineTransform, fitting size: CGSize) -> CGAffineTransform {
        let rotation = CGAffineTransform(a: transform.a, b: transform.b, c: transform.c, d: transform.d, tx: 0, ty: 0)
        let rect = CGRect(origin: .zero, size: size).applying(rotation)
        return rotation.concatenating(CGAffineTransform(translationX: -rect.minX, y: -rect.minY))
    }

    /// Bits per second for the picture. Larger frames need fewer bits per
    /// pixel, so the rate grows with pixels^0.75; frame rates above 30 cost
    /// half as much again per frame. Capped by what the source itself spent.
    static func videoBitRate(for size: CGSize, quality: VideoQuality, codec: Codec, source: VideoSourceInfo) -> Int {
        let pixels = Double(size.width * size.height)
        let sizeScale: Double = pow(pixels / referencePixels, 0.75)
        let fps: Double = source.frameRate > 0 ? source.frameRate : 30
        let motion: Double = (fps <= 30 ? fps : 30 + (fps - 30) / 2) / 30
        var rate: Double = referenceBitRate * sizeScale * motion * quality.videoFactor * codec.efficiency

        if source.bitRate > 0 {
            let sourcePixels = Double(max(source.naturalSize.width * source.naturalSize.height, 1))
            let sourceCodec: Codec = source.isHEVC ? .hevc : .h264
            let asH264: Double = Double(source.bitRate) / sourceCodec.efficiency
            let cap: Double = asH264 * codec.efficiency * pow(pixels / sourcePixels, 0.75) * sourceShare
            rate = min(rate, cap)
        }
        return max(Int(rate), 100_000)
    }

    /// AAC tracks already at or below the target are copied untouched;
    /// anything else becomes AAC at the shared audio policy's rate.
    static func audio(for track: VideoSourceInfo.AudioTrack, quality: VideoQuality) -> Audio {
        let target = AudioBitRate.target(for: .aac, source: track.audio, limit: quality.audioLimit)
        if track.audio.codec == .aac {
            let rate = track.audio.bitRate ?? target
            if Double(rate) <= Double(target) * 1.1 { return .passthrough(bitRate: rate) }
        }
        // AAC tops out at 48 kHz; lower rates are lifted to 44.1 kHz.
        let sampleRate: Double = track.sampleRate >= 44_100 ? min(track.sampleRate, 48_000) : 44_100
        return .aac(bitRate: target, channels: min(track.audio.channels, 2), sampleRate: sampleRate)
    }
}

// MARK: - Encoding

extension MediaTools {
    /// Re-encodes a video into "<name> Compressed.mp4" as `VideoCompressionPlan`
    /// lays out, keeping its rotation and sound. Returns nil, keeping nothing,
    /// when the result isn't smaller than the original.
    static func compress(
        _ url: URL,
        resolution: VideoResolution,
        quality: VideoQuality,
        tools: ExternalTools = .current,
        progress: @escaping @Sendable (Double) -> Void
    ) async throws -> URL? {
        let source = try await VideoSourceInfo.load(url, tools: tools)
        let plan = VideoCompressionPlan(source: source, resolution: resolution, quality: quality)
        let destination = OutputNaming.destination(for: url, suffix: "Compressed", pathExtension: "mp4")
        try SaveError.checkWritable(destination)

        // Like `export`, write a hidden file and move it into place only once
        // it's complete and worth keeping. ffmpeg follows the same plan when
        // AVFoundation can't read the file, in a partial file of its own.
        let partial: URL = try await MediaFallback.run(url, tools: tools) {
            let partial = partialURL(for: destination)
            do {
                try await transcode(url, source: source, plan: plan, to: partial, progress: progress)
            } catch {
                discardPartial(partial)
                throw error
            }
            return partial
        } fallback: { ffmpeg in
            let partial = partialURL(for: destination)
            let arguments: [String] = FFmpegEdits.compress(url, plan: plan, source: source)
            do {
                try await FFmpegJob.run(ffmpeg, arguments, output: partial, duration: source.duration, progress: progress)
            } catch {
                discardPartial(partial)
                throw error
            }
            return partial
        }
        guard MediaSize.fileSize(partial) < source.fileSize else {
            discardPartial(partial)
            return nil
        }
        try moveIntoPlace(partial, destination: destination)
        progress(1)
        return destination
    }

    private static func transcode(
        _ url: URL,
        source: VideoSourceInfo,
        plan: VideoCompressionPlan,
        to output: URL,
        progress: @escaping @Sendable (Double) -> Void
    ) async throws {
        let asset = AVURLAsset(url: url)
        guard let videoTrack = try await asset.loadTracks(withMediaType: .video).first else { throw MediaToolError.noVideo(url) }
        let audioTracks = try await asset.loadTracks(withMediaType: .audio)
        let reader = try AVAssetReader(asset: asset)
        let writer = try AVAssetWriter(outputURL: output, fileType: .mp4)
        writer.shouldOptimizeForNetworkUse = true
        writer.metadata = await mp4Metadata(of: asset)

        let pixelFormat = plan.codec == .hevc ? kCVPixelFormatType_420YpCbCr10BiPlanarVideoRange : kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange
        let videoOutput = AVAssetReaderTrackOutput(track: videoTrack, outputSettings: [kCVPixelBufferPixelFormatTypeKey as String: pixelFormat])
        let videoInput = AVAssetWriterInput(mediaType: .video, outputSettings: videoSettings(for: plan, source: source))
        videoInput.transform = plan.transform
        var pairs: [(AVAssetReaderOutput, AVAssetWriterInput)] = [(videoOutput, videoInput)]

        for (track, audio) in zip(audioTracks, plan.audio) {
            switch audio {
            case .passthrough:
                let formats = try await track.load(.formatDescriptions)
                let input = AVAssetWriterInput(mediaType: .audio, outputSettings: nil, sourceFormatHint: formats.first)
                pairs.append((AVAssetReaderTrackOutput(track: track, outputSettings: nil), input))
            case .aac(let bitRate, let channels, let sampleRate):
                let layout = channelLayout(channels: channels)
                let pcm: [String: Any] = [
                    AVFormatIDKey: kAudioFormatLinearPCM,
                    AVSampleRateKey: sampleRate,
                    AVNumberOfChannelsKey: channels,
                    AVChannelLayoutKey: layout,
                ]
                let aac: [String: Any] = [
                    AVFormatIDKey: kAudioFormatMPEG4AAC,
                    AVSampleRateKey: sampleRate,
                    AVNumberOfChannelsKey: channels,
                    AVChannelLayoutKey: layout,
                    AVEncoderBitRateKey: bitRate,
                ]
                pairs.append((AVAssetReaderTrackOutput(track: track, outputSettings: pcm), AVAssetWriterInput(mediaType: .audio, outputSettings: aac)))
            }
        }

        for (readerOutput, writerInput) in pairs {
            readerOutput.alwaysCopiesSampleData = false
            writerInput.expectsMediaDataInRealTime = false
            guard reader.canAdd(readerOutput), writer.canAdd(writerInput) else { throw MediaToolError.cannotExport("smaller") }
            reader.add(readerOutput)
            writer.add(writerInput)
        }
        guard reader.startReading() else { throw reader.error ?? MediaToolError.cannotExport("smaller") }
        guard writer.startWriting() else {
            reader.cancelReading()
            throw writer.error ?? MediaToolError.cannotExport("smaller")
        }
        writer.startSession(atSourceTime: .zero)

        let job = TranscodeJob(reader: reader, writer: writer, pairs: pairs)
        let duration = source.duration
        let reporter = Task {
            while !Task.isCancelled {
                if duration > 0 { progress(min(job.videoSeconds / duration, 0.99)) }
                // A failure on one track won't wake the others, so stop them here.
                if job.hasFailed { job.stop() }
                try? await Task.sleep(for: .milliseconds(150))
            }
        }
        defer { reporter.cancel() }

        await withTaskCancellationHandler {
            await withTaskGroup(of: Void.self) { group in
                for pump in job.pumps {
                    group.addTask { await pump.run() }
                }
            }
        } onCancel: {
            job.stop()
        }

        if Task.isCancelled || job.hasFailed {
            reader.cancelReading()
            writer.cancelWriting()
            if Task.isCancelled { throw CancellationError() }
            throw writer.error ?? reader.error ?? MediaToolError.failed("Compressing failed.")
        }
        await writer.finishWriting()
        guard writer.status == .completed else {
            throw writer.error ?? MediaToolError.failed("Compressing failed.")
        }
    }

    private static func videoSettings(for plan: VideoCompressionPlan, source: VideoSourceInfo) -> [String: Any] {
        var compression: [String: Any] = [
            AVVideoAverageBitRateKey: plan.videoBitRate,
            AVVideoExpectedSourceFrameRateKey: source.frameRate > 0 ? Int(source.frameRate.rounded()) : 30,
            AVVideoMaxKeyFrameIntervalDurationKey: 2,
        ]
        var settings: [String: Any] = [
            AVVideoWidthKey: Int(plan.encodedSize.width),
            AVVideoHeightKey: Int(plan.encodedSize.height),
            // The sides were rounded to even numbers, so fill rather than letterbox a pixel.
            AVVideoScalingModeKey: AVVideoScalingModeResizeAspectFill,
        ]
        switch plan.codec {
        case .h264:
            settings[AVVideoCodecKey] = AVVideoCodecType.h264
            compression[AVVideoProfileLevelKey] = AVVideoProfileLevelH264HighAutoLevel
            compression[AVVideoH264EntropyModeKey] = AVVideoH264EntropyModeCABAC
        case .hevc:
            settings[AVVideoCodecKey] = AVVideoCodecType.hevc
            compression[AVVideoProfileLevelKey] = kVTProfileLevel_HEVC_Main10_AutoLevel as String
        }
        settings[AVVideoCompressionPropertiesKey] = compression
        // Keep the source's colours; HDR in particular must stay tagged as HDR.
        if let colors = source.colorProperties {
            settings[AVVideoColorPropertiesKey] = colors
        }
        return settings
    }

    /// When and where the video was filmed, and on what. Phones store these as
    /// QuickTime metadata, which an MP4 writer drops, so they're handed over
    /// by their common identifiers and the writer stores them the MP4 way.
    private static func mp4Metadata(of asset: AVAsset) async -> [AVMetadataItem] {
        let identifiers: [AVMetadataKey: AVMetadataIdentifier] = [
            .commonKeyCreationDate: .commonIdentifierCreationDate,
            .commonKeyLocation: .commonIdentifierLocation,
            .commonKeyMake: .commonIdentifierMake,
            .commonKeyModel: .commonIdentifierModel,
            .commonKeySoftware: .commonIdentifierSoftware,
        ]
        var items: [AVMetadataItem] = []
        for item in (try? await asset.load(.commonMetadata)) ?? [] {
            guard let key = item.commonKey, let identifier = identifiers[key],
                  !items.contains(where: { $0.identifier == identifier }),
                  let value = try? await item.load(.value)
            else { continue }
            let copy = AVMutableMetadataItem()
            copy.identifier = identifier
            copy.value = value
            copy.extendedLanguageTag = item.extendedLanguageTag
            items.append(copy)
        }
        return items
    }

    /// A mono or stereo layout, as the Data AVChannelLayoutKey expects.
    private static func channelLayout(channels: Int) -> Data {
        var layout = AudioChannelLayout()
        layout.mChannelLayoutTag = channels == 1 ? kAudioChannelLayoutTag_Mono : kAudioChannelLayoutTag_Stereo
        return Data(bytes: &layout, count: MemoryLayout<AudioChannelLayout>.size)
    }
}

/// A reader and writer with one pump per track. The AVFoundation objects are
/// safe to use this way but aren't marked Sendable.
private final class TranscodeJob: @unchecked Sendable {
    let reader: AVAssetReader
    let writer: AVAssetWriter
    let pumps: [SamplePump]
    private let lastVideoTime = OSAllocatedUnfairLock(initialState: 0.0)

    init(reader: AVAssetReader, writer: AVAssetWriter, pairs: [(AVAssetReaderOutput, AVAssetWriterInput)]) {
        self.reader = reader
        self.writer = writer
        var pumps: [SamplePump] = []
        for (index, pair) in pairs.enumerated() {
            pumps.append(SamplePump(output: pair.0, input: pair.1, writer: writer, label: "OpenShelf.compress.\(index)"))
        }
        self.pumps = pumps
        // The first pump is the picture; its timestamps drive progress.
        let lastVideoTime = lastVideoTime
        pumps.first?.onSample = { time in
            lastVideoTime.withLock { $0 = time.seconds }
        }
    }

    var videoSeconds: Double { lastVideoTime.withLock { $0 } }

    var hasFailed: Bool { reader.status == .failed || writer.status == .failed }

    func stop() {
        for pump in pumps { pump.stop() }
    }
}

/// Moves samples from a reader output into a writer input on its own queue
/// until the track ends, appending fails, or it's stopped.
private final class SamplePump: @unchecked Sendable {
    private let output: AVAssetReaderOutput
    private let input: AVAssetWriterInput
    private let writer: AVAssetWriter
    private let queue: DispatchQueue
    private let stopRequested = OSAllocatedUnfairLock(initialState: false)
    /// Set before `run()`; called on the pump's queue.
    var onSample: (@Sendable (CMTime) -> Void)?
    // Only touched on `queue`.
    private var isFinished = false
    private var continuation: CheckedContinuation<Void, Never>?

    init(output: AVAssetReaderOutput, input: AVAssetWriterInput, writer: AVAssetWriter, label: String) {
        self.output = output
        self.input = input
        self.writer = writer
        queue = DispatchQueue(label: label)
    }

    func run() async {
        await withCheckedContinuation { (continuation: CheckedContinuation<Void, Never>) in
            queue.async { [self] in
                guard !isFinished else { return continuation.resume() }
                self.continuation = continuation
                input.requestMediaDataWhenReady(on: queue) { [self] in drain() }
            }
        }
    }

    /// Ends the pump early. Safe from any thread.
    func stop() {
        stopRequested.withLock { $0 = true }
        queue.async { [self] in finish() }
    }

    private func drain() {
        while !isFinished && input.isReadyForMoreMediaData {
            guard !stopRequested.withLock({ $0 }),
                  let sample = output.copyNextSampleBuffer(),
                  input.append(sample)
            else { return finish() }
            onSample?(CMSampleBufferGetPresentationTimeStamp(sample))
        }
    }

    private func finish() {
        guard !isFinished else { return }
        isFinished = true
        if writer.status == .writing { input.markAsFinished() }
        continuation?.resume()
        continuation = nil
    }
}
