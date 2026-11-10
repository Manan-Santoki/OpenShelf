using OpenShelf.Convert;
using static OpenShelf.Convert.Media.Tests.Media;

namespace OpenShelf.Convert.Media.Tests;

public class TranscriptionTests
{
    private const string Whisper = @"C:\fake\whisper-cli.exe";

    private static ExternalTools Tools(string? model) => FakeTools with { Whisper = Whisper, WhisperModel = model };

    private sealed class Fake
    {
        public List<ToolInvocation> Calls { get; } = [];
        public string Text { get; set; } = "1\n00:00:00,000 --> 00:00:02,000\nHello there.\n";
        public ToolRunner Runner => (invocation, onLine, ct) =>
        {
            Calls.Add(invocation);
            ct.ThrowIfCancellationRequested();
            var args = invocation.Arguments.ToList();
            if (invocation.Executable == Whisper)
            {
                onLine?.Invoke("whisper_print_progress_callback: progress =  50%");
                var flag = args.First(a => a is "-otxt" or "-osrt" or "-ovtt");
                var ext = flag[2..];
                File.WriteAllText(args[args.IndexOf("--output-file") + 1] + "." + ext, Text);
                onLine?.Invoke("whisper_print_progress_callback: progress = 100%");
            }
            else File.WriteAllText(args[^1], "wav");
            return Task.CompletedTask;
        };
    }

    private static (TempDir Dir, string Source, string Model) Setup(string name = "talk.mp3")
    {
        var dir = new TempDir();
        var source = dir.Combine(name);
        File.WriteAllText(source, "x");
        var model = dir.Combine("ggml-base.bin");
        File.WriteAllText(model, "m");
        return (dir, source, model);
    }

    [Fact]
    public void OnlyOfferedWithWhisperAndAModel()
    {
        var engine = new TranscriptionEngine();
        IConversionEngine asked = engine;
        Assert.True(engine.CanConvert(FileKind.Audio, OutputFormat.Txt));
        Assert.True(engine.CanConvert(FileKind.Video, OutputFormat.Srt));
        Assert.False(engine.CanConvert(FileKind.Video, OutputFormat.Txt));
        Assert.False(engine.CanConvert(FileKind.Image, OutputFormat.Srt));
        Assert.True(asked.CanConvert(Request("a.mp3", OutputFormat.Vtt, tools: Tools(@"C:\m\ggml-base.bin"))));
        Assert.False(asked.CanConvert(Request("a.mp3", OutputFormat.Vtt, tools: Tools(null))));
        Assert.False(asked.CanConvert(Request("a.mp3", OutputFormat.Vtt)));
        Assert.False(asked.CanConvert(Request("a.mp3", OutputFormat.Vtt, tools: Tools(@"C:\m\ggml-base.bin") with { Ffmpeg = null })));
        // The catalog agrees: transcripts appear only with whisper and a model.
        Assert.DoesNotContain(OutputFormat.Srt, ConversionCatalog.Formats(["a.mp3"], FakeTools));
        Assert.Contains(OutputFormat.Srt, ConversionCatalog.Formats(["a.mp3"], Tools(@"C:\m\ggml-base.bin")));
    }

    [Fact]
    public void WhisperArgumentsAreAnArrayOfAbsolutePaths()
    {
        var args = TranscriptionEngine.WhisperArguments(@"C:\Models Dir\ggml-base.en.bin", @"C:\tmp dir\audio.wav", OutputFormat.Srt, @"C:\tmp dir\transcript", 4)!;
        Assert.Equal(["--model", @"C:\Models Dir\ggml-base.en.bin", "--file", @"C:\tmp dir\audio.wav", "--language", "en", "--threads", "4",
            "--print-progress", "-osrt", "--output-file", @"C:\tmp dir\transcript"], args);
        Assert.Equal("auto", TranscriptionEngine.WhisperArguments(@"C:\m\ggml-large-v3.bin", "a", OutputFormat.Txt, "b", 1)![5]);
        Assert.Contains("-otxt", TranscriptionEngine.WhisperArguments("m", "a", OutputFormat.Txt, "b", 1)!);
        Assert.Contains("-ovtt", TranscriptionEngine.WhisperArguments("m", "a", OutputFormat.Vtt, "b", 1)!);
        Assert.Null(TranscriptionEngine.WhisperArguments("m", "a", OutputFormat.Pdf, "b", 1));
        Assert.Equal(["-nostdin", "-hide_banner", "-loglevel", "error", "-y", "-i", @"C:\a b\x.mkv", "-vn", "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", @"C:\t\audio.wav"],
            TranscriptionEngine.DecodeArguments(@"C:\a b\x.mkv", @"C:\t\audio.wav"));
    }

