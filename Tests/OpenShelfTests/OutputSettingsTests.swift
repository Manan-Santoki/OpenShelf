@preconcurrency import AVFoundation
import CoreGraphics
import Foundation
import ImageIO
import Testing
@testable import OpenShelf

/// Settings → Output: how they're stored, and that `.automatic` is exactly
/// what conversions did before.
struct OutputSettingsStorageTests {
    private var everything: OutputSettings {
        var settings = OutputSettings()
        settings.video = .init(codec: .hevc, quality: .custom, customMbps: 2.5, maxSize: .uhd4K)
        settings.audio.kbps = 256
        settings.image = .init(quality: 0.7, keepsMetadata: false)
        settings.gif = .init(width: .original, frameRate: 24)
        return settings
    }

    @Test func roundTripsThroughJSON() {
        #expect(OutputSettings.decode(everything.stored) == everything)
        #expect(OutputSettings.decode(OutputSettings.automatic.stored) == .automatic)
    }

    @Test func defaultsAreTodaysChoices() {
        let settings = OutputSettings.automatic
        #expect(settings.video.isAutomatic)
        #expect(settings.video.customMbps == 8)
        #expect(settings.audio.kbps == nil)
        #expect(settings.image.quality == nil && settings.image.keepsMetadata)
        #expect(settings.gif.width.maxWidth == 720 && settings.gif.frameRate == 12)
        #expect(!settings.reencodesVideo)
    }

    @Test func nothingOrSomethingUnreadableMeansAutomatic() {
        #expect(OutputSettings.decode(nil) == .automatic)
        #expect(OutputSettings.decode("") == .automatic)
        #expect(OutputSettings.decode("not json") == .automatic)
        #expect(OutputSettings.decode("[1, 2]") == .automatic)
    }

