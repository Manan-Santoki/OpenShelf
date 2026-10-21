@preconcurrency import AVFoundation
import CoreGraphics
import Foundation
import ImageIO
import UniformTypeIdentifiers
import VideoToolbox

/// Animated GIFs from video without ffmpeg: frames are read with AVFoundation,
/// each one is encoded by ImageIO, and the pieces are joined into one looping
/// GIF (see `GIFAnimation`).
///
/// ImageIO can write an animated GIF in one go, but it keeps every frame
/// uncompressed until the end, close to 900 MB for 600 frames at 720 px, so
/// frames are encoded one at a time instead. ffmpeg's two-pass palette still
/// looks better, so it's used when installed (see `MediaConversion`).
enum VideoGIF {
    /// The same rate and width cap as the ffmpeg path (`FFmpegArguments`).
    static let framesPerSecond: Double = 12
    static let maxWidth: Double = 720
    /// Longer videos are sampled less often rather than cut short, so the GIF
    /// shows the whole clip at its real speed without growing without bound.
    static let maxFrames = 600

    /// Size and timing of the GIF made from a video.
    struct Plan: Equatable {
        /// Output size in pixels.
        var width: Int
        var height: Int
        var frameCount: Int
        /// Seconds between frames.
        var interval: Double

        /// When frame `index` starts, in seconds from the beginning.
        func time(ofFrame index: Int) -> Double {
            Double(index) * interval
        }

        /// The same, in the hundredths of a second GIF delays are counted in.
        /// Delays are differences of these, so rounding never adds up to drift.
        func centiseconds(atFrame index: Int) -> Int {
            Int((time(ofFrame: index) * 100).rounded())
        }
    }

    /// Like ffmpeg's `fps=12,scale='min(720,iw)':-2`: at most 720 px wide, the
    /// height kept in proportion and even, and at most `maxFrames` frames.
    static func plan(displayWidth: Double, displayHeight: Double, duration: Double) -> Plan {
        let sourceWidth: Double = max(displayWidth.rounded(), 2)
        let sourceHeight: Double = max(displayHeight.rounded(), 2)
        let width: Double = min(maxWidth, sourceWidth)
        let scaledHeight: Double = sourceHeight * width / sourceWidth
        let height: Double = max((scaledHeight / 2).rounded() * 2, 2)

        let seconds: Double = duration.isFinite && duration > 0 ? duration : 0
        let natural = Int((seconds * framesPerSecond).rounded(.up))
        let frameCount = min(max(natural, 1), maxFrames)
        var interval: Double = 1 / framesPerSecond
        if natural > maxFrames {
            interval = seconds / Double(maxFrames)
        }
        return Plan(width: Int(width), height: Int(height), frameCount: frameCount, interval: interval)
    }

