using OpenShelf.Convert;
using OpenShelf.Core;
using OpenShelf.Tools.Pdf;

namespace OpenShelf.Tools.PdfEditors;

/// <summary>Ids of the PDF tools, for projects that reorder or replace them.</summary>
public static class PdfToolIds
{
    public const string Combine = ToolIds.Combine;
    public const string Pages = "tool.pdf.pages";
    public const string Split = "tool.pdf.split";
    public const string Compress = "tool.pdf.compress";
    public const string Metadata = "tool.pdf.metadata";
}

/// <summary>The PDF tools of the Tools wheel (SPEC §2.4): Combine for PDFs and images mixed, and Pages, Split, Compress and Metadata for PDFs.
/// <code>PdfTools.Register(toolsCatalog);</code></summary>
public static class PdfTools
{
    /// <summary>Registers the PDF tools. Combine replaces the built-in image-only one so it also takes PDFs and mixed selections.
    /// It keeps the image order (120), so with PDFs it still comes before Pages, Split, Compress and Metadata (300 to 330).</summary>
    public static void Register(ToolsCatalog tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        var services = tools.Services;
        tools.Register(new ToolDefinition(PdfToolIds.Combine, "Combine", "files", CanCombine, 120, run => CombineAsync(run, services)));
        tools.Register(new ToolDefinition(PdfToolIds.Pages, "Pages", "layout-grid", AllPdfs, 300,
            run => OpenAsync(run, services, (host, files) => new PdfPagesWindow(host, files))));
        tools.Register(new ToolDefinition(PdfToolIds.Split, "Split", "scissors", AllPdfs, 310,
            run => OpenAsync(run, services, (host, files) => new PdfSplitWindow(host, files))));
        tools.Register(new ToolDefinition(PdfToolIds.Compress, "Compress", "minimize-2", AllPdfs, 320,
            run => OpenAsync(run, services, (host, files) => new PdfCompressWindow(host, files))));
        tools.Register(new ToolDefinition(PdfToolIds.Metadata, "Metadata", "tag", AllPdfs, 330,
            run => OpenAsync(run, services, (host, files) => new PdfMetadataWindow(host, files))));
    }

    /// <summary>Several files, each a PDF or an image.</summary>
    public static bool CanCombine(IReadOnlyList<string> paths) => paths.Count > 1 && ToolsCatalog.AllOfKinds(paths, FileKind.Image, FileKind.Pdf);

    /// <summary>Every dragged file is a PDF.</summary>
    public static bool AllPdfs(IReadOnlyList<string> paths) => ToolsCatalog.AllOfKind(paths, FileKind.Pdf);

    /// <summary>Combine: one click, a progress card, then a toast; "Combined.pdf" lands beside the first file.</summary>
    public static async Task CombineAsync(WheelRun run, ToolServices services)
    {
        using var cancel = new CancellationTokenSource();
        using var card = services.Progress.Begin("Combining into PDF", run.Paths.Count, run.Pointer, cancel);
        try
        {
            var progress = new Progress<double>(fraction => card.Report(0, fraction));
            var path = await PdfCombiner.CombineAsync(run.Paths, progress, cancel.Token);
            services.Shelves.AddResults([path], run.SourceShelf);
            card.Finish("Saved · " + Path.GetFileName(path));
        }
        catch (OperationCanceledException)
        {
            card.Finish("Cancelled");
        }
        catch (PdfToolException e)
        {
            card.Finish("Couldn’t combine · " + e.Message);
        }
        catch (Exception e)
        {
            card.Finish("Couldn’t combine · " + (e is IOException ? e.Message : "the files couldn’t be read."));
        }
    }

    /// <summary>Reads the PDFs off the UI thread, then shows the editor zooming out of the drop point.</summary>
    public static async Task OpenAsync(WheelRun run, ToolServices services, Func<EditorHost, IReadOnlyList<PdfFileState>, EditorWindow> make)
    {
        if (run.Paths.Count == 0) return;
        IReadOnlyList<PdfFileState> files;
        try { files = await Task.Run(() => PdfFileState.ReadAll(run.Paths)); }
        catch (Exception e)
        {
            services.Toasts.Show("Couldn’t open PDF · " + e.Message, run.Pointer);
            return;
        }
        make(new EditorHost(run, services), files).Show();
    }
}
