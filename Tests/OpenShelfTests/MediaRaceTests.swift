import Foundation
import Testing
@testable import OpenShelf

/// Regressions adapted from David NeSmith's b7f0f32 and ab60495.
struct MediaRaceTests {
    static let ffmpeg = URL(fileURLWithPath: "/opt/homebrew/bin/ffmpeg")

    @Test(.enabled(if: FileManager.default.isExecutableFile(atPath: ffmpeg.path)))
    func ffmpegFramesOutliveTheirTemporaryFile() async throws {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("openshelf-frame-test-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: folder) }
        let clip = folder.appendingPathComponent("clip.mp4")
        try await ProcessRunner.run(Self.ffmpeg, ["-y", "-nostdin", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=size=320x240:rate=10:duration=2", "-c:v", "libx264", clip.path])
        let image = try await MediaLoading.frame(of: clip, at: 1, maximumSize: 400, ffmpeg: Self.ffmpeg)
        let saved = folder.appendingPathComponent("frame.png")
        try EditableImage.write(image, to: saved, type: .png)
        #expect(try EditableImage.load(saved).cgImage.width == 320)
    }

    @Test func progressStopsWhenTheJobEnds() {
        let log = RecordedProgress()
        let gate = ProgressGate { log.add($0) }
        gate.report(0.5)
        gate.close()
        gate.report(0.9)
        #expect(log.values == [0.5])
    }
}

private final class RecordedProgress: @unchecked Sendable {
    private let lock = NSLock()
    private var recorded: [Double] = []
    func add(_ value: Double) { lock.withLock { recorded.append(value) } }
    var values: [Double] { lock.withLock { recorded } }
}
