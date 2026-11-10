using OpenShelf.Convert;
using OpenShelf.Core;
using OpenShelf.Settings;
using Settings = OpenShelf.Settings;
using Engine = OpenShelf.Convert;

namespace OpenShelf.App;

/// <summary>
/// What the Convert and Tools wheels offer: the conversion catalog turned into
/// wedges, each running a batch with the progress card and putting results back
/// on the shelf they came from. Tools are supplied separately.
/// </summary>
public sealed class ConversionWheelCatalog(
    IPreferences preferences,
    IProgressService progress,
    IToastService toasts,
    IShelfService shelves) : IWheelCatalog
{
    /// <summary>The Tools wheel; set once the tools exist.</summary>
    public Func<IReadOnlyList<string>, WheelContent>? ToolsProvider { get; set; }

    /// <summary>Shows the output-options window for a format and returns the options to use, or null when cancelled.
    /// Set once that window exists; until then Alt converts with the saved settings.</summary>
    public Func<OutputFormat, IReadOnlyList<string>, ScreenPoint, Task<ConversionOptions?>>? OptionsPrompt { get; set; }

    /// <summary>Raised with the files a batch wrote, so watched folders never treat them as arrivals.</summary>
    public event Action<IReadOnlyList<string>>? Saved;

    public WheelContent Convert(IReadOnlyList<string> paths)
    {
        var tools = ExternalToolsProvider.Default.Current;
        var formats = ConversionCatalog.Formats(paths, tools, MediaProbe.HasAudioTrack);
        if (formats.Count == 0)
            return new WheelContent([], MissingToolAdvice.EmptyMessage(paths, formats, tools) ?? "Nothing to convert these to");

        var entries = formats
            .Select(format => new WheelEntry(
                Id: "convert." + format.Id(),
                Title: format.Title(),
                Run: run => RunAsync(format, run),
                HasOptions: format.HasOptions()))
            .ToList();
        return new WheelContent(entries, MissingToolAdvice.EmptyMessage(paths, formats, tools));
    }

    public WheelContent Tools(IReadOnlyList<string> paths) =>
        ToolsProvider?.Invoke(paths) ?? new WheelContent([], "Tools arrive in a later build");

    /// <summary>Runs one format over the files, as a wedge does. Also used by watched folders and the command line.</summary>
    public async Task<BatchResult?> RunAsync(OutputFormat format, WheelRun run)
    {
        var options = CurrentOptions();
        if (run.OptionsRequested && format.HasOptions() && OptionsPrompt is { } prompt)
        {
            var chosen = await prompt(format, run.Paths, run.Pointer);
            if (chosen is null) return null;
            options = chosen;
        }

        using var cancel = new CancellationTokenSource();
        var jobs = ConversionRunner.Jobs(run.Paths, format).Count;
        var card = progress.Begin(ConversionRunner.Title(format), jobs, run.Pointer, cancel);
        var reporter = new Progress<BatchProgress>(p => card.Report(p.Completed, p.Fraction, p.CurrentFile));
        try
        {
            var result = await Task.Run(() => ConversionRunner.Default.ConvertAsync(
                run.Paths, format, options, reporter, cancel.Token,
                saved =>
                {
                    Saved?.Invoke(saved);
                    System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => shelves.AddResults(saved, run.SourceShelf));
                }));
            card.Finish(Summary(format, result));
            return result;
        }
        catch (Exception exception)
        {
            card.Finish(exception.Message);
            return null;
        }
        finally
        {
            card.Dispose();
        }
    }

    /// <summary>The saved output settings, as the engines take them.</summary>
    public ConversionOptions CurrentOptions() => ToOptions(preferences.GetOutput());

    public static ConversionOptions ToOptions(OutputSettings settings) => new()
    {
        ImageQuality = settings.Image.Quality,
        KeepMetadata = settings.Image.KeepsMetadata,
        Video = new VideoOptions
        {
            Codec = Map<Engine.VideoCodec>(settings.Video.Codec),
            Quality = Map<Engine.VideoQuality>(settings.Video.Quality),
            CustomMbps = settings.Video.Quality == Settings.VideoQuality.Custom ? settings.Video.CustomMbps : null,
            LargestSize = settings.Video.MaxSize switch
            {
                VideoMaxSize.Uhd4K => VideoSize.UltraHD4K,
                VideoMaxSize.P1080 => VideoSize.FullHD1080,
                VideoMaxSize.P720 => VideoSize.HD720,
                VideoMaxSize.P480 => VideoSize.SD480,
                _ => VideoSize.Original,
            },
        },
        Audio = new AudioOptions { BitrateKbps = settings.Audio.Kbps },
        Gif = new GifOptions
        {
            Width = Map<Engine.GifWidth>(settings.Gif.Width),
            FramesPerSecond = settings.Gif.FrameRate,
        },
    };

    private static T Map<T>(Enum value) where T : struct, Enum =>
        Enum.TryParse<T>(value.ToString(), ignoreCase: true, out var mapped) ? mapped : default;

    private static string Summary(OutputFormat format, BatchResult result)
    {
        var saved = result.Jobs.Count(job => job.Succeeded);
        var failed = result.Failures;
        var name = format == OutputFormat.Extract ? null : format.Title();
        if (result.Cancelled)
            return saved == 0 ? "Cancelled" : $"Cancelled · {saved} saved";
        if (failed.Count == 0)
        {
            if (name is null) return saved == 1 ? "Unpacked" : $"Unpacked {saved} archives";
            if (format.IsArchive()) return $"Made a {name}";
            return saved == 1 ? $"Converted to {name}" : $"Converted {saved} files to {name}";
        }
        if (saved == 0)
            return failed.Count == 1 ? failed[0] : $"{failed[0]} (and {failed.Count - 1} more failed)";
        return $"{saved} saved · {failed.Count} failed: {failed[0]}";
    }
}
