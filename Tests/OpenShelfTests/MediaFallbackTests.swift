@preconcurrency import AVFoundation
import CoreGraphics
import Foundation
import Testing
@testable import OpenShelf

/// Which failures send a file to ffmpeg.
struct MediaFallbackDecisionTests {
    private let withFFmpeg = ExternalTools(ffmpeg: URL(fileURLWithPath: "/opt/homebrew/bin/ffmpeg"), cwebp: nil)
    private let withoutFFmpeg = ExternalTools(ffmpeg: nil, cwebp: nil)

    private func avError(_ code: Int, underlying: NSError? = nil) -> NSError {
        var info: [String: Any] = [:]
        if let underlying { info[NSUnderlyingErrorKey] = underlying }
        return NSError(domain: AVFoundationErrorDomain, code: code, userInfo: info)
    }

    @Test func fallsBackWhenAVFoundationCantReadTheFile() {
        let unreadable: [Error] = [
            // "Cannot Open", as for an MPEG-TS stream named .mp4.
            avError(-11829, underlying: NSError(domain: NSOSStatusErrorDomain, code: -12848)),
            avError(-11828),
            avError(-11833),
            avError(-11821),
            avError(-11855),
            // An export session that can't read its source.
            avError(-11800, underlying: NSError(domain: NSOSStatusErrorDomain, code: -16979)),
            MediaToolError.cannotExport("as MOV"),
            ProcessFailure(tool: "afconvert", message: "Error: Couldn't open input file ('typ?')"),
            NSError(domain: "com.apple.coreaudio.avfaudio", code: 1_954_115_647),
        ]
        for error in unreadable {
            #expect(MediaFallback.shouldUseFFmpeg(after: error, tools: withFFmpeg, isCancelled: false), "\(error)")
        }
    }

    @Test func neverFallsBackOnceCancelled() {
        #expect(!MediaFallback.shouldUseFFmpeg(after: CancellationError(), tools: withFFmpeg, isCancelled: false))
        #expect(!MediaFallback.shouldUseFFmpeg(after: CancellationError(), tools: withFFmpeg, isCancelled: true))
        // An unreadable file whose job was cancelled meanwhile still stops.
        #expect(!MediaFallback.shouldUseFFmpeg(after: avError(-11829), tools: withFFmpeg, isCancelled: true))
        // AVErrorOperationCancelled.
        #expect(!MediaFallback.shouldUseFFmpeg(after: avError(-11878), tools: withFFmpeg, isCancelled: false))
    }

    @Test func doesntFallBackForFailuresFFmpegWouldShare() {
        let others: [Error] = [
            avError(-11807),
            avError(-11800, underlying: NSError(domain: NSPOSIXErrorDomain, code: Int(ENOSPC))),
            avError(-11831),
            avError(-11823),
            SaveError.diskFull,
            MediaToolError.noVideo(URL(fileURLWithPath: "/tmp/a.mp4")),
            MediaToolError.notSmaller(count: 1),
            ProcessFailure(tool: "afconvert", message: "Error: Couldn't open output file ('wht?')"),
            ProcessFailure(tool: "ffmpeg", message: "Invalid data found when processing input"),
            NSError(domain: NSCocoaErrorDomain, code: NSFileWriteNoPermissionError),
        ]
        for error in others {
            #expect(!MediaFallback.shouldUseFFmpeg(after: error, tools: withFFmpeg, isCancelled: false), "\(error)")
        }
    }

    @Test func needsFFmpeg() {
        #expect(!MediaFallback.shouldUseFFmpeg(after: avError(-11829), tools: withoutFFmpeg, isCancelled: false))
    }

