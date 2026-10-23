import Foundation

/// How the Convert wheel encodes what it writes, chosen in Settings → Output
/// or, holding ⌥, for one conversion (see `ConvertOptionsView`).
///
/// `.automatic` is exactly what conversions did before there were settings:
/// the same export presets, stream copies and encoder settings.
struct OutputSettings: Codable, Equatable, Sendable {
    var video = Video()
    var audio = Audio()
    var image = Image()
    var gif = GIF()

    static let automatic = OutputSettings()

    /// MP4, MOV, MKV, WebM, AVI and WMV.
    struct Video: Codable, Equatable, Sendable {
        enum Codec: String, Codable, CaseIterable, Identifiable, Sendable {
            case automatic, h264, hevc

            var id: Self { self }

            var title: String {
                switch self {
                case .automatic: "Automatic"
                case .h264: "H.264"
                case .hevc: "HEVC (H.265)"
                }
            }
        }

        /// In menu order, best first.
        enum Quality: String, Codable, CaseIterable, Identifiable, Sendable {
            case automatic, best, balanced, smaller, custom

            var id: Self { self }

            var title: String {
                switch self {
                case .automatic: "Automatic"
                case .best: "Best"
                case .balanced: "Balanced"
                case .smaller: "Smaller"
                case .custom: "Custom"
                }
            }

            /// Share of the bitrate Compress's High quality gets (see
            /// `VideoCompressionPlan.videoBitRate`), when MP4 and MOV are
            /// re-encoded natively; nil for a custom bitrate.
            var videoFactor: Double? {
                switch self {
                case .automatic: 1.5
                case .best: 2
                case .balanced: 1
                case .smaller: 0.5
                case .custom: nil
                }
            }

            /// The most of the source's own bits a re-encode may spend for the
            /// same picture. Automatic and Best keep about the source's quality;
            /// the others stay under it, as Compress does.
            var sourceShare: Double {
                switch self {
                case .automatic, .best, .custom: 1
                case .balanced, .smaller: VideoCompressionPlan.sourceShare
                }
            }
        }

        enum MaxSize: String, Codable, CaseIterable, Identifiable, Sendable {
            case original
            case uhd4K = "4k"
            case p1080, p720, p480

            var id: Self { self }

            var title: String {
                switch self {
                case .original: "Original"
                case .uhd4K: "4K"
                case .p1080: "1080p"
                case .p720: "720p"
                case .p480: "480p"
                }
            }

            /// The frame a video is fitted inside, by its long and short edges
            /// (as `VideoResolution.bounds`); nil keeps the size.
            var bounds: (long: Double, short: Double)? {
                switch self {
                case .original: nil
                case .uhd4K: (3840, 2160)
                case .p1080: VideoResolution.p1080.bounds
                case .p720: VideoResolution.p720.bounds
                case .p480: VideoResolution.p480.bounds
                }
            }
        }

        static let customMbpsRange: ClosedRange<Double> = 0.1...200

        var codec = Codec.automatic
        var quality = Quality.automatic
        /// Megabits per second for `.custom`.
        var customMbps: Double = 8
        var maxSize = MaxSize.original

        var isAutomatic: Bool {
            codec == .automatic && quality == .automatic && maxSize == .original
        }

        /// `customMbps` in bits per second.
        var customBitRate: Int {
            Int((customMbps * 1_000_000).rounded())
        }

        /// `mbps` within `customMbpsRange`; the default for something that isn't a number.
        static func clampedMbps(_ mbps: Double) -> Double {
            guard mbps.isFinite else { return Video().customMbps }
            return min(max(mbps, customMbpsRange.lowerBound), customMbpsRange.upperBound)
        }
    }

    /// The sound in lossy outputs: MP3, M4A, OGG, Opus, WMA and video.
    struct Audio: Codable, Equatable, Sendable {
        /// The fixed rates offered, in kb/s.
        static let choices = [96, 128, 160, 192, 256, 320]

        /// kb/s, or nil to follow the source (see `AudioBitRate`).
        var kbps: Int?

        /// The ffmpeg `-b:a` value for `codec`: the fixed rate, or the
        /// automatic policy's for `source`.
        func argument(for codec: AudioBitRate.Codec, source: SourceAudio?) -> String {
            guard let kbps else { return AudioBitRate.argument(for: codec, source: source) }
            return "\(Self.kbps(kbps, for: codec, channels: source?.channels ?? 2))k"
        }

        /// A fixed rate lowered to what `codec` accepts for `channels`: some
        /// encoders refuse 320 kb/s for a single channel.
        static func kbps(_ kbps: Int, for codec: AudioBitRate.Codec, channels: Int) -> Int {
            guard channels == 1, let limit = monoLimit(of: codec) else { return kbps }
            return min(kbps, limit)
        }

