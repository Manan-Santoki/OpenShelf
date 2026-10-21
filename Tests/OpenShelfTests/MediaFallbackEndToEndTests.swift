@preconcurrency import AVFoundation
import CoreGraphics
import Foundation
import Testing
@testable import OpenShelf

/// MPEG-TS streams saved as .mp4, which AVFoundation can't open, converted
/// and edited through the ffmpeg fallback, each result checked with ffprobe.
struct MediaFallbackEndToEndTests {
    static let ffmpeg = URL(fileURLWithPath: "/opt/homebrew/bin/ffmpeg")
    static let ffprobe = URL(fileURLWithPath: "/opt/homebrew/bin/ffprobe")
    static var hasFFmpeg: Bool {
        FileManager.default.isExecutableFile(atPath: ffmpeg.path) && FileManager.default.isExecutableFile(atPath: ffprobe.path)
    }

    private let tools = ExternalTools(ffmpeg: Self.ffmpeg, cwebp: nil, ffprobe: Self.ffprobe)

    /// Made once, then copied into a folder of its own for each test.
    static let fixtures: URL? = try? makeFixtures()

    private static func makeFixtures() throws -> URL {
        // Tests can't clean up after a whole suite, so clear out earlier runs' fixtures.
        let temporary = FileManager.default.temporaryDirectory
        let prefix = "openshelf-fallback-fixtures-"
        for name in (try? FileManager.default.contentsOfDirectory(atPath: temporary.path)) ?? [] where name.hasPrefix(prefix) {
            try? FileManager.default.removeItem(at: temporary.appendingPathComponent(name))
        }
        let folder = temporary.appendingPathComponent(prefix + UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        func path(_ name: String) -> String { folder.appendingPathComponent(name).path }
        let picture: [String] = ["-f", "lavfi", "-i", "testsrc=duration=3:size=320x240:rate=25"]
        let tone: [String] = ["-f", "lavfi", "-i", "sine=frequency=440:duration=3:sample_rate=48000"]
        let h264: [String] = ["-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p"]
        // MPEG-TS under an MP4 name: picture and sound, picture only, sound only.
        try run(ffmpeg, picture + tone + h264 + ["-c:a", "aac", "-ac", "2", "-f", "mpegts", path("stream.mp4")])
        try run(ffmpeg, picture + h264 + ["-f", "mpegts", path("silent.mp4")])
        try run(ffmpeg, ["-f", "lavfi", "-i", "sine=frequency=440:duration=3:sample_rate=44100", "-c:a", "aac", "-f", "mpegts", path("sound.mp4")])
        // 40 seconds with a keyframe every 2, to cut far from the start.
        try run(ffmpeg, ["-f", "lavfi", "-i", "testsrc=duration=40:size=64x48:rate=10", "-f", "lavfi", "-i", "sine=duration=40"]
            + h264 + ["-g", "20", "-c:a", "aac", "-f", "mpegts", path("long.mp4")])
        // Generous bitrates, for Compress to work on.
        try run(ffmpeg, ["-f", "lavfi", "-i", "testsrc2=duration=3:size=1280x720:rate=25"] + tone
            + ["-c:v", "libx264", "-preset", "ultrafast", "-b:v", "4M", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "192k", "-f", "mpegts", path("hd.mp4")])
        // Busy pictures in very few bits: Compress can't beat it.
        try run(ffmpeg, ["-f", "lavfi", "-i", "testsrc2=duration=3:size=320x240:rate=25", "-c:v", "libx264", "-preset", "medium", "-b:v", "15k", "-pix_fmt", "yuv420p", "-f", "mpegts", path("lean.mp4")])
        // Long enough to cancel partway: as MPEG-TS, and as a real MP4.
        let busy: [String] = ["-f", "lavfi", "-i", "testsrc2=duration=20:size=1280x720:rate=30", "-f", "lavfi", "-i", "sine=duration=20"] + h264 + ["-c:a", "aac"]
        try run(ffmpeg, busy + ["-f", "mpegts", path("busy.mp4")])
        try run(ffmpeg, busy + [path("busy-native.mp4")])
        // A real MP4, to compare with what AVFoundation does.
        try run(ffmpeg, picture + tone + h264 + ["-c:a", "aac", path("native.mp4")])
        return folder
    }

    private static func run(_ tool: URL, _ arguments: [String]) throws {
        let process = Process()
        process.executableURL = tool
        process.arguments = ["-y", "-loglevel", "error"] + arguments
        try process.run()
        process.waitUntilExit()
        guard process.terminationStatus == 0 else { throw MediaToolError.failed("Couldn't make \(arguments.last ?? "")") }
    }

    /// A fresh folder holding copies of `names`.
    private func workspace(_ names: String...) throws -> (folder: URL, files: [URL]) {
        let fixtures = try #require(Self.fixtures)
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("openshelf-fallback-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let files: [URL] = try names.map { name in
            let copy = folder.appendingPathComponent(name)
            try FileManager.default.copyItem(at: fixtures.appendingPathComponent(name), to: copy)
            return copy
        }
        return (folder, files)
    }

    private func contents(of folder: URL) -> [String] {
        ((try? FileManager.default.contentsOfDirectory(atPath: folder.path)) ?? []).sorted()
    }

    private func isNear(_ value: Double, _ expected: Double, within tolerance: Double = 0.2) -> Bool {
        abs(value - expected) <= tolerance
    }

    // MARK: Conversions

    @Test(.enabled(if: MediaFallbackEndToEndTests.hasFFmpeg))
    func convertsToMOVAndMP4ByCopyingTheStreams() async throws {
        let (folder, files) = try workspace("stream.mp4")
        defer { try? FileManager.default.removeItem(at: folder) }
        #expect(MediaFormats.isAVFoundationReadable(files[0]))

        for format in [OutputFormat.mov, .mp4] {
            let output = try await MediaConversion.convert(files[0], to: format, tools: tools) { _ in }
            let probe = try MediaCheck.probe(output)
            #expect(output.pathExtension == format.fileExtension)
            #expect(probe.format.contains("mp4"))
            #expect(isNear(probe.duration, 3))
            #expect(probe.videos.map(\.codec) == ["h264"])
            #expect(probe.videos.first?.displaySize == CGSize(width: 320, height: 240))
            #expect(probe.audios.map(\.codec) == ["aac"])
            #expect(probe.audios.first?.channels == 2)
            // The result is one macOS can open itself.
            let tracks = try await AVURLAsset(url: output).loadTracks(withMediaType: .video)
            #expect(tracks.count == 1)
        }
        #expect(contents(of: folder).filter { $0.hasPrefix(".") }.isEmpty)
    }

    @Test(.enabled(if: MediaFallbackEndToEndTests.hasFFmpeg))
    func extractsSoundThroughFFmpeg() async throws {
        let (folder, files) = try workspace("stream.mp4")
        defer { try? FileManager.default.removeItem(at: folder) }
        // M4A goes through an export session, WAV through afconvert; both give up.
        for format in [OutputFormat.m4a, .wav] {
            let output = try await MediaConversion.convert(files[0], to: format, tools: tools) { _ in }
            let probe = try MediaCheck.probe(output)
            #expect(probe.videos.isEmpty)
            #expect(probe.audios.count == 1)
            #expect(isNear(probe.duration, 3))
        }
        #expect(probeCodec(folder, "stream.wav") == "pcm_s16le")
    }

    private func probeCodec(_ folder: URL, _ name: String) -> String? {
        try? MediaCheck.probe(folder.appendingPathComponent(name)).audios.first?.codec
    }

    // MARK: Tools

    @Test(.enabled(if: MediaFallbackEndToEndTests.hasFFmpeg))
    func mutes() async throws {
        let (folder, files) = try workspace("stream.mp4")
        defer { try? FileManager.default.removeItem(at: folder) }
        let progress = ProgressLog()
        let output = try await MediaTools.mute(files[0], tools: tools) { progress.add($0) }
        #expect(output.lastPathComponent == "stream Muted.mp4")
        let probe = try MediaCheck.probe(output)
        #expect(probe.videos.map(\.codec) == ["h264"])
        #expect(probe.videos.first?.displaySize == CGSize(width: 320, height: 240))
        #expect(probe.audios.isEmpty)
        #expect(isNear(probe.duration, 3))
        #expect(progress.last == 1)
        #expect(contents(of: folder) == ["stream Muted.mp4", "stream.mp4"])
    }

    @Test(.enabled(if: MediaFallbackEndToEndTests.hasFFmpeg))
    func trimsExactly() async throws {
        let (folder, files) = try workspace("stream.mp4", "long.mp4")
        defer { try? FileManager.default.removeItem(at: folder) }
        let range = CMTimeRange(start: MediaTime.cmTime(0.5), end: MediaTime.cmTime(2))
        let output = try await MediaTools.trim(files[0], range: range, tools: tools) { _ in }
        #expect(output.lastPathComponent == "stream Trimmed.mp4")
        let probe = try MediaCheck.probe(output, countingFrames: true)
        #expect(isNear(probe.duration, 1.5, within: 0.1))
        #expect(probe.videos.first?.frames == 38 || probe.videos.first?.frames == 37)
        #expect(probe.videos.first?.displaySize == CGSize(width: 320, height: 240))
        #expect(probe.audios.map(\.codec) == ["aac"])

        // 33 s in: past the direct seek, decoded up to the cut.
        let late = CMTimeRange(start: MediaTime.cmTime(33), end: MediaTime.cmTime(35))
        let part = try await MediaTools.trim(files[1], range: late, tools: tools) { _ in }
        let partProbe = try MediaCheck.probe(part, countingFrames: true)
        #expect(isNear(partProbe.duration, 2, within: 0.1))
        #expect(partProbe.videos.first.map { (19...21).contains($0.frames) } == true)
    }

    @Test(.enabled(if: MediaFallbackEndToEndTests.hasFFmpeg))
    func splits() async throws {
        let (folder, files) = try workspace("stream.mp4")
        defer { try? FileManager.default.removeItem(at: folder) }
        let parts = try await MediaTools.split(files[0], into: SplitPlan.equalParts(duration: 3, count: 2), tools: tools) { _, _ in }
        #expect(parts.map(\.lastPathComponent) == ["stream Part 1.mp4", "stream Part 2.mp4"])
        for part in parts {
            let probe = try MediaCheck.probe(part)
            #expect(isNear(probe.duration, 1.5, within: 0.15))
            #expect(probe.videos.count == 1 && probe.audios.count == 1)
        }
    }

    @Test(.enabled(if: MediaFallbackEndToEndTests.hasFFmpeg))
    func changesSpeedKeepingOrShiftingPitch() async throws {
        let (folder, files) = try workspace("stream.mp4")
        defer { try? FileManager.default.removeItem(at: folder) }

        let kept = try await MediaTools.changeSpeed(files[0], rate: 2, keepsPitch: true, tools: tools) { _ in }
        #expect(kept.lastPathComponent == "stream 2x.mp4")
        let keptProbe = try MediaCheck.probe(kept, countingFrames: true)
        #expect(isNear(keptProbe.duration, 1.5))
        // Every frame is kept, at twice the rate.
        #expect(keptProbe.videos.first?.frameRate == 50)
        #expect(keptProbe.videos.first.map { $0.frames >= 74 } == true)
        #expect(isNear(try MediaCheck.toneFrequency(of: kept), 440, within: 25))

        let shifted = try await MediaTools.changeSpeed(files[0], rate: 2, keepsPitch: false, tools: tools) { _ in }
        #expect(isNear(try MediaCheck.probe(shifted).duration, 1.5))
        #expect(isNear(try MediaCheck.toneFrequency(of: shifted), 880, within: 45))

        let slow = try await MediaTools.changeSpeed(files[0], rate: 0.5, keepsPitch: true, tools: tools) { _ in }
        #expect(slow.lastPathComponent == "stream 0.5x.mp4")
        #expect(isNear(try MediaCheck.probe(slow).duration, 6, within: 0.3))
        #expect(isNear(try MediaCheck.toneFrequency(of: slow), 440, within: 25))
    }

    @Test(.enabled(if: MediaFallbackEndToEndTests.hasFFmpeg))
    func changesSpeedOfSilentVideoAndSoundOnly() async throws {
        let (folder, files) = try workspace("silent.mp4", "sound.mp4")
        defer { try? FileManager.default.removeItem(at: folder) }

        let silent = try await MediaTools.changeSpeed(files[0], rate: 2, keepsPitch: true, tools: tools) { _ in }
        let silentProbe = try MediaCheck.probe(silent)
        #expect(silentProbe.audios.isEmpty)
        #expect(silentProbe.videos.count == 1)
        #expect(isNear(silentProbe.duration, 1.5))

        for keepsPitch in [true, false] {
            let output = try await MediaTools.changeSpeed(files[1], rate: 2, keepsPitch: keepsPitch, tools: tools) { _ in }
            #expect(output.pathExtension == "m4a")
            let probe = try MediaCheck.probe(output)
            #expect(probe.videos.isEmpty)
            #expect(probe.audios.map(\.codec) == ["aac"])
            #expect(isNear(probe.duration, 1.5))
            #expect(isNear(try MediaCheck.toneFrequency(of: output), keepsPitch ? 440 : 880, within: 45))
        }
    }

    @Test(.enabled(if: MediaFallbackEndToEndTests.hasFFmpeg))
    func rotatesTheWayAVFoundationDoes() async throws {
        let (folder, files) = try workspace("stream.mp4", "native.mp4")
        defer { try? FileManager.default.removeItem(at: folder) }

        let turned = try await MediaTools.rotate(files[0], by: .right, tools: tools) { _ in }
        #expect(turned.lastPathComponent == "stream Rotated.mp4")
        let turnedProbe = try MediaCheck.probe(turned)
        #expect(turnedProbe.videos.first?.displaySize == CGSize(width: 240, height: 320))
        #expect(turnedProbe.audios.count == 1)
        #expect(isNear(turnedProbe.duration, 3))

        // AVFoundation turning a real MP4 the same way leaves the same angle.
        let native = try await MediaTools.rotate(files[1], by: .right, tools: tools) { _ in }
        let nativeProbe = try MediaCheck.probe(native)
        #expect(nativeProbe.videos.first?.rotation == turnedProbe.videos.first?.rotation)

        let mirrored = try await MediaTools.rotate(files[0], by: .flipHorizontal, tools: tools) { _ in }
        let mirroredProbe = try MediaCheck.probe(mirrored)
        #expect(mirroredProbe.videos.first?.displaySize == CGSize(width: 320, height: 240))
        #expect(mirroredProbe.videos.first?.rotation == 0)
    }

    @Test(.enabled(if: MediaFallbackEndToEndTests.hasFFmpeg))
    func crops() async throws {
        let (folder, files) = try workspace("stream.mp4")
        defer { try? FileManager.default.removeItem(at: folder) }
        let output = try await MediaTools.crop(files[0], to: CGRect(x: 40, y: 20, width: 201, height: 150), tools: tools) { _ in }
        let probe = try MediaCheck.probe(output)
        // Even sides, as video encoders need.
        #expect(probe.videos.first?.displaySize == CGSize(width: 200, height: 150))
        #expect(probe.audios.count == 1)
        #expect(isNear(probe.duration, 3))
    }

    @Test(.enabled(if: MediaFallbackEndToEndTests.hasFFmpeg))
    func merges() async throws {
        let (folder, files) = try workspace("stream.mp4", "silent.mp4")
        defer { try? FileManager.default.removeItem(at: folder) }
        let output = try await MediaTools.merge(files, tools: tools) { _ in }
        #expect(output.lastPathComponent == "Merged.mp4")
        let probe = try MediaCheck.probe(output)
        #expect(isNear(probe.duration, 6, within: 0.3))
        #expect(probe.videos.first?.displaySize == CGSize(width: 320, height: 240))
        // The silent clip gets silence, so the sound lasts the whole way.
        #expect(probe.audios.count == 1)
        #expect(isNear(probe.audios.first?.duration ?? 0, 6, within: 0.3))
    }

    @Test(.enabled(if: MediaFallbackEndToEndTests.hasFFmpeg))
    func compressesAsThePlanSays() async throws {
        let (folder, files) = try workspace("hd.mp4")
        defer { try? FileManager.default.removeItem(at: folder) }
        // What the editor shows comes from ffprobe here.
        let source = try await VideoSourceInfo.load(files[0], tools: tools)
        #expect(source.naturalSize == CGSize(width: 1280, height: 720))
        #expect(source.bitRate > 3_000_000)
        let plan = VideoCompressionPlan(source: source, resolution: .p480, quality: .medium)

        let output = try #require(try await MediaTools.compress(files[0], resolution: .p480, quality: .medium, tools: tools) { _ in })
        #expect(output.lastPathComponent == "hd Compressed.mp4")
        let probe = try MediaCheck.probe(output)
        #expect(probe.videos.first?.displaySize == plan.displaySize)
        #expect(plan.displaySize == CGSize(width: 854, height: 480))
        #expect(probe.videos.first?.codec == "h264")
        let videoRate = Double(try #require(probe.videos.first?.bitRate))
        #expect(abs(videoRate - Double(plan.videoBitRate)) / Double(plan.videoBitRate) < 0.35)
        #expect(probe.audios.map(\.codec) == ["aac"])
        #expect(isNear(probe.duration, 3))
        #expect(MediaSize.fileSize(output) < MediaSize.fileSize(files[0]))
        #expect(contents(of: folder) == ["hd Compressed.mp4", "hd.mp4"])
    }

    @Test(.enabled(if: MediaFallbackEndToEndTests.hasFFmpeg))
    func keepsNothingWhenCompressingWouldntShrink() async throws {
        let (folder, files) = try workspace("lean.mp4")
        defer { try? FileManager.default.removeItem(at: folder) }
        let output = try await MediaTools.compress(files[0], resolution: .original, quality: .high, tools: tools) { _ in }
        #expect(output == nil)
        #expect(contents(of: folder) == ["lean.mp4"])
    }

    // MARK: Previews

    @Test(.enabled(if: MediaFallbackEndToEndTests.hasFFmpeg))
    func editorsLoadWhatAVFoundationCant() async throws {
        let (folder, files) = try workspace("stream.mp4", "sound.mp4")
        defer { try? FileManager.default.removeItem(at: folder) }
        let basics = try await MediaLoading.basics(of: files[0], tools: tools)
        #expect(!basics.isPlayable && basics.hasVideo)
        #expect(isNear(basics.duration, 3))
        #expect(basics.displaySize == CGSize(width: 320, height: 240))

        let poster = try await MediaLoading.poster(of: files[0], maximumSize: 200, tools: tools)
        #expect(poster.width == 200 && poster.height == 150)
        let strip = await MediaLoading.thumbnails(of: files[0], count: 4, duration: basics.duration, tools: tools)
        #expect(strip.count == 4)
        let sound = try await MediaLoading.basics(of: files[1], tools: tools)
        #expect(!sound.hasVideo)
        let waveform = await MediaLoading.waveform(of: files[1], bars: 50, duration: sound.duration, tools: tools)
        #expect(waveform.count == 50)
        // ffmpeg's test tone peaks at an eighth of full scale, all the way through.
        #expect(waveform.dropFirst().dropLast().allSatisfy { abs($0 - 0.125) < 0.03 })
    }

    // MARK: Cancelling and missing ffmpeg

    @Test(.enabled(if: MediaFallbackEndToEndTests.hasFFmpeg))
    func cancellingTheFallbackStopsAndLeavesNothing() async throws {
        let (folder, files) = try workspace("busy.mp4")
        defer { try? FileManager.default.removeItem(at: folder) }
        let progress = ProgressLog()
        let tools = tools
        let task = Task {
            try await MediaTools.changeSpeed(files[0], rate: 0.5, keepsPitch: true, tools: tools) { progress.add($0) }
        }
        try await progress.waitForWork()
        task.cancel()
        let cancelledAt = Date()
        await #expect(throws: CancellationError.self) { _ = try await task.value }
        #expect(Date().timeIntervalSince(cancelledAt) < 3)
        // Long enough for `discardPartial`'s second sweep.
        try await Task.sleep(for: .seconds(2.5))
        #expect(contents(of: folder) == ["busy.mp4"])
    }

    @Test(.enabled(if: MediaFallbackEndToEndTests.hasFFmpeg))
    func cancellingANativeExportNeverStartsFFmpeg() async throws {
        let (folder, files) = try workspace("busy-native.mp4")
        defer { try? FileManager.default.removeItem(at: folder) }
        // An ffmpeg that leaves a mark if anything runs it.
        let marker = folder.appendingPathComponent("ffmpeg ran")
        let fake = folder.appendingPathComponent("ffmpeg")
        try Data("#!/bin/sh\ntouch '\(marker.path)'\nexit 1\n".utf8).write(to: fake)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: fake.path)
        let tools = ExternalTools(ffmpeg: fake, cwebp: nil, ffprobe: Self.ffprobe)

        let progress = ProgressLog()
        let task = Task {
            try await MediaTools.changeSpeed(files[0], rate: 0.5, keepsPitch: true, tools: tools) { progress.add($0) }
        }
        try await progress.waitForWork()
        task.cancel()
        await #expect(throws: CancellationError.self) { _ = try await task.value }
        try await Task.sleep(for: .seconds(2.5))
        #expect(!FileManager.default.fileExists(atPath: marker.path))
        #expect(contents(of: folder) == ["busy-native.mp4", "ffmpeg"])
    }

    @Test(.enabled(if: MediaFallbackEndToEndTests.hasFFmpeg))
    func withoutFFmpegItSaysSoAndWritesNothing() async throws {
        let (folder, files) = try workspace("stream.mp4")
        defer { try? FileManager.default.removeItem(at: folder) }
        let none = ExternalTools(ffmpeg: nil, cwebp: nil)
        await #expect(throws: UnreadableMediaError(url: files[0])) {
            _ = try await MediaTools.mute(files[0], tools: none) { _ in }
        }
        await #expect(throws: UnreadableMediaError(url: files[0])) {
            _ = try await MediaConversion.convert(files[0], to: .mov, tools: none) { _ in }
        }
        #expect(contents(of: folder) == ["stream.mp4"])
    }
}

