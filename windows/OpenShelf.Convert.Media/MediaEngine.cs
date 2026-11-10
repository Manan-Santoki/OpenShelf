namespace OpenShelf.Convert;

/// <summary>Audio and video conversions, all through ffmpeg (there is no AVFoundation here). Port of MediaConversion.swift's
/// ffmpeg path: the SPEC 2.3.1 ffmpeg column for every format, a stream copy first where the container allows, HEVC through
/// x265 or the hardware encoders, and progress read from ffmpeg against the probed duration.</summary>
public sealed class MediaEngine : IConversionEngine
{
    /// <summary>Audio-only outputs, from any audio or video source.</summary>
    public static readonly IReadOnlySet<OutputFormat> AudioOutputs = new HashSet<OutputFormat>
        { OutputFormat.Mp3, OutputFormat.M4a, OutputFormat.Wav, OutputFormat.Aiff, OutputFormat.Flac, OutputFormat.Ogg, OutputFormat.Opus, OutputFormat.Wma };

    /// <summary>Outputs with a picture, from video.</summary>
    public static readonly IReadOnlySet<OutputFormat> VideoOutputs = new HashSet<OutputFormat>
        { OutputFormat.Mp4, OutputFormat.Mov, OutputFormat.Mkv, OutputFormat.Webm, OutputFormat.Avi, OutputFormat.Wmv, OutputFormat.Gif };

    private readonly ToolRunner _runner;
    private readonly MediaProbe _probe;
    private readonly FFmpegEncoders _encoders;

    public MediaEngine(ToolRunner? runner = null, MediaProbe? probe = null, FFmpegEncoders? encoders = null)
    {
        _runner = runner ?? ToolRunners.Default;
        _probe = probe ?? MediaProbe.Default;
        _encoders = encoders ?? FFmpegEncoders.Default;
    }

    public bool CanConvert(FileKind kind, OutputFormat format) => kind switch
    {
        FileKind.Audio => AudioOutputs.Contains(format),
        FileKind.Video => AudioOutputs.Contains(format) || VideoOutputs.Contains(format),
        _ => false,
    };

    /// <summary>Without ffmpeg there is nothing to offer.</summary>
    public bool CanConvert(ConversionRequest request) =>
        CanConvert(FileKinds.Of(request.SourcePath), request.Format) && request.ToolsOrDefault.Ffmpeg is not null;

    public async Task<IReadOnlyList<string>> ConvertAsync(ConversionRequest request, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var tools = request.ToolsOrDefault;
        var ffmpeg = tools.Ffmpeg ?? throw new MissingToolException("ffmpeg");
        var extension = request.Format.FileExtension() ?? throw ConversionException.Unsupported($"media to {request.Format.Title()}");
        var source = Path.GetFullPath(FileKinds.ResolveLink(request.SourcePath));
        var name = Path.GetFileName(source);
        cancellationToken.ThrowIfCancellationRequested();

        var probed = await _probe.LoadAsync(source, tools.Ffprobe, cancellationToken).ConfigureAwait(false);
        // ffprobe is there but cannot read it, so ffmpeg will not either.
        if (probed is null && tools.Ffprobe is not null) throw ConversionException.Unreadable(source);
        if (probed is not null)
        {
            if (request.Format.IsAudioOnly() && !probed.HasAudio) throw ConversionException.Failed($"“{name}” has no audio track.");
            if (!request.Format.IsAudioOnly() && probed.Video is null) throw ConversionException.Failed($"“{name}” has no video to make {request.Format.Title()} from.");
        }

        var encoders = await _encoders.LoadAsync(ffmpeg, cancellationToken).ConfigureAwait(false);
        RequireEncoders(request.Format, encoders);

        var attempts = FFmpegArguments.Attempts(request.Format, encoders, probed, request.Options);
        var destination = request.OutputPath($"{request.SourceStem}.{extension}");
        try
        {
            await FFmpegJob.RunAttemptsAsync(_runner, ffmpeg, source, attempts, destination, probed?.Duration ?? 0, progress, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            FFmpegJob.Discard(destination);
            throw;
        }
        return [destination];
    }

    /// <summary>The encoder each format cannot do without; an ffmpeg that lacks it says so instead of failing cryptically.
    /// Skipped when the encoder list is unknown, and for MP4, MOV and MKV, which may be stream copies.</summary>
    public static void RequireEncoders(OutputFormat format, IReadOnlySet<string> encoders)
    {
        if (encoders.Count == 0) return;
        string[] needed = format switch
        {
            OutputFormat.Mp3 => ["libmp3lame"],
            OutputFormat.Opus => ["libopus"],
            OutputFormat.Wma => ["wmav2"],
            OutputFormat.M4a => ["aac"],
            OutputFormat.Webm => ["libvpx-vp9", "libopus"],
            OutputFormat.Avi => ["mpeg4", "libmp3lame"],
            OutputFormat.Wmv => ["wmv2", "wmav2"],
            _ => [],
        };
        var missing = needed.FirstOrDefault(encoder => !encoders.Contains(encoder));
        if (missing is not null)
            throw ConversionException.Failed($"This copy of ffmpeg can’t make {format.Title()} files (it has no {missing} encoder). A full ffmpeg build can.");
    }
}

/// <summary>Registers the audio, video and transcription engines.</summary>
public static class MediaEngines
{
    /// <summary>Adds the ffmpeg media engine and the whisper transcription engine to <paramref name="registry"/>.</summary>
    public static void Register(EngineRegistry registry)
    {
        registry.Register(new MediaEngine());
        registry.Register(new TranscriptionEngine());
        // Ask ffmpeg for its encoder list in the background so the first conversion does not wait for it.
        FFmpegEncoders.Default.Warm(ExternalToolsProvider.Default.Current.Ffmpeg);
    }
}
