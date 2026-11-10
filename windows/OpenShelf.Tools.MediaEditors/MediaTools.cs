using OpenShelf.Convert;
using OpenShelf.Core;
using OpenShelf.Tools.Media;

namespace OpenShelf.Tools.MediaEditors;

/// <summary>Ids of the video and audio tools on the Tools wheel. Mute's is the one the watched-folder actions refer to.</summary>
public static class MediaToolIds
{
    public const string Trim = "tool.trim";
    public const string Speed = "tool.speed";
    public const string Crop = "tool.cropvideo";
    public const string Rotate = "tool.rotate";
    public const string Split = "tool.splitmedia";
    public const string Merge = "tool.merge";
    public const string Compress = "tool.compressvideo";
    public const string Mute = "tool.mute";
}

/// <summary>
/// The video and audio tools of the Tools wheel (SPEC 2.4): Trim, Speed, Crop, Rotate, Split, Merge, Compress and Mute.
/// <code>MediaTools.Register(toolsCatalog);</code>
/// Wheel order is the Mac's (Merge, Trim, Compress, Crop, Rotate, Split, Speed, Mute) in the 200s. Video tools take MP4/MOV/M4V/3GP
/// files, audio tools the editable sound formats; other containers only convert. Everything runs through ffmpeg: without it a tool
/// shows "Trim needs ffmpeg · Install it in Settings → Extra Formats" instead of opening.
/// </summary>
public static class MediaTools
{
    /// <summary>Registers every media tool with <paramref name="tools"/>, using the app's ffmpeg.</summary>
    public static void Register(ToolsCatalog tools) => Register(tools, new MediaOperations(new MediaEditor()));

    internal static void Register(ToolsCatalog tools, IMediaOperations operations)
    {
        ArgumentNullException.ThrowIfNull(tools);
        var services = tools.Services;

        static bool Video(IReadOnlyList<string> p) => ToolsCatalog.AllEditableMedia(p, FileKind.Video);
        static bool Either(IReadOnlyList<string> p) => ToolsCatalog.AllEditableMedia(p, FileKind.Video) || ToolsCatalog.AllEditableMedia(p, FileKind.Audio);
        static bool Several(IReadOnlyList<string> p) => p.Count > 1 && Either(p);

        void Add(string id, string title, string glyph, Func<IReadOnlyList<string>, bool> applies, int order, Func<EditorHost, EditorWindow> make) =>
            tools.Register(new ToolDefinition(id, title, glyph, applies, order, run => OpenAsync(run, services, operations, title, make)));

        Add(MediaToolIds.Merge, "Merge", Glyphs.Merge, Several, 200, h => new MergeEditor(h, operations, h.Paths));
        Add(MediaToolIds.Trim, "Trim", Glyphs.Trim, Either, 210, h => new TrimEditor(h, operations, h.Paths));
        Add(MediaToolIds.Compress, "Compress", Glyphs.Compress, Video, 220, h => new VideoCompressEditor(h, operations, h.Paths));
        Add(MediaToolIds.Crop, "Crop", Glyphs.Crop, Video, 230, h => new VideoCropEditor(h, operations, h.Paths));
        Add(MediaToolIds.Rotate, "Rotate", Glyphs.Rotate, Video, 240, h => new RotateEditor(h, operations, h.Paths));
        Add(MediaToolIds.Split, "Split", Glyphs.Split, Either, 250, h => new SplitEditor(h, operations, h.Paths));
        Add(MediaToolIds.Speed, "Speed", Glyphs.Speed, Either, 260, h => new SpeedEditor(h, operations, h.Paths));
        tools.Register(new ToolDefinition(MediaToolIds.Mute, "Mute", Glyphs.Mute, Video, 270, run => MuteAsync(run, services, operations)));
    }

    /// <summary>Shows the editor, or says ffmpeg is needed.</summary>
    private static Task OpenAsync(WheelRun run, ToolServices services, IMediaOperations operations, string title, Func<EditorHost, EditorWindow> make)
    {
        if (run.Paths.Count == 0) return Task.CompletedTask;
        if (!operations.IsAvailable)
        {
            services.Toasts.Show(Guidance.NeedsFfmpeg(title), run.Pointer);
            return Task.CompletedTask;
        }
        make(new EditorHost(run, services)).Show();
        return Task.CompletedTask;
    }

    /// <summary>Mute: one click, no editor. Removes the sound from each video with the progress card, saving "Name Muted.mp4".</summary>
    internal static async Task MuteAsync(WheelRun run, ToolServices services, IMediaOperations operations)
    {
        if (run.Paths.Count == 0) return;
        if (!operations.IsAvailable)
        {
            services.Toasts.Show(Guidance.NeedsFfmpeg("Mute"), run.Pointer);
            return;
        }
        using var cancel = new CancellationTokenSource();
        using var card = services.Progress.Begin("Removing audio", run.Paths.Count, run.Pointer, cancel);
        var outcome = new SaveOutcome { SavedLabel = "muted" };
        try
        {
            await Task.Run(() => ExportRunner.RunAsync(run.Paths, outcome, p => p,
                async (path, _, progress, ct) => [await operations.MuteAsync(path, progress, ct)],
                (fraction, index) => card.Report(index, Math.Clamp(fraction * run.Paths.Count - index, 0, 1)), cancel.Token));
            services.Shelves.AddResults(outcome.Saved, run.SourceShelf);
            card.Finish(outcome.Message);
        }
        catch (OperationCanceledException)
        {
            if (outcome.Saved.Count > 0) services.Shelves.AddResults(outcome.Saved, run.SourceShelf);
            card.Finish("Cancelled");
        }
        catch (SaveException e)
        {
            if (e.SavedBefore.Count > 0) services.Shelves.AddResults(e.SavedBefore, run.SourceShelf);
            card.Finish("Couldn’t mute · " + e.Message);
        }
        catch (Exception e)
        {
            card.Finish("Couldn’t mute · " + Guidance.ForSave(e, run.Paths[0]).Message);
        }
    }
}
