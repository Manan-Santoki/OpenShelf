namespace OpenShelf.Core;

/// <summary>
/// Picks where a converted or edited file is saved: next to the original, never
/// overwriting anything ("Photo.jpg", "Photo Cropped.jpg", "Photo Cropped 2.jpg").
/// MINIMAL placeholder with the final signature; the full port of
/// OutputNaming.swift (suffix de-duplication, 230-byte limit, leading dots) replaces the body.
/// </summary>
public static class OutputNaming
{
    /// <param name="sourcePath">The original file.</param>
    /// <param name="suffix">Appended to the base name, e.g. "Cropped"; null for none.</param>
    /// <param name="extension">Extension of the new file without the dot, or null for a folder.</param>
    public static string Destination(string sourcePath, string? suffix, string? extension)
    {
        var directory = Path.GetDirectoryName(sourcePath) ?? ".";
        var name = Path.GetFileNameWithoutExtension(sourcePath);
        if (!string.IsNullOrEmpty(suffix)) name += " " + suffix;
        return UniquePath(directory, name, extension);
    }

    public static string UniquePath(string directory, string baseName, string? extension)
    {
        string Candidate(string n) => Path.Combine(directory, extension is null ? n : n + "." + extension);
        var path = Candidate(baseName);
        for (var counter = 2; File.Exists(path) || Directory.Exists(path); counter++)
            path = Candidate($"{baseName} {counter}");
        return path;
    }
}
