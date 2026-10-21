@preconcurrency import AVFoundation
import Foundation

/// The facts about a source's sound that decide how many bits a lossy copy needs.
struct SourceAudio: Equatable, Sendable {
    var channels: Int
    /// Bits per second, or nil when the container doesn't say.
    var bitRate: Int?
    /// The lossy codec it's in, when it's one `AudioBitRate` knows.
    var codec: AudioBitRate.Codec?
}

/// One bitrate policy for every lossy audio output: what the codec needs to
/// sound good, but never more than the source had to give.
enum AudioBitRate {
    enum Codec: Sendable {
        case aac, opus, vorbis, mp3, wma

        init?(formatID: AudioFormatID) {
            switch formatID {
            case kAudioFormatMPEG4AAC, kAudioFormatMPEG4AAC_HE, kAudioFormatMPEG4AAC_HE_V2: self = .aac
            case kAudioFormatOpus: self = .opus
            case kAudioFormatMPEGLayer3: self = .mp3
            default: return nil
            }
        }

        /// From ffprobe's codec_name.
        init?(ffprobeName: String) {
            switch ffprobeName {
            case "aac": self = .aac
            case "opus": self = .opus
            case "vorbis": self = .vorbis
            case "mp3": self = .mp3
            case "wmav1", "wmav2": self = .wma
            default: return nil
            }
        }

        /// Bits needed for what AAC, Opus or Vorbis do with one.
        var bitsPerAACBit: Double {
            switch self {
            case .aac, .opus, .vorbis: 1
            case .mp3, .wma: 1.25
            }
        }

        /// Stereo floor and ceiling in kb/s: below the floor it sounds poor,
        /// above the ceiling no one can hear a difference.
        var kbpsRange: (floor: Int, ceiling: Int) {
            switch self {
            case .opus: (32, 128)
            case .aac: (64, 192)
            case .vorbis: (64, 160)
            case .mp3: (64, 192)
            case .wma: (64, 160)
            }
        }
    }

    /// Bitrates encoders and players commonly use, in kb/s.
    static let steps = [24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320]

    /// Bits per second for a lossy copy of `source` in `codec`.
    ///
    /// A clean source (lossless, or lossy at a high rate) gets the codec's
    /// ceiling, halved for mono, which is usually speech. A lossy source gets
    /// what matches its own rate, since re-encoding can't restore what it
    /// dropped and extra bits only make the file bigger. MP3 and WMA need a
    /// quarter more than AAC, Opus and Vorbis for the same sound, both ways.
    /// - Parameter limit: An optional stereo ceiling below the codec's own.
    static func target(for codec: Codec, source: SourceAudio?, limit: Int? = nil) -> Int {
        let range = codec.kbpsRange
        var ceiling: Int = range.ceiling
        if let limit { ceiling = min(ceiling, limit / 1000) }
        var floor: Int = range.floor
        if source?.channels == 1 {
            ceiling /= 2
            floor = floor * 3 / 4
        }
        ceiling = max(ceiling, floor)

        var kbps: Int = ceiling
        if let source, let bitRate = source.bitRate, bitRate > 0 {
            let sourceFactor: Double = source.codec?.bitsPerAACBit ?? 1
            let needed: Double = Double(bitRate) / 1000 / sourceFactor * codec.bitsPerAACBit
            kbps = min(ceiling, max(floor, Int(needed.rounded())))
        }
        // Round down to a common rate, but never below the codec's floor.
        let step: Int = steps.last { $0 <= kbps } ?? floor
        return max(step, floor) * 1000
    }

    /// The ffmpeg argument for `target(for:source:)`, e.g. "96k".
    static func argument(for codec: Codec, source: SourceAudio?) -> String {
        "\(target(for: codec, source: source) / 1000)k"
    }
}

extension SourceAudio {
    /// The first sound track of a file AVFoundation can read, or nil when it has none.
    static func load(from url: URL) async -> SourceAudio? {
        guard let track = try? await AVURLAsset(url: url).loadTracks(withMediaType: .audio).first,
              let loaded = try? await track.load(.estimatedDataRate, .formatDescriptions)
        else { return nil }
        let (rate, formats) = loaded
        guard let description = formats.first.flatMap({ CMAudioFormatDescriptionGetStreamBasicDescription($0)?.pointee }) else {
            return SourceAudio(channels: 2, bitRate: rate > 0 ? Int(rate) : nil)
        }
        return SourceAudio(description, bitRate: rate)
    }

    init(_ description: AudioStreamBasicDescription, bitRate: Float) {
        self.init(
            channels: max(Int(description.mChannelsPerFrame), 1),
            bitRate: bitRate > 0 ? Int(bitRate) : nil,
            codec: AudioBitRate.Codec(formatID: description.mFormatID)
        )
    }

    /// The first sound stream as ffprobe sees it, for files AVFoundation can't
    /// open. For audio-only files whose stream has no rate (Ogg, Matroska) the
    /// container's overall rate stands in.
    static func probe(_ url: URL, ffprobe: URL?) -> SourceAudio? {
        guard let ffprobe,
              let output = MediaProbe.runSync(ffprobe, ["-v", "error", "-show_entries", "stream=codec_type,codec_name,channels,bit_rate:format=bit_rate", "-of", "json", url.path])
        else { return nil }
        return parse(ffprobeJSON: Data(output.utf8))
    }

    static func parse(ffprobeJSON data: Data) -> SourceAudio? {
        guard let root = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let streams = root["streams"] as? [[String: Any]],
              let audio = streams.first(where: { $0["codec_type"] as? String == "audio" })
        else { return nil }
        // ffprobe writes rates as strings ("96000"), or leaves them out.
        func rate(_ value: Any?) -> Int? {
            guard let text = value as? String, let rate = Int(text), rate > 0 else { return nil }
            return rate
        }
        let hasVideo = streams.contains { $0["codec_type"] as? String == "video" }
        let format = root["format"] as? [String: Any]
        return SourceAudio(
            channels: max(audio["channels"] as? Int ?? 2, 1),
            bitRate: rate(audio["bit_rate"]) ?? (hasVideo ? nil : rate(format?["bit_rate"])),
            codec: (audio["codec_name"] as? String).flatMap(AudioBitRate.Codec.init(ffprobeName:))
        )
    }
}