/// What ffprobe says about a result, read independently of `ProbedMedia`.
enum MediaCheck {
    struct Video {
        var codec: String
        var width: Int
        var height: Int
        /// Counter-clockwise degrees from the display matrix, as ffprobe gives them.
        var rotation: Int
        var frameRate: Double
        var bitRate: Int?
        var frames: Int

        var displaySize: CGSize {
            rotation % 180 == 0 ? CGSize(width: width, height: height) : CGSize(width: height, height: width)
        }
    }

    struct Audio {
        var codec: String
        var channels: Int
        var duration: Double
    }

    struct Probe {
        var format: String
        var duration: Double
        var videos: [Video]
        var audios: [Audio]
    }

    static func probe(_ url: URL, countingFrames: Bool = false) throws -> Probe {
        var arguments: [String] = ["-v", "error"]
        if countingFrames { arguments.append("-count_frames") }
        arguments += [
            "-show_entries", "format=format_name,duration:stream=codec_type,codec_name,width,height,avg_frame_rate,bit_rate,channels,duration,nb_read_frames:stream_side_data=rotation",
            "-of", "json", url.path,
        ]
        let output = try #require(MediaProbe.runSync(MediaFallbackEndToEndTests.ffprobe, arguments))
        let root = try #require(try JSONSerialization.jsonObject(with: Data(output.utf8)) as? [String: Any])
        let format = root["format"] as? [String: Any] ?? [:]
        let streams = root["streams"] as? [[String: Any]] ?? []
        func number(_ value: Any?) -> Double? { (value as? String).flatMap(Double.init) ?? (value as? NSNumber)?.doubleValue }

        var videos: [Video] = []
        var audios: [Audio] = []
        for stream in streams {
            let codec = stream["codec_name"] as? String ?? ""
            if stream["codec_type"] as? String == "video" {
                let sideData = stream["side_data_list"] as? [[String: Any]] ?? []
                let rotation = sideData.compactMap { number($0["rotation"]) }.first ?? 0
                videos.append(Video(
                    codec: codec,
                    width: stream["width"] as? Int ?? 0,
                    height: stream["height"] as? Int ?? 0,
                    rotation: Int(rotation),
                    frameRate: (stream["avg_frame_rate"] as? String).flatMap(ProbedMedia.rate) ?? 0,
                    bitRate: number(stream["bit_rate"]).map { Int($0) },
                    frames: number(stream["nb_read_frames"]).map { Int($0) } ?? 0
                ))
            } else if stream["codec_type"] as? String == "audio" {
                audios.append(Audio(codec: codec, channels: stream["channels"] as? Int ?? 0, duration: number(stream["duration"]) ?? 0))
            }
        }
        return Probe(format: format["format_name"] as? String ?? "", duration: number(format["duration"]) ?? 0, videos: videos, audios: audios)
    }