    /// Writes `source` as a looping GIF at `destination`, through a hidden
    /// partial file that only appears once it's complete.
    static func write(
        _ source: URL,
        to destination: URL,
        progress: @escaping @Sendable (Double) -> Void
    ) async throws {
        let asset = AVURLAsset(url: source)
        guard let track = try await asset.loadTracks(withMediaType: .video).first else {
            throw ConversionError.failed("“\(source.lastPathComponent)” has no video to make a GIF from.")
        }
        let (naturalSize, transform) = try await track.load(.naturalSize, .preferredTransform)
        let duration = try await asset.load(.duration).seconds
        let oriented = CGRect(origin: .zero, size: naturalSize).applying(transform)
        let plan = plan(displayWidth: abs(oriented.width), displayHeight: abs(oriented.height), duration: duration)

        let partial = MediaTools.partialURL(for: destination)
        let job = GIFJob(asset: asset, track: track, naturalSize: naturalSize, transform: transform, plan: plan, output: partial, progress: progress)
        do {
            try await withTaskCancellationHandler {
                try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
                    // Decoding and encoding are synchronous; keep them off the
                    // concurrency pool's few threads.
                    DispatchQueue.global(qos: .userInitiated).async {
                        do {
                            try job.run()
                            continuation.resume()
                        } catch {
                            continuation.resume(throwing: error)
                        }
                    }
                }
            } onCancel: {
                job.cancel()
            }
            try Task.checkCancellation()
        } catch {
            MediaTools.discardPartial(partial)
            throw error
        }
        try MediaTools.moveIntoPlace(partial, destination: destination)
        progress(1)
    }

    /// The transform that draws a decoded frame (`naturalSize`, stored
    /// orientation) upright and scaled into a `width` × `height` context.
    /// Video transforms work top-down and Core Graphics bottom-up, so the
    /// frame is flipped in, rotated and scaled, then flipped back out.
    static func drawingTransform(naturalSize: CGSize, preferredTransform: CGAffineTransform, width: Int, height: Int) -> CGAffineTransform {
        let oriented = CGRect(origin: .zero, size: naturalSize).applying(preferredTransform)
        let upright = preferredTransform.concatenating(CGAffineTransform(translationX: -oriented.minX, y: -oriented.minY))
        let scaleX: CGFloat = CGFloat(width) / max(abs(oriented.width), 1)
        let scaleY: CGFloat = CGFloat(height) / max(abs(oriented.height), 1)
        let flipIn = CGAffineTransform(a: 1, b: 0, c: 0, d: -1, tx: 0, ty: naturalSize.height)
        let flipOut = CGAffineTransform(a: 1, b: 0, c: 0, d: -1, tx: 0, ty: CGFloat(height))
        return flipIn
            .concatenating(upright)
            .concatenating(CGAffineTransform(scaleX: scaleX, y: scaleY))
            .concatenating(flipOut)
    }
}

/// One GIF being made: reads the video in order and keeps, for each frame
/// slot, the picture on screen at that moment. A picture that covers several
/// slots (a slow source) is written once with a longer delay.
private final class GIFJob: @unchecked Sendable {
    private let asset: AVAsset
    private let track: AVAssetTrack
    private let naturalSize: CGSize
    private let plan: VideoGIF.Plan
    private let output: URL
    private let progress: @Sendable (Double) -> Void
    private let transform: CGAffineTransform

    private let lock = NSLock()
    private var isCancelled = false
    private var reader: AVAssetReader?

    init(
        asset: AVAsset,
        track: AVAssetTrack,
        naturalSize: CGSize,
        transform: CGAffineTransform,
        plan: VideoGIF.Plan,
        output: URL,
        progress: @escaping @Sendable (Double) -> Void
    ) {
        self.asset = asset
        self.track = track
        self.naturalSize = naturalSize
        self.plan = plan
        self.output = output
        self.progress = progress
        self.transform = VideoGIF.drawingTransform(naturalSize: naturalSize, preferredTransform: transform, width: plan.width, height: plan.height)
    }

    func cancel() {
        lock.withLock {
            isCancelled = true
            reader?.cancelReading()
        }
    }

    private var cancelled: Bool {
        lock.withLock { isCancelled }
    }

