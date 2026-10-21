@preconcurrency import AVFoundation
import CoreGraphics
import Foundation
import Testing
@testable import OpenShelf

struct VideoCompressionPlanTests {
    private func size(_ natural: CGSize, _ resolution: VideoResolution) -> CGSize {
        VideoCompressionPlan.encodedSize(for: natural, resolution: resolution)
    }

    /// A 30-second 720p clip like the one QA used: 3.1 Mb/s H.264, mono AAC.
    private var clip: VideoSourceInfo {
        var info = VideoSourceInfo(naturalSize: CGSize(width: 1280, height: 720), duration: 30, fileSize: 12_002_657)
        info.bitRate = 3_122_526
        info.audioTracks = [.init(audio: SourceAudio(channels: 1, bitRate: 69_355, codec: .aac), sampleRate: 44_100)]
        return info
    }

    @Test func eachResolutionFitsItsFrame() {
        let hd = CGSize(width: 1920, height: 1080)
        #expect(size(hd, .original) == hd)
        #expect(size(hd, .p1080) == hd)
        #expect(size(hd, .p720) == CGSize(width: 1280, height: 720))
        #expect(size(hd, .p480) == CGSize(width: 854, height: 480))
        let fourByThree = CGSize(width: 1440, height: 1080)
        #expect(size(fourByThree, .p720) == CGSize(width: 960, height: 720))
        #expect(size(fourByThree, .p480) == CGSize(width: 640, height: 480))
    }

    @Test func neverEnlarges() {
        let small = CGSize(width: 1280, height: 720)
        #expect(size(small, .p1080) == small)
        #expect(size(small, .p720) == small)
        #expect(size(CGSize(width: 640, height: 360), .p480) == CGSize(width: 640, height: 360))
    }

    @Test func portraitUsesTheSameFrameTurned() {
        #expect(size(CGSize(width: 1080, height: 1920), .p720) == CGSize(width: 720, height: 1280))
        #expect(size(CGSize(width: 1080, height: 1920), .p480) == CGSize(width: 480, height: 854))
    }

    @Test func sidesAreAlwaysEven() {
        let odd = CGSize(width: 1281, height: 721)
        #expect(size(odd, .original) == CGSize(width: 1280, height: 720))
        for natural in [odd, CGSize(width: 1917, height: 1079), CGSize(width: 333, height: 777), CGSize(width: 3840, height: 1607)] {
            for resolution in VideoResolution.allCases {
                let result = size(natural, resolution)
                #expect(Int(result.width) % 2 == 0 && Int(result.height) % 2 == 0)
                #expect(result.width <= natural.width && result.height <= natural.height)
            }
        }
    }

    @Test func rotationIsKeptForTheNewSize() {
        // A portrait phone video: landscape frames turned a quarter.
        var phone = clip
        phone.naturalSize = CGSize(width: 1920, height: 1080)
        phone.preferredTransform = CGAffineTransform(a: 0, b: 1, c: -1, d: 0, tx: 1080, ty: 0)
        let plan = VideoCompressionPlan(source: phone, resolution: .p720, quality: .medium)
        #expect(plan.encodedSize == CGSize(width: 1280, height: 720))
        #expect(plan.displaySize == CGSize(width: 720, height: 1280))
        #expect(plan.transform == CGAffineTransform(a: 0, b: 1, c: -1, d: 0, tx: 720, ty: 0))
        #expect(phone.displaySize == CGSize(width: 1080, height: 1920))
    }

    @Test func qualityLowersTheBitrate() {
        let rates = VideoQuality.allCases.map { VideoCompressionPlan(source: clip, resolution: .p480, quality: $0).videoBitRate }
        #expect(rates[0] > rates[1] && rates[1] > rates[2])
        // Smaller frames get fewer bits.
        let at720 = VideoCompressionPlan(source: clip, resolution: .p720, quality: .medium).videoBitRate
        #expect(at720 > rates[1])
    }

    @Test func neverSpendsMoreThanTheSource() {
        var efficient = clip
        efficient.bitRate = 1_000_000
        for resolution in VideoResolution.allCases {
            for quality in VideoQuality.allCases {
                let plan = VideoCompressionPlan(source: efficient, resolution: resolution, quality: quality)
                #expect(plan.videoBitRate <= 800_000)
                #expect(plan.estimatedBytes < efficient.fileSize)
            }
        }
    }