    @Test func runUsesFFmpegOnlyWhenAVFoundationCantRead() async throws {
        let calls = CallLog()
        let native: String = try await MediaFallback.run(nil, tools: withFFmpeg) {
            "native"
        } fallback: { _ in
            calls.add("fallback")
            return "ffmpeg"
        }
        #expect(native == "native")

        let fallback: String = try await MediaFallback.run(nil, tools: withFFmpeg) {
            throw avError(-11829)
        } fallback: { ffmpeg in
            #expect(ffmpeg == withFFmpeg.ffmpeg)
            return "ffmpeg"
        }
        #expect(fallback == "ffmpeg")

        await #expect(throws: CancellationError.self) {
            try await MediaFallback.run(nil, tools: withFFmpeg) {
                throw CancellationError()
            } fallback: { _ in
                calls.add("fallback")
            }
        }
        #expect(calls.entries.isEmpty)
    }

    @Test func aCancelledJobNeverFallsBack() async {
        let calls = CallLog()
        let tools = withFFmpeg
        let task = Task {
            try await MediaFallback.run(nil, tools: tools) {
                // AVFoundation gives up "unreadable" after the cancel.
                while !Task.isCancelled { await Task.yield() }
                throw NSError(domain: AVFoundationErrorDomain, code: -11800)
            } fallback: { _ in
                calls.add("fallback")
            }
        }
        task.cancel()
        let result = await task.result
        #expect(throws: (any Error).self) { try result.get() }
        #expect(calls.entries.isEmpty)
    }

    @Test func withoutFFmpegSaysItMightHelp() async {
        let source = URL(fileURLWithPath: "/tmp/stream.mp4")
        await #expect(throws: UnreadableMediaError(url: source)) {
            try await MediaFallback.run(source, tools: withoutFFmpeg) {
                throw avError(-11829)
            } fallback: { _ in }
        }
        #expect(UnreadableMediaError(url: source).localizedDescription.contains("“stream.mp4”"))
        // Failures that aren't about reading are left as they are.
        await #expect(throws: SaveError.diskFull) {
            try await MediaFallback.run(source, tools: withoutFFmpeg) {
                throw SaveError.diskFull
            } fallback: { _ in }
        }
    }
}

/// The ffmpeg arguments behind each tool's fallback.
struct FFmpegEditsTests {
    private func audio(_ codec: String = "aac", channels: Int = 2, sampleRate: Double = 48_000, bitRate: Int? = 128_000) -> ProbedMedia.Audio {
        ProbedMedia.Audio(codec: codec, channels: channels, sampleRate: sampleRate, bitRate: bitRate)
    }

    private func video(rotation: Int = 0, codec: String = "h264") -> ProbedMedia.Video {
        ProbedMedia.Video(width: 1920, height: 1080, rotation: rotation, frameRate: "30000/1001", bitRate: 8_000_000, codec: codec, pixelFormat: "yuv420p")
    }

    @Test func atempoIsChainedOutsideHalfToDouble() {
        #expect(FFmpegEdits.atempoChain(2) == "atempo=2")
        #expect(FFmpegEdits.atempoChain(1.25) == "atempo=1.25")
        #expect(FFmpegEdits.atempoChain(4) == "atempo=2,atempo=2")
        #expect(FFmpegEdits.atempoChain(3) == "atempo=2,atempo=1.5")
        #expect(FFmpegEdits.atempoChain(0.25) == "atempo=0.5,atempo=0.5")
        #expect(FFmpegEdits.atempoChain(0.3) == "atempo=0.5,atempo=0.6")
    }

    @Test func varispeedScalesTheRateAndResamplesBack() {
        #expect(FFmpegEdits.varispeed(2, sampleRate: 44_100) == "asetrate=88200,aresample=44100")
        #expect(FFmpegEdits.varispeed(0.5, sampleRate: 48_000) == "asetrate=24000,aresample=48000")
    }

    @Test func speedHandlesPictureSoundOrBoth() {
        let both = FFmpegEdits.speed(ProbedMedia(duration: 10, video: video(), audio: [audio()]), rate: 2, keepsPitch: true)
        let graph = both[1]
        #expect(graph == "[0:V:0]setpts=PTS/2[v];[0:a:0]atempo=2[a0]")
        #expect(both.contains("[v]") && both.contains("[a0]"))
        // Every frame stays: 29.97 fps played twice as fast.
        #expect(both.contains("-r") && both.contains("59.9401"))

        let silent = FFmpegEdits.speed(ProbedMedia(duration: 10, video: video(), audio: []), rate: 2, keepsPitch: false)
        #expect(silent[1] == "[0:V:0]setpts=PTS/2[v]")
        #expect(!silent.contains { $0.contains("0:a") || $0.contains("[a") })

        let sound = FFmpegEdits.speed(ProbedMedia(duration: 10, video: nil, audio: [audio(sampleRate: 44_100), audio("mp3")]), rate: 0.5, keepsPitch: false)
        #expect(sound[1] == "[0:a:0]asetrate=22050,aresample=44100[a0];[0:a:1]asetrate=24000,aresample=48000[a1]")
        #expect(!sound.contains("[v]") && !sound.contains("-c:v"))
        #expect(sound.contains("-c:a:1") && sound.contains("aac"))
    }

