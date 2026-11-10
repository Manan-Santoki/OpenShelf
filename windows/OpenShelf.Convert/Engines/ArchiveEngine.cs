using System.Formats.Tar;
using System.IO.Compression;

namespace OpenShelf.Convert;

/// <summary>ZIP, TAR and TAR.GZ creation and archive extraction. Port of ArchiveConversion.swift using
/// System.IO.Compression and System.Formats.Tar; 7z, rar, bz2 and xz are unpacked by Windows' own bsdtar
/// (%SystemRoot%\System32\tar.exe). Extraction refuses entries that would land outside the target folder ("zip slip").</summary>
public sealed class ArchiveEngine : IConversionEngine
{
    public bool CanConvert(FileKind kind, OutputFormat format) =>
        format is OutputFormat.Zip or OutputFormat.Tar or OutputFormat.Tgz || (kind == FileKind.Archive && format == OutputFormat.Extract);

    /// <summary>Files macOS keeps beside real ones: Finder's folder settings, AppleDouble forks and the folder archives carry
    /// them in. Clutter anywhere else, so archives are made without them and extracting ignores them.</summary>
    public static bool IsMacMetadata(string name) => name == ".DS_Store" || name == "__MACOSX" || name.StartsWith("._", StringComparison.Ordinal);

    public async Task<IReadOnlyList<string>> ConvertAsync(ConversionRequest request, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        switch (request.Format)
        {
            case OutputFormat.Zip:
                return [await Create(request, "zip", progress, cancellationToken, (entries, destination, report, ct) => WriteZip(entries, destination, report, ct))];
            case OutputFormat.Tar:
                return [await Create(request, "tar", progress, cancellationToken, (entries, destination, report, ct) => WriteTar(entries, destination, false, report, ct))];
            case OutputFormat.Tgz:
                return [await Create(request, "tar.gz", progress, cancellationToken, (entries, destination, report, ct) => WriteTar(entries, destination, true, report, ct))];
            case OutputFormat.Extract:
                var result = await Task.Run(() => Extract(request, cancellationToken), cancellationToken).ConfigureAwait(false);
                progress?.Report(1);
                return [result];
            default:
                throw ConversionException.Unsupported($"archive to {request.Format.Title()}");
        }
    }

    // ---------------------------------------------------------------- creating

    /// <summary>One item in an archive: where it is, and its name inside.</summary>
    public sealed record Entry(string FullPath, string Name, bool IsDirectory);

    private delegate void Writer(IReadOnlyList<Entry> entries, string destination, Action<double> report, CancellationToken ct);

    private static async Task<string> Create(ConversionRequest request, string extension, IProgress<double>? progress, CancellationToken ct, Writer write)
    {
        var sources = request.AllSources;
        if (sources.Count == 0) throw ConversionException.Failed("Nothing to compress.");
        // One file or folder becomes "<name>.<ext>"; several become "Archive.<ext>".
        var baseName = sources.Count == 1 ? Path.GetFileName(sources[0].TrimEnd('\\', '/')) : "Archive";
        var destination = request.OutputPath($"{baseName}.{extension}");
        try
        {
            await Task.Run(() =>
            {
                var entries = ArchiveEntries(sources, ct);
                write(entries, destination, fraction => progress?.Report(fraction), ct);
            }, ct).ConfigureAwait(false);
        }
        catch
        {
            try { File.Delete(destination); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            throw;
        }
        progress?.Report(1);
        return destination;
    }

    /// <summary>The files and folders in <paramref name="sources"/> with their names inside the archive, Mac metadata left out.
    /// Linked folders are not followed.</summary>
    public static IReadOnlyList<Entry> ArchiveEntries(IReadOnlyList<string> sources, CancellationToken ct = default)
    {
        var entries = new List<Entry>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in sources)
        {
            var source = raw.TrimEnd('\\', '/');
            var name = Path.GetFileName(source);
            // Items from different folders all go in at the top, where a second one with the same name would replace the first.
            if (!names.Add(name))
                throw ConversionException.Failed($"Two of the items are named “{name}”, so they can’t go in one archive. Rename one and try again.");
            if (Directory.Exists(source))
            {
                entries.Add(new Entry(source, name, true));
                Walk(source, name, entries, ct);
            }
            else if (File.Exists(source))
            {
                entries.Add(new Entry(source, name, false));
            }
            else
            {
                throw ConversionException.Unreadable(source);
            }
        }
        return entries;
    }