    [Fact]
    public void ReadsProgressAndRecognisesSilence()
    {
        Assert.Equal(0.25, TranscriptionEngine.FractionFromProgressLine("whisper_print_progress_callback: progress =  25%"));
        Assert.Equal(1, TranscriptionEngine.FractionFromProgressLine("whisper_print_progress_callback: progress = 100%"));
        Assert.Null(TranscriptionEngine.FractionFromProgressLine("system_info: n_threads = 4"));
        Assert.False(TranscriptionEngine.HasSpeech(""));
        Assert.False(TranscriptionEngine.HasSpeech("WEBVTT\n\n"));
        Assert.False(TranscriptionEngine.HasSpeech("[BLANK_AUDIO]\n"));
        Assert.False(TranscriptionEngine.HasSpeech("1\n00:00:00,000 --> 00:00:02,000\n[BLANK_AUDIO]\n"));
        Assert.True(TranscriptionEngine.HasSpeech("1\n00:00:00,000 --> 00:00:02,000\nHello\n"));
    }

    [Fact]
    public async Task DecodesThenTranscribesAndWritesBesideTheSource()
    {
        var (dir, source, model) = Setup();
        using (dir)
        {
            var fake = new Fake();
            var log = new Collected();
            var result = await new TranscriptionEngine(fake.Runner).ConvertAsync(Request(source, OutputFormat.Srt, tools: Tools(model)), log, default);
            Assert.Equal([dir.Combine("talk.srt")], result);
            Assert.Equal("1\n00:00:00,000 --> 00:00:02,000\nHello there.\n", File.ReadAllText(result[0]));
            Assert.Equal([FakeFfmpegPath, Whisper], fake.Calls.Select(c => c.Executable));
            Assert.Equal("-vn", fake.Calls[0].Arguments[7]);
            Assert.Equal(model, After(fake.Calls[1].Arguments, "--model"));
            Assert.Equal(0.05, log.Values[0]);
            Assert.Equal(1, log.Values[^1]);
            Assert.True(log.Values.Zip(log.Values.Skip(1)).All(p => p.First <= p.Second));
            // The decode workspace is gone.
            Assert.False(Directory.Exists(Path.GetDirectoryName(After(fake.Calls[1].Arguments, "--file"))!));
            Assert.Equal(["ggml-base.bin", "talk.mp3", "talk.srt"], dir.Names());
        }
    }

    [Fact]
    public async Task AMissingModelFailsClearlyWithoutRunningAnything()
    {
        var (dir, source, _) = Setup();
        using (dir)
        {
            var fake = new Fake();
            var e = await Assert.ThrowsAsync<ConversionException>(() =>
                new TranscriptionEngine(fake.Runner).ConvertAsync(Request(source, OutputFormat.Txt, tools: Tools(dir.Combine("gone.bin"))), null, default));
            Assert.Contains("speech model", e.Message);
            Assert.Empty(fake.Calls);
        }
    }

    [Fact]
    public async Task SilenceIsReportedAndLeavesNothing()
    {
        var (dir, source, model) = Setup();
        using (dir)
        {
            var fake = new Fake { Text = "[BLANK_AUDIO]\n" };
            var e = await Assert.ThrowsAsync<ConversionException>(() =>
                new TranscriptionEngine(fake.Runner).ConvertAsync(Request(source, OutputFormat.Txt, tools: Tools(model)), null, default));
            Assert.Contains("No speech", e.Message);
            Assert.Equal(["ggml-base.bin", "talk.mp3"], dir.Names());
        }
    }

    [Fact]
    public async Task CancellingStopsBeforeWhisper()
    {
        var (dir, source, model) = Setup();
        using (dir)
        {
            var fake = new Fake();
            using var cancel = new CancellationTokenSource();
            ToolRunner runner = (invocation, onLine, ct) =>
            {
                var task = fake.Runner(invocation, onLine, ct);
                cancel.Cancel();
                return task;
            };
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                new TranscriptionEngine(runner).ConvertAsync(Request(source, OutputFormat.Vtt, tools: Tools(model)), null, cancel.Token));
            Assert.Single(fake.Calls);
            Assert.Equal(["ggml-base.bin", "talk.mp3"], dir.Names());
        }
    }

    [Fact]
    public async Task AFailingWhisperLeavesNoWorkspace()
    {
        var (dir, source, model) = Setup();
        using (dir)
        {
            var wavs = new List<string>();
            ToolRunner runner = (invocation, onLine, ct) =>
            {
                if (invocation.Executable == Whisper) throw new ProcessFailure("whisper-cli", "failed to load model");
                File.WriteAllText(invocation.Arguments[^1], "wav");
                wavs.Add(invocation.Arguments[^1]);
                return Task.CompletedTask;
            };
            await Assert.ThrowsAsync<ProcessFailure>(() =>
                new TranscriptionEngine(runner).ConvertAsync(Request(source, OutputFormat.Txt, tools: Tools(model)), null, default));
            Assert.False(Directory.Exists(Path.GetDirectoryName(wavs[0])!));
        }
    }
}
