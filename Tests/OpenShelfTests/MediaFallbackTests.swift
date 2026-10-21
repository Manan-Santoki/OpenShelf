import Foundation
import Testing
import CoreMedia
import AVFoundation
@testable import OpenShelf

struct MediaFallbackTests {
    @Test func fallsBackToFFmpegWhenNativeExportFails() async throws {
        let tools = ExternalTools.detect()
        guard tools.ffmpeg != nil else { return }

        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("openshelf-fallback-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: directory) }

        // Create an MPEG-TS video stream named with .mp4 extension
        let fakeMp4 = directory.appendingPathComponent("stream.mp4")
        try await ProcessRunner.run(
            tools.ffmpeg!,
            ["-y", "-f", "lavfi", "-i", "testsrc=duration=1:size=160x120:rate=15",
             "-f", "lavfi", "-i", "sine=frequency=1000:duration=1",
             "-c:v", "libx264", "-c:a", "aac", "-f", "mpegts", fakeMp4.path]
        )

        // Converting this .mp4 natively with AVFoundation fails, but with our fallback to FFmpeg it succeeds!
        let mov = try await MediaConversion.convert(fakeMp4, to: .mov, tools: tools) { _ in }
        #expect(FileManager.default.fileExists(atPath: mov.path))
        let movSize = (try? FileManager.default.attributesOfItem(atPath: mov.path)[.size] as? Int64) ?? 0
        #expect(movSize > 0)

        // Mute tool fallback
        let muted = try await MediaTools.mute(fakeMp4) { _ in }
        #expect(FileManager.default.fileExists(atPath: muted.path))

        // Compress tool fallback
        let compressed = try await MediaTools.compress(fakeMp4, preset: AVAssetExportPresetMediumQuality) { _ in }
        #expect(FileManager.default.fileExists(atPath: compressed.path))

        // Rotate tool fallback
        let rotated = try await MediaTools.rotate(fakeMp4, by: .right) { _ in }
        #expect(FileManager.default.fileExists(atPath: rotated.path))
    }
}