    private static void Walk(string directory, string prefix, List<Entry> entries, CancellationToken ct)
    {
        foreach (var item in new DirectoryInfo(directory).EnumerateFileSystemInfos().OrderBy(i => i.Name, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            if (IsMacMetadata(item.Name)) continue;
            var name = prefix + "/" + item.Name;
            if (item is DirectoryInfo sub)
            {
                if (sub.LinkTarget is not null) continue; // junctions and symlinks could loop
                entries.Add(new Entry(sub.FullName, name, true));
                Walk(sub.FullName, name, entries, ct);
            }
            else
            {
                entries.Add(new Entry(item.FullName, name, false));
            }
        }
    }

    private static void WriteZip(IReadOnlyList<Entry> entries, string destination, Action<double> report, CancellationToken ct)
    {
        var total = Math.Max(entries.Where(e => !e.IsDirectory).Sum(e => new FileInfo(e.FullPath).Length), 1);
        long done = 0;
        using var file = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            if (entry.IsDirectory)
            {
                var folder = zip.CreateEntry(entry.Name + "/");
                folder.LastWriteTime = ClampZipTime(Directory.GetLastWriteTime(entry.FullPath));
                continue;
            }
            var item = zip.CreateEntry(entry.Name, CompressionLevel.Optimal);
            item.LastWriteTime = ClampZipTime(File.GetLastWriteTime(entry.FullPath));
            using var output = item.Open();
            using var input = new FileStream(entry.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[81920];
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                output.Write(buffer, 0, read);
                done += read;
                report(Math.Min((double)done / total, 0.99));
            }
        }
    }

