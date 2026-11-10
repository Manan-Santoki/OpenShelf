using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using PDFtoImage;
using SkiaSharp;

namespace OpenShelf.Tools.Pdf;

/// <summary>One page in the Pages editor: where it came from and how much to turn it.</summary>
public sealed record PdfPageEdit
{
    /// <summary>Stable identity of this entry (survives reordering), for list binding.</summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>0-based index in the original document.</summary>
    public int SourceIndex { get; init; }

    /// <summary>Extra clockwise rotation in degrees: 0, 90, 180 or 270.</summary>
    public int Rotation { get; init; }
}

/// <summary>A rendered page image.</summary>
/// <param name="Width">Pixels.</param>
/// <param name="Height">Pixels.</param>
/// <param name="Bgra">Premultiplied BGRA, 4 bytes a pixel, rows packed (stride = Width * 4): hand it straight to a WPF
/// <c>BitmapSource.Create</c> with <c>PixelFormats.Pbgra32</c>.</param>
/// <param name="Png">The same image as PNG bytes.</param>
public sealed record PdfThumbnail(int Width, int Height, byte[] Bgra, byte[] Png)
{
    internal static PdfThumbnail From(SKBitmap source)
    {
        using var converted = source.ColorType == SKColorType.Bgra8888 && source.AlphaType == SKAlphaType.Premul
            ? source.Copy()
            : source.Copy(SKColorType.Bgra8888);
        using var image = SKImage.FromBitmap(converted);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        var pixels = converted.Bytes;
        if (converted.RowBytes != converted.Width * 4)
        {
            var packed = new byte[converted.Width * converted.Height * 4];
            for (var y = 0; y < converted.Height; y++)
                Buffer.BlockCopy(pixels, y * converted.RowBytes, packed, y * converted.Width * 4, converted.Width * 4);
            pixels = packed;
        }
        return new PdfThumbnail(converted.Width, converted.Height, pixels, data.ToArray());
    }
}

/// <summary>The model behind the Pages editor: one PDF's pages in their current order and rotation, with thumbnails,
/// undo/redo and Save. Not thread-safe for editing; call from one thread (the UI). Rendering and saving are async.</summary>
public sealed class PdfPagesDocument
{
    private readonly byte[] _bytes;
    private readonly List<PdfPageEdit> _pages;
    private readonly Stack<List<PdfPageEdit>> _undo = new();
    private readonly Stack<List<PdfPageEdit>> _redo = new();
    private readonly IReadOnlyList<(double Width, double Height)> _sizes;

    private PdfPagesDocument(string path, byte[] bytes, IReadOnlyList<(double, double)> sizes)
    {
        Path = path;
        _bytes = bytes;
        _sizes = sizes;
        SourcePageCount = sizes.Count;
        _pages = Enumerable.Range(0, sizes.Count).Select(i => new PdfPageEdit { SourceIndex = i }).ToList();
    }

    /// <summary>The PDF being edited.</summary>
    public string Path { get; }

    /// <summary>Pages in the original.</summary>
    public int SourcePageCount { get; }

    /// <summary>The pages as they would be saved, in order.</summary>
    public IReadOnlyList<PdfPageEdit> Pages => _pages;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>Raised after every edit, undo, redo or reset.</summary>
    public event EventHandler? Changed;

    /// <summary>Whether saving would make a different document: some page removed, moved or turned.</summary>
    public bool IsChanged => !PdfPagesEditor.KeepsDocument(_pages, SourcePageCount);

