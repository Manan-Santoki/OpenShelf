using OpenShelf.Core;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace OpenShelf.Tools.Pdf;

/// <summary>Opening PDFs with clear failures, and writing results through a hidden staging file.</summary>
internal static class PdfFiles
{
    /// <summary>Opens a PDF. Throws <see cref="PdfToolException"/> (Locked or Unreadable) instead of anything vague.</summary>
    public static PdfDocument Open(string path, PdfDocumentOpenMode mode)
    {
        if (!File.Exists(path)) throw PdfToolException.Unreadable(path);
        try
        {
            var memory = new MemoryStream();
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                file.CopyTo(memory);
            memory.Position = 0;
            var document = PdfReader.Open(memory, mode);
            if (document.PageCount < 0) throw PdfToolException.Unreadable(path);
            return document;
        }
        catch (PdfToolException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) when (IsPassword(e)) { throw PdfToolException.Locked(path, e); }
        catch (Exception e) { throw PdfToolException.Unreadable(path, e); }
    }

    public static bool IsPassword(Exception e)
    {
        for (var current = e; current is not null; current = current.InnerException!)
        {
            if (current.Message.Contains("password", StringComparison.OrdinalIgnoreCase) ||
                current.Message.Contains("encrypt", StringComparison.OrdinalIgnoreCase))
                return true;
            if (current.InnerException is null) break;
        }
        return false;
    }

    /// <summary>True when the bytes at the start of the file look like a PDF.</summary>
    public static bool LooksLikePdf(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[1024];
            var read = stream.Read(buffer, 0, buffer.Length);
            return System.Text.Encoding.Latin1.GetString(buffer, 0, read).Contains("%PDF-", StringComparison.Ordinal);
        }
        catch { return false; }
    }

    public static long Size(string path)
    {
        try { return new FileInfo(path).Length; }
        catch { return 0; }
    }

    /// <summary>Serialises <paramref name="document"/> to memory.</summary>
    public static byte[] ToBytes(PdfDocument document)
    {
        using var memory = new MemoryStream();
        document.Save(memory, closeStream: false);
        return memory.ToArray();
    }

    /// <summary>PDFsharp stamps its own name into Creator and Producer on every save. When the caller wants those empty, the
    /// stamp is overwritten with spaces of the same length, so no offset in the file moves and the fields read back blank.</summary>
    public static byte[] BlankWriterStamp(byte[] bytes)
    {
        var text = System.Text.Encoding.Latin1.GetString(bytes);
        var creator = System.Text.RegularExpressions.Regex.Replace(text, @"/Creator\(PDFsharp [\d.]+ \\\(www\.pdfsharp\.com\\\)\)",
            m => "/Creator(" + new string(' ', m.Length - 10) + ")");
        // A producer the user set is kept by PDFsharp as "PDFsharp x.y.z (Original: theirs)"; give it back.
        var blanked = System.Text.RegularExpressions.Regex.Replace(creator, @"/Producer\(PDFsharp [\d.]+(?: \\\(Original: (.*?)\\\))?\)",
            m => "/Producer(" + m.Groups[1].Value + new string(' ', m.Length - 11 - m.Groups[1].Length) + ")");
        // The XMP packet PDFsharp writes from the info carries the same stamps.
        blanked = System.Text.RegularExpressions.Regex.Replace(blanked, @"<xmp:CreatorTool>PDFsharp [\d.]+ \(www\.pdfsharp\.com\)</xmp:CreatorTool>",
            m => "<xmp:CreatorTool>" + new string(' ', m.Length - 17 - 18) + "</xmp:CreatorTool>");
        blanked = System.Text.RegularExpressions.Regex.Replace(blanked, @"<pdf:Producer>PDFsharp [\d.]+(?: \(Original: (.*?)\))?</pdf:Producer>",
            m => "<pdf:Producer>" + m.Groups[1].Value + new string(' ', m.Length - 14 - 15 - m.Groups[1].Length) + "</pdf:Producer>");
        return blanked == text ? bytes : System.Text.Encoding.Latin1.GetBytes(blanked);
    }

    /// <summary>Writes <paramref name="bytes"/> to a hidden staging file beside the destination folder, then moves it
    /// to a name that does not exist yet. A failure or cancellation leaves nothing behind.</summary>
    /// <param name="chooseDestination">Called at the moment of moving, so a file that appeared meanwhile is never overwritten.</param>
    public static string WriteNew(byte[] bytes, string directory, Func<string> chooseDestination, CancellationToken ct)
    {
        string? staging = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            staging = Path.Combine(directory, ".os-" + Guid.NewGuid().ToString("N")[..8] + ".tmp");
            using (var stream = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
            File.SetAttributes(staging, FileAttributes.Hidden);
            ct.ThrowIfCancellationRequested();
            for (var attempt = 0; ; attempt++)
            {
                var destination = chooseDestination();
                try
                {
                    File.Move(staging, destination, overwrite: false);
                    File.SetAttributes(destination, FileAttributes.Normal);
                    staging = null;
                    return destination;
                }
                catch (IOException) when (attempt < 20 && File.Exists(destination)) { /* raced; pick the next name */ }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new PdfToolException(PdfToolErrorKind.Unwritable,
                e is UnauthorizedAccessException
                    ? $"OpenShelf doesn’t have permission to save in “{FolderName(directory)}”."
                    : e.Message.Contains("space", StringComparison.OrdinalIgnoreCase)
                        ? "There isn’t enough free space to save the result."
                        : $"The result couldn’t be saved in “{FolderName(directory)}”.",
                directory, e);
        }
        finally
        {
            if (staging is not null) { try { File.Delete(staging); } catch { /* best effort */ } }
        }
    }

    private static string FolderName(string directory) => Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

    /// <summary>Saves <paramref name="document"/> next to <paramref name="source"/> as "name suffix.pdf".</summary>
    /// <param name="blankStamp">Erase the library's own Creator/Producer stamp (for tools whose job is to leave no metadata).</param>
    public static string SaveSuffixed(PdfDocument document, string source, string suffix, CancellationToken ct, bool blankStamp = false)
    {
        if (document.PageCount == 0) throw PdfToolException.NothingToSave();
        byte[] bytes;
        try { bytes = ToBytes(document); if (blankStamp) bytes = BlankWriterStamp(bytes); }
        catch (Exception e) when (e is not OperationCanceledException) { throw PdfToolException.Unwritable(source, e); }
        var directory = Path.GetDirectoryName(Path.GetFullPath(source))!;
        return WriteNew(bytes, directory, () => OutputNaming.Destination(source, suffix, "pdf"), ct);
    }
}
