using OpenShelf.Convert;


namespace OpenShelf.Tools.VisionEditors;

/// <summary>
/// Registers the tools that read pictures with the Vision project (SPEC §2.4): Remove BG, Read QR and OCR. Call once after creating the
/// <see cref="ToolsCatalog"/>.
/// <code>
/// var tools = new ToolsCatalog(toasts, progress, shelves);
/// VisionTools.Register(tools);
/// </code>
/// Wheel positions follow the Mac's image order: Compress, Crop, Resize, Annotate, Remove BG (50), Edit, Metadata, Redact, Add BG,
/// Read QR (100), OCR (110), Combine. OCR also takes PDFs and mixed selections of images and PDFs.
/// The segmentation model is copied to a <c>models</c> folder beside any executable that references this project.
/// </summary>
public static class VisionTools
{
    public const string RemoveBackgroundGlyph = "";
    public const string ReadQrGlyph = "";
    public const string OcrGlyph = "";

    public const int RemoveBackgroundOrder = 50;
    public const int ReadQrOrder = 100;
    public const int OcrOrder = 110;

    private static bool AllImages(IReadOnlyList<string> paths) => ToolsCatalog.AllOfKind(paths, FileKind.Image);

    public static void Register(ToolsCatalog tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        var services = tools.Services;

        tools.Register(new ToolDefinition(ToolIds.RemoveBackground, "Remove BG", RemoveBackgroundGlyph, AllImages, RemoveBackgroundOrder,
            run => ImageEditors.OpenAsync(run, services, (host, images) => new RemoveBackgroundEditor(host, images))));

        tools.Register(new ToolDefinition(ToolIds.ReadQr, "Read QR", ReadQrGlyph, AllImages, ReadQrOrder,
            run => QrTool.RunAsync(run.Paths, run.Pointer, services.Toasts)));

        tools.Register(new ToolDefinition(ToolIds.Ocr, "OCR", OcrGlyph, OcrRunner.Applies, OcrOrder, run =>
        {
            if (run.Paths.Count == 0) return Task.CompletedTask;
            new OcrEditor(new EditorHost(run, services), run.Paths).Show();
            return Task.CompletedTask;
        }));
    }
}
