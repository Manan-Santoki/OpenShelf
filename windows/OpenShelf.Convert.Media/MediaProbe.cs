using System.Collections.Concurrent;

namespace OpenShelf.Convert;

/// <summary>Runs a short tool (ffprobe, <c>ffmpeg -encoders</c>) and returns what it printed on standard output.
/// Tests pass a fake that returns canned JSON.</summary>
public delegate Task<string> OutputRunner(ToolInvocation invocation, CancellationToken cancellationToken);

public static class OutputRunners
{
    public static OutputRunner Default { get; } = async (invocation, ct) =>
    {
        var result = await ProcessRunner.RunAsync(invocation.Executable, invocation.Arguments,
            new ProcessOptions { Timeout = invocation.Timeout ?? TimeSpan.FromSeconds(30), MaxCapturedLines = 20000, MaxCapturedCharacters = 4 * 1024 * 1024 }, ct)
            .ConfigureAwait(false);
        return result.Output;
    };
}

/// <summary>Facts about media files from ffprobe's JSON, cached per file, modification time and size. Port of MediaProbe.swift
/// and ProbedMedia.load. The catalog's silent-video check is <see cref="HasAudioTrack(string)"/>.</summary>
public sealed class MediaProbe
{
    private readonly OutputRunner _run;
    private readonly ConcurrentDictionary<string, Lazy<ProbedMedia?>> _cache = new();

    public MediaProbe(OutputRunner? run = null) => _run = run ?? OutputRunners.Default;

    public static MediaProbe Default { get; } = new();

    /// <summary>The entries asked of ffprobe: enough for every decision the engine makes.</summary>
    public static IReadOnlyList<string> Arguments(string path) =>
    [
        "-v", "error",
        "-show_entries",
        "format=duration,bit_rate:stream=codec_type,codec_name,width,height,pix_fmt,color_transfer,color_primaries,avg_frame_rate,r_frame_rate,bit_rate,channels,sample_rate,duration:stream_disposition=attached_pic:stream_tags=rotate:stream_side_data=rotation",
        "-of", "json",
        path,
    ];

    /// <summary>The streams of <paramref name="path"/>; null when ffprobe is missing, fails or finds no audio or video. Blocks
    /// (a probe takes tens of milliseconds); the answer is cached until the file changes.</summary>
    public ProbedMedia? Load(string path, string? ffprobe)
    {
        if (ffprobe is null) return null;
        var full = Path.GetFullPath(FileKinds.ResolveLink(path));
        var lazy = _cache.GetOrAdd(Key(full), _ => new Lazy<ProbedMedia?>(() => Run(full, ffprobe, CancellationToken.None)));
        var result = lazy.Value;
        // A failed probe may be a tool that went missing; do not remember that forever.
        if (result is null) _cache.TryRemove(new KeyValuePair<string, Lazy<ProbedMedia?>>(Key(full), lazy));
        return result;
    }

    /// <summary>Like <see cref="Load"/> without blocking the caller's thread, and cancellable.</summary>
    public async Task<ProbedMedia?> LoadAsync(string path, string? ffprobe, CancellationToken cancellationToken = default)
    {
        if (ffprobe is null) return null;
        var full = Path.GetFullPath(FileKinds.ResolveLink(path));
        if (_cache.TryGetValue(Key(full), out var known) && known.IsValueCreated && known.Value is { } cached) return cached;
        var probed = await RunAsync(full, ffprobe, cancellationToken).ConfigureAwait(false);
        if (probed is not null)
        {
            var lazy = new Lazy<ProbedMedia?>(() => probed);
            _ = lazy.Value;
            _cache[Key(full)] = lazy;
        }
        return probed;
    }

    /// <summary>Whether <paramref name="path"/> has a sound track: null when that cannot be told (no ffprobe, unreadable file),
    /// which the catalog treats as "maybe". Suitable as <c>ConversionCatalog.Formats</c>'s hasAudioTrack callback.</summary>
    public bool? HasAudioTrack(string path, string? ffprobe) => Load(path, ffprobe) is { } probed ? probed.HasAudio : null;

    /// <summary>The catalog callback: <c>ConversionCatalog.Formats(paths, tools, MediaProbe.HasAudioTrack)</c>. Uses the default
    /// probe and whichever ffprobe <see cref="ExternalToolsProvider.Default"/> currently finds.</summary>
    public static bool? HasAudioTrack(string path) => Default.HasAudioTrack(path, ExternalToolsProvider.Default.Current.Ffprobe);

