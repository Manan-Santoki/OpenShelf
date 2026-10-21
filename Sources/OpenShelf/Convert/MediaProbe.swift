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

private extension Data {
    func readBigEndian(at offset: Int, count: Int) -> UInt64 {
        var value: UInt64 = 0
        for byte in self[startIndex + offset ..< startIndex + offset + count] {
            value = value << 8 | UInt64(byte)
        }
        return value
    }
}
