import Foundation
import Testing
@testable import OpenShelf

/// Transcripts and subtitles through whisper.cpp. Everything but the last
/// test runs without it: the process runner is a fake.
struct TranscriptionTests {
    static let whisper = URL(fileURLWithPath: "/opt/homebrew/bin/whisper-cli")
    static let model = URL(fileURLWithPath: "/Models/ggml-base.en.bin")
    static let tools = ExternalTools(ffmpeg: nil, cwebp: nil, whisper: whisper, whisperModel: model)

    // MARK: Detection

    @Test func findsWhisperAndTheBestModel() throws {
        let folder = try ToolFolder("whisper")
        defer { folder.remove() }
        try folder.executable("bin/whisper-cli")
        for name in ["ggml-tiny.bin", "ggml-small.en.bin", "for-tests-ggml-large.bin", "ggml-base.bin", "notes.txt"] {
            try folder.file("Models/\(name)")
        }
        let locations = ToolLocations(
            commandDirectories: [folder.root.appendingPathComponent("bin")],
            libreOfficeApps: [],
            modelDirectories: [folder.root.appendingPathComponent("Models")]
        )
        let tools = ExternalTools.detect(in: locations, ignoring: [])
        #expect(tools.whisper?.lastPathComponent == "whisper-cli")
        // Small beats base and tiny; the test model never counts.
        #expect(tools.whisperModel?.lastPathComponent == "ggml-small.en.bin")
        #expect(tools.canTranscribe)
        #expect(ExternalTools.detect(in: locations, ignoring: ["whisper"]).whisper == nil)
    }

    @Test func findsTheOlderProgramName() throws {
        let folder = try ToolFolder("whisper-old")
        defer { folder.remove() }
        try folder.executable("whisper-cpp")
        let tools = ExternalTools.detect(in: ToolLocations(commandDirectories: [folder.root], libreOfficeApps: []), ignoring: [])
        #expect(tools.whisper?.lastPathComponent == "whisper-cpp")
    }

    @Test func missingWhisperOrModelOffersNothing() throws {
        let folder = try ToolFolder("whisper-missing")
        defer { folder.remove() }
        try folder.file("Models/ggml-base.bin")
        // A model but no program: nothing found, not even the model.
        let noProgram = ExternalTools.detect(
            in: ToolLocations(commandDirectories: [folder.root], libreOfficeApps: [], modelDirectories: [folder.root.appendingPathComponent("Models")]),
            ignoring: []
        )
        #expect(noProgram.whisper == nil)
        #expect(noProgram.whisperModel == nil)
        #expect(OptionalTool.whisper.state(in: noProgram) == .missing)

        // A program but no model: installed, yet incomplete.
        try folder.executable("bin/whisper-cli")
        let noModel = ExternalTools.detect(
            in: ToolLocations(commandDirectories: [folder.root.appendingPathComponent("bin")], libreOfficeApps: [], modelDirectories: [folder.root.appendingPathComponent("Empty")]),
            ignoring: []
        )
        #expect(noModel.whisper != nil)
        #expect(!noModel.canTranscribe)
        #expect(OptionalTool.whisper.state(in: noModel) == .incomplete(noModel.whisper!))
    }

    // MARK: The wheel

    @Test func wedgesAppearOnlyWithWhisperAndAModel() {
        let audio = URL(fileURLWithPath: "/tmp/Talk.m4a")
        let without = ConversionCatalog.formats(for: audio, tools: ExternalTools(ffmpeg: nil, cwebp: nil))
        #expect(!without.contains(.srt) && !without.contains(.vtt) && !without.contains(.txt))
        let noModel = ConversionCatalog.formats(for: audio, tools: ExternalTools(ffmpeg: nil, cwebp: nil, whisper: Self.whisper))
        #expect(!noModel.contains(.srt))
        let with = ConversionCatalog.formats(for: audio, tools: Self.tools)
        #expect(with.suffix(4) == [.txt, .srt, .vtt, .zip])
    }

    @Test func fullWheelsStayWithinTheirLimit() {
        let ffmpeg = URL(fileURLWithPath: "/opt/homebrew/bin/ffmpeg")
        var tools = Self.tools
        tools.ffmpeg = ffmpeg
        for name in ["clip.mkv", "song.ogg", "talk.mp3"] {
            let formats = ConversionCatalog.formats(for: URL(fileURLWithPath: "/tmp/\(name)"), tools: tools)
            #expect(formats.count <= ConversionCatalog.maxWedges, "\(name)")
            #expect(formats.last == .zip)
            #expect(formats.contains(.srt) && formats.contains(.vtt), "\(name)")
        }
    }

    // MARK: Arguments