    /// <summary>The duration in seconds, or null.</summary>
    public double? Duration(string path, string? ffprobe) => Load(path, ffprobe) is { Duration: > 0 } probed ? probed.Duration : null;

    private ProbedMedia? Run(string path, string ffprobe, CancellationToken ct)
    {
        // Off the caller's context, so a UI thread calling this synchronously cannot deadlock.
        return Task.Run(() => RunAsync(path, ffprobe, ct), ct).GetAwaiter().GetResult();
    }

    private async Task<ProbedMedia?> RunAsync(string path, string ffprobe, CancellationToken ct)
    {
        try
        {
            var json = await _run(new ToolInvocation(ffprobe, Arguments(path)), ct).ConfigureAwait(false);
            return ProbedMedia.Parse(json);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) when (e is ProcessFailure or MissingToolException or IOException) { return null; }
    }

    private static string Key(string full)
    {
        try
        {
            var info = new FileInfo(full);
            return info.Exists ? $"{full}|{info.LastWriteTimeUtc.Ticks}|{info.Length}" : full;
        }
        catch (IOException) { return full; }
    }
}

/// <summary>The encoders an ffmpeg build offers, asked once per binary (<c>ffmpeg -hide_banner -encoders</c>) and cached by
/// path and modification time, so a reinstall in place counts as a different ffmpeg.</summary>
public sealed class FFmpegEncoders
{
    private readonly OutputRunner _run;
    private readonly ConcurrentDictionary<string, Lazy<IReadOnlySet<string>>> _cache = new();

    public FFmpegEncoders(OutputRunner? run = null) => _run = run ?? OutputRunners.Default;

    public static FFmpegEncoders Default { get; } = new();

    /// <summary>The encoders <paramref name="ffmpeg"/> offers, running it the first time. Blocks; use <see cref="LoadAsync"/>
    /// from code that must not.</summary>
    public IReadOnlySet<string> Available(string ffmpeg) =>
        _cache.GetOrAdd(Identity(ffmpeg), _ => new Lazy<IReadOnlySet<string>>(() =>
            Task.Run(() => AskAsync(ffmpeg, CancellationToken.None)).GetAwaiter().GetResult())).Value;

    public async Task<IReadOnlySet<string>> LoadAsync(string ffmpeg, CancellationToken cancellationToken = default)
    {
        var key = Identity(ffmpeg);
        if (_cache.TryGetValue(key, out var known) && known.IsValueCreated) return known.Value;
        var names = await AskAsync(ffmpeg, cancellationToken).ConfigureAwait(false);
        var lazy = new Lazy<IReadOnlySet<string>>(() => names);
        _ = lazy.Value;
        _cache[key] = lazy;
        return names;
    }

    /// <summary>Starts asking in the background so the first conversion does not wait.</summary>
    public void Warm(string? ffmpeg)
    {
        if (ffmpeg is null) return;
        _ = Task.Run(() => { try { Available(ffmpeg); } catch (Exception) { /* a later call asks again */ } });
    }

    /// <summary>Stores what <paramref name="ffmpeg"/> offers without running it: for tests.</summary>
    public void Record(string ffmpeg, IEnumerable<string> encoders)
    {
        var names = encoders.ToHashSet();
        var lazy = new Lazy<IReadOnlySet<string>>(() => names);
        _ = lazy.Value;
        _cache[Identity(ffmpeg)] = lazy;
    }

    private async Task<IReadOnlySet<string>> AskAsync(string ffmpeg, CancellationToken ct)
    {
        try
        {
            return Parse(await _run(new ToolInvocation(ffmpeg, ["-hide_banner", "-encoders"]), ct).ConfigureAwait(false));
        }
        catch (Exception e) when (e is ProcessFailure or IOException) { return new HashSet<string>(); }
    }

    /// <summary>Encoder names from <c>ffmpeg -encoders</c>, whose lines look like " A....D libopus   libopus Opus (codec opus)"
    /// after a legend (" V..... = Video").</summary>
    public static IReadOnlySet<string> Parse(string output)
    {
        var names = new HashSet<string>();
        foreach (var line in output.Split('\n'))
        {
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (fields.Length >= 2 && fields[0].Length == 6 && fields[1] != "=") names.Add(fields[1]);
        }
        return names;
    }

    private static string Identity(string ffmpeg)
    {
        try
        {
            var info = new FileInfo(ffmpeg);
            return info.Exists ? $"{info.FullName}@{info.LastWriteTimeUtc.Ticks}" : ffmpeg;
        }
        catch (IOException) { return ffmpeg; }
    }
}