    func run() throws {
        let reader = try AVAssetReader(asset: asset)
        let trackOutput = AVAssetReaderTrackOutput(track: track, outputSettings: [
            kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_32BGRA,
        ])
        trackOutput.alwaysCopiesSampleData = false
        guard reader.canAdd(trackOutput) else { throw ConversionError.failed("This video can’t be read.") }
        reader.add(trackOutput)
        lock.withLock { self.reader = reader }
        guard !cancelled else { throw CancellationError() }
        guard reader.startReading() else { throw reader.error ?? ConversionError.failed("This video can’t be read.") }
        defer { reader.cancelReading() }

        let writer = try GIFAnimation.Writer(url: output, width: plan.width, height: plan.height)
        defer { writer.close() }
        var held: CVPixelBuffer?
        var nextFrame = 0

        // Writes the held picture for every slot that starts before `time`.
        func writeHeld(upTo time: Double) throws {
            guard let picture = held else { return }
            var end = nextFrame
            while end < plan.frameCount && plan.time(ofFrame: end) < time {
                end += 1
            }
            guard end > nextFrame else { return }
            let delay = plan.centiseconds(atFrame: end) - plan.centiseconds(atFrame: nextFrame)
            try writer.add(try encode(picture), delay: delay)
            nextFrame = end
            progress(Double(nextFrame) / Double(plan.frameCount))
        }

        while nextFrame < plan.frameCount, let sample = trackOutput.copyNextSampleBuffer() {
            guard !cancelled else { throw CancellationError() }
            guard let buffer = CMSampleBufferGetImageBuffer(sample) else { continue }
            try autoreleasepool {
                try writeHeld(upTo: CMSampleBufferGetPresentationTimeStamp(sample).seconds)
            }
            held = buffer
        }
        guard !cancelled else { throw CancellationError() }
        if reader.status == .failed {
            throw reader.error ?? ConversionError.failed("This video can’t be read.")
        }
        try autoreleasepool {
            try writeHeld(upTo: .infinity)
        }
        guard writer.frameCount > 0 else { throw ConversionError.failed("No frames could be read from this video.") }
        try writer.finish()
    }

    /// One picture, upright and scaled, as a single-image GIF.
    private func encode(_ buffer: CVPixelBuffer) throws -> Data {
        var decoded: CGImage?
        VTCreateCGImageFromCVPixelBuffer(buffer, options: nil, imageOut: &decoded)
        guard let decoded,
              let space = CGColorSpace(name: CGColorSpace.sRGB),
              let context = CGContext(
                  data: nil,
                  width: plan.width,
                  height: plan.height,
                  bitsPerComponent: 8,
                  bytesPerRow: 0,
                  space: space,
                  bitmapInfo: CGImageAlphaInfo.premultipliedFirst.rawValue | CGBitmapInfo.byteOrder32Little.rawValue
              )
        else { throw ConversionError.failed("A frame of this video couldn’t be read.") }
        context.interpolationQuality = .high
        context.concatenate(transform)
        context.draw(decoded, in: CGRect(origin: .zero, size: naturalSize))
        guard let frame = context.makeImage() else { throw ConversionError.failed("A frame of this video couldn’t be read.") }

        let data = NSMutableData()
        guard let destination = CGImageDestinationCreateWithData(data, UTType.gif.identifier as CFString, 1, nil) else {
            throw ConversionError.failed("GIFs can’t be written on this Mac.")
        }
        CGImageDestinationAddImage(destination, frame, nil)
        guard CGImageDestinationFinalize(destination) else { throw ConversionError.failed("A frame couldn’t be encoded as GIF.") }
        return data as Data
    }
}

/// Joins single-image GIFs, as ImageIO writes them, into one looping animation.
/// Each image keeps its own 256-colour palette, as a local colour table.
enum GIFAnimation {
    enum FormatError: Error {
        case malformed
    }

    /// "GIF89a", a logical screen without a global palette, and the NETSCAPE
    /// extension that makes it loop (`loopCount` 0 loops forever).
    static func header(width: Int, height: Int, loopCount: Int = 0) -> Data {
        var data = Data("GIF89a".utf8)
        data.append(littleEndian: width)
        data.append(littleEndian: height)
        // No global colour table; background colour 0; square pixels.
        data.append(contentsOf: [0x00, 0x00, 0x00])
        data.append(contentsOf: [0x21, 0xFF, 0x0B])
        data.append(contentsOf: Array("NETSCAPE2.0".utf8))
        data.append(contentsOf: [0x03, 0x01])
        data.append(littleEndian: loopCount)
        data.append(0x00)
        return data
    }

    static let trailer = Data([0x3B])