    /// <summary>Zip timestamps run 1980 to 2107.</summary>
    private static DateTimeOffset ClampZipTime(DateTime time) =>
        time.Year < 1980 ? new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero) : time.Year > 2107 ? new DateTimeOffset(2107, 12, 31, 0, 0, 0, TimeSpan.Zero) : new DateTimeOffset(time);

    private static void WriteTar(IReadOnlyList<Entry> entries, string destination, bool gzip, Action<double> report, CancellationToken ct)
    {
        using var file = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using Stream stream = gzip ? new GZipStream(file, CompressionLevel.Optimal) : file;
        using var tar = new TarWriter(stream, TarEntryFormat.Pax, leaveOpen: false);
        for (var i = 0; i < entries.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            tar.WriteEntry(entries[i].FullPath, entries[i].IsDirectory ? entries[i].Name + "/" : entries[i].Name);
            report(Math.Min((i + 1.0) / entries.Count, 0.99));
        }
    }

    // ---------------------------------------------------------------- extracting

    /// <summary>Extracts next to the archive. An archive holding a single folder gives that folder; anything else goes in a
    /// new folder named after the archive. A lone .gz (not a tarball) is one compressed file, not an archive.</summary>
    private static string Extract(ConversionRequest request, CancellationToken ct)
    {
        var source = request.SourcePath;
        var fileName = Path.GetFileName(source);
        var lower = fileName.ToLowerInvariant();
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        if (baseName.EndsWith(".tar", StringComparison.OrdinalIgnoreCase)) baseName = baseName[..^4];

        if (lower.EndsWith(".gz") && !lower.EndsWith(".tar.gz"))
        {
            var destination = request.OutputPath(baseName);
            try
            {
                using var input = new GZipStream(File.OpenRead(source), CompressionMode.Decompress);
                using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write);
                input.CopyTo(output);
            }
            catch
            {
                try { File.Delete(destination); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                throw;
            }
            return destination;
        }

        // Unpack into a hidden folder beside the result (same volume, so it moves into place instantly).
        var parent = request.OutputDirectory ?? request.SourceDirectory;
        var work = Path.Combine(parent, ".os-x-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        try
        {
            var target = Path.Combine(work, "out");
            Directory.CreateDirectory(target);
            if (lower.EndsWith(".zip")) ExtractZip(source, target, ct);
            else if (lower.EndsWith(".tar") || lower.EndsWith(".tar.gz") || lower.EndsWith(".tgz")) ExtractTar(source, target, lower.EndsWith(".tar") ? false : true, ct);
            else ExtractWithBsdtar(source, target, ct);
            RemoveEscapingLinks(target);
            return PlaceExtracted(target, baseName, request);
        }
        finally
        {
            try { Directory.Delete(work, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>The full path of <paramref name="entryName"/> inside <paramref name="root"/>, or null when it would land outside it.</summary>
    public static string? SafeTarget(string root, string entryName)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd('\\') + Path.DirectorySeparatorChar;
        var normalized = entryName.Replace('/', '\\');
        if (Path.IsPathRooted(normalized) || normalized.Contains(':')) return null;
        var full = Path.GetFullPath(Path.Combine(fullRoot, normalized));
        return full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    private static bool IsClutter(string entryName) =>
        entryName.Split('/', '\\', StringSplitOptions.RemoveEmptyEntries).Any(IsMacMetadata);

    private static void ExtractZip(string source, string target, CancellationToken ct)
    {
        using var zip = ZipFile.OpenRead(source);
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (IsClutter(entry.FullName)) continue;
            var path = SafeTarget(target, entry.FullName)
                ?? throw ConversionException.Failed($"“{Path.GetFileName(source)}” contains files that would be written outside the destination folder, so it was not opened.");
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                Directory.CreateDirectory(path);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            try
            {
                entry.ExtractToFile(path, overwrite: true);
            }
            catch (InvalidDataException) when (IsEncrypted(entry))
            {
                throw ConversionException.Failed($"“{Path.GetFileName(source)}” is password-protected; OpenShelf can’t open encrypted archives.");
            }
        }
    }

    private static bool IsEncrypted(ZipArchiveEntry entry) => entry.IsEncrypted;

    private static void ExtractTar(string source, string target, bool gzip, CancellationToken ct)
    {
        using var file = File.OpenRead(source);
        using Stream stream = gzip ? new GZipStream(file, CompressionMode.Decompress) : file;
        using var reader = new TarReader(stream);
        TarEntry? entry;
        while ((entry = reader.GetNextEntry()) is not null)
        {
            ct.ThrowIfCancellationRequested();
            if (IsClutter(entry.Name)) continue;
            var path = SafeTarget(target, entry.Name)
                ?? throw ConversionException.Failed($"“{Path.GetFileName(source)}” contains files that would be written outside the destination folder, so it was not opened.");
            switch (entry.EntryType)
            {
                case TarEntryType.Directory:
                    Directory.CreateDirectory(path);
                    break;
                case TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile:
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    entry.ExtractToFile(path, overwrite: true);
                    break;
                // Links, devices and pax headers are skipped: a link could point anywhere.
            }
        }
    }

    /// <summary>7z, rar, bz2, xz and friends through bsdtar, which ships with Windows 10 and 11.</summary>
    private static void ExtractWithBsdtar(string source, string target, CancellationToken ct)
    {
        var tar = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "tar.exe");
        if (!File.Exists(tar)) throw new MissingToolException("tar");
        ProcessRunner.RunAsync(tar, ["-xf", source, "-C", target], new ProcessOptions { Timeout = TimeSpan.FromMinutes(30) }, ct).GetAwaiter().GetResult();
    }

    /// <summary>Deletes links that point outside <paramref name="root"/>, which an archive could use to reach other files.</summary>
    private static void RemoveEscapingLinks(string root)
    {
        var full = Path.GetFullPath(root).TrimEnd('\\') + Path.DirectorySeparatorChar;
        foreach (var item in new DirectoryInfo(root).EnumerateFileSystemInfos("*", SearchOption.AllDirectories).ToList())
        {
            if (item.LinkTarget is null) continue;
            var resolved = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(item.FullName)!, item.LinkTarget));
            if (resolved.StartsWith(full, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                if (item is DirectoryInfo directory) directory.Delete();
                else item.Delete();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Moves what was unpacked into place. When it is one folder (Mac metadata aside), that folder goes there directly
    /// rather than nested as "Archive\Album"; otherwise the unpacked folder becomes a folder named after the archive.</summary>
    private static string PlaceExtracted(string target, string archiveName, ConversionRequest request)
    {
        foreach (var item in new DirectoryInfo(target).GetFileSystemInfos().Where(i => IsMacMetadata(i.Name)))
        {
            if (item is DirectoryInfo directory) directory.Delete(true);
            else item.Delete();
        }
        var contents = new DirectoryInfo(target).GetFileSystemInfos();
        if (contents.Length == 1 && contents[0] is DirectoryInfo folder && !folder.Name.StartsWith('.') && folder.LinkTarget is null)
        {
            var destination = request.OutputFolderPath(folder.Name);
            Directory.Move(folder.FullName, destination);
            return destination;
        }
        var named = request.OutputFolderPath(archiveName);
        Directory.Move(target, named);
        return named;
    }
}
