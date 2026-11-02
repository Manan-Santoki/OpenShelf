import Foundation

/// Transcripts and subtitles of speech in audio and video, made on this Mac by
/// whisper.cpp (https://github.com/ggml-org/whisper.cpp) when it's installed:
/// `brew install whisper-cpp`, plus a speech model in
/// `ManagedTools.modelsDirectory`. Nothing leaves the Mac.
///
/// The sound is first decoded to the 16 kHz mono WAV whisper.cpp reads best,
/// with the built-in `afconvert` where it can and ffmpeg otherwise; then
/// whisper.cpp writes the one format asked for.
enum Transcription {
    /// What audio can become: a plain transcript, or subtitles.
    static let audioFormats: [OutputFormat] = [.txt, .srt, .vtt]
    /// What video can become. Video wheels are full, and a plain transcript is
    /// a conversion away on the subtitle's own wheel.
    static let videoFormats: [OutputFormat] = [.srt, .vtt]

    static func handles(_ format: OutputFormat) -> Bool {
        audioFormats.contains(format)
    }

    /// The model to use from those in `directories`: the most capable that
    /// people commonly run, skipping whisper.cpp's tiny test models.
    static func preferredModel(in directories: [URL], fileManager: FileManager = .default) -> URL? {
        let models = directories.flatMap { directory in
            ((try? fileManager.contentsOfDirectory(at: directory, includingPropertiesForKeys: nil)) ?? [])
                .filter(isModel)
        }
        return models.min { rank($0) == rank($1) ? $0.lastPathComponent < $1.lastPathComponent : rank($0) < rank($1) }
    }

    /// whisper.cpp's model files are named "ggml-<size>[.en][-q5_0].bin".
    static func isModel(_ url: URL) -> Bool {
        let name = url.lastPathComponent.lowercased()
        return name.hasPrefix("ggml-") && name.hasSuffix(".bin") && !name.contains("for-tests")
            && FileManager.default.isReadableFile(atPath: url.path)
    }

    /// Lower is preferred: turbo is near large-v3's accuracy at a fraction of
    /// its time; then by size, largest first.
    private static func rank(_ model: URL) -> Int {
        let name = model.lastPathComponent.lowercased()
        let order = ["large-v3-turbo", "large", "medium", "small", "base", "tiny"]
        return order.firstIndex { name.contains($0) } ?? order.count
    }

    /// English-only models ("ggml-base.en.bin") can't detect a language.
    static func language(for model: URL) -> String {
        model.lastPathComponent.lowercased().contains(".en") ? "en" : "auto"
    }

    /// whisper.cpp's flag for writing `format`.
    static func outputFlag(for format: OutputFormat) -> String? {
        switch format {
        case .txt: "-otxt"
        case .srt: "-osrt"
        case .vtt: "-ovtt"
        default: nil
        }
    }

    /// The command line that turns `audio` (a 16 kHz WAV) into
    /// `outputBase` + "." + the format's extension. Every path is absolute.
    static func whisperArguments(model: URL, audio: URL, format: OutputFormat, outputBase: URL, threads: Int) -> [String]? {
        guard let flag = outputFlag(for: format) else { return nil }
        return [
            "--model", model.path,
            "--file", audio.path,
            "--language", language(for: model),
            "--threads", String(threads),
            "--print-progress",
            flag,
            "--output-file", outputBase.path,
        ]
    }

    /// Decodes `source` to 16 kHz mono 16-bit WAV with the built-in afconvert.
    static func afconvertArguments(source: URL, audio: URL) -> [String] {
        ["-f", "WAVE", "-d", "LEI16@16000", "-c", "1", source.path, audio.path]
    }

    /// The same with ffmpeg, for what Apple's frameworks can't read.
    static func ffmpegArguments(source: URL, audio: URL) -> [String] {
        ["-nostdin", "-hide_banner", "-loglevel", "error", "-y", "-i", source.path, "-vn", "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", audio.path]
    }

    /// Uses most cores, leaving some for the rest of the Mac.
    static var threadCount: Int {
        max(1, min(8, ProcessInfo.processInfo.activeProcessorCount - 2))
    }