    @Test func theClipQAUsedGetsSmallerAtEverySetting() {
        for resolution in VideoResolution.allCases {
            for quality in VideoQuality.allCases {
                #expect(VideoCompressionPlan(source: clip, resolution: resolution, quality: quality).estimatedBytes < clip.fileSize)
            }
        }
        let medium720 = VideoCompressionPlan(source: clip, resolution: .p720, quality: .medium)
        #expect(medium720.estimatedBytes < clip.fileSize * 3 / 4)
        // Its small mono AAC is copied, not re-encoded.
        #expect(medium720.audio == [.passthrough(bitRate: 69_355)])
    }

    @Test func richAudioIsReencoded() {
        var loud = clip
        loud.audioTracks = [.init(audio: SourceAudio(channels: 6, bitRate: 640_000), sampleRate: 96_000)]
        let plan = VideoCompressionPlan(source: loud, resolution: .p720, quality: .medium)
        #expect(plan.audio == [.aac(bitRate: 128_000, channels: 2, sampleRate: 48_000)])
        #expect(VideoCompressionPlan(source: loud, resolution: .p720, quality: .low).audio.first?.bitRate == 96_000)
    }

    @Test func hdrStaysHDR() {
        var hdr = clip
        hdr.isHDR = true
        hdr.isHEVC = true
        #expect(VideoCompressionPlan(source: hdr, resolution: .p720, quality: .high).codec == .hevc)
        #expect(VideoCompressionPlan(source: clip, resolution: .p720, quality: .high).codec == .h264)
    }

    @Test func adviceSaysWhetherAnythingHelps() {
        // Tiny already: even the lowest setting can't beat it.
        var tiny = clip
        tiny.fileSize = 10_000
        #expect(VideoCompressionPlan.advice(for: [tiny], originalSize: tiny.fileSize) == "Already as small as it gets.")
        // Big audio, small picture: going lower helps.
        var talk = clip
        talk.fileSize = VideoCompressionPlan.estimatedBytes(for: [clip], resolution: .p720, quality: .medium)
        #expect(VideoCompressionPlan.advice(for: [talk], originalSize: talk.fileSize).contains("Try a lower"))
    }

    @Test func notSmallerMatchesTheImageCompressor() {
        #expect(MediaToolError.notSmaller(count: 1).localizedDescription == CompressError.notSmaller(count: 1).localizedDescription)
        #expect(MediaToolError.notSmaller(count: 2).localizedDescription == "These 2 videos are already as small as they get — nothing saved.")
    }
}

struct AudioBitRateTests {
    @Test func voiceMemoStaysSmall() {
        // The 55 KB voice memo QA converted: mono AAC at 33 kb/s.
        let voice = SourceAudio(channels: 1, bitRate: 33_333, codec: .aac)
        #expect(AudioBitRate.target(for: .opus, source: voice) == 32_000)
        #expect(FFmpegArguments.arguments(for: .ogg, encoders: ["libopus"], audio: voice).suffix(2) == ["-b:a", "32k"])
    }

    @Test func cleanSourcesGetTheCodecCeiling() {
        let cd = SourceAudio(channels: 2, bitRate: 1_411_200)
        #expect(AudioBitRate.target(for: .opus, source: cd) == 128_000)
        #expect(AudioBitRate.target(for: .vorbis, source: cd) == 160_000)
        #expect(AudioBitRate.target(for: .aac, source: cd) == 192_000)
        #expect(AudioBitRate.target(for: .mp3, source: cd) == 192_000)
        #expect(AudioBitRate.target(for: .opus, source: SourceAudio(channels: 1, bitRate: 705_600)) == 64_000)
        // Unknown sources are treated as clean stereo.
        #expect(AudioBitRate.target(for: .opus, source: nil) == 128_000)
    }

