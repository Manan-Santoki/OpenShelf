@preconcurrency import AVFoundation
import CoreGraphics
import Foundation
import Testing
@testable import OpenShelf

private func near(_ value: Double?, _ expected: Double, within tolerance: Double = 0.0001) -> Bool {
    guard let value else { return false }
    return abs(value - expected) <= tolerance
}

/// Trim's "Same for all": the same seconds cut from every file.
struct BatchTrimTests {
    @Test func sameCutsOnFilesOfAnyLength() {
        // Two seconds off the start and three off the end of the shown file.
        let long = TrimModel.range(cuttingStart: 2, end: 3, duration: 60)
        #expect(near(long?.start, 2) && near(long?.end, 57))
        let short = TrimModel.range(cuttingStart: 2, end: 3, duration: 10.5)
        #expect(near(short?.start, 2) && near(short?.end, 7.5))
    }

    @Test func untouchedEndsKeepTheWholeFile() {
        // Nothing cut from the end keeps the very last frames, not a tenth.
        let range = TrimModel.range(cuttingStart: 1.5, end: 0, duration: 30.033)
        #expect(near(range?.start, 1.5) && range?.end == 30.033)
        let whole = TrimModel.range(cuttingStart: 0, end: 0, duration: 12.34)
        #expect(whole?.start == 0 && whole?.end == 12.34)
        #expect(!TrimModel.changes(start: 0, end: 12.34, duration: 12.34))
    }

    @Test func endsLandOnShownTenths() {
        let range = TrimModel.range(cuttingStart: 0.04, end: 0.73, duration: 20.07)
        #expect(near(range?.start, 0))
        #expect(near(range?.end, 19.3))
        #expect(MediaTime.format(range!.end) == "0:19.3")
    }

    @Test func filesShorterThanTheCutsAreLeftOut() {
        #expect(TrimModel.range(cuttingStart: 5, end: 5, duration: 9.9) == nil)
        #expect(TrimModel.range(cuttingStart: 12, end: 0, duration: 10) == nil)
        #expect(TrimModel.range(cuttingStart: 0, end: 10, duration: 10) == nil)
        // A tenth left is still something.
        let sliver = TrimModel.range(cuttingStart: 4.9, end: 5, duration: 10)
        #expect(near(sliver?.start, 4.9) && near(sliver?.end, 5))
    }

    @Test func onlyRealCutsCount() {
        #expect(TrimModel.changes(start: 1, end: 10, duration: 10))
        #expect(TrimModel.changes(start: 0, end: 9.5, duration: 10))
        #expect(!TrimModel.changes(start: 0, end: 10, duration: 10))
        // An empty selection (a file cut away entirely) isn't a trim.
        #expect(!TrimModel.changes(start: 4, end: 4, duration: 10))
    }
}

/// Split's "Same for all": one list of times for files of different lengths.
struct BatchSplitTests {
    @Test func timesPastTheEndAreLeftOut() throws {
        let segments = try SplitPlan.at("0:30, 1:15, 2:00", inside: 90)
        #expect(segments == [SplitSegment(start: 0, end: 30), SplitSegment(start: 30, end: 75), SplitSegment(start: 75, end: 90)])
        // A file only as long as a cut doesn't get an empty last part.
        #expect(try SplitPlan.at("0:30, 1:15", inside: 75) == [SplitSegment(start: 0, end: 30), SplitSegment(start: 30, end: 75)])
    }

    @Test func aFileNoTimeReachesStaysWhole() throws {
        #expect(try SplitPlan.at("0:30, 1:15", inside: 20).isEmpty)
    }

    @Test func unreadableTimesAreStillAProblem() {
        #expect(throws: SplitPlanError.unreadable("soon")) { try SplitPlan.at("0:30, soon", inside: 90) }
        #expect(throws: SplitPlanError.noCuts) { try SplitPlan.at("0", inside: 90) }
    }
}