    /// whisper.cpp prints "whisper_print_progress_callback: progress =  25%".
    static func fraction(fromProgressLine line: String) -> Double? {
        guard let range = line.range(of: "progress =") else { return nil }
        let digits = line[range.upperBound...].trimmingCharacters(in: .whitespaces).prefix { $0.isNumber }
        guard let percent = Double(digits) else { return nil }
        return min(max(percent / 100, 0), 1)
    }

    private static let afconvert = URL(fileURLWithPath: "/usr/bin/afconvert")

    /// Decoding the sound is quick next to recognising it.
    private static let decodeShare = 0.05

    static func convert(
        _ source: URL,
        to format: OutputFormat,
        tools: ExternalTools,
        runner: ToolRunner = ProcessRunner.live,
        progress: @escaping @Sendable (Double) -> Void
    ) async throws -> URL {
        guard let whisper = tools.whisper else { throw MissingToolError(name: "whisper.cpp") }
        guard let model = tools.whisperModel, FileManager.default.isReadableFile(atPath: model.path) else {
            ExternalTools.refresh()
            throw ConversionError.failed("No speech model for whisper.cpp. Put one in Settings → Extra Formats → Open Models Folder.")
        }
        guard let pathExtension = format.fileExtension, handles(format) else {
            throw ConversionError.unsupported("\(source.pathExtension.uppercased()) to \(format.title)")
        }

        let workspace = try PackageZip.makeWorkspace("transcribe")
        defer { try? FileManager.default.removeItem(at: workspace) }
        let audio = workspace.appendingPathComponent("audio.wav")
        try await decode(source, to: audio, tools: tools, runner: runner)
        progress(decodeShare)

        try Task.checkCancellation()
        let outputBase = workspace.appendingPathComponent("transcript")
        guard let arguments = whisperArguments(model: model, audio: audio, format: format, outputBase: outputBase, threads: threadCount) else {
            throw ConversionError.unsupported("\(source.pathExtension.uppercased()) to \(format.title)")
        }
        let gate = ProgressGate(progress)
        defer { gate.close() }
        try await runner(ToolInvocation(executable: whisper, arguments: arguments, currentDirectory: workspace)) { line in
            if let fraction = fraction(fromProgressLine: line) {
                gate.report(decodeShare + (1 - decodeShare) * fraction)
            }
        }

        let output = outputBase.appendingPathExtension(pathExtension)
        guard let text = try? String(contentsOf: output, encoding: .utf8) else {
            throw ConversionError.failed("whisper.cpp couldn’t transcribe “\(source.lastPathComponent)”.")
        }
        guard hasSpeech(text, format: format) else {
            throw ConversionError.failed("No speech was found in “\(source.lastPathComponent)”.")
        }
        let destination = OutputNaming.destination(for: source, pathExtension: pathExtension)
        try FileManager.default.moveItem(at: output, to: destination)
        return destination
    }

    /// Whether whisper.cpp heard anything: it writes "[BLANK_AUDIO]" (or
    /// nothing, or a WebVTT header alone) for silence.
    static func hasSpeech(_ text: String, format: OutputFormat) -> Bool {
        text.split(whereSeparator: \.isNewline).contains { line in
            let line = line.trimmingCharacters(in: .whitespaces)
            if line.isEmpty || line == "WEBVTT" || line.contains("-->") || line.allSatisfy(\.isNumber) { return false }
            return line != "[BLANK_AUDIO]"
        }
    }

    /// The sound of `source` as 16 kHz mono WAV at `audio`: afconvert for
    /// what Apple reads (including the sound of MP4 and MOV videos), ffmpeg
    /// for the rest and for files afconvert turns down.
    private static func decode(_ source: URL, to audio: URL, tools: ExternalTools, runner: ToolRunner) async throws {
        if MediaFormats.isAVFoundationReadable(source) {
            do {
                try await runner(ToolInvocation(executable: afconvert, arguments: afconvertArguments(source: source, audio: audio))) { _ in }
                return
            } catch is CancellationError {
                throw CancellationError()
            } catch {
                guard tools.ffmpeg != nil else { throw error }
                try? FileManager.default.removeItem(at: audio)
            }
        }
        guard let ffmpeg = tools.ffmpeg else { throw MissingToolError(name: "ffmpeg") }
        try await runner(ToolInvocation(executable: ffmpeg, arguments: ffmpegArguments(source: source, audio: audio))) { _ in }
    }
}
