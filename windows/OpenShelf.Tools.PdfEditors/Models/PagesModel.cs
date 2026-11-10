using OpenShelf.Tools.Pdf;

namespace OpenShelf.Tools.PdfEditors;

/// <summary>The Pages editor without the window: each PDF's pages once it has been shown, which one is shown, and what Save would do.
/// Each PDF keeps its own pages, undo history and load error (SPEC §2.4: no Same for all).</summary>
public sealed class PagesModel
{
    private readonly PdfPagesDocument?[] _documents;
    private readonly PdfPagesDocument?[] _renderers;
    private readonly string?[] _errors;
    private readonly bool[] _loading;

    public PagesModel(IReadOnlyList<PdfFileState> files)
    {
        Files = files;
        _documents = new PdfPagesDocument?[files.Count];
        _renderers = new PdfPagesDocument?[files.Count];
        _errors = new string?[files.Count];
        _loading = new bool[files.Count];
        for (var i = 0; i < files.Count; i++) _errors[i] = files[i].Problem;
    }

    public IReadOnlyList<PdfFileState> Files { get; }
    public bool IsBatch => Files.Count > 1;
    public int Index { get; set; }

    /// <summary>The document being edited at <paramref name="index"/>, once loaded.</summary>
    public PdfPagesDocument? Document(int index) => _documents[index];

    /// <summary>An untouched twin of the document, used only to draw thumbnails: the same index always means the same source page.</summary>
    public PdfPagesDocument? Renderer(int index) => _renderers[index];

    public PdfPagesDocument? Current => _documents[Index];
    public string? Error(int index) => _errors[index];
    public string? CurrentError => _errors[Index];
    public bool IsLoading(int index) => _loading[index];

    /// <summary>Reads the PDF the first time it is shown. Failures (locked, damaged) are kept as <see cref="Error"/>.</summary>
    public async Task LoadAsync(int index, CancellationToken cancel = default)
    {
        if (_documents[index] is not null || _errors[index] is not null || _loading[index]) return;
        _loading[index] = true;
        try
        {
            var path = Files[index].Path;
            var document = await PdfPagesDocument.OpenAsync(path, cancel).ConfigureAwait(true);
            var renderer = await PdfPagesDocument.OpenAsync(path, cancel).ConfigureAwait(true);
            _documents[index] = document;
            _renderers[index] = renderer;
        }
        catch (PdfToolException e) { _errors[index] = e.Message; }
        catch (OperationCanceledException) { }
        finally { _loading[index] = false; }
    }

    /// <summary>PDFs with page changes waiting to be saved: the filmstrip dots.</summary>
    public IReadOnlyList<bool> Edited => _documents.Select(d => d?.IsChanged == true).ToList();

    public int EditedCount => Edited.Count(e => e);

    /// <summary>Whether Save is offered. A PDF with every page deleted has nothing to save.</summary>
    public bool CanSave
    {
        get
        {
            if (!IsBatch) return Current is { Pages.Count: > 0, IsChanged: true };
            return EditedCount > 0 && !_documents.Any(d => d is { IsChanged: true, Pages.Count: 0 });
        }
    }

    /// <summary>"Save" for one PDF; "Save PDFs", "Save 1 PDF", "Save 3 PDFs" for several.</summary>
    public string PrimaryTitle => IsBatch ? PdfText.Counted("Save", EditedCount) : "Save";

    /// <summary>"12 pages · Drag to reorder, click to select" or "12 pages · 3 selected".</summary>
    public string Summary(int selected)
    {
        var count = PdfText.Pages(Current?.Pages.Count ?? 0);
        return selected == 0 ? $"{count} · Drag to reorder, click to select" : $"{count} · {selected} selected";
    }

    /// <summary>A copy of what Save will write, taken on the UI thread so the save can run on another. Null entries are left alone.</summary>
    public IReadOnlyList<PagesJob?> Snapshot() =>
        _documents.Select(d => d is { IsChanged: true } ? new PagesJob(d.Path, d.Pages.ToList()) : null).ToList();

    /// <summary>Saves the snapshot: each changed PDF becomes "Name Edited.pdf"; the rest are left alone and counted.</summary>
    public static PdfOutcome Save(IReadOnlyList<PagesJob?> snapshot, IProgress<double>? progress, CancellationToken cancel)
    {
        var saved = new List<string>();
        var unchanged = 0;
        for (var i = 0; i < snapshot.Count; i++)
        {
            cancel.ThrowIfCancellationRequested();
            if (snapshot[i] is { } job) saved.Add(Outcomes.Wait(PdfPagesEditor.SaveAsync(job.Path, job.Pages, cancel)));
            else unchanged++;
            progress?.Report((i + 1.0) / snapshot.Count);
        }
        return new PdfOutcome { Saved = saved, Unchanged = unchanged, SavedLabel = "edited", UnchangedLabel = "unchanged" };
    }
}

/// <summary>One PDF's pages as they will be written.</summary>
public sealed record PagesJob(string Path, IReadOnlyList<PdfPageEdit> Pages);
