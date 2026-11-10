using System.Text;

namespace OpenShelf.Tools;

/// <summary>Why a file could not be saved, in words a person can act on. Port of SaveError.swift.</summary>
public sealed class SaveException : Exception
{
    public enum Reason { NoPermission, FolderMissing, ReadOnlyVolume, DiskFull, NameTooLong, Failed }

    public SaveException(Reason kind, string message, Exception? inner = null) : base(message, inner) => Kind = kind;
    public Reason Kind { get; }
    /// <summary>Files written before the failure, so a batch that stopped halfway can still hand them over.</summary>
    public IReadOnlyList<string> SavedBefore { get; init; } = [];

    /// <summary>Names close to the limit fail in the encoder, which adds its own suffix while writing.</summary>
    public const int SafeNameLength = 240;

    public static SaveException NoPermission(string folder) => new(Reason.NoPermission, $"You don’t have permission to save in “{folder}”.");
    public static SaveException FolderMissing(string folder) => new(Reason.FolderMissing, $"The folder “{folder}” no longer exists.");
    public static SaveException ReadOnlyVolume(string folder) => new(Reason.ReadOnlyVolume, $"“{folder}” is on a disk that can’t be written to.");
    public static SaveException DiskFull() => new(Reason.DiskFull, "There isn’t enough free space on the disk.");
    public static SaveException NameTooLong(string file) => new(Reason.NameTooLong, $"The name “{file}” is too long. Rename the original to something shorter.");
    public static SaveException CouldNotSave(string file) => new(Reason.Failed, $"“{file}” couldn’t be saved.");

    /// <summary>Throws a specific error when <paramref name="path"/> cannot be written: its folder is missing or its name is too long.</summary>
    public static void CheckWritable(string path)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
        var folderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
        if (folderName.Length == 0) folderName = folder;
        if (!Directory.Exists(folder)) throw FolderMissing(folderName);
        var name = Path.GetFileName(path);
        if (Encoding.UTF8.GetByteCount(name) > SafeNameLength) throw NameTooLong(name);
    }

    /// <summary>Maps a write failure to a message; anything unexplained becomes "couldn’t be saved".</summary>
    public static SaveException From(Exception error, string path)
    {
        if (error is SaveException already) return already;
        var folder = Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ""));
        // ERROR_HANDLE_DISK_FULL (39) and ERROR_DISK_FULL (112) are the low 16 bits of the HRESULT.
        var code = error.HResult & 0xFFFF;
        switch (error)
        {
            case UnauthorizedAccessException:
                return NoPermission(folder);
            case DirectoryNotFoundException:
                return FolderMissing(folder);
            case PathTooLongException:
                return NameTooLong(Path.GetFileName(path));
            case IOException when code is 39 or 112:
                return DiskFull();
            case IOException when code == 19:   // ERROR_WRITE_PROTECT
                return ReadOnlyVolume(folder);
        }
        try { CheckWritable(path); }
        catch (SaveException e) { return e; }
        return new SaveException(Reason.Failed, $"“{Path.GetFileName(path)}” couldn’t be saved.", error);
    }
}
