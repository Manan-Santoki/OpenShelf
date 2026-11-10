using OpenShelf.Convert;

namespace OpenShelf.Convert.Tests;

/// <summary>A fresh folder deleted afterwards.</summary>
public sealed class TempFolder : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "openshelf-convert-" + Guid.NewGuid().ToString("N")[..10]);

    public TempFolder() => Directory.CreateDirectory(Path);

    public string File(string name, string? text = null)
    {
        var path = System.IO.Path.Combine(Path, name);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, text ?? "");
        return path;
    }

    public string Combine(string name) => System.IO.Path.Combine(Path, name);

    public string[] Names() => Directory.GetFileSystemEntries(Path).Select(p => System.IO.Path.GetFileName(p)!).OrderBy(n => n).ToArray();

    public void Dispose()
    {
        try { Directory.Delete(Path, true); } catch (IOException) { }
    }
}

public static class Fakes
{
    /// <summary>No tools at all (WebP off too, as on a Mac without cwebp).</summary>
    public static ExternalTools None => ExternalTools.None with { CanEncodeWebP = false };

    /// <summary>ffmpeg and LibreOffice "installed"; the placeholder paths are never run.</summary>
    public static ExternalTools All => new() { Ffmpeg = @"C:\fake\ffmpeg.exe", Cwebp = @"C:\fake\cwebp.exe", LibreOffice = @"C:\fake\soffice.exe" };
}
