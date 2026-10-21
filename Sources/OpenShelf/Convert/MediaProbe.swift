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
    /// Video containers AVFoundation can't read. (Recent macOS opens some
    /// MPEG-TS files, but not reliably across the versions OpenShelf runs on.)
    static let ffmpegOnlyVideoExtensions: Set<String> = ["mkv", "webm", "avi", "wmv", "flv", "asf", "ogv", "mpg", "mpeg", "m2ts", "mts", "ts", "vob", "rm", "rmvb"]
    /// Audio containers AVFoundation can't read.
    static let ffmpegOnlyAudioExtensions: Set<String> = ["ogg", "oga", "opus", "wma", "mka", "ape", "ac3", "wv", "spx"]

    /// Whether AVFoundation (and afconvert) can decode the file; otherwise
    /// every conversion of it goes through ffmpeg.
    static func isAVFoundationReadable(_ url: URL) -> Bool {
        let ext = url.resolvingSymlinksInPath().pathExtension.lowercased()
        return !ffmpegOnlyVideoExtensions.contains(ext) && !ffmpegOnlyAudioExtensions.contains(ext)
    }

    /// Whether `url` starts like an MPEG transport stream: 188-byte packets,
    /// each opening with the sync byte 0x47. ".ts" is also TypeScript, so the
    /// name alone doesn't say. Nil when the file can't be read.
    static func isTransportStream(_ url: URL) -> Bool? {
        guard let handle = try? FileHandle(forReadingFrom: url) else { return nil }
        defer { try? handle.close() }
        guard let header = try? handle.read(upToCount: 189) else { return nil }
        return isTransportStream(header: header)
    }

    static func isTransportStream(header: Data) -> Bool {
        let bytes = [UInt8](header)
        guard bytes.first == 0x47 else { return false }
        // A second packet, when the file is long enough to have one.
        return bytes.count < 189 || bytes[188] == 0x47
    }
}

/// The encoders an ffmpeg build offers, asked once per binary.
///
/// Asking means running `ffmpeg -encoders`, which the wheel can't wait for, so
/// `warm(_:)` asks in the background as soon as ffmpeg is detected (at launch,
/// and whenever detection finds a different binary), and the wheel only reads
/// what's already `known(in:)`.
enum FFmpegEncoders {
    private final class Cache: @unchecked Sendable {
        let lock = NSLock()
        var encoders: [String: Set<String>] = [:]
        var asking: Set<String> = []
    }
    private static let cache = Cache()

    /// The encoders `ffmpeg` offers, running it the first time. Blocks; use
    /// `load(_:)` from async code that mustn't hold up a thread.
    static func available(in ffmpeg: URL) -> Set<String> {
        let key = identity(of: ffmpeg)
        if let known = cache.lock.withLock({ cache.encoders[key] }) { return known }
        let names = parse(MediaProbe.runSync(ffmpeg, ["-hide_banner", "-encoders"]) ?? "")
        cache.lock.withLock {
            cache.encoders[key] = names
            cache.asking.remove(key)
        }
        return names
    }

    /// `available(in:)` on a background thread.
    static func load(_ ffmpeg: URL) async -> Set<String> {
        await Task.detached(priority: .userInitiated) { available(in: ffmpeg) }.value
    }

    /// What `ffmpeg` is known to offer, or nil if it hasn't been asked yet.
    /// Never starts a process, so it's safe while building a wheel.
    static func known(in ffmpeg: URL) -> Set<String>? {
        let key = identity(of: ffmpeg)
        return cache.lock.withLock { cache.encoders[key] }
    }

    /// Starts asking `ffmpeg` in the background unless the answer is known or
    /// already on its way.
    static func warm(_ ffmpeg: URL?) {
        guard let ffmpeg else { return }
        let key = identity(of: ffmpeg)
        let isNeeded: Bool = cache.lock.withLock {
            guard cache.encoders[key] == nil, !cache.asking.contains(key) else { return false }
            cache.asking.insert(key)
            return true
        }
        guard isNeeded else { return }
        Task.detached(priority: .utility) { _ = available(in: ffmpeg) }
    }

    /// Stores what `ffmpeg` offers without running it: for tests.
    static func record(_ encoders: Set<String>, for ffmpeg: URL) {
        let key = identity(of: ffmpeg)
        cache.lock.withLock { cache.encoders[key] = encoders }
    }

    /// Encoder names from `ffmpeg -encoders`, whose lines look like
    /// " A....D libopus   libopus Opus (codec opus)", after a legend
    /// (" V..... = Video").
    static func parse(_ output: String) -> Set<String> {
        Set(output.split(separator: "\n").compactMap { line -> String? in
            let fields = line.split(separator: " ", omittingEmptySubsequences: true)
            guard fields.count >= 2, fields[0].count == 6, fields[1] != "=" else { return nil }
            return String(fields[1])
        })
    }

    /// The binary behind `ffmpeg` and when it was written, so `brew upgrade`
    /// (a new Cellar folder behind the same link) or a reinstall in place
    /// counts as a different ffmpeg.
    private static func identity(of ffmpeg: URL) -> String {
        let resolved = ffmpeg.resolvingSymlinksInPath().path
        let modified = (try? FileManager.default.attributesOfItem(atPath: resolved)[.modificationDate] as? Date)?.timeIntervalSince1970 ?? 0
        return "\(resolved)@\(modified)"
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
