using OpenShelf.Convert;
using OpenShelf.Core;
using OpenShelf.Tools.Editors;
using OpenShelf.Tools.Imaging;

namespace OpenShelf.Tools;

/// <summary>The image tools that ship with the Tools wheel, in the Mac's wheel order (WheelOrdering.imageTools): Compress, Crop, Resize,
/// Annotate, Remove BG, Edit, Metadata, Redact, Add BG, Read QR, OCR, Combine. Remove BG, Read QR and OCR are registered by their own projects.</summary>
internal static class BuiltInTools
{
    public static void RegisterAll(ToolsCatalog catalog)
    {
        var services = catalog.Services;

        void Editor(string id, string title, string glyph, int order, Func<EditorHost, IReadOnlyList<EditableImage>, EditorWindow> make) =>
            catalog.Register(new ToolDefinition(id, title, glyph, AllImages, order, run => ImageEditors.OpenAsync(run, services, make)));

        Editor(ToolIds.Compress, "Compress", "\uE73F", 10, (h, i) => new CompressEditor(h, i));
        Editor(ToolIds.Crop, "Crop", "\uE7A8", 20, (h, i) => new CropEditor(h, i));
        Editor(ToolIds.Resize, "Resize", "\uE740", 30, (h, i) => new ResizeEditor(h, i));
        Editor(ToolIds.Annotate, "Annotate", "\uE70F", 40, (h, i) => new AnnotateEditor(h, i));
        Editor(ToolIds.Edit, "Edit", "\uE9E9", 60, (h, i) => new AdjustEditor(h, i));
        Editor(ToolIds.Metadata, "Metadata", "\uE8EC", 70, (h, i) => new MetadataEditor(h, i));
        Editor(ToolIds.Redact, "Redact", "\uE7B3", 80, (h, i) => new RedactEditor(h, i));
        Editor(ToolIds.AddBackground, "Add BG", "\uEB9F", 90, (h, i) => new AddBackgroundEditor(h, i));

        // Combine: several images, one click, one PDF. Replace this registration to take mixed selections of PDFs and images as well.
        catalog.Register(new ToolDefinition(ToolIds.Combine, "Combine", "\uE8C8", CombineOperation.Applies, 120, run => CombineAsync(run, services)));
    }

    private static async Task CombineAsync(WheelRun run, ToolServices services)
    {
        using var cancel = new CancellationTokenSource();
        using var card = services.Progress.Begin("Combining into PDF", run.Paths.Count, run.Pointer, cancel);
        try
        {
            var progress = new Progress<double>(fraction => card.Report(0, fraction));
            var path = await Task.Run(() => CombineOperation.Run(run.Paths, progress, cancel.Token));
            services.Shelves.AddResults([path], run.SourceShelf);
            card.Finish("Saved · " + Path.GetFileName(path));
        }
        catch (OperationCanceledException)
        {
            card.Finish("Cancelled");
        }
        catch (Exception e)
        {
            card.Finish("Couldn’t combine · " + (e is SaveException or IOException ? e.Message : "the images couldn’t be read."));
        }
    }

    /// <summary>Whether every dragged file is an image.</summary>
    public static bool AllImages(IReadOnlyList<string> paths) => ToolsCatalog.AllOfKind(paths, FileKind.Image);
}

/// <summary>Ids of the built-in tools, for projects that replace or reorder them.</summary>
public static class ToolIds
{
    public const string Compress = "tool.compress";
    public const string Metadata = "tool.metadata";
    public const string Edit = "tool.edit";
    public const string Annotate = "tool.annotate";
    public const string AddBackground = "tool.addbg";
    public const string Crop = "tool.crop";
    public const string Resize = "tool.resize";
    public const string RemoveBackground = "tool.removebg";
    public const string Redact = "tool.redact";
    public const string ReadQr = "tool.readqr";
    public const string Ocr = "tool.ocr";
    public const string Combine = "tool.combine";
}

/// <summary>Opens an image editor for the dragged files: loads them off the UI thread, then shows the editor zooming out of the drop point.</summary>
public static class ImageEditors
{
    public static async Task OpenAsync(WheelRun run, ToolServices services, Func<EditorHost, IReadOnlyList<EditableImage>, EditorWindow> make)
    {
        if (run.Paths.Count == 0) return;
        IReadOnlyList<EditableImage> images;
        try
        {
            images = await Task.Run(() => (IReadOnlyList<EditableImage>)run.Paths.Select(EditableImage.Load).ToList());
        }
        catch (Exception e)
        {
            services.Toasts.Show("Couldn’t open image · " + e.Message, run.Pointer);
            return;
        }
        var window = make(new EditorHost(run, services), images);
        window.Show();
    }
}
