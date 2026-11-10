using System.Text.Json;
using OpenShelf.Convert;

namespace OpenShelf.Convert.Media.Tests;

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "openshelf-media-" + Guid.NewGuid().ToString("N")[..10]);

    public TempDir() => Directory.CreateDirectory(Path);

    public string Combine(string name) => System.IO.Path.Combine(Path, name);

    public string[] Names() => Directory.GetFileSystemEntries(Path).Select(p => System.IO.Path.GetFileName(p)!).OrderBy(n => n).ToArray();

    public void Dispose()
    {
        try { Directory.Delete(Path, true); } catch (IOException) { }
    }
}

/// <summary>Collects progress synchronously (Progress&lt;T&gt; posts to the thread pool).</summary>
public sealed class Collected : IProgress<double>
{
    private readonly List<double> _values = [];
    public IReadOnlyList<double> Values { get { lock (_values) return _values.ToList(); } }
    public void Report(double value) { lock (_values) _values.Add(value); }
}

/// <summary>A pretend ffmpeg: records each command line, optionally prints progress lines, writes a file where the output goes.</summary>
public sealed class FakeFfmpeg
{
    public List<string[]> Calls { get; } = [];
    /// <summary>Runs for each invocation after it is recorded; throw to fail it.</summary>
    public Func<string[], Action<string>?, CancellationToken, Task>? Behavior { get; set; }

    public ToolRunner Runner => async (invocation, onLine, ct) =>
    {
        var args = invocation.Arguments.ToArray();
        lock (Calls) Calls.Add(args);
        if (Behavior is not null) await Behavior(args, onLine, ct);
        else
        {
            onLine?.Invoke("out_time_us=1000000");
            onLine?.Invoke("progress=continue");
            File.WriteAllText(args[^1], "media");
            onLine?.Invoke("out_time_us=2000000");
            onLine?.Invoke("progress=end");
        }
    };

    /// <summary>The encoder named by -c:v in each call ("copy" for stream copies), in order.</summary>
    public List<string?> VideoCodecs() => Calls.Select(c =>
    {
        var i = Array.IndexOf(c, "-c:v");
        if (i >= 0) return c[i + 1];
        return Array.IndexOf(c, "-c") >= 0 ? "copy" : null;
    }).ToList();

    public string[] Last => Calls[^1];
}

public static class Media
{
    public const string FakeFfmpegPath = @"C:\fake\ffmpeg.exe";
    public const string FakeFfprobePath = @"C:\fake\ffprobe.exe";

    public static readonly IReadOnlySet<string> AllEncoders = new HashSet<string>
        { "libx264", "libx265", "hevc_nvenc", "hevc_qsv", "hevc_amf", "libvpx-vp9", "libvorbis", "libopus", "libmp3lame", "wmav2", "aac", "mpeg4", "wmv2" };

    public static ExternalTools FakeTools => new() { Ffmpeg = FakeFfmpegPath, Ffprobe = FakeFfprobePath };

    public static string ProbeJson(string videoCodec = "h264", string pixFmt = "yuv420p", string audioCodec = "aac", int width = 1280, int height = 720,
        string extraVideo = "", bool audio = true, bool video = true, int audioChannels = 2, int audioBitRate = 192000, double duration = 10) =>
        "{\"streams\":[" +
        (video ? $"{{\"codec_type\":\"video\",\"codec_name\":\"{videoCodec}\",\"width\":{width},\"height\":{height},\"pix_fmt\":\"{pixFmt}\",\"avg_frame_rate\":\"30/1\",\"bit_rate\":\"4000000\"{extraVideo}}}" : "") +
        (video && audio ? "," : "") +
        (audio ? $"{{\"codec_type\":\"audio\",\"codec_name\":\"{audioCodec}\",\"channels\":{audioChannels},\"sample_rate\":\"48000\",\"bit_rate\":\"{audioBitRate}\"}}" : "") +
        $"],\"format\":{{\"duration\":\"{duration.ToString(System.Globalization.CultureInfo.InvariantCulture)}\"}}}}";

    public static MediaProbe FakeProbe(string json, List<string>? asked = null) =>
        new((invocation, _) =>
        {
            asked?.Add(invocation.Arguments[^1]);
            return Task.FromResult(json);
        });

    public static FFmpegEncoders FakeEncoders(IEnumerable<string>? names = null)
    {
        var encoders = new FFmpegEncoders((_, _) => Task.FromResult(""));
        encoders.Record(FakeFfmpegPath, names ?? AllEncoders);
        return encoders;
    }

    public static MediaEngine Engine(FakeFfmpeg ffmpeg, string probeJson, IEnumerable<string>? encoders = null) =>
        new(ffmpeg.Runner, FakeProbe(probeJson), FakeEncoders(encoders));

    public static ConversionRequest Request(string source, OutputFormat format, ConversionOptions? options = null, ExternalTools? tools = null, string? outputDirectory = null) =>
        new(source, format, options ?? ConversionOptions.Automatic) { Tools = tools ?? FakeTools, OutputDirectory = outputDirectory };

    /// <summary>The value after <paramref name="flag"/> (first occurrence).</summary>
    public static string After(IEnumerable<string> arguments, string flag)
    {
        var list = arguments.ToList();
        return list[list.IndexOf(flag) + 1];
    }

    /// <summary>Whether <paramref name="sequence"/> appears in <paramref name="arguments"/> as consecutive items.</summary>
    public static bool Contains(IEnumerable<string> arguments, params string[] sequence)
    {
        var list = arguments.ToList();
        for (var i = 0; i + sequence.Length <= list.Count; i++)
            if (list.Skip(i).Take(sequence.Length).SequenceEqual(sequence)) return true;
        return false;
    }

    public static JsonDocument Json(string json) => JsonDocument.Parse(json);
}