    /// The pitch of a steady tone in the first sound track, from how often
    /// the decoded wave crosses zero.
    static func toneFrequency(of url: URL) throws -> Double {
        let raw = FileManager.default.temporaryDirectory.appendingPathComponent("openshelf-tone-\(UUID().uuidString).raw")
        defer { try? FileManager.default.removeItem(at: raw) }
        let process = Process()
        process.executableURL = MediaFallbackEndToEndTests.ffmpeg
        process.arguments = ["-y", "-loglevel", "error", "-i", url.path, "-map", "0:a:0", "-ac", "1", "-ar", "16000", "-f", "s16le", raw.path]
        try process.run()
        process.waitUntilExit()
        let data = try Data(contentsOf: raw)
        let samples: [Int16] = data.withUnsafeBytes { bytes in
            (0..<bytes.count / 2).map { bytes.loadUnaligned(fromByteOffset: $0 * 2, as: Int16.self) }
        }
        // Skip the encoder's fade-in and fade-out.
        let steady = samples.dropFirst(1_600).dropLast(1_600)
        var crossings = 0
        var previous: Int16 = steady.first ?? 0
        for sample in steady {
            if (previous < 0) != (sample < 0) { crossings += 1 }
            previous = sample
        }
        let seconds = Double(steady.count) / 16_000
        return seconds > 0 ? Double(crossings) / 2 / seconds : 0
    }
}

/// Progress reports, gathered across threads.
final class ProgressLog: @unchecked Sendable {
    private let lock = NSLock()
    private var values: [Double] = []

    func add(_ value: Double) {
        lock.withLock { values.append(value) }
    }

    var last: Double? {
        lock.withLock { values.last }
    }

    /// Waits until some progress has been reported, so a cancel lands mid-way.
    func waitForWork(timeout: Duration = .seconds(20)) async throws {
        let clock = ContinuousClock()
        let deadline = clock.now + timeout
        while lock.withLock({ !values.contains { $0 > 0.01 } }) {
            guard clock.now < deadline else {
                Issue.record("No progress was reported")
                return
            }
            try await Task.sleep(for: .milliseconds(20))
        }
    }
}
