import CoreGraphics
import Foundation
import Testing
@testable import OpenShelf

/// Offering and writing WebP without making the wheel wait for ffmpeg.
struct WebPEncoderTests {
    private let cwebp = URL(fileURLWithPath: "/opt/homebrew/bin/cwebp")
    private let ffmpeg = URL(fileURLWithPath: "/opt/homebrew/bin/ffmpeg")

    @Test func offersWebPUnlessFFmpegIsKnownToLackLibwebp() {
        #expect(WebPEncoder.isOffered(cwebp: cwebp, ffmpeg: nil, ffmpegEncoders: nil))
        #expect(WebPEncoder.isOffered(cwebp: cwebp, ffmpeg: ffmpeg, ffmpegEncoders: ["libx264"]))
        // Not asked yet: offered, and checked before encoding.
        #expect(WebPEncoder.isOffered(cwebp: nil, ffmpeg: ffmpeg, ffmpegEncoders: nil))
        #expect(WebPEncoder.isOffered(cwebp: nil, ffmpeg: ffmpeg, ffmpegEncoders: ["libwebp", "libx264"]))
        #expect(!WebPEncoder.isOffered(cwebp: nil, ffmpeg: ffmpeg, ffmpegEncoders: ["libx264", "aac"]))
        #expect(!WebPEncoder.isOffered(cwebp: nil, ffmpeg: nil, ffmpegEncoders: nil))
    }

    @Test func choosesCwebpFirstAndExplainsAnFFmpegWithoutLibwebp() throws {
        #expect(try WebPEncoder.choose(cwebp: cwebp, ffmpeg: ffmpeg, ffmpegEncoders: []) == .cwebp(cwebp))
        #expect(try WebPEncoder.choose(cwebp: nil, ffmpeg: ffmpeg, ffmpegEncoders: ["libwebp"]) == .ffmpeg(ffmpeg))
        do {
            _ = try WebPEncoder.choose(cwebp: nil, ffmpeg: ffmpeg, ffmpegEncoders: ["libx264"])
            Issue.record("An ffmpeg without libwebp can't write WebP")
        } catch {
            #expect(error.localizedDescription == "This ffmpeg can’t write WebP. Install cwebp, or ffmpeg with libwebp.")
        }
    }

    @Test func readsTheEncoderList() {
        let listing = """
        Encoders:
         V..... = Video
         A..... = Audio
         ------
         V....D libx264              libx264 H.264 / AVC / MPEG-4 AVC / MPEG-4 part 10 (codec h264)
         V....D libwebp_anim         libwebp WebP image (codec webp)
         V....D libwebp              libwebp WebP image (codec webp)
         A....D aac                  AAC (Advanced Audio Coding)
        """
        let encoders = FFmpegEncoders.parse(listing)
        #expect(encoders.isSuperset(of: ["libx264", "libwebp", "libwebp_anim", "aac"]))
        #expect(!encoders.contains("="))
    }

    /// The wheel reads only what's already known, and a conversion checks
    /// before running anything.
    @Test func usesTheCachedAnswerWithoutRunningFFmpeg() async throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("openshelf-webp-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        // A stand-in that would leave a mark if it were ever run.
        let fake = directory.appendingPathComponent("ffmpeg")
        let marker = directory.appendingPathComponent("ran")
        try Data("#!/bin/sh\ntouch '\(marker.path)'\nexit 1\n".utf8).write(to: fake)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: fake.path)
        let tools = ExternalTools(ffmpeg: fake, cwebp: nil)

        #expect(FFmpegEncoders.known(in: fake) == nil)
        #expect(tools.canEncodeWebP)
        FFmpegEncoders.record(["libx264", "aac"], for: fake)
        #expect(!tools.canEncodeWebP)
        #expect(ConversionCatalog.formats(for: [directory.appendingPathComponent("photo.png")], tools: tools).contains(.webp) == false)

        let image = directory.appendingPathComponent("photo.png")
        let context = try #require(CGContext(data: nil, width: 8, height: 8, bitsPerComponent: 8, bytesPerRow: 0, space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue))
        try EditableImage.write(try #require(context.makeImage()), to: image, type: .png)
        await #expect(throws: ConversionError.self) {
            _ = try await ImageConversion.convert(image, to: .webp, tools: tools)
        }
        #expect(!FileManager.default.fileExists(atPath: marker.path))
        #expect(!FileManager.default.fileExists(atPath: directory.appendingPathComponent("photo.webp").path))

        FFmpegEncoders.record(["libwebp"], for: fake)
        #expect(tools.canEncodeWebP)
    }
}