    @Test func lossyAudioFollowsTheSharedBitratePolicy() {
        let mono = audio("mp3", channels: 1, sampleRate: 44_100, bitRate: 64_000)
        let expected = AudioBitRate.argument(for: .aac, source: mono.source)
        #expect(FFmpegEdits.aac(mono, index: 0) == ["-c:a:0", "aac", "-b:a:0", expected])
        #expect(FFmpegEdits.audioCopyOrAAC([audio(), mono]) == ["-c:a:0", "copy", "-c:a:1", "aac", "-b:a:1", expected])
    }

    @Test func trimsSeekNearThenDecodeToTheCut() {
        let url = URL(fileURLWithPath: "/tmp/a.mp4")
        #expect(FFmpegEdits.input(url, from: 0, length: 2) == ["-i", url.path, "-t", "2.000"])
        #expect(FFmpegEdits.input(url, from: 12.5, length: nil) == ["-i", url.path, "-ss", "12.500"])
        #expect(FFmpegEdits.input(url, from: 100, length: 5) == ["-ss", "70.000", "-i", url.path, "-ss", "30.000", "-t", "5.000"])
    }

    @Test func turnsAddToTheSourcesRotation() {
        // Counter-clockwise, as -display_rotation takes it.
        #expect(FFmpegEdits.displayRotation(turning: video(), by: .right) == 270)
        #expect(FFmpegEdits.displayRotation(turning: video(), by: .left) == 90)
        #expect(FFmpegEdits.displayRotation(turning: video(rotation: 90), by: .left) == 0)
        #expect(FFmpegEdits.displayRotation(turning: video(rotation: 90), by: .upsideDown) == 90)
        #expect(FFmpegEdits.rotationFilter(.right) == "transpose=clock")
        #expect(FFmpegEdits.rotationFilter(.flipHorizontal) == "hflip")
    }

    @Test func hevcCopiesAreTaggedForQuickTime() {
        #expect(FFmpegEdits.videoCopy(for: video(codec: "hevc")) == ["-c:v", "copy", "-tag:v", "hvc1"])
        #expect(FFmpegEdits.videoCopy(for: video()) == ["-c:v", "copy"])
    }

    @Test func compressFollowsThePlan() {
        var source = VideoSourceInfo(naturalSize: CGSize(width: 1920, height: 1080), duration: 30, fileSize: 40_000_000)
        source.preferredTransform = VideoSourceInfo.transform(rotatedClockwise: 90)
        source.bitRate = 10_000_000
        source.frameRate = 30
        source.audioTracks = [
            .init(audio: SourceAudio(channels: 2, bitRate: 96_000, codec: .aac), sampleRate: 48_000),
            .init(audio: SourceAudio(channels: 6, bitRate: 384_000, codec: nil), sampleRate: 48_000),
        ]
        let plan = VideoCompressionPlan(source: source, resolution: .p720, quality: .medium)
        let arguments = FFmpegEdits.compress(URL(fileURLWithPath: "/tmp/a.mp4"), plan: plan, source: source)

        // Upright, as players show it, and never larger than the source.
        #expect(plan.displaySize == CGSize(width: 720, height: 1280))
        #expect(arguments.contains("scale=720:1280,setsar=1"))
        #expect(arguments.contains("\(plan.videoBitRate)"))
        #expect(arguments.contains("libx264") && arguments.contains("60"))
        #expect(plan.audio[0] == .passthrough(bitRate: 96_000))
        #expect(arguments.contains("-c:a:0") && arguments[arguments.firstIndex(of: "-c:a:0")! + 1] == "copy")
        guard case .aac(let bitRate, let channels, _) = plan.audio[1] else {
            Issue.record("The 5.1 track should be re-encoded")
            return
        }
        #expect(arguments.contains("\(bitRate)") && channels == 2)
        #expect(arguments.suffix(2) == ["-movflags", "+faststart"])
    }

