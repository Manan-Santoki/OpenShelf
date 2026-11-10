using System.Text;
using System.Text.RegularExpressions;

namespace OpenShelf.Convert;

/// <summary>Turns tool and file-system failures into messages a person can act on, without raw tool output or temporary
/// paths. Port of FailureMessage.swift.</summary>
public static class FailureMessage
{
    /// <summary>The error to show for a failed conversion of <paramref name="source"/>. Errors that are already user-facing
    /// pass through unchanged.</summary>
    public static Exception Describe(Exception error, string source)
    {
        if (error is OperationCanceledException or ConversionException or MissingToolException) return error;
        var kind = FileKinds.Of(source);
        var text = error is ProcessFailure failure ? failure.Detail : error.Message;

        if (Message(text, source, kind) is { } message) return ConversionException.Failed(message);
        var folder = Path.GetDirectoryName(Path.GetFullPath(source)) ?? ".";
        if (error is UnauthorizedAccessException || !IsWritable(folder))
            return ConversionException.Failed(PermissionMessage(folder));
        if (error is ProcessFailure process)
        {
            if (process.TimedOut || process.Crashed) return process;
            return ConversionException.Failed($"“{Path.GetFileName(source)}” couldn’t be converted: {Sanitized(text)}");
        }
        return error;
    }

    /// <summary>A plain explanation for known failure text from a tool or the system, or null when nothing specific applies.</summary>
    public static string? Message(string text, string source, FileKind kind)
    {
        var lowered = text.ToLowerInvariant();
        var name = Path.GetFileName(source);
        bool Mentions(params string[] phrases) => phrases.Any(lowered.Contains);

        if (Mentions("permission denied", "operation not permitted", "read-only file system", "cannot open output file",
                "don’t have permission", "don't have permission", "access is denied", "access to the path"))
            return PermissionMessage(Path.GetDirectoryName(Path.GetFullPath(source)) ?? ".");
        if (Mentions("no space left", "not enough space on the disk", "disk full", "there is not enough space"))
            return "There isn’t enough free space to save the result.";
        if (kind == FileKind.Archive)
        {
            if (Mentions("password", "encryption key", "passphrase", "encrypted"))
                return $"“{name}” is password-protected; OpenShelf can’t open encrypted archives.";
            if (Mentions("pkzip signature", "unrecognized archive format", "not in gzip format", "damaged", "truncated",
                    "central directory", "unexpected end", "invalid", "error reading", "corrupt", "magic number", "not a valid", "unrecognized or corrupt"))
                return $"“{name}” is damaged or isn’t an archive OpenShelf can open.";
        }
        if (Mentions("invalid data found", "moov atom not found", "could not find codec", "end of file", "unknown format"))
            return $"“{name}” is damaged or in a format OpenShelf can’t read.";
        return null;
    }

    public static string PermissionMessage(string folder) =>
        $"You don’t have permission to save in “{LeafName(folder)}”.";

    private static string LeafName(string folder)
    {
        var trimmed = folder.TrimEnd('\\', '/');
        var name = Path.GetFileName(trimmed);
        return name.Length == 0 ? trimmed : name;
    }

    /// <summary>Tool output with temporary paths replaced and the tool's name prefix removed.</summary>
    public static string Sanitized(string text)
    {
        // Windows temp paths (C:\...\Temp\x, or inside a hidden OpenShelf staging folder) and the Mac ones.
        var result = Regex.Replace(text, @"'?""?([A-Za-z]:\\[^\s'""]*\\(Temp|tmp)\\[^\s'""]*|[A-Za-z]:\\[^\s'""]*\\\.os-[^\s'""]*)""?'?", "a temporary file", RegexOptions.IgnoreCase);
        result = Regex.Replace(result, @"'?(/private)?/(var/folders|tmp|private/var/folders)/[^\s']*'?", "a temporary file");
        result = Regex.Replace(result, @"^[A-Za-z0-9_.-]+:\s*", "");
        result = result.Replace("Error!", "").Trim();
        return result.Length == 0 ? "the tool reported an error." : result;
    }

    /// <summary>Whether files can be created in <paramref name="folder"/>.</summary>
    public static bool IsWritable(string folder)
    {
        try
        {
            if (!Directory.Exists(folder)) return false;
            var probe = Path.Combine(folder, ".os-write-" + Guid.NewGuid().ToString("N")[..8]);
            using (File.Create(probe)) { }
            File.Delete(probe);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }
}

/// <summary>Decodes text files of unknown encoding: byte-order marks, UTF-8, UTF-16 without one, then Windows-1252.</summary>
public static class TextDecoding
{
    static TextDecoding() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static string? Decode(byte[] data)
    {
        if (data.Length == 0) return "";
        // A byte-order mark settles it.
        if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
            return new UTF8Encoding(false, true).GetString(data, 3, data.Length - 3);
        if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE) return Encoding.Unicode.GetString(data, 2, data.Length - 2);
        if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(data, 2, data.Length - 2);
        try
        {
            return new UTF8Encoding(false, true).GetString(data);
        }
        catch (DecoderFallbackException) { }
        // UTF-16 text is mostly zero bytes in every other position.
        if (data.Length >= 4 && data.Length % 2 == 0)
        {
            int evenZeros = 0, oddZeros = 0;
            for (var i = 0; i + 1 < data.Length; i += 2)
            {
                if (data[i] == 0) evenZeros++;
                if (data[i + 1] == 0) oddZeros++;
            }
            var pairs = data.Length / 2;
            if (oddZeros > pairs / 3 && evenZeros == 0) return Encoding.Unicode.GetString(data);
            if (evenZeros > pairs / 3 && oddZeros == 0) return Encoding.BigEndianUnicode.GetString(data);
        }
        // Windows-1252 maps nearly every byte, so it is a safe last resort.
        return Encoding.GetEncoding(1252).GetString(data);
    }

    public static string? Read(string path)
    {
        try { return Decode(File.ReadAllBytes(path)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
}
