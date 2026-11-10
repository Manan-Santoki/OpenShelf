using System.Globalization;

namespace OpenShelf.Convert;

/// <summary>Transcripts and subtitles of speech in audio and video, made on this PC by whisper.cpp (whisper-cli) when it and a
/// speech model are installed (<see cref="ExternalTools.CanTranscribe"/>). Port of Transcription.swift: ffmpeg decodes the
/// sound to the 16 kHz mono WAV whisper.cpp reads best, then whisper.cpp writes the one format asked for. Without whisper.cpp
/// or a model <see cref="CanConvert(ConversionRequest)"/> is false, so the catalog (which asks the same tools) hides the formats.
/// Nothing leaves the PC.</summary>
public sealed class TranscriptionEngine : IConversionEngine
{
    /// <summary>What audio can become: a plain transcript, or subtitles.</summary>
    public static readonly IReadOnlyList<OutputFormat> AudioFormats = [OutputFormat.Txt, OutputFormat.Srt, OutputFormat.Vtt];
    /// <summary>What video can become. A plain transcript is a conversion away on the subtitle's own wheel.</summary>
    public static readonly IReadOnlyList<OutputFormat> VideoFormats = [OutputFormat.Srt, OutputFormat.Vtt];

    /// <summary>Decoding the sound is quick next to recognising it.</summary>
    private const double DecodeShare = 0.05;

    private readonly ToolRunner _runner;

    public TranscriptionEngine(ToolRunner? runner = null) => _runner = runner ?? ToolRunners.Default;

    public bool CanConvert(FileKind kind, OutputFormat format) => kind switch
    {
        FileKind.Audio => AudioFormats.Contains(format),
        FileKind.Video => VideoFormats.Contains(format),
        _ => false,
    };

    public bool CanConvert(ConversionRequest request)
    {
        var tools = request.ToolsOrDefault;
        return CanConvert(FileKinds.Of(request.SourcePath), request.Format) && tools.CanTranscribe && tools.Ffmpeg is not null;
    }

    /// <summary>whisper.cpp's flag for writing <paramref name="format"/>.</summary>
    public static string? OutputFlag(OutputFormat format) => format switch
    {
        OutputFormat.Txt => "-otxt",
        OutputFormat.Srt => "-osrt",
        OutputFormat.Vtt => "-ovtt",
        _ => null,
    };

    /// <summary>English-only models ("ggml-base.en.bin") cannot detect a language.</summary>
    public static string Language(string model) => Path.GetFileName(model).Contains(".en", StringComparison.OrdinalIgnoreCase) ? "en" : "auto";

    /// <summary>The command line that turns <paramref name="audio"/> (a 16 kHz WAV) into <paramref name="outputBase"/> plus the
    /// format's extension. Every path is absolute.</summary>
    public static IReadOnlyList<string>? WhisperArguments(string model, string audio, OutputFormat format, string outputBase, int threads) =>
        OutputFlag(format) is not { } flag ? null :
        [
            "--model", model,
            "--file", audio,
            "--language", Language(model),
            "--threads", threads.ToString(CultureInfo.InvariantCulture),
            "--print-progress",
            flag,
            "--output-file", outputBase,
        ];

    /// <summary>ffmpeg's decode to 16 kHz mono 16-bit WAV.</summary>
    public static IReadOnlyList<string> DecodeArguments(string source, string audio) =>
        ["-nostdin", "-hide_banner", "-loglevel", "error", "-y", "-i", source, "-vn", "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", audio];

    /// <summary>Uses most cores, leaving some for the rest of the PC.</summary>
    public static int ThreadCount => Math.Max(1, Math.Min(8, Environment.ProcessorCount - 2));

    /// <summary>whisper.cpp prints "whisper_print_progress_callback: progress =  25%".</summary>
    public static double? FractionFromProgressLine(string line)
    {
        const string marker = "progress =";
        var at = line.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0) return null;
        var rest = line[(at + marker.Length)..].Trim();
        var digits = new string(rest.TakeWhile(char.IsAsciiDigit).ToArray());
        return double.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var percent) ? Math.Clamp(percent / 100, 0, 1) : null;
    }

    /// <summary>Whether whisper.cpp heard anything: it writes "[BLANK_AUDIO]" (or nothing, or a WebVTT header alone) for silence.</summary>
    public static bool HasSpeech(string text) =>
        text.Split(['\r', '\n']).Select(line => line.Trim()).Any(line =>
            line.Length > 0 && line != "WEBVTT" && !line.Contains("-->") && !line.All(char.IsAsciiDigit) && line != "[BLANK_AUDIO]");

    public async Task<IReadOnlyList<string>> ConvertAsync(ConversionRequest request, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var tools = request.ToolsOrDefault;
        var whisper = tools.Whisper ?? throw new MissingToolException("whisper.cpp");
        var ffmpeg = tools.Ffmpeg ?? throw new MissingToolException("ffmpeg");
        if (tools.WhisperModel is not { } model || !File.Exists(model))
        {
            ExternalToolsProvider.Default.Refresh();
            throw ConversionException.Failed("No speech model for whisper.cpp. Put one in Settings → Extra Formats → Open Models Folder.");
        }
        if (request.Format.FileExtension() is not { } extension || OutputFlag(request.Format) is null)
            throw ConversionException.Unsupported($"{FileKinds.ExtensionOf(request.SourcePath).ToUpperInvariant()} to {request.Format.Title()}");

        var source = Path.GetFullPath(FileKinds.ResolveLink(request.SourcePath));
        var workspace = Path.Combine(Path.GetTempPath(), "openshelf-transcribe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            var audio = Path.Combine(workspace, "audio.wav");
            await _runner(new ToolInvocation(ffmpeg, DecodeArguments(source, audio)) { WorkingDirectory = workspace, Timeout = TimeSpan.FromMinutes(30) }, null, cancellationToken).ConfigureAwait(false);
            progress?.Report(DecodeShare);

            cancellationToken.ThrowIfCancellationRequested();
            var outputBase = Path.Combine(workspace, "transcript");
            var arguments = WhisperArguments(model, audio, request.Format, outputBase, ThreadCount)!;
            var gate = new ProgressGate(progress);
            try
            {
                await _runner(new ToolInvocation(whisper, arguments) { WorkingDirectory = workspace, Timeout = TimeSpan.FromHours(6) }, line =>
                {
                    if (FractionFromProgressLine(line) is { } fraction) gate.Report(DecodeShare + (1 - DecodeShare) * fraction);
                }, cancellationToken).ConfigureAwait(false);
            }
            finally { gate.Close(); }

            var written = outputBase + "." + extension;
            var text = File.Exists(written) ? File.ReadAllText(written) : throw ConversionException.Failed($"whisper.cpp couldn’t transcribe “{Path.GetFileName(source)}”.");
            if (!HasSpeech(text)) throw ConversionException.Failed($"No speech was found in “{Path.GetFileName(source)}”.");

            var destination = request.OutputPath($"{request.SourceStem}.{extension}");
            File.Move(written, destination);
            progress?.Report(1);
            return [destination];
        }
        finally
        {
            try { Directory.Delete(workspace, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }
}