        /// The most `codec` takes for one channel, in kb/s, when it's below
        /// what's offered: Apple's AAC encoder and libopus stop at 256, and
        /// libvorbis lower still.
        static func monoLimit(of codec: AudioBitRate.Codec) -> Int? {
            switch codec {
            case .aac, .opus: 256
            case .vorbis: 192
            case .mp3, .wma: nil
            }
        }

        /// The fixed rate for an AAC track of `channels`, in bits per second.
        static func aacBitRate(kbps: Int, channels: Int) -> Int {
            Self.kbps(kbps, for: .aac, channels: channels) * 1000
        }
    }

    /// JPEG, HEIC, AVIF and WebP quality, and metadata in every image format.
    struct Image: Codable, Equatable, Sendable {
        static let qualityRange: ClosedRange<Double> = 0.4...1
        /// The qualities offered, highest first.
        static let qualityChoices: [Double] = [1, 0.9, 0.8, 0.7, 0.6, 0.5, 0.4]

        /// 0.4–1, or nil for each format's usual (JPEG, HEIC and AVIF 0.9,
        /// WebP 88, PDF pages 0.92).
        var quality: Double?
        /// Copy the source's metadata (camera, date, location) into the copy.
        var keepsMetadata = true

        /// The quality to write with, given the format's usual one.
        func quality(otherwise usual: Double) -> Double {
            quality ?? usual
        }

        /// The 0–100 value cwebp and ffmpeg take for WebP.
        var webPQuality: String {
            quality.map { "\(Int(($0 * 100).rounded()))" } ?? "88"
        }
    }

    /// Animated GIFs made from video.
    struct GIF: Codable, Equatable, Sendable {
        enum Width: String, Codable, CaseIterable, Identifiable, Sendable {
            case w480 = "480"
            case w720 = "720"
            case w1080 = "1080"
            /// The video's own width, up to 1920 px.
            case original

            var id: Self { self }

            var title: String {
                switch self {
                case .w480: "480 px"
                case .w720: "720 px"
                case .w1080: "1080 px"
                case .original: "Original (up to 1920 px)"
                }
            }

            /// The widest a GIF gets; narrower videos keep their width.
            var maxWidth: Int {
                switch self {
                case .w480: 480
                case .w720: 720
                case .w1080: 1080
                case .original: 1920
                }
            }
        }

        static let frameRates = [10, 12, 15, 24]

        var width = Width.w720
        var frameRate = 12
    }

    /// Whether an MP4 or MOV is re-encoded to these settings rather than
    /// exported, or copied, the usual way.
    var reencodesVideo: Bool {
        !video.isAutomatic || audio.kbps != nil
    }

    // MARK: Storing

    /// JSON, as kept in UserDefaults.
    var stored: String {
        guard let data = try? JSONEncoder().encode(self) else { return "{}" }
        return String(decoding: data, as: UTF8.self)
    }

    /// Nothing stored, or something unreadable, means `.automatic`. Settings
    /// missing from what's stored (from an older version) keep their defaults.
    static func decode(_ stored: String?) -> OutputSettings {
        guard let stored, let settings = try? JSONDecoder().decode(OutputSettings.self, from: Data(stored.utf8)) else {
            return .automatic
        }
        return settings
    }
}

// Decoding keeps every readable setting, falling back to the default for one
// that's missing or out of range, so one odd value doesn't reset the rest.

extension OutputSettings {
    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        self.init()
        video = container.value(.video, or: video)
        audio = container.value(.audio, or: audio)
        image = container.value(.image, or: image)
        gif = container.value(.gif, or: gif)
    }
}

extension OutputSettings.Video {
    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        self.init()
        codec = container.value(.codec, or: codec)
        quality = container.value(.quality, or: quality)
        customMbps = Self.clampedMbps(container.value(.customMbps, or: customMbps))
        maxSize = container.value(.maxSize, or: maxSize)
    }
}

extension OutputSettings.Audio {
    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        self.init()
        let kbps: Int? = container.value(.kbps, or: nil)
        self.kbps = kbps.flatMap { Self.choices.contains($0) ? $0 : nil }
    }
}

extension OutputSettings.Image {
    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        self.init()
        let quality: Double? = container.value(.quality, or: nil)
        // Kept to the offered steps, so the menu always shows the choice.
        self.quality = quality.flatMap { value in
            guard value.isFinite else { return nil }
            let clamped: Double = min(max(value, Self.qualityRange.lowerBound), Self.qualityRange.upperBound)
            return (clamped * 10).rounded() / 10
        }
        keepsMetadata = container.value(.keepsMetadata, or: keepsMetadata)
    }
}

