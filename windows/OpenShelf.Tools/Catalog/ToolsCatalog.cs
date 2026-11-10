using OpenShelf.Convert;
using OpenShelf.Core;

namespace OpenShelf.Tools;

/// <summary>One entry the Tools wheel can offer. Register it with <see cref="ToolsCatalog.Register"/>.</summary>
/// <param name="Id">Stable id, e.g. "tool.crop". Registering the same id again replaces the earlier tool.</param>
/// <param name="Title">The wedge label ("Crop").</param>
/// <param name="Glyph">Segoe Fluent/MDL2 icon glyph shown above the label.</param>
/// <param name="AppliesTo">Whether the tool applies to every one of the dragged paths. Called on the UI thread for each wheel; keep it cheap
/// (look at extensions or <see cref="FileKinds"/>, never open the file).</param>
/// <param name="Order">Position on the wheel, ascending; ties keep registration order. Built-in image tools use 10..120, video and audio
/// 200..299, PDF 300..399, so everyday tools come first on the first page.</param>
/// <param name="Run">Runs the tool (opens its editor, or works straight away for one-click tools). Called on the UI thread after the drop
/// has returned; do long work asynchronously.</param>
/// <param name="HasOptions">True when holding Alt should request an options window first.</param>
public sealed record ToolDefinition(string Id, string Title, string Glyph, Func<IReadOnlyList<string>, bool> AppliesTo, int Order,
    Func<WheelRun, Task> Run, bool HasOptions = false);

/// <summary>The services every tool uses: toasts, the progress card, and the shelf results go back to.</summary>
public sealed record ToolServices(IToastService Toasts, IProgressService Progress, IShelfService Shelves);

/// <summary>
/// The Tools wheel (SPEC §2.4): which tools apply to the dragged files, in wheel order. Image tools are built in; PDF, media, OCR, Read QR
/// and Remove BG tools are registered by their own projects through <see cref="Register"/>.
/// <code>
/// var tools = new ToolsCatalog(toasts, progress, shelves);
/// tools.Register(new ToolDefinition("tool.ocr", "OCR", "type",
///     paths =&gt; paths.All(p =&gt; FileKinds.Of(p) == FileKind.Image), order: 110, run: run =&gt; Ocr.OpenAsync(run)));
/// catalog.ToolsProvider = tools.For;   // ConversionWheelCatalog.ToolsProvider
/// </code>
/// </summary>
public sealed class ToolsCatalog
{
    private readonly List<ToolDefinition> _tools = [];
    private readonly object _lock = new();

    public ToolsCatalog(IToastService toasts, IProgressService progress, IShelfService shelves, bool registerBuiltIns = true)
    {
        Services = new ToolServices(toasts, progress, shelves);
        if (registerBuiltIns) BuiltInTools.RegisterAll(this);
    }

    public ToolServices Services { get; }

    /// <summary>Adds a tool, or replaces the one with the same id (so a PDF project can take over Combine for mixed selections).</summary>
    public void Register(ToolDefinition tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        lock (_lock)
        {
            var existing = _tools.FindIndex(t => t.Id == tool.Id);
            if (existing >= 0) _tools[existing] = tool;
            else _tools.Add(tool);
        }
    }

    public bool Unregister(string id)
    {
        lock (_lock) return _tools.RemoveAll(t => t.Id == id) > 0;
    }

    public IReadOnlyList<ToolDefinition> Tools
    {
        get { lock (_lock) return _tools.ToList(); }
    }

    /// <summary>The tools that apply to <paramref name="paths"/>, in wheel order.</summary>
    public IReadOnlyList<ToolDefinition> Applicable(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return [];
        List<ToolDefinition> snapshot;
        lock (_lock) snapshot = _tools.ToList();
        var result = new List<ToolDefinition>();
        foreach (var tool in snapshot.Select((t, i) => (t, i)).OrderBy(x => x.t.Order).ThenBy(x => x.i))
        {
            bool applies;
            try { applies = tool.t.AppliesTo(paths); }
            catch (Exception) { applies = false; }   // a broken predicate must not take the wheel down
            if (applies) result.Add(tool.t);
        }
        return result;
    }

    /// <summary>The wheel content for the dragged files. Use as <c>ConversionWheelCatalog.ToolsProvider</c>.</summary>
    public WheelContent For(IReadOnlyList<string> paths)
    {
        var tools = Applicable(paths);
        if (tools.Count == 0) return new WheelContent([], EmptyMessage(paths));
        return new WheelContent(tools.Select(t => new WheelEntry(t.Id, t.Title, t.Run, t.Glyph, null, t.HasOptions)).ToList());
    }

    /// <summary>What to say when no tool applies.</summary>
    public static string EmptyMessage(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return "Nothing to work on";
        if (NeedsConversionForTools(paths))
        {
            var kinds = paths.Select(FileKinds.Of).ToHashSet();
            var format = kinds.SetEquals([FileKind.Video]) ? "MP4" : kinds.SetEquals([FileKind.Audio]) ? "M4A" : "MP4 or M4A";
            return $"Convert to {format} first";
        }
        return "No tools for these files";
    }

    /// <summary>Containers the media tools can edit. Others (MKV, WebM, AVI, WMV, FLV, WMA, …) only convert, through ffmpeg.</summary>
    public static readonly IReadOnlySet<string> EditableMediaExtensions = new HashSet<string>
    {
        "mp4", "mov", "m4v", "3gp", "m4a", "mp3", "wav", "aif", "aiff", "caf", "aac", "flac", "ogg", "opus",
    };

    /// <summary>Whether every file is audio or video the media tools cannot open.</summary>
    public static bool NeedsConversionForTools(IReadOnlyList<string> paths) =>
        paths.Count > 0 && paths.All(p =>
        {
            var kind = FileKinds.Of(p);
            return kind is FileKind.Video or FileKind.Audio && !EditableMediaExtensions.Contains(FileKinds.ExtensionOf(FileKinds.ResolveLink(p)));
        });

    // ---------------------------------------------------------------- applicability helpers for registrations

    /// <summary>Whether every path is of <paramref name="kind"/>.</summary>
    public static bool AllOfKind(IReadOnlyList<string> paths, FileKind kind) =>
        paths.Count > 0 && paths.All(p => FileKinds.Of(p) == kind);

    /// <summary>Whether every path is one of <paramref name="kinds"/>.</summary>
    public static bool AllOfKinds(IReadOnlyList<string> paths, params FileKind[] kinds) =>
        paths.Count > 0 && paths.All(p => kinds.Contains(FileKinds.Of(p)));

    /// <summary>Whether every path is a video or audio file the media tools can edit (and at least one exists).</summary>
    public static bool AllEditableMedia(IReadOnlyList<string> paths, params FileKind[] kinds) =>
        AllOfKinds(paths, kinds) && paths.All(p => EditableMediaExtensions.Contains(FileKinds.ExtensionOf(FileKinds.ResolveLink(p))));
}
