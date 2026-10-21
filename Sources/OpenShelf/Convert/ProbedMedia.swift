import CoreGraphics
import Foundation

/// A file's streams as ffprobe reports them, for files AVFoundation can't open.
struct ProbedMedia: Equatable, Sendable {
    struct Video: Equatable, Sendable {
        /// Frame size as stored, before rotation.
        var width: Int
        var height: Int
        /// Degrees players turn the picture clockwise: 0, 90, 180 or 270.
        var rotation: Int = 0
        /// Frames per second, as ffmpeg's exact fraction ("30000/1001"), or nil when unknown.
        var frameRate: String?
        /// Bits per second; nil when the container doesn't say.
        var bitRate: Int?
        var codec: String
        var pixelFormat: String?
        /// "smpte2084" (PQ) or "arib-std-b67" (HLG) for HDR.
        var colorTransfer: String?

        /// The size players show, after rotation.
        var displaySize: CGSize {
            rotation % 180 == 0
                ? CGSize(width: width, height: height)
                : CGSize(width: height, height: width)
        }

        var framesPerSecond: Double? {
            frameRate.flatMap(ProbedMedia.rate)
        }

        var isHDR: Bool {
            colorTransfer == "smpte2084" || colorTransfer == "arib-std-b67"
        }
    }

    struct Audio: Equatable, Sendable {
        var codec: String
        var channels: Int
        var sampleRate: Double?
        var bitRate: Int?

        /// What `AudioBitRate` needs to pick a rate for a lossy copy.
        var source: SourceAudio {
            SourceAudio(channels: channels, bitRate: bitRate, codec: AudioBitRate.Codec(ffprobeName: codec))
        }
    }

    var duration: Double
    /// The first video stream that isn't cover art.
    var video: Video?
    var audio: [Audio]

    /// Asks ffprobe about `url`. Throws when ffprobe can't read it either.
    static func load(_ url: URL, ffprobe: URL?) async throws -> ProbedMedia {
        guard let ffprobe else { throw MissingToolError(name: "ffprobe") }
        let output = FileManager.default.temporaryDirectory.appendingPathComponent("openshelf-probe-\(UUID().uuidString).json")
        defer { try? FileManager.default.removeItem(at: output) }
        do {
            try await ProcessRunner.run(ffprobe, [
                "-v", "error",
                "-show_entries", "format=duration:stream=codec_type,codec_name,width,height,pix_fmt,color_transfer,avg_frame_rate,r_frame_rate,bit_rate,channels,sample_rate,duration:stream_disposition=attached_pic:stream_tags=rotate:stream_side_data=rotation",
                "-of", "json",
                url.path,
            ], standardOutputFile: output)
        } catch is ProcessFailure {
            // Neither AVFoundation nor ffmpeg can read it.
            throw ConversionError.unreadable(url)
        }
        guard let probed = parse(try Data(contentsOf: output)), probed.video != nil || !probed.audio.isEmpty else {
            throw ConversionError.unreadable(url)
        }
        return probed
    }

    static func parse(_ data: Data) -> ProbedMedia? {
        guard let root = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else { return nil }
        let streams = root["streams"] as? [[String: Any]] ?? []
        let format = root["format"] as? [String: Any]

        var video: Video?
        var audio: [Audio] = []
        var longestStream: Double = 0
        for stream in streams {
            longestStream = max(longestStream, number(stream["duration"]) ?? 0)
            switch stream["codec_type"] as? String {
            case "video":
                let disposition = stream["disposition"] as? [String: Any]
                guard video == nil, disposition?["attached_pic"] as? Int != 1,
                      let width = stream["width"] as? Int, let height = stream["height"] as? Int
                else { continue }
                let frameRate = [stream["avg_frame_rate"], stream["r_frame_rate"]]
                    .compactMap { $0 as? String }
                    .first { (rate($0) ?? 0) > 0 }
                video = Video(
                    width: width,
                    height: height,
                    rotation: rotation(of: stream),
                    frameRate: frameRate,
                    bitRate: number(stream["bit_rate"]).map { Int($0) },
                    codec: stream["codec_name"] as? String ?? "",
                    pixelFormat: stream["pix_fmt"] as? String,
                    colorTransfer: stream["color_transfer"] as? String
                )
            case "audio":
                audio.append(Audio(
                    codec: stream["codec_name"] as? String ?? "",
                    channels: max(stream["channels"] as? Int ?? 2, 1),
                    sampleRate: number(stream["sample_rate"]),
                    bitRate: number(stream["bit_rate"]).map { Int($0) }
                ))
            default:
                continue
            }
        }
        let duration: Double = number(format?["duration"]) ?? longestStream
        return ProbedMedia(duration: duration, video: video, audio: audio)
    }

    /// Clockwise degrees from the display matrix ffmpeg 5 and later report
    /// (counter-clockwise, so -90 for a portrait phone video), or from the
    /// older "rotate" tag (clockwise).
    private static func rotation(of stream: [String: Any]) -> Int {
        var clockwise: Int = 0
        let sideData = stream["side_data_list"] as? [[String: Any]] ?? []
        if let counterClockwise = sideData.compactMap({ number($0["rotation"]) }).first {
            clockwise = -Int(counterClockwise.rounded())
        } else if let tags = stream["tags"] as? [String: Any], let tag = number(tags["rotate"]) {
            clockwise = Int(tag.rounded())
        }
        let normalized: Int = ((clockwise % 360) + 360) % 360
        return (normalized / 90) * 90
    }

    /// "30000/1001" or "25" as a number; nil for "0/0".
    static func rate(_ text: String) -> Double? {
        let parts = text.split(separator: "/")
        guard let numerator = parts.first.flatMap({ Double($0) }) else { return nil }
        let denominator: Double = parts.count > 1 ? Double(parts[1]) ?? 0 : 1
        guard denominator > 0, numerator > 0 else { return nil }
        return numerator / denominator
    }

    /// ffprobe writes most numbers as strings ("96000"), some as numbers.
    private static func number(_ value: Any?) -> Double? {
        if let text = value as? String { return Double(text) }
        if let number = value as? NSNumber { return number.doubleValue }
        return nil
    }
}