    @Test func mergesFitEachClipAndFillMissingSound() {
        let first = ProbedMedia(duration: 3, video: video(), audio: [audio()])
        let silent = ProbedMedia(duration: 2.5, video: ProbedMedia.Video(width: 640, height: 480, frameRate: "25/1", codec: "h264"), audio: [])
        let arguments = FFmpegEdits.mergeVideos([first, silent])
        let graph = arguments[1]
        #expect(graph.contains("[0:V:0]scale=1920:1080:force_original_aspect_ratio=decrease"))
        #expect(graph.contains("fps=30000/1001"))
        #expect(graph.contains("anullsrc=r=48000:cl=stereo,atrim=duration=2.500[a1]"))
        #expect(graph.hasSuffix("[v0][a0][v1][a1]concat=n=2:v=1:a=1[v][a]"))

        let noSound = FFmpegEdits.mergeVideos([silent, silent])
        #expect(noSound[1].hasSuffix("[v0][v1]concat=n=2:v=1:a=0[v]"))
        #expect(!noSound.contains("[a]"))
    }

    @Test func streamCopyOnlyForCodecsApplePlays() {
        let aacAudio = [audio()]
        let ts = ProbedMedia(duration: 3, video: video(), audio: aacAudio)
        #expect(FFmpegArguments.streamCopy(to: .mp4, from: ts) == ["-map", "0:V:0", "-map", "0:a?", "-c", "copy", "-movflags", "+faststart"])
        #expect(FFmpegArguments.streamCopy(to: .mov, from: ts) == ["-map", "0:V:0", "-map", "0:a?", "-c", "copy"])
        #expect(FFmpegArguments.streamCopy(to: .mov, from: ProbedMedia(duration: 3, video: video(codec: "hevc"), audio: aacAudio))?.contains("hvc1") == true)
        // MPEG-2 video, MP2 sound, or 10-bit H.264 need re-encoding to play on a Mac.
        #expect(FFmpegArguments.streamCopy(to: .mp4, from: ProbedMedia(duration: 3, video: video(codec: "mpeg2video"), audio: aacAudio)) == nil)
        #expect(FFmpegArguments.streamCopy(to: .mp4, from: ProbedMedia(duration: 3, video: video(), audio: [audio("mp2")])) == nil)
        var tenBit = video()
        tenBit.pixelFormat = "yuv420p10le"
        #expect(FFmpegArguments.streamCopy(to: .mp4, from: ProbedMedia(duration: 3, video: tenBit, audio: aacAudio)) == nil)
        #expect(FFmpegArguments.streamCopy(to: .mkv, from: ts) == nil)
    }
}

/// Reading ffprobe's answers.
struct ProbedMediaTests {
    private func parse(_ json: String) -> ProbedMedia? {
        ProbedMedia.parse(Data(json.utf8))
    }

