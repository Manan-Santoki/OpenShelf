@preconcurrency import AVFoundation
import CoreGraphics
import Foundation
import ImageIO
import Testing
import UniformTypeIdentifiers
@testable import OpenShelf

struct VideoGIFPlanTests {
    @Test func matchesTheFFmpegPath() {
        let plan = VideoGIF.plan(displayWidth: 640, displayHeight: 360, duration: 5)
        #expect(plan.width == 640 && plan.height == 360)
        #expect(plan.frameCount == 60)
        #expect(abs(plan.interval - 1.0 / 12) < 0.0001)
    }

    @Test func capsTheWidthAndKeepsTheHeightEven() {
        let plan = VideoGIF.plan(displayWidth: 1920, displayHeight: 1080, duration: 2)
        #expect(plan.width == 720)
        #expect(plan.height == 406)
        let portrait = VideoGIF.plan(displayWidth: 1080, displayHeight: 1920, duration: 2)
        #expect(portrait.width == 720 && portrait.height == 1280)
    }

    @Test func longVideosAreSampledLessOftenNotCut() {
        let plan = VideoGIF.plan(displayWidth: 640, displayHeight: 360, duration: 120)
        #expect(plan.frameCount == VideoGIF.maxFrames)
        #expect(abs(plan.time(ofFrame: plan.frameCount) - 120) < 0.001)
    }

    @Test func delaysAddUpWithoutDrift() {
        let plan = VideoGIF.plan(displayWidth: 640, displayHeight: 360, duration: 10)
        var total = 0
        for index in 0..<plan.frameCount {
            total += plan.centiseconds(atFrame: index + 1) - plan.centiseconds(atFrame: index)
        }
        #expect(total == 1000)
    }

    @Test func oddAndUnknownSizes() {
        #expect(VideoGIF.plan(displayWidth: 0, displayHeight: 0, duration: .nan).frameCount == 1)
        #expect(VideoGIF.plan(displayWidth: 321, displayHeight: 241, duration: 1).height % 2 == 0)
    }
}

struct GIFAnimationTests {
    private func singleImageGIF(width: Int, height: Int) throws -> Data {
        let context = CGContext(data: nil, width: width, height: height, bitsPerComponent: 8, bytesPerRow: 0,
                                space: CGColorSpace(name: CGColorSpace.sRGB)!,
                                bitmapInfo: CGImageAlphaInfo.premultipliedFirst.rawValue | CGBitmapInfo.byteOrder32Little.rawValue)!
        context.setFillColor(CGColor(red: 1, green: 0, blue: 0, alpha: 1))
        context.fill(CGRect(x: 0, y: 0, width: width / 2, height: height))
        let data = NSMutableData()
        let destination = CGImageDestinationCreateWithData(data, UTType.gif.identifier as CFString, 1, nil)!
        CGImageDestinationAddImage(destination, context.makeImage()!, nil)
        #expect(CGImageDestinationFinalize(destination))
        return data as Data
    }

    @Test func joinsFramesIntoALoopingAnimation() throws {
        var gif = GIFAnimation.header(width: 40, height: 30)
        gif.append(try GIFAnimation.frame(fromSingleImageGIF: singleImageGIF(width: 40, height: 30), delay: 8))
        gif.append(try GIFAnimation.frame(fromSingleImageGIF: singleImageGIF(width: 40, height: 30), delay: 9))
        gif.append(GIFAnimation.trailer)

        let source = try #require(CGImageSourceCreateWithData(gif as CFData, nil))
        #expect(CGImageSourceGetCount(source) == 2)
        let file = CGImageSourceCopyProperties(source, nil) as? [CFString: Any]
        let gifInfo = file?[kCGImagePropertyGIFDictionary] as? [CFString: Any]
        #expect(gifInfo?[kCGImagePropertyGIFLoopCount] as? Int == 0)
        let second = CGImageSourceCopyPropertiesAtIndex(source, 1, nil) as? [CFString: Any]
        let delay = (second?[kCGImagePropertyGIFDictionary] as? [CFString: Any])?[kCGImagePropertyGIFUnclampedDelayTime] as? Double
        #expect(delay == 0.09)
        let image = try #require(CGImageSourceCreateImageAtIndex(source, 1, nil))
        #expect(image.width == 40 && image.height == 30)
    }