    @Test func whisperArgumentsAreAnArrayOfAbsolutePaths() throws {
        let audio = URL(fileURLWithPath: "/tmp/work space/-audio é.wav")
        let base = URL(fileURLWithPath: "/tmp/work space/transcript")
        let arguments = try #require(Transcription.whisperArguments(model: Self.model, audio: audio, format: .srt, outputBase: base, threads: 4))
        #expect(arguments == [
            "--model", "/Models/ggml-base.en.bin",
            "--file", "/tmp/work space/-audio é.wav",
            "--language", "en",
            "--threads", "4",
            "--print-progress",
            "-osrt",
            "--output-file", "/tmp/work space/transcript",
        ])
        #expect(Transcription.whisperArguments(model: Self.model, audio: audio, format: .mp3, outputBase: base, threads: 4) == nil)
        #expect(Transcription.language(for: URL(fileURLWithPath: "/m/ggml-large-v3-turbo.bin")) == "auto")
    }

    @Test func decodingArgumentsKeepPathsWhole() {
        let source = URL(fileURLWithPath: "/Users/me/-My Talk ünïcode.mov")
        let audio = URL(fileURLWithPath: "/tmp/w/audio.wav")
        #expect(Transcription.afconvertArguments(source: source, audio: audio)
            == ["-f", "WAVE", "-d", "LEI16@16000", "-c", "1", "/Users/me/-My Talk ünïcode.mov", "/tmp/w/audio.wav"])
        let ffmpeg = Transcription.ffmpegArguments(source: source, audio: audio)
        #expect(FakeToolRunner.value(after: "-i", in: ffmpeg) == "/Users/me/-My Talk ünïcode.mov")
        #expect(ffmpeg.last == "/tmp/w/audio.wav")
    }

    @Test func readsProgress() {
        #expect(Transcription.fraction(fromProgressLine: "whisper_print_progress_callback: progress =  25%") == 0.25)
        #expect(Transcription.fraction(fromProgressLine: "whisper_print_progress_callback: progress = 100%") == 1)
        #expect(Transcription.fraction(fromProgressLine: "[00:00:00.000 --> 00:00:01.760]   Hello") == nil)
    }

    @Test func recognisesSilence() {
        #expect(!Transcription.hasSpeech("1\n00:00:00,000 --> 00:00:10,000\n [BLANK_AUDIO]\n", format: .srt))
        #expect(!Transcription.hasSpeech("WEBVTT\n\n", format: .vtt))
        #expect(Transcription.hasSpeech("1\n00:00:00,000 --> 00:00:01,760\n Hello.\n", format: .srt))
    }

    // MARK: Converting, with a fake whisper

    @Test func writesTheSubtitleNextToTheSourceAndCleansUp() async throws {
        let folder = try ToolFolder("transcribe")
        defer { folder.remove() }
        let source = try folder.file("-My Talk é.m4a")
        try folder.file("-My Talk é.srt", contents: "taken")
        let workspaces = Box<[URL]>([])
        let fake = FakeToolRunner { invocation in
            let arguments = invocation.arguments
            if invocation.executable.lastPathComponent == "afconvert" {
                try Data().write(to: URL(fileURLWithPath: arguments.last!))
                return []
            }
            let base = URL(fileURLWithPath: FakeToolRunner.value(after: "--output-file", in: arguments)!)
            workspaces.value.append(base.deletingLastPathComponent())
            try "1\n00:00:00,000 --> 00:00:01,000\n Hello.\n".write(to: base.appendingPathExtension("srt"), atomically: true, encoding: .utf8)
            return ["whisper_print_progress_callback: progress =  50%", "whisper_print_progress_callback: progress = 100%"]
        }
        let reported = Box<[Double]>([])
        var tools = Self.tools
        tools.whisperModel = try folder.file("ggml-base.en.bin")
        let output = try await Transcription.convert(source, to: .srt, tools: tools, runner: fake.runner) { reported.value.append($0) }

        // Never overwrites, and keeps the odd name intact.
        #expect(output.lastPathComponent == "-My Talk é 2.srt")
        #expect(try String(contentsOf: output, encoding: .utf8).contains("Hello."))
        #expect(fake.invocations.map(\.executable.lastPathComponent) == ["afconvert", "whisper-cli"])
        #expect(fake.invocations[0].arguments.contains(source.path))
        #expect(reported.value.last == 1)
        #expect(reported.value == reported.value.sorted())
        // The decoded sound and whisper's files went with the workspace.
        #expect(!workspaces.value.isEmpty)
        #expect(workspaces.value.allSatisfy { !FileManager.default.fileExists(atPath: $0.path) })
    }

    @Test func aMissingModelFailsClearlyWithoutRunningAnything() async throws {
        let fake = FakeToolRunner()
        var tools = Self.tools
        tools.whisperModel = URL(fileURLWithPath: "/nonexistent/ggml-base.bin")
        await #expect(throws: ConversionError.self) {
            _ = try await Transcription.convert(URL(fileURLWithPath: "/tmp/a.m4a"), to: .txt, tools: tools, runner: fake.runner) { _ in }
        }
        #expect(fake.invocations.isEmpty)
    }

    @Test func silenceIsReportedAndLeavesNothing() async throws {
        let folder = try ToolFolder("transcribe-silence")
        defer { folder.remove() }
        let source = try folder.file("quiet.wav")
        let fake = FakeToolRunner { invocation in
            if let base = FakeToolRunner.value(after: "--output-file", in: invocation.arguments) {
                try " [BLANK_AUDIO]\n".write(to: URL(fileURLWithPath: base + ".txt"), atomically: true, encoding: .utf8)
            } else {
                try Data().write(to: URL(fileURLWithPath: invocation.arguments.last!))
            }
            return []
        }
        var tools = Self.tools
        tools.whisperModel = try folder.file("ggml-base.en.bin")
        await #expect(throws: ConversionError.self) {
            _ = try await Transcription.convert(source, to: .txt, tools: tools, runner: fake.runner) { _ in }
        }
        #expect(try FileManager.default.contentsOfDirectory(atPath: folder.root.path).sorted() == ["ggml-base.en.bin", "quiet.wav"])
    }

    @Test func unreadableAudioNeedsFFmpeg() async throws {
        let folder = try ToolFolder("transcribe-mkv")
        defer { folder.remove() }
        let source = try folder.file("clip.mkv")
        var tools = Self.tools
        tools.whisperModel = try folder.file("ggml-base.en.bin")
        let fake = FakeToolRunner()
        await #expect(throws: MissingToolError(name: "ffmpeg")) {
            _ = try await Transcription.convert(source, to: .srt, tools: tools, runner: fake.runner) { _ in }
        }
        #expect(fake.invocations.isEmpty)
    }

    @Test func cancellingStopsBeforeWhisper() async throws {
        let folder = try ToolFolder("transcribe-cancel")
        defer { folder.remove() }
        let source = try folder.file("talk.m4a")
        var tools = Self.tools
        tools.whisperModel = try folder.file("ggml-base.en.bin")
        let fake = FakeToolRunner { invocation in
            try Data().write(to: URL(fileURLWithPath: invocation.arguments.last!))
            throw CancellationError()
        }
        await #expect(throws: CancellationError.self) {
            _ = try await Transcription.convert(source, to: .srt, tools: tools, runner: fake.runner) { _ in }
        }
        #expect(fake.invocations.count == 1)
        #expect(try FileManager.default.contentsOfDirectory(atPath: folder.root.path).sorted() == ["ggml-base.en.bin", "talk.m4a"])
    }

    // MARK: The real thing, where whisper.cpp and a model are installed

    static var realTools: ExternalTools { ExternalTools.detect(ignoring: []) }

    @Test(.enabled(if: RealToolTests.enabled && realTools.canTranscribe), .timeLimit(.minutes(5)))
    func transcribesSpeechOnThisMac() async throws {
        let folder = try ToolFolder("transcribe-real")
        defer { folder.remove() }
        let speech = folder.root.appendingPathComponent("-Spoken é.aiff")
        // `say` can stall when the speech service is busy; never let it hold up the run.
        try await withThrowingTaskGroup(of: Void.self) { group in
            group.addTask {
                try await ProcessRunner.run(URL(fileURLWithPath: "/usr/bin/say"), ["-o", speech.path, "Hello from OpenShelf. This is a test of offline transcription."])
            }
            group.addTask {
                try await Task.sleep(for: .seconds(60))
                throw ConversionError.failed("say didn’t finish making the test speech within a minute")
            }
            defer { group.cancelAll() }
            try await group.next()
        }

        for format in Transcription.audioFormats {
            let output = try await Converter.convert(speech, to: format, tools: Self.realTools) { _ in }
            #expect(output.count == 1)
            #expect(output[0].pathExtension == format.rawValue)
            let text = try String(contentsOf: output[0], encoding: .utf8).lowercased()
            #expect(text.contains("hello"), "\(format): \(text)")
            #expect(text.contains("transcription"), "\(format): \(text)")
        }
    }
}

/// A mutable value tests' Sendable closures can write to.
final class Box<Value>: @unchecked Sendable {
    private let lock = NSLock()
    private var stored: Value
    init(_ value: Value) { stored = value }
    var value: Value {
        get { lock.withLock { stored } }
        set { lock.withLock { stored = newValue } }
    }
}
