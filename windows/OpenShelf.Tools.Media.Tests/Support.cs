using System.Diagnostics;
using OpenShelf.Convert;
using OpenShelf.Tools.Media;

namespace OpenShelf.Tools.Media.Tests;

/// <summary>A [Fact] that shows up as skipped (with the reason) when ffmpeg or ffprobe is not installed.</summary>
public sealed class RequiresFfmpegFactAttribute : FactAttribute
{
    public RequiresFfmpegFactAttribute()
    {
        if (!Clips.Available) Skip = "ffmpeg and ffprobe were not found on this machine";
    }
}

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "openshelf-toolsmedia-" + Guid.NewGuid().ToString("N")[..10]);

    public TempDir() => Directory.CreateDirectory(Path);

    public string Combine(string name) => System.IO.Path.Combine(Path, name);

    /// <summary>Every file name in the folder, hidden ones included.</summary>
    public string[] Names() => Directory.GetFileSystemEntries(Path).Select(p => System.IO.Path.GetFileName(p)!).OrderBy(n => n, StringComparer.Ordinal).ToArray();

    public void Dispose()
    {
        try { Directory.Delete(Path, true); } catch (IOException) { }
    }
}

/// <summary>Short clips made once with ffmpeg's lavfi sources, shared by every test; each test copies what it needs into its own folder.</summary>
public static class Clips
{
    private static readonly Lazy<State> Shared = new(() => new State());

    public static ExternalTools Tools { get; } = ExternalTools.Detect(ToolLocations.Standard);
    public static bool Available => Tools.Ffmpeg is not null && Tools.Ffprobe is not null;

    /// <summary>320x240, 10 fps, 4 s, H.264 with a keyframe every second, AAC tone.</summary>
    public const string Video = "video.mp4";
    /// <summary>The same picture without sound.</summary>
    public const string Silent = "silent.mp4";
    /// <summary>160x320 shown portrait, stored as 320x160 with a rotation of 90 degrees; 2 s, with sound.</summary>
    public const string Portrait = "portrait.mp4";
    public const string Wav = "tone.wav";
    public const string Mp3 = "tone.mp3";
    public const string M4a = "tone.m4a";
    /// <summary>640x480, 3 s, noisy and encoded at a high rate: plenty to compress.</summary>
    public const string Heavy = "heavy.mp4";
    /// <summary>A tiny, already very small video.</summary>
    public const string Tiny = "tiny.mp4";

    /// <summary>Copies the named clips into a fresh folder.</summary>
    public static TempDir Copy(params string[] names)
    {
        var dir = new TempDir();
        foreach (var name in names) File.Copy(Shared.Value.Path(name), dir.Combine(name));
        return dir;
    }

    public static MediaEditor Editor() => new(() => Tools);

    public static ProbedMedia Probe(string path) =>
        new MediaProbe().Load(path, Tools.Ffprobe) ?? throw new InvalidOperationException("ffprobe could not read " + path);

    public static ProbedMedia Probe(TempDir dir, string name) => Probe(dir.Combine(name));

    /// <summary>The codec_name of the first video stream.</summary>
    public static string VideoCodec(string path) => Probe(path).Video!.Codec;

    private sealed class State
    {
        private readonly TempDir _dir = new();

        public string Path(string name) => _dir.Combine(name);

        public State()
        {
            if (!Available) return;
            AppDomain.CurrentDomain.ProcessExit += (_, _) => _dir.Dispose();
            var picture = "testsrc2=size=320x240:rate=10:duration=4";
            var tone = "sine=frequency=440:duration=4";
            Run("-f", "lavfi", "-i", picture, "-f", "lavfi", "-i", tone, "-c:v", "libx264", "-pix_fmt", "yuv420p", "-g", "10", "-keyint_min", "10", "-sc_threshold", "0",
                "-c:a", "aac", "-shortest", Path(Video));
            Run("-f", "lavfi", "-i", picture, "-c:v", "libx264", "-pix_fmt", "yuv420p", "-g", "10", "-keyint_min", "10", "-sc_threshold", "0", Path(Silent));
            Run("-f", "lavfi", "-i", "sine=frequency=330:duration=3", "-c:a", "pcm_s16le", Path(Wav));
            Run("-f", "lavfi", "-i", "sine=frequency=330:duration=3", "-c:a", "libmp3lame", "-b:a", "128k", Path(Mp3));
            Run("-f", "lavfi", "-i", "sine=frequency=330:duration=3", "-c:a", "aac", Path(M4a));
            // A 320x160 picture that players turn 90 degrees clockwise: 160x320 on screen.
            var plain = _dir.Combine("landscape.mp4");
            Run("-f", "lavfi", "-i", "testsrc2=size=320x160:rate=10:duration=2", "-f", "lavfi", "-i", "sine=frequency=550:duration=2",
                "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", plain);
            Run("-display_rotation:v:0", "270", "-i", plain, "-c", "copy", Path(Portrait));
            Run("-f", "lavfi", "-i", "testsrc2=size=640x480:rate=25:duration=3", "-c:v", "libx264", "-preset", "ultrafast", "-crf", "6", "-pix_fmt", "yuv420p", Path(Heavy));
            Run("-f", "lavfi", "-i", "color=c=gray:size=64x64:rate=5:duration=1", "-c:v", "libx264", "-b:v", "20k", "-pix_fmt", "yuv420p", Path(Tiny));
        }

        private static void Run(params string[] args)
        {
            var start = new ProcessStartInfo(Tools.Ffmpeg!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var a in new[] { "-y", "-nostdin", "-loglevel", "error" }.Concat(args)) start.ArgumentList.Add(a);
            using var process = Process.Start(start)!;
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0) throw new InvalidOperationException("fixture ffmpeg failed: " + error);
        }
    }
}

/// <summary>Collects progress synchronously (Progress&lt;T&gt; posts to the thread pool).</summary>
public sealed class Collected : IProgress<double>
{
    private readonly List<double> _values = [];
    public IReadOnlyList<double> Values { get { lock (_values) return _values.ToList(); } }
    public void Report(double value) { lock (_values) _values.Add(value); }
}

/// <summary>A pretend ffmpeg: records each command line and writes a file where the output goes.</summary>
public sealed class FakeFfmpeg
{
    public List<string[]> Calls { get; } = [];
    /// <summary>Runs for each invocation after it is recorded, instead of the default; throw to fail it.</summary>
    public Func<string[], Action<string>?, CancellationToken, Task>? Behavior { get; set; }

    public ToolRunner Runner => async (invocation, onLine, ct) =>
    {
        var args = invocation.Arguments.ToArray();
        lock (Calls) Calls.Add(args);
        if (Behavior is not null) await Behavior(args, onLine, ct);
        else
        {
            onLine?.Invoke("out_time_us=1000000");
            File.WriteAllText(args[^1], "media");
            onLine?.Invoke("progress=end");
        }
    };
}
