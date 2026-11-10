using OpenShelf.Core;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace OpenShelf.Tools.Pdf;

/// <summary>Combine: PDFs and images into one PDF, in the order given.</summary>
public static class PdfCombiner
{
    /// <summary>Combines PDFs and images, in order, into "Combined.pdf" next to the first file (or "Combined 2.pdf" and so on:
    /// nothing is overwritten). Images become one page each at their stated resolution; PDFs contribute all their pages.</summary>
    /// <param name="progress">Reported 0..1 as each file is added; writing the result takes the last share.</param>
    /// <returns>The full path of the new file.</returns>
    /// <exception cref="PdfToolException">A file is locked, damaged or not an image; nothing is written.</exception>
    public static Task<string> CombineAsync(IReadOnlyList<string> paths, IProgress<double>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            if (paths.Count == 0) throw PdfToolException.NothingToSave();
            using var output = new PdfDocument();
            for (var i = 0; i < paths.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = paths[i];
                if (PdfFiles.LooksLikePdf(path) || path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                {
                    using var source = PdfFiles.Open(path, PdfDocumentOpenMode.Import);
                    foreach (var page in source.Pages) output.AddPage(page);
                }
                else
                {
                    PdfImages.AddImagePage(output, path);
                }
                progress?.Report((i + 1.0) / (paths.Count + 1));
            }
            if (output.PageCount == 0) throw PdfToolException.NothingToSave();
            var directory = Path.GetDirectoryName(Path.GetFullPath(paths[0]))!;
            var bytes = PdfFiles.ToBytes(output);
            var result = PdfFiles.WriteNew(bytes, directory, () => OutputNaming.UniquePath(directory, "Combined", "pdf"), cancellationToken);
            progress?.Report(1);
            return result;
        }, cancellationToken);
}