/// Crop Video's "Same for all": one crop as fractions of each frame.
struct BatchVideoCropTests {
    @Test func topHalfOfEveryFrame() {
        let hd = CGRect(x: 0, y: 0, width: 1920, height: 1080)
        let unit = CropMath.unit(CGRect(x: 0, y: 0, width: 1920, height: 540), in: hd)
        #expect(CropMath.mapped(unit, to: CGRect(x: 0, y: 0, width: 1280, height: 720), ratio: nil) == CGRect(x: 0, y: 0, width: 1280, height: 360))
        // A portrait phone video keeps the top half too.
        #expect(CropMath.mapped(unit, to: CGRect(x: 0, y: 0, width: 1080, height: 1920), ratio: nil) == CGRect(x: 0, y: 0, width: 1080, height: 960))
    }

    @Test func squareCropStaysSquareOnEveryShape() {
        let hd = CGRect(x: 0, y: 0, width: 1920, height: 1080)
        let square = CropMath.largest(ratio: 1, centeredAt: CGPoint(x: 960, y: 540), in: hd)
        let unit = CropMath.unit(square, in: hd)
        for size in [CGSize(width: 1080, height: 1920), CGSize(width: 640, height: 480), CGSize(width: 3840, height: 1600)] {
            let bounds = CGRect(origin: .zero, size: size)
            let mapped = CropMath.mapped(unit, to: bounds, ratio: 1)
            #expect(bounds.contains(mapped))
            #expect(mapped.width == mapped.height)
        }
    }
}

/// Running several exports one after another.
struct BatchExportTests {
    @Test func progressRunsAcrossEveryFile() {
        #expect(MediaBatch.overall(0.5, index: 1, count: 4) == 0.375)
        #expect(MediaBatch.overall(0, index: 0, count: 3) == 0)
        #expect(MediaBatch.overall(1, index: 2, count: 3) == 1)
        #expect(MediaBatch.overall(2, index: 0, count: 2) == 0.5)
    }

    @Test func collectsEveryFileWrittenAndCountsTheRest() async throws {
        let outcome = try await MediaBatch.run(2, into: SaveOutcome(saved: [], unchanged: 1, savedLabel: "trimmed", unchangedLabel: "not trimmed"), progress: { _, _ in }) { index, progress in
            progress(1)
            return [URL(fileURLWithPath: "/tmp/\(index).mov")]
        }
        #expect(outcome.saved.map(\.lastPathComponent) == ["0.mov", "1.mov"])
        #expect(outcome.title == "2 trimmed · 1 not trimmed")
    }

    @Test func stopsAtTheFirstError() async {
        let written = Counter()
        await #expect(throws: MediaToolError.self) {
            _ = try await MediaBatch.run(3, into: SaveOutcome(saved: []), progress: { _, _ in }) { index, _ in
                guard index < 1 else { throw MediaToolError.failed("no") }
                await written.add()
                return [URL(fileURLWithPath: "/tmp/\(index).mov")]
            }
        }
        #expect(await written.value == 1)
    }
}

private actor Counter {
    private(set) var value = 0
    func add() { value += 1 }
}

/// Exports real clips, so it checks what players will see.
struct BatchMediaEndToEndTests {
    /// Writes a plain H.264 clip with every frame a keyframe, so cuts land exactly.
    private func makeClip(_ name: String, in directory: URL, size: CGSize, seconds: Int) async throws -> URL {
        let url = directory.appendingPathComponent(name)
        let writer = try AVAssetWriter(outputURL: url, fileType: .mov)
        let input = AVAssetWriterInput(mediaType: .video, outputSettings: [
            AVVideoCodecKey: AVVideoCodecType.h264,
            AVVideoWidthKey: Int(size.width),
            AVVideoHeightKey: Int(size.height),
            AVVideoCompressionPropertiesKey: [AVVideoMaxKeyFrameIntervalKey: 1],
        ])
        input.expectsMediaDataInRealTime = false
        let adaptor = AVAssetWriterInputPixelBufferAdaptor(assetWriterInput: input, sourcePixelBufferAttributes: [
            kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_32BGRA,
            kCVPixelBufferWidthKey as String: Int(size.width),
            kCVPixelBufferHeightKey as String: Int(size.height),
        ])
        writer.add(input)
        #expect(writer.startWriting())
        writer.startSession(atSourceTime: .zero)
        for frame in 0..<(seconds * 10) {
            while !input.isReadyForMoreMediaData { try await Task.sleep(for: .milliseconds(5)) }
            var buffer: CVPixelBuffer?
            CVPixelBufferPoolCreatePixelBuffer(nil, adaptor.pixelBufferPool!, &buffer)
            guard let pixels = buffer else { throw MediaToolError.failed("no pixel buffer") }
            CVPixelBufferLockBaseAddress(pixels, [])
            memset(CVPixelBufferGetBaseAddress(pixels), Int32(frame * 8 % 256), CVPixelBufferGetDataSize(pixels))
            CVPixelBufferUnlockBaseAddress(pixels, [])
            adaptor.append(pixels, withPresentationTime: CMTime(value: CMTimeValue(frame), timescale: 10))
        }
        input.markAsFinished()
        await writer.finishWriting()
        #expect(writer.status == .completed)
        return url
    }