    /// Settings from an older version, or one odd value, don't reset the rest.
    @Test func missingOrOddValuesKeepTheirDefaults() {
        let partial = OutputSettings.decode(#"{"video": {"maxSize": "p720"}, "gif": {"frameRate": 15}}"#)
        #expect(partial.video.maxSize == .p720 && partial.video.codec == .automatic && partial.video.quality == .automatic)
        #expect(partial.gif.frameRate == 15 && partial.gif.width == .w720)
        #expect(partial.audio.kbps == nil && partial.image.keepsMetadata)

        let odd = OutputSettings.decode(#"{"video": {"codec": "av1", "quality": "best", "customMbps": 9000}, "audio": {"kbps": 200}, "image": {"quality": 0.13}, "gif": {"width": "480", "frameRate": 60}}"#)
        #expect(odd.video.codec == .automatic && odd.video.quality == .best)
        #expect(odd.video.customMbps == 200)
        #expect(odd.audio.kbps == nil)
        #expect(odd.image.quality == 0.4)
        #expect(odd.gif.width == .w480 && odd.gif.frameRate == 12)
        // Image quality snaps to the menu's steps.
        #expect(OutputSettings.decode(#"{"image": {"quality": 0.86}}"#).image.quality == 0.9)
    }

    @MainActor
    @Test func preferencesKeepThem() throws {
        let suite = "OpenShelfTests.output.\(UUID().uuidString)"
        let defaults = try #require(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        #expect(Preferences(defaults: defaults).output == .automatic)
        Preferences(defaults: defaults).output = everything
        #expect(Preferences(defaults: defaults).output == everything)
        defaults.set("{broken", forKey: "outputSettings")
        #expect(Preferences(defaults: defaults).output == .automatic)
    }

    @Test func eachSettingSaysWhetherMP4sAreReencoded() {
        var settings = OutputSettings.automatic
        settings.image.quality = 0.5
        settings.gif.frameRate = 24
        #expect(!settings.reencodesVideo)
        for change: (inout OutputSettings) -> Void in [
            { $0.video.codec = .h264 },
            { $0.video.quality = .balanced },
            { $0.video.maxSize = .p1080 },
            { $0.audio.kbps = 128 },
        ] {
            var changed = OutputSettings.automatic
            change(&changed)
            #expect(changed.reencodesVideo)
        }
        // The custom rate alone means nothing until Custom is chosen.
        settings.video.customMbps = 3
        #expect(!settings.reencodesVideo)
    }

    @Test func optionsOnlyForLossyOutputs() {
        let video = OutputOptions(format: .mp4, kinds: [.video])
        #expect(video.videoCodec && video.video && video.audioBitRate && !video.gif)
        let webm = OutputOptions(format: .webm, kinds: [.video])
        #expect(!webm.videoCodec && webm.video && webm.audioBitRate)
        #expect(OutputOptions(format: .mp3, kinds: [.audio]) == OutputOptions.with { $0.audioBitRate = true })
        #expect(OutputOptions(format: .gif, kinds: [.video]) == OutputOptions.with { $0.gif = true })
        #expect(OutputOptions(format: .jpg, kinds: [.image]) == OutputOptions.with { $0.imageQuality = true; $0.imageMetadata = true })
        #expect(OutputOptions(format: .jpg, kinds: [.pdf]) == OutputOptions.with { $0.imageQuality = true })
        #expect(OutputOptions(format: .webp, kinds: [.image]) == OutputOptions.with { $0.imageQuality = true })
        for (format, kind) in [(OutputFormat.wav, FileKind.audio), (.flac, .video), (.png, .image), (.gif, .image), (.jpg, .presentation), (.zip, .video), (.pdf, .document)] {
            #expect(OutputOptions(format: format, kinds: [kind]).isEmpty, "\(format) from \(kind)")
        }
    }

    @Test func fixedRatesStayWithinTheEncoders() {
        #expect(OutputSettings.Audio.aacBitRate(kbps: 320, channels: 2) == 320_000)
        #expect(OutputSettings.Audio.aacBitRate(kbps: 320, channels: 1) == 256_000)
        #expect(OutputSettings.Audio.aacBitRate(kbps: 128, channels: 1) == 128_000)
        let fixed = OutputSettings.Audio(kbps: 320)
        let mono = SourceAudio(channels: 1, bitRate: nil)
        #expect(fixed.argument(for: .opus, source: mono) == "256k")
        #expect(fixed.argument(for: .vorbis, source: mono) == "192k")
        #expect(fixed.argument(for: .mp3, source: mono) == "320k")
        #expect(fixed.argument(for: .opus, source: SourceAudio(channels: 2, bitRate: nil)) == "320k")
        // Unknown sound is taken as stereo.
        #expect(fixed.argument(for: .opus, source: nil) == "320k")
    }
}

private extension OutputOptions {
    static func with(_ change: (inout OutputOptions) -> Void) -> OutputOptions {
        var options = OutputOptions()
        change(&options)
        return options
    }
}

/// The ffmpeg arguments each setting gives.
struct OutputSettingsFFmpegTests {
    private let all: Set<String> = ["libx264", "libx265", "hevc_videotoolbox", "libvpx-vp9", "libvorbis", "libopus", "libmp3lame", "wmav2"]
    private let stereo = SourceAudio(channels: 2, bitRate: nil)

    private func arguments(_ format: OutputFormat, encoders: Set<String>? = nil, isHDR: Bool = false, _ change: (inout OutputSettings) -> Void = { _ in }) -> [String] {
        var settings = OutputSettings.automatic
        change(&settings)
        return FFmpegArguments.arguments(for: format, encoders: encoders ?? all, audio: stereo, settings: settings, isHDR: isHDR)
    }

    /// Exactly what each format got before there were settings.
    @Test func automaticIsUnchanged() {
        let h264 = ["-c:v", "libx264", "-preset", "medium", "-crf", "20", "-pix_fmt", "yuv420p"]
        let expected: [OutputFormat: [String]] = [
            .mp4: h264 + ["-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart"],
            .mov: h264 + ["-c:a", "aac", "-b:a", "192k"],
            .mkv: h264 + ["-c:a", "aac", "-b:a", "192k"],
            .m4a: ["-vn", "-c:a", "aac", "-b:a", "192k"],
            .mp3: ["-vn", "-c:a", "libmp3lame", "-b:a", "192k"],
            .opus: ["-vn", "-c:a", "libopus", "-b:a", "128k"],
            .wma: ["-vn", "-c:a", "wmav2", "-b:a", "160k"],
            .ogg: ["-vn", "-c:a", "libvorbis", "-b:a", "160k"],
            .wav: ["-vn", "-c:a", "pcm_s16le"],
            .webm: ["-c:v", "libvpx-vp9", "-crf", "32", "-b:v", "0", "-deadline", "good", "-cpu-used", "4", "-row-mt", "1", "-c:a", "libopus", "-b:a", "128k"],
            .avi: ["-c:v", "mpeg4", "-vtag", "xvid", "-q:v", "4", "-c:a", "libmp3lame", "-b:a", "192k"],
            .wmv: ["-c:v", "wmv2", "-q:v", "4", "-c:a", "wmav2", "-b:a", "160k"],
            .gif: ["-vf", "fps=12,scale='min(720,iw)':-2:flags=lanczos,split[a][b];[a]palettegen[p];[b][p]paletteuse", "-loop", "0"],
        ]
        for (format, arguments) in expected {
            #expect(FFmpegArguments.arguments(for: format, encoders: all, audio: stereo) == arguments, "\(format)")
            #expect(FFmpegArguments.arguments(for: format, encoders: all, audio: stereo, settings: .automatic) == arguments, "\(format)")
            // HDR sources only matter once a video setting is chosen.
            #expect(FFmpegArguments.arguments(for: format, encoders: all, audio: stereo, isHDR: true) == arguments, "\(format)")
        }
    }

    @Test func h264QualityIsACRF() {
        #expect(arguments(.mp4) { $0.video.quality = .best }.contains(sequence: ["-crf", "18"]))
        #expect(arguments(.mp4) { $0.video.quality = .balanced }.contains(sequence: ["-crf", "23"]))
        #expect(arguments(.mov) { $0.video.quality = .smaller }.contains(sequence: ["-crf", "28"]))
        #expect(arguments(.mkv) { $0.video.codec = .h264 }.contains(sequence: ["-c:v", "libx264", "-preset", "medium", "-crf", "20"]))
    }

    @Test func customIsATargetBitrate() {
        let custom = arguments(.mp4) {
            $0.video.quality = .custom
            $0.video.customMbps = 4
        }
        #expect(custom.contains(sequence: ["-b:v", "4000000", "-maxrate", "6000000", "-bufsize", "8000000"]))
        #expect(!custom.contains("-crf"))
        let webm = arguments(.webm) {
            $0.video.quality = .custom
            $0.video.customMbps = 1.5
        }
        #expect(webm.contains(sequence: ["-c:v", "libvpx-vp9", "-b:v", "1500000", "-deadline"]))
        #expect(arguments(.avi) { $0.video.quality = .custom }.contains(sequence: ["-vtag", "xvid", "-b:v", "8000000", "-c:a"]))
        #expect(arguments(.wmv) { $0.video.quality = .custom }.contains(sequence: ["wmv2", "-b:v", "8000000", "-c:a"]))
    }

    @Test func otherCodecsFollowTheQuality() {
        #expect(arguments(.webm) { $0.video.quality = .best }.contains(sequence: ["-crf", "28", "-b:v", "0"]))
        #expect(arguments(.webm) { $0.video.quality = .balanced }.contains(sequence: ["-crf", "33", "-b:v", "0"]))
        #expect(arguments(.webm) { $0.video.quality = .smaller }.contains(sequence: ["-crf", "38", "-b:v", "0"]))
        #expect(arguments(.avi) { $0.video.quality = .best }.contains(sequence: ["-q:v", "2"]))
        #expect(arguments(.avi) { $0.video.quality = .balanced }.contains(sequence: ["-q:v", "5"]))
        #expect(arguments(.wmv) { $0.video.quality = .smaller }.contains(sequence: ["-q:v", "8"]))
        // These containers have one codec each; a codec choice changes nothing.
        #expect(arguments(.webm) { $0.video.codec = .hevc } == arguments(.webm))
    }

    @Test func hevcPrefersX265ThenVideoToolbox() {
        let x265 = arguments(.mp4) {
            $0.video.codec = .hevc
            $0.video.quality = .balanced
        }
        #expect(x265.contains(sequence: ["-c:v", "libx265", "-preset", "medium", "-crf", "28", "-pix_fmt", "yuv420p", "-tag:v", "hvc1"]))
        // QuickTime's tag only matters in its own containers.
        #expect(!arguments(.mkv) { $0.video.codec = .hevc }.contains("-tag:v"))
        #expect(arguments(.mkv) { $0.video.codec = .hevc }.contains(sequence: ["-crf", "25"]))

        let toolbox = arguments(.mov, encoders: ["hevc_videotoolbox"]) { $0.video.codec = .hevc }
        #expect(toolbox.contains(sequence: ["-c:v", "hevc_videotoolbox", "-q:v", "65", "-pix_fmt", "yuv420p", "-tag:v", "hvc1"]))
        let toolboxCustom = arguments(.mov, encoders: ["hevc_videotoolbox"]) {
            $0.video.codec = .hevc
            $0.video.quality = .custom
        }
        #expect(toolboxCustom.contains(sequence: ["-c:v", "hevc_videotoolbox", "-b:v", "8000000"]))
        // Without either, H.264 it is.
        #expect(arguments(.mp4, encoders: ["libx264"]) { $0.video.codec = .hevc }.contains(sequence: ["-c:v", "libx264"]))
    }

    @Test func hdrKeepsHEVCOnceVideoSettingsAreChosen() {
        let hdr = arguments(.mp4, isHDR: true) {
            $0.video.codec = .h264
            $0.video.maxSize = .p1080
        }
        #expect(hdr.contains(sequence: ["-c:v", "libx265"]) && hdr.contains("yuv420p10le"))
        let toolbox = arguments(.mov, encoders: ["hevc_videotoolbox"], isHDR: true) { $0.video.quality = .best }
        #expect(toolbox.contains(sequence: ["-pix_fmt", "p010le", "-profile:v", "main10"]))
        // A fixed audio rate alone leaves the picture as it always was.
        #expect(arguments(.mp4, isHDR: true) { $0.audio.kbps = 128 }.contains(sequence: ["-c:v", "libx264", "-preset", "medium", "-crf", "20"]))
    }

    @Test func maxSizeFitsEitherOrientationWithoutEnlarging() {
        let filter = FFmpegArguments.scaleFilter(fitting: (1280, 720))
        #expect(filter == "scale=w='if(gte(iw,ih),min(1280,iw),min(720,iw))':h='if(gte(iw,ih),min(720,ih),min(1280,ih))':force_original_aspect_ratio=decrease:force_divisible_by=2")
        for format in [OutputFormat.mp4, .mov, .mkv, .webm, .avi, .wmv] {
            let scaled = arguments(format) { $0.video.maxSize = .p720 }
            #expect(scaled.prefix(2) == ["-vf", filter], "\(format)")
        }
        #expect(arguments(.mp4) { $0.video.maxSize = .uhd4K }[1].contains("min(3840,iw)"))
        #expect(arguments(.mp4) { $0.video.maxSize = .p480 }[1].contains("min(854,iw)"))
        #expect(!arguments(.mp4) { $0.video.maxSize = .original }.contains("-vf"))
    }

    @Test func aFixedAudioRateReplacesThePolicy() {
        // A lean source, which the automatic policy would give far less.
        let voice = SourceAudio(channels: 2, bitRate: 64_000, codec: .aac)
        var settings = OutputSettings.automatic
        settings.audio.kbps = 256
        for format in [OutputFormat.mp3, .m4a, .opus, .wma, .ogg, .mp4, .webm, .avi, .wmv] {
            let fixed = FFmpegArguments.arguments(for: format, encoders: all, audio: voice, settings: settings)
            #expect(fixed.contains(sequence: ["-b:a", "256k"]), "\(format)")
        }
        // Lossless formats have no rate to set.
        #expect(FFmpegArguments.arguments(for: .flac, encoders: all, audio: voice, settings: settings) == ["-vn", "-c:a", "flac"])
        #expect(settings.audio.argument(for: .opus, source: voice) == "256k")
        #expect(OutputSettings.Audio().argument(for: .opus, source: voice) == AudioBitRate.argument(for: .opus, source: voice))
    }

    @Test func gifWidthAndFrameRate() {
        let gif = arguments(.gif) {
            $0.gif.width = .w1080
            $0.gif.frameRate = 24
        }
        #expect(gif[1].hasPrefix("fps=24,scale='min(1080,iw)':-2:flags=lanczos,"))
        #expect(arguments(.gif) { $0.gif.width = .original }[1].contains("min(1920,iw)"))
        #expect(arguments(.gif) { $0.gif.width = .w480; $0.gif.frameRate = 10 }[1].hasPrefix("fps=10,scale='min(480,iw)'"))
    }

    @Test func webPQuality() {
        #expect(OutputSettings.Image().webPQuality == "88")
        #expect(OutputSettings.Image(quality: 0.7).webPQuality == "70")
        #expect(OutputSettings.Image(quality: 1).webPQuality == "100")
    }
}

private extension Array where Element == String {
    func contains(sequence: [String]) -> Bool {
        guard sequence.count <= count else { return false }
        return (0...(count - sequence.count)).contains { Array(self[$0..<$0 + sequence.count]) == sequence }
    }
}

/// The plans native MP4 and MOV conversions re-encode with.
struct OutputSettingsPlanTests {
    /// A 1080p phone clip: 17 Mb/s H.264 with stereo AAC.
    private var phone: VideoSourceInfo {
        var info = VideoSourceInfo(naturalSize: CGSize(width: 1920, height: 1080), duration: 10, fileSize: 22_000_000)
        info.bitRate = 17_000_000
        info.audioTracks = [.init(audio: SourceAudio(channels: 2, bitRate: 160_000, codec: .aac), sampleRate: 48_000)]
        return info
    }

    private func plan(_ source: VideoSourceInfo, _ change: (inout OutputSettings) -> Void) -> VideoCompressionPlan {
        var settings = OutputSettings.automatic
        change(&settings)
        return VideoCompressionPlan(source: source, settings: settings)
    }

    @Test func sizesFitTheBoundsInEitherOrientation() {
        #expect(plan(phone) { $0.video.maxSize = .p720 }.encodedSize == CGSize(width: 1280, height: 720))
        #expect(plan(phone) { $0.video.maxSize = .p480 }.encodedSize == CGSize(width: 854, height: 480))
        // Never enlarged.
        #expect(plan(phone) { $0.video.maxSize = .uhd4K }.encodedSize == CGSize(width: 1920, height: 1080))
        var portrait = phone
        portrait.naturalSize = CGSize(width: 2160, height: 3840)
        #expect(plan(portrait) { $0.video.maxSize = .p1080 }.encodedSize == CGSize(width: 1080, height: 1920))
        #expect(plan(portrait) { $0.video.maxSize = .uhd4K }.encodedSize == CGSize(width: 2160, height: 3840))
        var odd = phone
        odd.naturalSize = CGSize(width: 1281, height: 721)
        #expect(plan(odd) { $0.video.codec = .h264 }.encodedSize == CGSize(width: 1280, height: 720))
    }

    @Test func codecFollowsTheSettingButHDRStaysHEVC() {
        #expect(plan(phone) { $0.video.maxSize = .p720 }.codec == .h264)
        #expect(plan(phone) { $0.video.codec = .hevc }.codec == .hevc)
        var hdr = phone
        hdr.isHDR = true
        hdr.isHEVC = true
        #expect(plan(hdr) { $0.video.codec = .h264 }.codec == .hevc)
        #expect(plan(hdr) { $0.video.maxSize = .p720 }.codec == .hevc)
    }

    @Test func qualityChoosesTheBitrate() {
        let rates = OutputSettings.Video.Quality.allCases.filter { $0 != .custom }.map { quality in
            (quality, plan(phone) { $0.video.quality = quality; $0.video.codec = .h264 }.videoBitRate)
        }
        let rate = Dictionary(uniqueKeysWithValues: rates)
        // 6 Mb/s is High's rate for 1080p at 30 fps (`VideoCompressionPlan`).
        #expect(rate[.best] == 12_000_000)
        #expect(rate[.automatic] == 9_000_000)
        #expect(rate[.balanced] == 6_000_000)
        #expect(rate[.smaller] == 3_000_000)
        // HEVC needs fewer bits for the same picture.
        #expect(plan(phone) { $0.video.codec = .hevc; $0.video.quality = .balanced }.videoBitRate == 3_900_000)
    }

    @Test func automaticAndBestKeepAboutTheSourcesQuality() {
        var lean = phone
        lean.bitRate = 2_000_000
        #expect(plan(lean) { $0.video.quality = .best }.videoBitRate == 2_000_000)
        #expect(plan(lean) { $0.video.maxSize = .p1080 }.videoBitRate == 2_000_000)
        // Balanced and Smaller stay under it, as Compress does.
        #expect(plan(lean) { $0.video.quality = .balanced }.videoBitRate == 1_600_000)
        // A custom rate is used as it is, even above the source's.
        let custom = plan(lean) {
            $0.video.quality = .custom
            $0.video.customMbps = 5
        }
        #expect(custom.videoBitRate == 5_000_000)
        #expect(custom.estimatedBytes == Int64(Double(5_000_000 + 160_000) / 8 * 10 * VideoCompressionPlan.containerOverhead))
    }

    @Test func soundFollowsTheFixedRate() {
        // Automatic: the small AAC track is copied, as Compress does on High.
        #expect(plan(phone) { $0.video.maxSize = .p720 }.audio == [.passthrough(bitRate: 160_000)])
        #expect(plan(phone) { $0.audio.kbps = 96 }.audio == [.aac(bitRate: 96_000, channels: 2, sampleRate: 48_000)])
        var mono = phone
        mono.audioTracks = [.init(audio: SourceAudio(channels: 1, bitRate: nil), sampleRate: 22_050)]
        #expect(plan(mono) { $0.audio.kbps = 320 }.audio == [.aac(bitRate: 256_000, channels: 1, sampleRate: 44_100)])
    }

    @Test func gifPlansFollowTheSettings() {
        // The defaults are the old fixed values.
        #expect(VideoGIF.plan(displayWidth: 1920, displayHeight: 1080, duration: 2) == VideoGIF.plan(displayWidth: 1920, displayHeight: 1080, duration: 2, maxWidth: 720, framesPerSecond: 12))
        let wide = VideoGIF.plan(displayWidth: 1920, displayHeight: 1080, duration: 2, maxWidth: 1080, framesPerSecond: 24)
        #expect(wide.width == 1080 && wide.height == 608)
        #expect(wide.frameCount == 48 && abs(wide.interval - 1.0 / 24) < 1e-9)
        let small = VideoGIF.plan(displayWidth: 640, displayHeight: 360, duration: 2, maxWidth: 1920, framesPerSecond: 10)
        #expect(small.width == 640 && small.frameCount == 20)
    }
}

/// Converts real files with chosen settings and checks what comes out.
struct OutputSettingsEndToEndTests {
    private func makeDirectory() throws -> URL {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("openshelf-output-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        return directory
    }

    /// Two seconds of 1080p H.264 at a high bitrate: moving bands with a
    /// little grain, which an encoder can fit into what it's given (pure
    /// noise can't be, at any setting).
    private func makeClip(in directory: URL) async throws -> URL {
        let url = directory.appendingPathComponent("Clip.mov")
        let size = CGSize(width: 1920, height: 1080)
        let writer = try AVAssetWriter(outputURL: url, fileType: .mov)
        let input = AVAssetWriterInput(mediaType: .video, outputSettings: [
            AVVideoCodecKey: AVVideoCodecType.h264,
            AVVideoWidthKey: Int(size.width),
            AVVideoHeightKey: Int(size.height),
            AVVideoCompressionPropertiesKey: [AVVideoAverageBitRateKey: 30_000_000],
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
        var state: UInt64 = 0x9E37_79B9_7F4A_7C15
        for frame in 0..<60 {
            while !input.isReadyForMoreMediaData { try await Task.sleep(for: .milliseconds(5)) }
            var buffer: CVPixelBuffer?
            CVPixelBufferPoolCreatePixelBuffer(nil, adaptor.pixelBufferPool!, &buffer)
            let pixels = try #require(buffer)
            CVPixelBufferLockBaseAddress(pixels, [])
            let rowWords = CVPixelBufferGetBytesPerRow(pixels) / 8
            let words = CVPixelBufferGetBaseAddress(pixels)!.assumingMemoryBound(to: UInt64.self)
            for row in 0..<Int(size.height) {
                let band = UInt64(UInt8(truncatingIfNeeded: row / 4 + frame * 3))
                let shade = band * 0x0101_0101_0101_0101
                for column in 0..<rowWords {
                    state ^= state << 13
                    state ^= state >> 7
                    state ^= state << 17
                    words[row * rowWords + column] = shade ^ (state & 0x1F1F_1F1F_1F1F_1F1F)
                }
            }
            CVPixelBufferUnlockBaseAddress(pixels, [])
            adaptor.append(pixels, withPresentationTime: CMTime(value: CMTimeValue(frame), timescale: 30))
        }
        input.markAsFinished()
        await writer.finishWriting()
        #expect(writer.status == .completed)
        return url
    }

    @Test func convertsToHEVCAtTheChosenSizeAndBitrate() async throws {
        let directory = try makeDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        let source = try await makeClip(in: directory)
        var settings = OutputSettings.automatic
        settings.video = .init(codec: .hevc, quality: .custom, customMbps: 3, maxSize: .p720)

        let output = try await MediaConversion.convert(source, to: .mp4, tools: ExternalTools(), settings: settings) { _ in }
        #expect(output.lastPathComponent == "Clip.mp4")
        let track = try #require(try await AVURLAsset(url: output).loadTracks(withMediaType: .video).first)
        let (naturalSize, formats, rate) = try await track.load(.naturalSize, .formatDescriptions, .estimatedDataRate)
        #expect(naturalSize == CGSize(width: 1280, height: 720))
        #expect(formats.first.map(CMFormatDescriptionGetMediaSubType) == kCMVideoCodecType_HEVC)
        #expect(rate > 2_000_000 && rate < 4_000_000, "\(rate)")
        // Nothing hidden is left behind.
        let leftovers = try FileManager.default.contentsOfDirectory(atPath: directory.path).filter { $0.hasPrefix(".openshelf-") }
        #expect(leftovers.isEmpty)
    }

    @Test func movKeepsItsContainer() async throws {
        let directory = try makeDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        let source = try await makeClip(in: directory)
        var settings = OutputSettings.automatic
        settings.video = .init(codec: .h264, quality: .smaller, maxSize: .p480)

        let output = try await MediaConversion.convert(source, to: .mov, tools: ExternalTools(), settings: settings) { _ in }
        #expect(output.lastPathComponent == "Clip 2.mov")
        let header = try FileHandle(forReadingFrom: output).read(upToCount: 12) ?? Data()
        // A QuickTime file's "ftyp" brand is "qt  ".
        #expect(String(decoding: header.suffix(4), as: UTF8.self) == "qt  ")
        let track = try #require(try await AVURLAsset(url: output).loadTracks(withMediaType: .video).first)
        let (naturalSize, formats) = try await track.load(.naturalSize, .formatDescriptions)
        #expect(naturalSize == CGSize(width: 854, height: 480))
        #expect(formats.first.map(CMFormatDescriptionGetMediaSubType) == kCMVideoCodecType_H264)
    }

    @Test func m4aAtAFixedBitrate() async throws {
        let directory = try makeDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        let source = try makeTone(in: directory)
        var settings = OutputSettings.automatic
        settings.audio.kbps = 96
        let output = try await MediaConversion.convert(source, to: .m4a, tools: ExternalTools(), settings: settings) { _ in }
        #expect(output.pathExtension == "m4a")
        let track = try #require(try await AVURLAsset(url: output).loadTracks(withMediaType: .audio).first)
        let (formats, rate) = try await track.load(.formatDescriptions, .estimatedDataRate)
        let description = try #require(formats.first.flatMap { CMAudioFormatDescriptionGetStreamBasicDescription($0)?.pointee })
        #expect(description.mFormatID == kAudioFormatMPEG4AAC)
        #expect(description.mChannelsPerFrame == 2)
        #expect(rate > 80_000 && rate < 112_000, "\(rate)")
    }

    /// Ten seconds of a chord over hiss in stereo PCM, which AAC needs its
    /// whole bitrate for. The file is finished when it's let go of, on return.
    private func makeTone(in directory: URL) throws -> URL {
        let url = directory.appendingPathComponent("Tone.wav")
        let format = try #require(AVAudioFormat(standardFormatWithSampleRate: 44_100, channels: 2))
        let file = try AVAudioFile(forWriting: url, settings: format.settings)
        let frames: AVAudioFrameCount = 441_000
        let buffer = try #require(AVAudioPCMBuffer(pcmFormat: format, frameCapacity: frames))
        buffer.frameLength = frames
        var state: UInt32 = 2_463_534_242
        for channel in 0..<2 {
            let samples = buffer.floatChannelData![channel]
            for index in 0..<Int(frames) {
                let time = Double(index) / 44_100
                state = state &* 1_664_525 &+ 1_013_904_223
                let hiss = Double(state >> 8) / Double(1 << 24) - 0.5
                samples[index] = Float(0.2 * sin(2 * .pi * 440 * time) + 0.2 * sin(2 * .pi * (660 + Double(channel) * 3) * time) + 0.2 * hiss)
            }
        }
        try file.write(from: buffer)
        return url
    }

    @Test func imagesAtAChosenQualityWithoutMetadata() async throws {
        let directory = try makeDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        // A noisy TIFF from a camera, with the camera and the date it was taken.
        let source = directory.appendingPathComponent("Photo.tiff")
        let width = 400
        let height = 300
        var state: UInt32 = 12_345
        let bytes: [UInt8] = (0..<width * height * 4).map { _ in
            state = state &* 1_664_525 &+ 1_013_904_223
            return UInt8(truncatingIfNeeded: state >> 24)
        }
        let image = try #require(bytes.withUnsafeBytes { raw -> CGImage? in
            let context = CGContext(
                data: UnsafeMutableRawPointer(mutating: raw.baseAddress), width: width, height: height, bitsPerComponent: 8,
                bytesPerRow: width * 4, space: CGColorSpace(name: CGColorSpace.sRGB)!, bitmapInfo: CGImageAlphaInfo.noneSkipLast.rawValue
            )
            return context?.makeImage()
        })
        let metadata: [CFString: Any] = [
            kCGImagePropertyTIFFDictionary: [kCGImagePropertyTIFFMake: "OpenShelf Camera"],
            kCGImagePropertyExifDictionary: [kCGImagePropertyExifDateTimeOriginal: "2026:10:22 12:00:00"],
        ]
        try EditableImage.write(image, to: source, type: .tiff, metadata: metadata)

        func properties(_ url: URL) throws -> [CFString: Any] {
            let reader = try #require(CGImageSourceCreateWithURL(url as CFURL, nil))
            return try #require(CGImageSourceCopyPropertiesAtIndex(reader, 0, nil) as? [CFString: Any])
        }
        func make(_ url: URL) throws -> String? {
            (try properties(url)[kCGImagePropertyTIFFDictionary] as? [CFString: Any])?[kCGImagePropertyTIFFMake] as? String
        }

        let usual = try await ImageConversion.convert(source, to: .jpg, tools: ExternalTools())
        #expect(try make(usual) == "OpenShelf Camera")
        let small = try await ImageConversion.convert(source, to: .jpg, tools: ExternalTools(), settings: .init(quality: 0.4, keepsMetadata: false))
        #expect(small.lastPathComponent == "Photo 2.jpg")
        #expect(try make(small) == nil)
        #expect((try properties(small)[kCGImagePropertyExifDictionary] as? [CFString: Any])?[kCGImagePropertyExifDateTimeOriginal] == nil)
        #expect(MediaSize.fileSize(small) < MediaSize.fileSize(usual) * 2 / 3)
        // Metadata kept at a chosen quality too.
        let kept = try await ImageConversion.convert(source, to: .heic, tools: ExternalTools(), settings: .init(quality: 0.5))
        #expect(try make(kept) == "OpenShelf Camera")
    }
}