extension OutputSettings.GIF {
    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        self.init()
        width = container.value(.width, or: width)
        let rate: Int = container.value(.frameRate, or: frameRate)
        if Self.frameRates.contains(rate) { frameRate = rate }
    }
}

private extension KeyedDecodingContainer {
    /// The value under `key`, or `fallback` when it's missing or can't be read.
    func value<T: Decodable>(_ key: Key, or fallback: T) -> T {
        (try? decodeIfPresent(T.self, forKey: key)) ?? fallback
    }
}

// MARK: - Which settings apply

/// The settings that apply to one conversion, for the ⌥ options window: only
/// lossy outputs have anything to choose.
struct OutputOptions: Equatable {
    /// H.264 or HEVC, for MP4, MOV and MKV.
    var videoCodec = false
    /// Quality and size, for every video output.
    var video = false
    var audioBitRate = false
    var imageQuality = false
    var imageMetadata = false
    var gif = false

    var isEmpty: Bool {
        self == OutputOptions()
    }

    /// What applies when `urls` are converted to `format`.
    init(format: OutputFormat, urls: [URL]) {
        self.init(format: format, kinds: Set(urls.map { FileKind(url: $0) }))
    }

    /// What applies when files of `kinds` are converted to `format`.
    init(format: OutputFormat, kinds: Set<FileKind>) {
        let isMedia = !kinds.isDisjoint(with: [.video, .audio])
        let isImage = kinds.contains(.image)
        switch format {
        case .mp4, .mov, .mkv:
            videoCodec = isMedia
            video = isMedia
            audioBitRate = isMedia
        case .webm, .avi, .wmv:
            video = isMedia
            audioBitRate = isMedia
        case .m4a, .mp3, .ogg, .opus, .wma:
            audioBitRate = isMedia
        case .jpg:
            // PDF pages become JPEGs too; slides go through LibreOffice instead.
            imageQuality = isImage || kinds.contains(.pdf)
            imageMetadata = isImage
        case .heic, .avif:
            imageQuality = isImage
            imageMetadata = isImage
        case .webp:
            // cwebp and ffmpeg write WebP without metadata either way.
            imageQuality = isImage
        case .gif:
            gif = isMedia
        default:
            break
        }
    }

    init() {}
}

extension OutputSettings {
    /// These settings with the parts `options` cover taken from `other`: to
    /// save what the ⌥ window shows without touching the rest, or to reset it.
    func applying(_ options: OutputOptions, from other: OutputSettings) -> OutputSettings {
        var result = self
        if options.videoCodec { result.video.codec = other.video.codec }
        if options.video {
            result.video.quality = other.video.quality
            result.video.customMbps = other.video.customMbps
            result.video.maxSize = other.video.maxSize
        }
        if options.audioBitRate { result.audio = other.audio }
        if options.imageQuality { result.image.quality = other.image.quality }
        if options.imageMetadata { result.image.keepsMetadata = other.image.keepsMetadata }
        if options.gif { result.gif = other.gif }
        return result
    }
}

// MARK: - Native MP4 and MOV

extension VideoCompressionPlan {
    /// How a conversion to MP4 or MOV re-encodes `source` when `settings`
    /// ask for more than the export preset does (see `reencodesVideo`).
    ///
    /// HDR always stays 10-bit HEVC, whatever codec is chosen, since H.264
    /// can't hold its colours. Automatic quality keeps about the source's
    /// own quality; a custom bitrate is used as it is. Sound follows the
    /// fixed bitrate when there is one, else the usual policy.
    init(source: VideoSourceInfo, settings: OutputSettings) {
        let video = settings.video
        let codec: Codec = source.isHDR || video.codec == .hevc ? .hevc : .h264
        let encodedSize = Self.encodedSize(for: source.naturalSize, bounds: video.maxSize.bounds)
        let videoBitRate: Int = if let factor = video.quality.videoFactor {
            Self.videoBitRate(for: encodedSize, factor: factor, sourceShare: video.quality.sourceShare, codec: codec, source: source)
        } else {
            video.customBitRate
        }
        let audio: [Audio] = source.audioTracks.map { track in
            guard let kbps = settings.audio.kbps else { return Self.audio(for: track, limit: nil) }
            let channels = min(track.audio.channels, 2)
            return Self.aac(for: track, bitRate: OutputSettings.Audio.aacBitRate(kbps: kbps, channels: channels))
        }
        self.init(source: source, codec: codec, encodedSize: encodedSize, videoBitRate: videoBitRate, audio: audio)
    }
}
