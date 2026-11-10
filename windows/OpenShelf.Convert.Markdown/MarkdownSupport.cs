using System.Globalization;
using System.IO.Compression;
using System.Text;
using ImageMagick;

namespace OpenShelf.Convert.Markdown;

/// <summary>The paper a printed or Word page is laid out for.</summary>
public enum PaperSize { A4, Letter }

public static class PaperSizes
{
    /// <summary>Countries that print on US Letter; everywhere else uses A4.</summary>
    private static readonly HashSet<string> LetterRegions = ["US", "CA", "MX", "PH", "CL", "CO", "VE", "CR", "GT", "PA", "DO", "PR", "SV", "HN", "NI", "BO"];

    public static PaperSize ForRegion(string? twoLetterRegion) =>
        twoLetterRegion is not null && LetterRegions.Contains(twoLetterRegion.ToUpperInvariant()) ? PaperSize.Letter : PaperSize.A4;

    /// <summary>A4 or Letter according to the user's region (Windows region setting).</summary>
    public static PaperSize ForCurrentRegion()
    {
        try { return ForRegion(RegionInfo.CurrentRegion.TwoLetterISORegionName); }
        catch (Exception) { return PaperSize.Letter; }
    }
}

/// <summary>Finds the pictures a Markdown file refers to. Port of MarkdownImages.</summary>
public static class MarkdownImages
{
    /// <summary>The file <paramref name="source"/> names, relative to the Markdown file's folder, if it is on this PC. Web
    /// addresses give null: OpenShelf never downloads them.</summary>
    public static string? LocalPath(string source, string? baseFolder)
    {
        string path;
        try { path = Uri.UnescapeDataString(source); }
        catch (UriFormatException) { path = source; }

        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme.Length > 1)
        {
            if (!uri.IsFile) return null;
            return File.Exists(uri.LocalPath) ? uri.LocalPath : null;
        }
        string full;
        try
        {
            if (path.StartsWith("~/", StringComparison.Ordinal))
                full = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..].Replace('/', '\\'));
            else if (Path.IsPathRooted(path) && !path.StartsWith('/') && !path.StartsWith('\\')) full = path;
            else if (path.StartsWith('/') || path.StartsWith('\\'))
            {
                // A rooted path without a drive is relative to the Markdown file's drive.
                full = baseFolder is null ? path : Path.Combine(Path.GetPathRoot(Path.GetFullPath(baseFolder)) ?? "", path.TrimStart('/', '\\').Replace('/', '\\'));
            }
            else if (baseFolder is not null) full = Path.Combine(baseFolder, path.Replace('/', '\\'));
            else return null;
            full = Path.GetFullPath(full);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        return File.Exists(full) ? full : null;
    }

    /// <summary>Every picture the document shows, in order.</summary>
    public static List<string> Sources(MarkdownDocument document)
    {
        var sources = new List<string>();
        void Visit(MarkdownText text)
        {
            foreach (var inline in text)
            {
                switch (inline)
                {
                    case MarkdownInline.Image image: sources.Add(image.Source); Visit(image.Alt); break;
                    case MarkdownInline.Emphasis e: Visit(e.Content); break;
                    case MarkdownInline.Strong s: Visit(s.Content); break;
                    case MarkdownInline.Strikethrough s: Visit(s.Content); break;
                    case MarkdownInline.Link l: Visit(l.Content); break;
                }
            }
        }
        foreach (var block in document.Blocks.Concat(document.Footnotes.SelectMany(n => n)))
            block.Map(text => { Visit(text); return text; });
        return sources;
    }
}

/// <summary>A picture ready for Word: its bytes, file extension and size in EMU at its own resolution.</summary>
internal sealed record EmbeddablePicture(byte[] Data, string Extension, double Width, double Height);

internal static class Pictures
{
    /// <summary>PNG, JPEG and GIF go in as they are; anything else ImageMagick can read becomes PNG.</summary>
    public static EmbeddablePicture? Embeddable(string path)
    {
        try
        {
            var info = new MagickImageInfo(path);
            if (info.Width == 0 || info.Height == 0) return null;
            var dpi = 96.0;
            if (info.Density is { X: > 0 } density)
                dpi = density.Units == DensityUnit.PixelsPerCentimeter ? density.X * 2.54 : density.Units == DensityUnit.PixelsPerInch ? density.X : 96;
            var emuPerPixel = 914_400 / dpi;
            var width = info.Width * emuPerPixel;
            var height = info.Height * emuPerPixel;
            switch (info.Format)
            {
                case MagickFormat.Png or MagickFormat.Png8 or MagickFormat.Png24 or MagickFormat.Png32 or MagickFormat.Png48 or MagickFormat.Png64 or MagickFormat.Png00:
                    return new EmbeddablePicture(File.ReadAllBytes(path), "png", width, height);
                case MagickFormat.Jpeg or MagickFormat.Jpg:
                    return new EmbeddablePicture(File.ReadAllBytes(path), "jpeg", width, height);
                case MagickFormat.Gif or MagickFormat.Gif87:
                    return new EmbeddablePicture(File.ReadAllBytes(path), "gif", width, height);
                default:
                    using (var image = new MagickImage(path))
                    {
                        image.AutoOrient();
                        return new EmbeddablePicture(image.ToByteArray(MagickFormat.Png), "png", width, height);
                    }
            }
        }
        catch (MagickException) { return null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>A JPEG of the picture on white, since EPUB pictures are packed as JPEG.</summary>
    public static byte[]? Jpeg(string path)
    {
        try
        {
            using var image = new MagickImage(path);
            image.AutoOrient();
            image.BackgroundColor = MagickColors.White;
            image.Alpha(AlphaAction.Remove);
            image.Alpha(AlphaAction.Off);
            image.Format = MagickFormat.Jpeg;
            image.Quality = 88;
            return image.ToByteArray();
        }
        catch (MagickException) { return null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}

/// <summary>Builds a zip package (DOCX, EPUB) entry by entry. Entries in order of adding; <c>stored</c> entries are not compressed
/// (EPUB's <c>mimetype</c> must be first and stored).</summary>
internal sealed class PackageZip : IDisposable
{
    private readonly FileStream _file;
    private readonly ZipArchive _zip;

    public PackageZip(string path)
    {
        _file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        _zip = new ZipArchive(_file, ZipArchiveMode.Create);
    }

    public void Write(string name, string contents, bool stored = false) => Write(name, new UTF8Encoding(false).GetBytes(contents), stored);

    public void Write(string name, byte[] data, bool stored = false)
    {
        var entry = _zip.CreateEntry(name, stored ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
        using var stream = entry.Open();
        stream.Write(data);
    }

    public void Dispose()
    {
        _zip.Dispose();
        _file.Dispose();
    }
}