    @Test func rejectsWhatIsntAGIF() {
        #expect(throws: GIFAnimation.FormatError.self) {
            try GIFAnimation.frame(fromSingleImageGIF: Data("not a gif at all".utf8), delay: 8)
        }
        #expect(throws: GIFAnimation.FormatError.self) {
            try GIFAnimation.frame(fromSingleImageGIF: Data([0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 1, 0, 1, 0, 0x80, 0, 0, 1, 2]), delay: 8)
        }
    }
}

/// Native GIFs from a real clip, without ffmpeg.
struct VideoGIFConversionTests {
    static let clip = URL(fileURLWithPath: ProcessInfo.processInfo.environment["OPENSHELF_TEST_CLIP"]
        ?? "/private/tmp/claude-501/-Users-manansantoki/13918f21-eb35-438a-8c6e-9a66dcd78487/scratchpad/uitest/Files/Clip.mp4")
    static let ffmpeg = URL(fileURLWithPath: "/opt/homebrew/bin/ffmpeg")
    static var hasClip: Bool { FileManager.default.fileExists(atPath: clip.path) }
    static var canRotate: Bool { hasClip && FileManager.default.isExecutableFile(atPath: ffmpeg.path) }

    private func copyClip() throws -> (folder: URL, clip: URL) {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("gif-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let copy = folder.appendingPathComponent("Clip.mp4")
        try FileManager.default.copyItem(at: Self.clip, to: copy)
        return (folder, copy)
    }

    @Test(.enabled(if: VideoGIFConversionTests.hasClip))
    func makesALoopingGIFOfTheWholeClip() async throws {
        let (folder, clip) = try copyClip()
        defer { try? FileManager.default.removeItem(at: folder) }
        let progress = ProgressRecorder()

        let output = try await MediaConversion.convert(clip, to: .gif, tools: ExternalTools()) { progress.record($0) }

        #expect(output.lastPathComponent == "Clip.gif")
        let source = try #require(CGImageSourceCreateWithURL(output as CFURL, nil))
        // 5 s at 12 frames a second.
        #expect(CGImageSourceGetCount(source) == 60)
        let file = CGImageSourceCopyProperties(source, nil) as? [CFString: Any]
        #expect((file?[kCGImagePropertyGIFDictionary] as? [CFString: Any])?[kCGImagePropertyGIFLoopCount] as? Int == 0)
        let first = try #require(CGImageSourceCreateImageAtIndex(source, 0, nil))
        #expect(first.width == 640 && first.height == 360)

        var total = 0.0
        for index in 0..<CGImageSourceGetCount(source) {
            let properties = CGImageSourceCopyPropertiesAtIndex(source, index, nil) as? [CFString: Any]
            total += (properties?[kCGImagePropertyGIFDictionary] as? [CFString: Any])?[kCGImagePropertyGIFUnclampedDelayTime] as? Double ?? 0
        }
        #expect(abs(total - 5) < 0.05)

        let size = try #require(try FileManager.default.attributesOfItem(atPath: output.path)[.size] as? Int)
        #expect(size < 4_000_000)
        #expect(progress.values.last == 1)
        #expect(progress.values == progress.values.sorted())
        // Nothing but the original and the GIF: no partial files left behind.
        #expect(try FileManager.default.contentsOfDirectory(atPath: folder.path).sorted() == ["Clip.gif", "Clip.mp4"])
    }

    @Test(.enabled(if: VideoGIFConversionTests.canRotate))
    func portraitVideoStaysUpright() async throws {
        let (folder, clip) = try copyClip()
        defer { try? FileManager.default.removeItem(at: folder) }
        // The same pixels, tagged to be shown rotated, as phones record portrait video.
        let rotated = folder.appendingPathComponent("Rotated.mp4")
        let process = try Process.run(Self.ffmpeg, arguments: ["-loglevel", "error", "-display_rotation", "90", "-i", clip.path, "-c", "copy", rotated.path])
        process.waitUntilExit()
        try #require(process.terminationStatus == 0)

        let output = try await MediaConversion.convert(rotated, to: .gif, tools: ExternalTools()) { _ in }

        let source = try #require(CGImageSourceCreateWithURL(output as CFURL, nil))
        let frame = try #require(CGImageSourceCreateImageAtIndex(source, 0, nil))
        #expect(frame.width == 360 && frame.height == 640)

        // Compare with AVFoundation's own upright rendering of the first frame.
        let generator = AVAssetImageGenerator(asset: AVURLAsset(url: rotated))
        generator.appliesPreferredTrackTransform = true
        generator.requestedTimeToleranceBefore = .zero
        generator.requestedTimeToleranceAfter = .zero
        let expected = try await generator.image(at: .zero).image
        #expect(expected.width == 360 && expected.height == 640)
        #expect(meanDifference(frame, expected) < 0.08)
        // And it really is turned: the unrotated picture doesn't match.
        let flipped = try #require(rotated180(expected))
        #expect(meanDifference(frame, flipped) > 0.2)
    }

    @Test(.enabled(if: VideoGIFConversionTests.hasClip))
    func cancellingLeavesNothingBehind() async throws {
        let (folder, clip) = try copyClip()
        defer { try? FileManager.default.removeItem(at: folder) }
        let task = Task {
            try await MediaConversion.convert(clip, to: .gif, tools: ExternalTools()) { _ in }
        }
        task.cancel()
        _ = await task.result
        try await Task.sleep(for: .seconds(2.5))
        #expect(try FileManager.default.contentsOfDirectory(atPath: folder.path) == ["Clip.mp4"])
    }

    /// Mean per-channel difference (0...1) of two images scaled to 24 × 24.
    private func meanDifference(_ a: CGImage, _ b: CGImage) -> Double {
        let left = pixels(a)
        let right = pixels(b)
        var sum = 0.0
        for index in left.indices {
            sum += abs(Double(left[index]) - Double(right[index])) / 255
        }
        return sum / Double(left.count)
    }

    private func pixels(_ image: CGImage) -> [UInt8] {
        let side = 24
        var data = [UInt8](repeating: 0, count: side * side * 4)
        data.withUnsafeMutableBytes { buffer in
            let context = CGContext(data: buffer.baseAddress, width: side, height: side, bitsPerComponent: 8, bytesPerRow: side * 4,
                                    space: CGColorSpace(name: CGColorSpace.sRGB)!, bitmapInfo: CGImageAlphaInfo.noneSkipLast.rawValue)!
            context.interpolationQuality = .medium
            context.draw(image, in: CGRect(x: 0, y: 0, width: side, height: side))
        }
        return data
    }

    private func rotated180(_ image: CGImage) -> CGImage? {
        let context = CGContext(data: nil, width: image.width, height: image.height, bitsPerComponent: 8, bytesPerRow: 0,
                                space: CGColorSpace(name: CGColorSpace.sRGB)!, bitmapInfo: CGImageAlphaInfo.noneSkipLast.rawValue)
        context?.translateBy(x: CGFloat(image.width), y: CGFloat(image.height))
        context?.rotate(by: .pi)
        context?.draw(image, in: CGRect(x: 0, y: 0, width: image.width, height: image.height))
        return context?.makeImage()
    }
}

private final class ProgressRecorder: @unchecked Sendable {
    private let lock = NSLock()
    private var recorded: [Double] = []

    func record(_ value: Double) {
        lock.withLock { recorded.append(value) }
    }

    var values: [Double] { lock.withLock { recorded } }
}