    @Test func lossySourcesAreNotInflated() {
        let podcast = SourceAudio(channels: 2, bitRate: 96_000, codec: .aac)
        #expect(AudioBitRate.target(for: .opus, source: podcast) == 96_000)
        #expect(AudioBitRate.target(for: .aac, source: podcast) == 96_000)
        // MP3 needs a little more for the same sound, and gives a little less.
        #expect(AudioBitRate.target(for: .mp3, source: podcast) == 112_000)
        let mp3 = SourceAudio(channels: 1, bitRate: 64_000, codec: .mp3)
        #expect(AudioBitRate.target(for: .opus, source: mp3) == 48_000)
        #expect(AudioBitRate.target(for: .mp3, source: mp3) == 64_000)
        #expect(AudioBitRate.target(for: .wma, source: mp3) == 64_000)
        // Never below what the codec needs to sound decent.
        #expect(AudioBitRate.target(for: .aac, source: SourceAudio(channels: 2, bitRate: 24_000)) == 64_000)
    }

    @Test func everyRateIsACommonOne() {
        for codec in [AudioBitRate.Codec.aac, .opus, .vorbis, .mp3, .wma] {
            for channels in [1, 2, 6] {
                for kbps in [8, 33, 70, 129, 250, 1411] {
                    let rate = AudioBitRate.target(for: codec, source: SourceAudio(channels: channels, bitRate: kbps * 1000))
                    #expect(AudioBitRate.steps.contains(rate / 1000))
                }
            }
        }
    }