    /// <summary>Opens a PDF for editing.</summary>
    /// <exception cref="PdfToolException">Locked (password) or damaged.</exception>
    public static Task<PdfPagesDocument> OpenAsync(string path, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            using var check = PdfFiles.Open(path, PdfDocumentOpenMode.Import);
            if (check.PageCount == 0) throw PdfToolException.Unreadable(path);
            var bytes = File.ReadAllBytes(path);
            try
            {
                using var stream = new MemoryStream(bytes);
                var sizes = Conversion.GetPageSizes(stream).Select(s => ((double)s.Width, (double)s.Height)).ToList();
                return new PdfPagesDocument(path, bytes, sizes);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                throw PdfFiles.IsPassword(e) ? PdfToolException.Locked(path, e) : PdfToolException.Unreadable(path, e);
            }
        }, cancellationToken);

    // MARK: Editing

    private void Edit(Action change)
    {
        var before = _pages.ToList();
        change();
        if (_pages.SequenceEqual(before)) return;
        _undo.Push(before);
        _redo.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Moves the page at <paramref name="from"/> so it ends up at index <paramref name="to"/>.</summary>
    public void Move(int from, int to)
    {
        if (from < 0 || from >= _pages.Count) return;
        to = Math.Clamp(to, 0, _pages.Count - 1);
        Edit(() =>
        {
            var page = _pages[from];
            _pages.RemoveAt(from);
            _pages.Insert(to, page);
        });
    }

    /// <summary>Moves several pages (by current index) as a block, in their current order, to start at <paramref name="to"/>
    /// (an index in the list after the moved pages are taken out).</summary>
    public void MoveMany(IEnumerable<int> indices, int to)
    {
        var chosen = indices.Distinct().Where(i => i >= 0 && i < _pages.Count).OrderBy(i => i).ToList();
        if (chosen.Count == 0) return;
        Edit(() =>
        {
            var moving = chosen.Select(i => _pages[i]).ToList();
            foreach (var i in chosen.AsEnumerable().Reverse()) _pages.RemoveAt(i);
            _pages.InsertRange(Math.Clamp(to, 0, _pages.Count), moving);
        });
    }

    /// <summary>Turns the pages at <paramref name="indices"/> by <paramref name="degrees"/> clockwise (90, 180, 270 or negatives).</summary>
    public void Rotate(IEnumerable<int> indices, int degrees)
    {
        var set = indices.Where(i => i >= 0 && i < _pages.Count).ToHashSet();
        if (set.Count == 0) return;
        Edit(() =>
        {
            foreach (var i in set) _pages[i] = _pages[i] with { Rotation = (((_pages[i].Rotation + degrees) % 360) + 360) % 360 };
        });
    }

    /// <summary>Turns one page.</summary>
    public void Rotate(int index, int degrees) => Rotate([index], degrees);

    /// <summary>Turns every page.</summary>
    public void RotateAll(int degrees) => Rotate(Enumerable.Range(0, _pages.Count), degrees);

    /// <summary>Removes the pages at <paramref name="indices"/>. Removing every page is allowed here; saving then reports
    /// that there is nothing to save.</summary>
    public void Delete(IEnumerable<int> indices)
    {
        var set = indices.Where(i => i >= 0 && i < _pages.Count).ToHashSet();
        if (set.Count == 0) return;
        Edit(() =>
        {
            var kept = _pages.Where((_, i) => !set.Contains(i)).ToList();
            _pages.Clear();
            _pages.AddRange(kept);
        });
    }

    /// <summary>Removes one page.</summary>
    public void Delete(int index) => Delete([index]);

    /// <summary>Puts every page back where it was, unturned (can be undone).</summary>
    public void Reset() => Edit(() =>
    {
        _pages.Clear();
        _pages.AddRange(Enumerable.Range(0, SourcePageCount).Select(i => new PdfPageEdit { SourceIndex = i }));
    });

    public void Undo()
    {
        if (_undo.Count == 0) return;
        _redo.Push(_pages.ToList());
        Restore(_undo.Pop());
    }

    public void Redo()
    {
        if (_redo.Count == 0) return;
        _undo.Push(_pages.ToList());
        Restore(_redo.Pop());
    }

    private void Restore(List<PdfPageEdit> state)
    {
        _pages.Clear();
        _pages.AddRange(state);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // MARK: Thumbnails

    /// <summary>Renders the page at <paramref name="index"/> of the current order, turned as edited, on white, with its long
    /// edge <paramref name="maxPixelSize"/> pixels. Does not change the model, so it can run for many pages at once.</summary>
    public Task<PdfThumbnail> RenderThumbnailAsync(int index, int maxPixelSize = 200, CancellationToken cancellationToken = default)
    {
        PdfPageEdit edit;
        try { edit = _pages[index]; }
        catch (ArgumentOutOfRangeException) { throw new ArgumentOutOfRangeException(nameof(index)); }
        return Task.Run(() => RenderNow(edit, maxPixelSize, cancellationToken), cancellationToken);
    }

    private PdfThumbnail RenderNow(PdfPageEdit edit, int maxPixelSize, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var (w, h) = _sizes[edit.SourceIndex];
        var scale = maxPixelSize / Math.Max(Math.Max(w, h), 1);
        var width = Math.Max((int)Math.Round(w * scale), 1);
        var height = Math.Max((int)Math.Round(h * scale), 1);
        using var stream = new MemoryStream(_bytes, writable: false);
        using var bitmap = Conversion.ToImage(stream, edit.SourceIndex, leaveOpen: true, options: new RenderOptions(Width: width, Height: height));
        ct.ThrowIfCancellationRequested();
        using var turned = Turn(bitmap, edit.Rotation);
        return PdfThumbnail.From(turned);
    }

    internal static SKBitmap Turn(SKBitmap source, int degrees)
    {
        degrees = ((degrees % 360) + 360) % 360;
        if (degrees == 0) return source.Copy();
        var swap = degrees != 180;
        var result = new SKBitmap(swap ? source.Height : source.Width, swap ? source.Width : source.Height, source.ColorType, source.AlphaType);
        using var canvas = new SKCanvas(result);
        canvas.Clear(SKColors.White);
        canvas.Translate(result.Width / 2f, result.Height / 2f);
        canvas.RotateDegrees(degrees);
        canvas.Translate(-source.Width / 2f, -source.Height / 2f);
        canvas.DrawBitmap(source, 0, 0);
        return result;
    }

    // MARK: Saving

    /// <summary>Saves the pages in their current order and rotation as "Name Edited.pdf" beside the original, never overwriting.
    /// The file is built in a hidden staging file and moved into place only when complete.</summary>
    /// <returns>The full path of the new PDF.</returns>
    /// <exception cref="PdfToolException">No pages left, or the original can no longer be read.</exception>
    public Task<string> SaveAsync(CancellationToken cancellationToken = default)
    {
        var pages = _pages.ToList();
        return Task.Run(() => PdfPagesEditor.SaveNow(Path, pages, cancellationToken), cancellationToken);
    }
}

/// <summary>Page edits without the interactive model.</summary>
public static class PdfPagesEditor
{
    /// <summary>Suffix of an edited PDF: "Report Edited.pdf".</summary>
    public const string EditedSuffix = "Edited";

    /// <summary>Whether <paramref name="pages"/> are every page of the document, in order and unturned.</summary>
    public static bool KeepsDocument(IReadOnlyList<PdfPageEdit> pages, int pageCount) =>
        pages.Count == pageCount && pages.Select((p, i) => p.SourceIndex == i && p.Rotation % 360 == 0).All(x => x);

    /// <summary>Saves the given pages of <paramref name="path"/> as "Name Edited.pdf".</summary>
    public static Task<string> SaveAsync(string path, IReadOnlyList<PdfPageEdit> pages, CancellationToken cancellationToken = default) =>
        Task.Run(() => SaveNow(path, pages.ToList(), cancellationToken), cancellationToken);

    /// <summary>Saves each document's edits; documents never opened (null) or with every page where it was are left alone and counted.</summary>
    public static Task<PdfOutcome> SaveAsync(IReadOnlyList<PdfPagesDocument?> documents, IProgress<double>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            var saved = new List<string>();
            var unchanged = 0;
            for (var i = 0; i < documents.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (documents[i] is { IsChanged: true } document) saved.Add(SaveNow(document.Path, document.Pages.ToList(), cancellationToken));
                else unchanged++;
                progress?.Report((i + 1.0) / documents.Count);
            }
            return new PdfOutcome { Saved = saved, Unchanged = unchanged, SavedLabel = "edited", UnchangedLabel = "unchanged" };
        }, cancellationToken);

    internal static string SaveNow(string path, IReadOnlyList<PdfPageEdit> pages, CancellationToken ct)
    {
        if (pages.Count == 0) throw PdfToolException.NothingToSave();
        using var source = PdfFiles.Open(path, PdfDocumentOpenMode.Import);
        using var output = new PdfDocument();
        foreach (var edit in pages)
        {
            ct.ThrowIfCancellationRequested();
            if (edit.SourceIndex < 0 || edit.SourceIndex >= source.PageCount) continue;
            var page = output.AddPage(source.Pages[edit.SourceIndex]);
            page.Rotate = (((page.Rotate + edit.Rotation) % 360) + 360) % 360;
        }
        PdfSplitter.CopyInfo(source, output);
        return PdfFiles.SaveSuffixed(output, path, EditedSuffix, ct);
    }
}

