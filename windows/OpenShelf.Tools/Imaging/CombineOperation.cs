using ImageMagick;
using OpenShelf.Core;

namespace OpenShelf.Tools.Imaging;

/// <summary>Combines images, in order, into one PDF, "Combined.pdf", next to the first file (one click, no editor). Each image
/// becomes a page the size of the image at its stated resolution. Port of PDFTools.combine for images.</summary>
public static class CombineOperation
{
    public const string BaseName = "Combined";

    /// <summary>Whether Combine applies: several files, all images.</summary>
    public static bool Applies(IReadOnlyList<string> paths) =>
        paths.Count > 1 && paths.All(p => OpenShelf.Convert.FileKinds.Of(p) == OpenShelf.Convert.FileKind.Image);

    public static string Run(IReadOnlyList<string> paths, IProgress<double>? progress = null, CancellationToken cancellation = default)
    {
        if (paths.Count == 0) throw new SaveException(SaveException.Reason.Failed, "There is nothing to combine.");
        using var pages = new MagickImageCollection();
        for (var i = 0; i < paths.Count; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            var image = ImageFiles.ReadUpright(paths[i]);
            if (image.HasAlpha)
            {
                image.BackgroundColor = MagickColors.White;
                image.Alpha(AlphaAction.Remove);
                image.Alpha(AlphaAction.Off);
            }
            var density = image.Density;
            var dpi = density.Units == DensityUnit.PixelsPerCentimeter ? density.X * 2.54 : density.X;
            if (density.Units == DensityUnit.Undefined || dpi < 1) dpi = 72;
            image.Density = new Density(dpi, dpi, DensityUnit.PixelsPerInch);
            image.Format = MagickFormat.Pdf;
            pages.Add(image);
            // Writing the combined file at the end takes the last share.
            progress?.Report((i + 1.0) / (paths.Count + 1));
        }
        var directory = Path.GetDirectoryName(Path.GetFullPath(paths[0])) ?? ".";
        for (var attempt = 0; ; attempt++)
        {
            var destination = OutputNaming.UniquePath(directory, BaseName, "pdf");
            SaveException.CheckWritable(destination);
            try
            {
                using (var stream = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    pages.Write(stream, MagickFormat.Pdf);
                progress?.Report(1);
                return destination;
            }
            catch (IOException) when (File.Exists(destination) && attempt < 8) { }
            catch (Exception e) when (e is not SaveException and not OperationCanceledException)
            {
                try { File.Delete(destination); } catch (IOException) { }
                throw e is MagickException ? SaveException.CouldNotSave(Path.GetFileName(destination)) : SaveException.From(e, destination);
            }
        }
    }
}