    @Test func ffprobeOutputIsRead() {
        let ogg = Data(#"{"streams":[{"codec_type":"audio","codec_name":"vorbis","channels":1}],"format":{"bit_rate":"48000"}}"#.utf8)
        #expect(SourceAudio.parse(ffprobeJSON: ogg) == SourceAudio(channels: 1, bitRate: 48_000, codec: .vorbis))
        // With video in the file, the overall rate says nothing about the sound.
        let mkv = Data(#"{"streams":[{"codec_type":"video"},{"codec_type":"audio","channels":2}],"format":{"bit_rate":"5000000"}}"#.utf8)
        #expect(SourceAudio.parse(ffprobeJSON: mkv) == SourceAudio(channels: 2, bitRate: nil))
        #expect(SourceAudio.parse(ffprobeJSON: Data(#"{"streams":[{"codec_type":"video"}]}"#.utf8)) == nil)
    }
}

struct TrimTimeTests {
    @Test func formatRoundsBeforeSplittingMinutes() {
        #expect(MediaTime.format(59.97) == "1:00.0")
        #expect(MediaTime.format(62.5) == "1:02.5")
        #expect(MediaTime.format(3.96) == "0:04.0")
        #expect(MediaTime.format(-1) == "0:00.0")
    }

    @Test func selectionSnapsToWhatIsShown() {
        // QA's case: a selection shown as 0:01.0 – 0:04.0 read "Length 0:02.9".
        let start = TrimModel.clampedStart(1.04, end: 10)
        let end = TrimModel.clampedEnd(3.96, start: start, duration: 30.033)
        #expect(MediaTime.format(start) == "0:01.0" && MediaTime.format(end) == "0:04.0")
        #expect(MediaTime.format(MediaTime.length(from: start, to: end)) == "0:03.0")
        // What's exported is exactly what's shown.
        let range = CMTimeRange(start: MediaTime.cmTime(start), end: MediaTime.cmTime(end))
        #expect(abs(range.duration.seconds - 3) < 0.002)
    }

    @Test func endOfFileIsKept() {
        #expect(TrimModel.clampedEnd(30.02, start: 0, duration: 30.033) == 30.033)
        #expect(TrimModel.clampedEnd(40, start: 0, duration: 30.07) == 30.07)
        #expect(TrimModel.clampedEnd(30.06, start: 0, duration: 30.07) == 30.07)
    }

    @Test func selectionStaysATenthLong() {
        let end = TrimModel.clampedEnd(1.0, start: 1.0, duration: 30)
        #expect(abs(end - 1.1) < 0.0001)
        #expect(abs(TrimModel.clampedStart(5, end: 2.0) - 1.9) < 0.0001)
        #expect(TrimModel.clampedStart(-3, end: 2.0) == 0)
    }

    @Test func shownLengthAlwaysMatchesShownTimes() {
        for step in 0..<500 {
            let start = Double(step) * 0.0371
            let end = start + Double(step % 37) * 0.093 + 0.1
            let shown = MediaTime.format(MediaTime.length(from: start, to: end))
            let difference = MediaTime.parse(MediaTime.format(end))! - MediaTime.parse(MediaTime.format(start))!
            #expect(shown == MediaTime.format(difference))
        }
    }
}

/// Encodes real files, so it checks what players will see.
struct VideoCompressEndToEndTests {
    /// Writes a noisy H.264 clip at a high bitrate, optionally turned for portrait.
    private func makeClip(in directory: URL, size: CGSize, transform: CGAffineTransform, bitRate: Int) async throws -> URL {
        let url = directory.appendingPathComponent("Clip.mov")
        let writer = try AVAssetWriter(outputURL: url, fileType: .mov)
        let input = AVAssetWriterInput(mediaType: .video, outputSettings: [
            AVVideoCodecKey: AVVideoCodecType.h264,
            AVVideoWidthKey: Int(size.width),
            AVVideoHeightKey: Int(size.height),
            AVVideoCompressionPropertiesKey: [AVVideoAverageBitRateKey: bitRate],
        ])
        input.transform = transform
        input.expectsMediaDataInRealTime = false
        let adaptor = AVAssetWriterInputPixelBufferAdaptor(assetWriterInput: input, sourcePixelBufferAttributes: [
            kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_32BGRA,
            kCVPixelBufferWidthKey as String: Int(size.width),
            kCVPixelBufferHeightKey as String: Int(size.height),
        ])
        writer.add(input)
        #expect(writer.startWriting())
        writer.startSession(atSourceTime: .zero)
        // Noise, so the encoder really spends the bits it's given.
        var state: UInt64 = 0x9E37_79B9_7F4A_7C15
        for frame in 0..<45 {
            while !input.isReadyForMoreMediaData { try await Task.sleep(for: .milliseconds(5)) }
            var buffer: CVPixelBuffer?
            CVPixelBufferPoolCreatePixelBuffer(nil, adaptor.pixelBufferPool!, &buffer)
            let pixels = try #require(buffer)
            CVPixelBufferLockBaseAddress(pixels, [])
            let words = CVPixelBufferGetBaseAddress(pixels)!.assumingMemoryBound(to: UInt64.self)
            for index in 0..<CVPixelBufferGetDataSize(pixels) / 8 {
                state ^= state << 13
                state ^= state >> 7
                state ^= state << 17
                words[index] = state
            }
            CVPixelBufferUnlockBaseAddress(pixels, [])
            adaptor.append(pixels, withPresentationTime: CMTime(value: CMTimeValue(frame), timescale: 30))
        }
        input.markAsFinished()
        await writer.finishWriting()
        #expect(writer.status == .completed)
        return url
    }

    @Test func compressesToTheChosenSizeKeepingRotation() async throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("openshelf-compress-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let portrait = CGAffineTransform(a: 0, b: 1, c: -1, d: 0, tx: 720, ty: 0)
        let source = try await makeClip(in: directory, size: CGSize(width: 1280, height: 720), transform: portrait, bitRate: 20_000_000)

        let output = try #require(try await MediaTools.compress(source, resolution: .p480, quality: .medium) { _ in })
        #expect(output.lastPathComponent == "Clip Compressed.mp4")
        let track = try #require(try await AVURLAsset(url: output).loadTracks(withMediaType: .video).first)
        let (naturalSize, transform) = try await track.load(.naturalSize, .preferredTransform)
        #expect(naturalSize == CGSize(width: 854, height: 480))
        #expect(try await MediaTools.displaySize(of: output) == CGSize(width: 480, height: 854))
        #expect(transform.b == 1 && transform.c == -1)
        #expect(MediaSize.fileSize(output) < MediaSize.fileSize(source))
        // Nothing hidden is left behind.
        let leftovers = try FileManager.default.contentsOfDirectory(atPath: directory.path).filter { $0.hasPrefix(".openshelf-") }
        #expect(leftovers.isEmpty)
    }

    @Test func cancellingLeavesNoFile() async throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("openshelf-compress-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let source = try await makeClip(in: directory, size: CGSize(width: 1280, height: 720), transform: .identity, bitRate: 20_000_000)

        let started = AsyncStream<Void>.makeStream()
        let task = Task {
            try await MediaTools.compress(source, resolution: .original, quality: .high) { _ in started.continuation.yield() }
        }
        for await _ in started.stream { break }
        task.cancel()
        await #expect(throws: CancellationError.self) { try await task.value }
        let names = try FileManager.default.contentsOfDirectory(atPath: directory.path)
        #expect(names == ["Clip.mov"])
    }
}