    @Test func readsAnMPEGTransportStream() throws {
        let probed = try #require(parse("""
        {"streams": [
          {"codec_name": "h264", "codec_type": "video", "width": 320, "height": 240, "pix_fmt": "yuv420p",
           "r_frame_rate": "25/1", "avg_frame_rate": "25/1", "duration": "3.000000", "disposition": {"attached_pic": 0}},
          {"codec_name": "aac", "codec_type": "audio", "sample_rate": "44100", "channels": 1, "bit_rate": "71567",
           "r_frame_rate": "0/0", "avg_frame_rate": "0/0", "duration": "2.879267"}
        ], "format": {"duration": "3.023222"}}
        """))
        #expect(probed.duration == 3.023222)
        let video = try #require(probed.video)
        #expect(video.displaySize == CGSize(width: 320, height: 240))
        #expect(video.framesPerSecond == 25)
        // MPEG-TS doesn't give the picture's own rate.
        #expect(video.bitRate == nil)
        #expect(probed.audio == [ProbedMedia.Audio(codec: "aac", channels: 1, sampleRate: 44_100, bitRate: 71_567)])
        #expect(probed.audio[0].source == SourceAudio(channels: 1, bitRate: 71_567, codec: .aac))
    }

    @Test func readsRotationEitherWay() throws {
        // ffmpeg 5 and later: a display matrix, counter-clockwise.
        let matrix = try #require(parse("""
        {"streams": [{"codec_type": "video", "codec_name": "h264", "width": 1920, "height": 1080,
          "side_data_list": [{"rotation": -90}]}], "format": {"duration": "1"}}
        """))
        #expect(matrix.video?.rotation == 90)
        #expect(matrix.video?.displaySize == CGSize(width: 1080, height: 1920))
        // Older ffprobe: the "rotate" tag, clockwise.
        let tag = try #require(parse("""
        {"streams": [{"codec_type": "video", "codec_name": "h264", "width": 1920, "height": 1080,
          "tags": {"rotate": "270"}}], "format": {"duration": "1"}}
        """))
        #expect(tag.video?.rotation == 270)
    }

    @Test func skipsCoverArt() throws {
        let probed = try #require(parse("""
        {"streams": [
          {"codec_type": "audio", "codec_name": "mp3", "channels": 2, "sample_rate": "44100"},
          {"codec_type": "video", "codec_name": "mjpeg", "width": 600, "height": 600, "disposition": {"attached_pic": 1}}
        ], "format": {"duration": "200.5"}}
        """))
        #expect(probed.video == nil)
        #expect(probed.audio.count == 1)
    }

    @Test func sourceInfoEstimatesThePicturesRate() throws {
        let probed = try #require(parse("""
        {"streams": [
          {"codec_type": "video", "codec_name": "hevc", "width": 3840, "height": 2160, "avg_frame_rate": "60/1",
           "color_transfer": "smpte2084", "side_data_list": [{"rotation": 90}]},
          {"codec_type": "audio", "codec_name": "aac", "channels": 2, "sample_rate": "48000", "bit_rate": "192000"}
        ], "format": {"duration": "10"}}
        """))
        let info = VideoSourceInfo(probed: probed, video: try #require(probed.video), fileSize: 25_000_000)
        // 20 Mb/s in all, less the sound.
        #expect(info.bitRate == 20_000_000 - 192_000)
        #expect(info.isHEVC && info.isHDR)
        #expect(info.frameRate == 60)
        #expect(info.displaySize == CGSize(width: 2160, height: 3840))
        #expect(info.audioTracks == [.init(audio: SourceAudio(channels: 2, bitRate: 192_000, codec: .aac), sampleRate: 48_000)])
    }

    @Test func waveformPeaksFillEveryBar() {
        let samples: [Int16] = [0, 100, -32_767, 50, 16_384, -2, 0, 0]
        let peaks = MediaLoading.peaks(of: samples, bars: 4)
        #expect(peaks.count == 4)
        #expect(peaks[1] == 1)
        #expect(abs(peaks[2] - 0.5) < 0.01)
        #expect(peaks[3] == 0)
        #expect(MediaLoading.peaks(of: [1, 2], bars: 10).count == 2)
    }

    @Test func tellsTransportStreamsFromTypeScript() throws {
        var packets = Data(repeating: 0xFF, count: 376)
        packets[0] = 0x47
        packets[188] = 0x47
        #expect(MediaFormats.isTransportStream(header: packets))
        #expect(!MediaFormats.isTransportStream(header: Data("const answer: number = 42;\n".utf8)))

        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("openshelf-ts-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let stream = directory.appendingPathComponent("recording.ts")
        let script = directory.appendingPathComponent("index.ts")
        try packets.write(to: stream)
        try Data("export const answer = 42;\n".utf8).write(to: script)

        #expect(FileKind(url: stream) == .video)
        #expect(!MediaFormats.isAVFoundationReadable(stream))
        #expect(FileKind(url: script) != .video)
        #expect(ConversionCatalog.formats(for: [script], tools: ExternalTools(ffmpeg: URL(fileURLWithPath: "/opt/homebrew/bin/ffmpeg"), cwebp: nil)).contains(.mp4) == false)
        // MPEG-TS converts with ffmpeg, but isn't edited directly.
        #expect(FileTool.available(for: [stream]).isEmpty)
        #expect(FileTool.needsConversionForTools([stream]))
    }
}

private final class CallLog: @unchecked Sendable {
    private let lock = NSLock()
    private var calls: [String] = []

    func add(_ call: String) {
        lock.withLock { calls.append(call) }
    }

    var entries: [String] {
        lock.withLock { calls }
    }
}
