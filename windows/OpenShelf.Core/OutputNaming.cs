using System.Globalization;
using System.Text;

namespace OpenShelf.Core;

/// <summary>
/// Picks where a converted or edited file is saved: next to the original, never
/// overwriting anything ("Photo.jpg", "Photo Cropped.jpg", "Photo Cropped 2.jpg").
/// Port of OutputNaming.swift, plus Windows rules: no trailing dots or spaces in
/// a name, and an existing directory counts as a collision.
/// </summary>
public static class OutputNaming
{
    /// <summary>File names are limited to 255 units; keep well under that so a
    /// counter and temporary names still fit.</summary>
    public const int MaxNameBytes = 230;

    /// <summary>Room kept for a collision counter such as " 12".</summary>
    private const int CounterReserve = 4;

    /// <param name="sourcePath">The original file.</param>
    /// <param name="suffix">Appended to the base name, e.g. "Cropped". A name that already
    /// ends with it doesn't get it twice: compressing "Photo Compressed.jpg" saves
    /// "Photo Compressed 2.jpg", not "Photo Compressed Compressed.jpg".</param>
    /// <param name="extension">Extension of the new file without the dot, or null for a folder.</param>
    public static string Destination(string sourcePath, string? suffix, string? extension)
    {
        var directory = Path.GetDirectoryName(sourcePath) ?? ".";
        var original = Path.GetFileNameWithoutExtension(sourcePath);
        if (original.Length == 0) original = Path.GetFileName(sourcePath); // ".hidden" has no stem
        var (name, suffixPart) = SplitSuffix(suffix, VisibleName(original));
        var extensionPart = extension is null ? "" : "." + extension;
        // Shorten the original name, never the suffix, so "... Cropped" stays readable.
        var room = MaxNameBytes - CounterReserve - Utf8(suffixPart) - Utf8(extensionPart);
        var baseName = Truncated(name, room);
        return UniquePath(directory, baseName + suffixPart, extension);
    }

    /// <summary>Splits off the part of a new name that comes after the original: " suffix".
    /// A name that already ends with the suffix, in any letter case, keeps its own
    /// spelling of it; any other name gets it appended.</summary>
    public static (string Name, string Suffix) SplitSuffix(string? suffix, string name)
    {
        if (string.IsNullOrEmpty(suffix)) return (name, "");
        var suffixPart = " " + suffix;
        if (name.EndsWith(suffixPart, StringComparison.OrdinalIgnoreCase))
        {
            var cut = name.Length - suffixPart.Length;
            var stem = name[..cut];
            if (stem.Trim(' ', '\t').Length > 0) return (stem, name[cut..]);
        }
        return (name, suffixPart);
    }

    public static string UniquePath(string directory, string baseName, string? extension)
    {
        var extensionPart = extension is null ? "" : "." + extension;
        baseName = Truncated(VisibleName(baseName), MaxNameBytes - CounterReserve - Utf8(extensionPart));
        // Windows silently drops trailing dots and spaces, which would alias other names.
        baseName = baseName.TrimEnd('.', ' ');
        if (baseName.Length == 0) baseName = "Untitled";

        string Candidate(string n) => Path.Combine(directory, n + extensionPart);
        var path = Candidate(baseName);
        for (var counter = 2; File.Exists(path) || Directory.Exists(path); counter++)
            path = Candidate($"{baseName} {counter}");
        return path;
    }

    /// <summary>Drops leading dots, which would make the new file hidden.</summary>
    public static string VisibleName(string name)
    {
        var trimmed = name.TrimStart('.');
        return trimmed.Length == 0 ? "Untitled" : trimmed;
    }

    /// <summary><paramref name="name"/> cut to at most <paramref name="limit"/> bytes of UTF-8, on a character boundary.</summary>
    public static string Truncated(string name, int limit)
    {
        if (Utf8(name) <= limit) return name;
        var result = new StringBuilder();
        var bytes = 0;
        var elements = StringInfo.GetTextElementEnumerator(name);
        while (elements.MoveNext())
        {
            var element = (string)elements.Current;
            var size = Utf8(element);
            if (bytes + size > Math.Max(limit, 1)) break;
            result.Append(element);
            bytes += size;
        }
        var trimmed = result.ToString().Trim(' ', '\t');
        return trimmed.Length == 0 ? "Untitled" : trimmed;
    }

    private static int Utf8(string s) => Encoding.UTF8.GetByteCount(s);
}
