using System.IO.Compression;
using SkiaSharp;

namespace OpenShelf.Convert.Documents;

/// <summary>Pages, Numbers and Keynote documents (zip files, or folder packages): through LibreOffice when it is installed (it reads
/// all three), falling back to the preview saved inside when it fails; without it, the Quick Look preview
/// (<c>QuickLook/Preview.pdf</c>, <c>preview.jpg</c>, <c>QuickLook/Thumbnail.jpg</c>) gives PDF, JPG and PNG. The PAGES, NUMBERS and
/// KEY outputs do not exist on Windows. Port of IWorkConversion.swift and QuickLookConversion's preview path.</summary>
public sealed class IWorkEngine : IConversionEngine
{
    private readonly OfficeConversion _office;

    public IWorkEngine(ToolRunner? runner = null) => _office = new OfficeConversion(runner);

    public bool CanConvert(FileKind kind, OutputFormat format) => false;

    public bool CanConvert(ConversionRequest request) =>
        IWorkFormats.FromPath(request.SourcePath) is { } format && format.LibreOfficeOutputs().Contains(request.Format);

    /// <summary>The picture of a document inside its package.</summary>
    public static byte[]? PreviewData(string path, IWorkPreview preview)
    {
        path = FileKinds.ResolveLink(path);
        try
        {
            if (Directory.Exists(path))
                return File.ReadAllBytes(Path.Combine(path, preview.Path.Replace('/', Path.DirectorySeparatorChar)));
            using var zip = ZipFile.OpenRead(path);
            return Zips.ReadEntry(zip, preview.Path);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<string>> ConvertAsync(ConversionRequest request, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var source = FileKinds.ResolveLink(request.SourcePath);
        var iWork = IWorkFormats.FromPath(source) ?? throw ConversionException.Unsupported("this file");
        var format = request.Format;
        var preview = IWorkPreview.Find(source);
        var label = iWork == IWorkFormat.Keynote ? "Slide" : "Page";
        var soffice = request.ToolsOrDefault.LibreOffice;

        if (soffice is not null && iWork.LibreOfficeOutputs().Contains(format))
        {
            try
            {
                return await _office.ConvertAsync(soffice, source, format, request.SourceStem, request.OutputPath, label,
                    request.Options.ImageQuality, progress, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // LibreOffice reads most, not all, of these; the preview may still do.
                if (preview is null || !preview.Outputs.Contains(format)) throw;
            }
        }
        if (preview is null || !preview.Outputs.Contains(format))
        {
            if (soffice is null)
                throw ConversionException.Failed($"“{Path.GetFileName(source)}” has no preview inside to convert. Install LibreOffice in Settings → Extra Formats to convert {iWork.AppName()} files.");
            throw ConversionException.Unsupported($"{iWork.AppName()} to {format.Title()}");
        }
        return await Task.Run(() => FromPreview(source, preview, format, request, label, progress, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Saves the preview as a PDF or image named after the source: the PDF as it is, its pages as pictures, or the JPEG
    /// preview as JPEG or PNG.</summary>
    private static IReadOnlyList<string> FromPreview(string source, IWorkPreview preview, OutputFormat format, ConversionRequest request, string label,
        IProgress<double>? progress, CancellationToken ct)
    {
        var data = PreviewData(source, preview);
        if (data is null || data.Length == 0)
            throw ConversionException.Failed($"The preview inside “{Path.GetFileName(source)}” couldn’t be read.");
        var stem = request.SourceStem;
        if (preview.IsPdf)
        {
            if (format == OutputFormat.Pdf)
            {
                var destination = request.OutputPath($"{stem}.pdf");
                File.WriteAllBytes(destination, data);
                progress?.Report(1);
                return [destination];
            }
            var workspace = Path.Combine(Path.GetTempPath(), "openshelf-iwork-" + Guid.NewGuid().ToString("N")[..12]);
            Directory.CreateDirectory(workspace);
            try
            {
                var extracted = Path.Combine(workspace, stem + ".pdf");
                File.WriteAllBytes(extracted, data);
                return PdfConversion.Convert(extracted, format, stem, request.OutputPath, label, request.Options.ImageQuality, progress, ct);
            }
            finally
            {
                Zips.DeleteFolderQuietly(workspace);
            }
        }
        var target = request.OutputPath($"{stem}.{format.FileExtension()}");
        try
        {
            if (format == OutputFormat.Jpg) File.WriteAllBytes(target, data);
            else if (format == OutputFormat.Png)
            {
                using var bitmap = SKBitmap.Decode(data) ?? throw ConversionException.Failed($"The preview inside “{Path.GetFileName(source)}” couldn’t be read.");
                File.WriteAllBytes(target, Pictures.EncodePng(bitmap));
            }
            else throw ConversionException.Unsupported($"{IWorkFormats.FromPath(source)!.Value.AppName()} to {format.Title()}");
        }
        catch
        {
            Zips.DeleteQuietly(target);
            throw;
        }
        progress?.Report(1);
        return [target];
    }
}