/// <summary>Small page previews of whole files, for a filmstrip.</summary>
public static class PdfThumbnails
{
    /// <summary>The first page on white, its long edge at most <paramref name="maxPixelSize"/>. A grey placeholder stands in
    /// when the PDF cannot be read or is password-protected, so this never throws for a bad file.</summary>
    public static Task<PdfThumbnail> FirstPageAsync(string path, int maxPixelSize = 160, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var sizes = Conversion.GetPageSizes(stream, leaveOpen: true);
                if (sizes.Count > 0 && sizes[0].Width > 0 && sizes[0].Height > 0)
                {
                    var scale = maxPixelSize / Math.Max(sizes[0].Width, sizes[0].Height);
                    var options = new RenderOptions(Width: Math.Max((int)Math.Round(sizes[0].Width * scale), 1),
                        Height: Math.Max((int)Math.Round(sizes[0].Height * scale), 1));
                    stream.Position = 0;
                    using var bitmap = Conversion.ToImage(stream, 0, leaveOpen: true, options: options);
                    return PdfThumbnail.From(bitmap);
                }
            }
            catch (Exception e) when (e is not OperationCanceledException) { /* fall through to the placeholder */ }
            var width = (int)(maxPixelSize * 0.75);
            using var blank = new SKBitmap(width, maxPixelSize);
            blank.Erase(new SKColor(224, 224, 224));
            return PdfThumbnail.From(blank);
        }, cancellationToken);
}