    /// The image in a single-image GIF as an animation frame shown for
    /// `delay` hundredths of a second: a graphic control block, then the image
    /// with the file's global palette moved into a local one.
    static func frame(fromSingleImageGIF gif: Data, delay: Int) throws -> Data {
        let bytes = [UInt8](gif)
        guard bytes.count > 13, bytes[0] == 0x47, bytes[1] == 0x49, bytes[2] == 0x46 else { throw FormatError.malformed }
        let screenFlags = bytes[10]
        var index = 13
        var globalTable: ArraySlice<UInt8> = []
        if screenFlags & 0x80 != 0 {
            let size = 3 << (Int(screenFlags & 0x07) + 1)
            guard index + size <= bytes.count else { throw FormatError.malformed }
            globalTable = bytes[index..<index + size]
            index += size
        }

        while index < bytes.count {
            switch bytes[index] {
            case 0x21:
                // An extension (ImageIO's own timing block): skip its sub-blocks.
                index = try skipSubBlocks(bytes, from: index + 2)
            case 0x2C:
                guard index + 10 <= bytes.count else { throw FormatError.malformed }
                let imageFlags = bytes[index + 9]
                var table: ArraySlice<UInt8>
                var flags: UInt8
                var dataStart = index + 10
                if imageFlags & 0x80 != 0 {
                    let size = 3 << (Int(imageFlags & 0x07) + 1)
                    guard dataStart + size <= bytes.count else { throw FormatError.malformed }
                    table = bytes[dataStart..<dataStart + size]
                    flags = imageFlags
                    dataStart += size
                } else {
                    guard !globalTable.isEmpty else { throw FormatError.malformed }
                    table = globalTable
                    // Local table present, interlacing kept, the global table's size.
                    flags = 0x80 | (imageFlags & 0x40) | (screenFlags & 0x07)
                }
                // LZW minimum code size, then the data sub-blocks.
                let dataEnd = try skipSubBlocks(bytes, from: dataStart + 1)

                var frame = Data([0x21, 0xF9, 0x04, 0x04])
                frame.append(littleEndian: min(max(delay, 2), 0xFFFF))
                frame.append(contentsOf: [0x00, 0x00])
                frame.append(contentsOf: bytes[index..<index + 9])
                frame.append(flags)
                frame.append(contentsOf: table)
                frame.append(contentsOf: bytes[dataStart..<dataEnd])
                return frame
            default:
                throw FormatError.malformed
            }
        }
        throw FormatError.malformed
    }

    /// The index just past the sub-blocks starting at `start` and their terminator.
    private static func skipSubBlocks(_ bytes: [UInt8], from start: Int) throws -> Int {
        var index = start
        while index < bytes.count {
            let length = Int(bytes[index])
            index += 1
            if length == 0 { return index }
            index += length
        }
        throw FormatError.malformed
    }

    /// Streams an animation to disk one frame at a time.
    final class Writer {
        private let handle: FileHandle
        private(set) var frameCount = 0

        init(url: URL, width: Int, height: Int, loopCount: Int = 0) throws {
            guard FileManager.default.createFile(atPath: url.path, contents: nil) else {
                throw SaveError.from(CocoaError(.fileWriteUnknown), writing: url)
            }
            handle = try FileHandle(forWritingTo: url)
            try handle.write(contentsOf: GIFAnimation.header(width: width, height: height, loopCount: loopCount))
        }

        /// Adds a single-image GIF as the next frame.
        func add(_ gif: Data, delay: Int) throws {
            try handle.write(contentsOf: GIFAnimation.frame(fromSingleImageGIF: gif, delay: delay))
            frameCount += 1
        }

        func finish() throws {
            try handle.write(contentsOf: GIFAnimation.trailer)
            try handle.synchronize()
        }

        func close() {
            try? handle.close()
        }
    }
}

private extension Data {
    mutating func append(littleEndian value: Int) {
        append(UInt8(truncatingIfNeeded: value))
        append(UInt8(truncatingIfNeeded: value >> 8))
    }
}