    private func duration(of url: URL) async throws -> Double {
        try await AVURLAsset(url: url).load(.duration).seconds
    }

    @Test func trimsCropsAndSplitsTwoClipsTheSameWay() async throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("openshelf-batchmedia-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let clips = [
            try await makeClip("Wide.mov", in: directory, size: CGSize(width: 320, height: 180), seconds: 2),
            try await makeClip("Tall.mov", in: directory, size: CGSize(width: 180, height: 320), seconds: 3),
        ]

        // Half a second off each end of both clips.
        var jobs: [(URL, Double, Double)] = []
        for clip in clips {
            let length = try await duration(of: clip)
            guard let range = TrimModel.range(cuttingStart: 0.5, end: 0.5, duration: length) else { continue }
            jobs.append((clip, range.start, range.end))
        }
        #expect(jobs.count == 2)
        let trimmed = try await MediaBatch.run(jobs.count, into: SaveOutcome(saved: []), progress: { _, _ in }) { index, progress in
            let range = CMTimeRange(start: MediaTime.cmTime(jobs[index].1), end: MediaTime.cmTime(jobs[index].2))
            return [try await MediaTools.trim(jobs[index].0, range: range, progress: progress)]
        }
        #expect(trimmed.saved.map(\.lastPathComponent) == ["Wide Trimmed.mov", "Tall Trimmed.mov"])
        #expect(abs(try await duration(of: trimmed.saved[0]) - 1) < 0.15)
        #expect(abs(try await duration(of: trimmed.saved[1]) - 2) < 0.15)

        // The top half of each frame.
        let topHalf = CGRect(x: 0, y: 0, width: 1, height: 0.5)
        var crops: [(URL, CGRect)] = []
        for clip in clips {
            let size = try await MediaTools.displaySize(of: clip)
            crops.append((clip, CropMath.mapped(topHalf, to: CGRect(origin: .zero, size: size), ratio: nil)))
        }
        let cropped = try await MediaBatch.run(crops.count, into: SaveOutcome(saved: []), progress: { _, _ in }) { index, progress in
            [try await MediaTools.crop(crops[index].0, to: crops[index].1, progress: progress)]
        }
        #expect(try await MediaTools.displaySize(of: cropped.saved[0]) == CGSize(width: 320, height: 90))
        #expect(try await MediaTools.displaySize(of: cropped.saved[1]) == CGSize(width: 180, height: 160))

        // The same split times, where each clip reaches them.
        var splits: [(URL, [SplitSegment])] = []
        for clip in clips {
            splits.append((clip, try SplitPlan.at("0:01, 0:02.5", inside: try await duration(of: clip))))
        }
        let parts = try await MediaBatch.run(splits.count, into: SaveOutcome(saved: []), progress: { _, _ in }) { index, progress in
            try await MediaTools.split(splits[index].0, into: splits[index].1) { fraction, _ in progress(fraction) }
        }
        #expect(parts.saved.map(\.lastPathComponent) == ["Wide Part 1.mov", "Wide Part 2.mov", "Tall Part 1.mov", "Tall Part 2.mov", "Tall Part 3.mov"])
        #expect(abs(try await duration(of: parts.saved[4]) - 0.5) < 0.15)
    }
}
