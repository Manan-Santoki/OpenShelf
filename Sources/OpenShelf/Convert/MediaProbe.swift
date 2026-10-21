import Foundation

/// Quick, synchronous facts about media files for building the wheel, which
/// can't wait for AVFoundation's asynchronous loading.
enum MediaProbe {
    /// Whether an MP4/MOV-family file has a sound track: true or false when the
    /// container could be read, nil when it isn't one or can't be parsed (callers
    /// then assume it might have audio).
    static func hasAudioTrack(_ url: URL) -> Bool? {
        guard let handle = try? FileHandle(forReadingFrom: url) else { return nil }
        defer { try? handle.close() }
        guard let moov = topLevelBox(named: "moov", in: handle) else { return nil }
        return handlerTypes(inMovieBox: moov).contains("soun")
    }

    /// Like `hasAudioTrack(_:)`, falling back to ffprobe for containers other
    /// than MP4/MOV (MKV, WebM, AVI, …). Nil when neither can tell.
    static func hasAudioTrack(_ url: URL, ffprobe: URL?) -> Bool? {
        if let answer = hasAudioTrack(url) { return answer }
        guard let ffprobe, let output = runSync(ffprobe, ["-v", "error", "-select_streams", "a", "-show_entries", "stream=index", "-of", "csv=p=0", url.path]) else {
            return nil
        }
        return !output.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
    }

    /// The duration in seconds reported by ffprobe, or nil.
    static func duration(of url: URL, ffprobe: URL?) -> Double? {
        guard let ffprobe, let output = runSync(ffprobe, ["-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", url.path]) else {
            return nil
        }
        return Double(output.trimmingCharacters(in: .whitespacesAndNewlines))
    }

    /// Runs a quick probe and returns its standard output, or nil on failure.
    /// Only for short-lived tools: it blocks until the process exits.
    static func runSync(_ executable: URL, _ arguments: [String]) -> String? {
        let process = Process()
        process.executableURL = executable
        process.arguments = arguments
        let pipe = Pipe()
        process.standardOutput = pipe
        process.standardError = FileHandle.nullDevice
        guard (try? process.run()) != nil else { return nil }
        let data = pipe.fileHandleForReading.readDataToEndOfFile()
        process.waitUntilExit()
        guard process.terminationStatus == 0 else { return nil }
        return String(decoding: data, as: UTF8.self)
    }

    /// Finds a top-level box by walking box headers, reading only the match.
    private static func topLevelBox(named name: String, in handle: FileHandle, limit: UInt64 = 64 << 20) -> Data? {
        var offset: UInt64 = 0
        while true {
            try? handle.seek(toOffset: offset)
            guard let header = try? handle.read(upToCount: 16), header.count >= 8 else { return nil }
            var size = UInt64(header.readBigEndian(at: 0, count: 4))
            let type = String(decoding: header[header.startIndex + 4 ..< header.startIndex + 8], as: UTF8.self)
            var headerLength: UInt64 = 8
            if size == 1, header.count >= 16 {
                size = header.readBigEndian(at: 8, count: 8)
                headerLength = 16
            } else if size == 0 {
                // Box runs to the end of the file.
                size = ((try? handle.seekToEnd()) ?? offset) - offset
            }
            guard size >= headerLength else { return nil }
            if type == name {
                guard size - headerLength <= limit else { return nil }
                try? handle.seek(toOffset: offset + headerLength)
                return try? handle.read(upToCount: Int(size - headerLength))
            }
            offset += size
        }
    }

    /// Handler types ("vide", "soun", …) of the tracks in a `moov` box's payload.
    static func handlerTypes(inMovieBox moov: Data) -> [String] {
        // Each track declares its media in moov › trak › mdia › hdlr; the handler
        // type sits 8 bytes after the "hdlr" tag (after version/flags and the
        // QuickTime component type).
        let bytes = [UInt8](moov)
        let tag = Array("hdlr".utf8)
        var types: [String] = []
        var index = 0
        while index + 16 <= bytes.count {
            if Array(bytes[index..<index + 4]) == tag {
                types.append(String(decoding: bytes[index + 12..<index + 16], as: UTF8.self))
                index += 16
            } else {
                index += 1
            }
        }
        return types
    }
}

/// Which media containers macOS can open itself, and which need ffmpeg.
enum MediaFormats {
    /// Video containers AVFoundation can't read.
    static let ffmpegOnlyVideoExtensions: Set<String> = ["mkv", "webm", "avi", "wmv", "flv", "asf", "ogv", "mpg", "mpeg", "m2ts", "mts", "ts", "vob", "rm", "rmvb"]
    /// Audio containers AVFoundation can't read.
    static let ffmpegOnlyAudioExtensions: Set<String> = ["ogg", "oga", "opus", "wma", "mka", "ape", "ac3", "wv", "spx"]

    /// Whether AVFoundation (and afconvert) can decode the file; otherwise
    /// every conversion of it goes through ffmpeg.
    static func isAVFoundationReadable(_ url: URL) -> Bool {
        let ext = url.resolvingSymlinksInPath().pathExtension.lowercased()
        return !ffmpegOnlyVideoExtensions.contains(ext) && !ffmpegOnlyAudioExtensions.contains(ext)
    }
}

/// The encoders an ffmpeg build offers, asked once per binary.
enum FFmpegEncoders {
    private final class Cache: @unchecked Sendable {
        let lock = NSLock()
        var encoders: [String: Set<String>] = [:]
    }
    private static let cache = Cache()

    static func available(in ffmpeg: URL) -> Set<String> {
        if let known = cache.lock.withLock({ cache.encoders[ffmpeg.path] }) { return known }
        let output = MediaProbe.runSync(ffmpeg, ["-hide_banner", "-encoders"]) ?? ""
        // Lines look like " A....D libopus   libopus Opus (codec opus)".
        let names = Set(output.split(separator: "\n").compactMap { line -> String? in
            let fields = line.split(separator: " ", omittingEmptySubsequences: true)
            guard fields.count >= 2, fields[0].count == 6 else { return nil }
            return String(fields[1])
        })
        cache.lock.withLock { cache.encoders[ffmpeg.path] = names }
        return names
    }
}

private extension Data {
    func readBigEndian(at offset: Int, count: Int) -> UInt64 {
        var value: UInt64 = 0
        for byte in self[startIndex + offset ..< startIndex + offset + count] {
            value = value << 8 | UInt64(byte)
        }
        return value
    }
}
