using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;

namespace OpenShelf.Shelf;

/// <summary>An NTFS file id plus the volume it lives on: finds a file again after it was moved or renamed.</summary>
/// <param name="Id">Hex. 32 characters for a 128-bit id (FileIdInfo), 16 for the 64-bit legacy index.</param>
internal readonly record struct FileIdentity(string Id, ulong Volume)
{
    const uint FileReadAttributes = 0x80;
    const uint ShareAll = 7;
    const uint OpenExisting = 3;
    const uint BackupSemantics = 0x02000000;
    const int FileIdInfoClass = 18;

    [StructLayout(LayoutKind.Sequential)]
    struct FileTime { public uint Low, High; }

    [StructLayout(LayoutKind.Sequential)]
    struct ByHandleFileInformation
    {
        public uint Attributes;
        public FileTime Created, Accessed, Written;
        public uint VolumeSerial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }

    [StructLayout(LayoutKind.Explicit)]
    struct FileIdDescriptor
    {
        [FieldOffset(0)] public uint Size;
        [FieldOffset(4)] public int Type;      // 0 FileIdType, 2 ExtendedFileIdType
        [FieldOffset(8)] public long Low;
        [FieldOffset(16)] public long High;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetFileInformationByHandleEx(SafeFileHandle h, int infoClass, IntPtr buffer, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetFileInformationByHandle(SafeFileHandle h, out ByHandleFileInformation info);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern SafeFileHandle OpenFileById(SafeFileHandle volumeHint, ref FileIdDescriptor id, uint access, uint share, IntPtr security, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint GetFinalPathNameByHandleW(SafeFileHandle h, StringBuilder buffer, uint length, uint flags);

    static SafeFileHandle Open(string path) =>
        CreateFileW(path, FileReadAttributes, ShareAll, IntPtr.Zero, OpenExisting, BackupSemantics, IntPtr.Zero);

    /// <summary>The identity of the file or folder at <paramref name="path"/>, or null when it can't be read.</summary>
    public static FileIdentity? Get(string path)
    {
        try
        {
            using var h = Open(path);
            return h.IsInvalid ? null : Get(h);
        }
        catch (Exception) { return null; }
    }

    static FileIdentity? Get(SafeFileHandle h)
    {
        var buffer = Marshal.AllocHGlobal(24);
        try
        {
            if (GetFileInformationByHandleEx(h, FileIdInfoClass, buffer, 24))
            {
                var volume = (ulong)Marshal.ReadInt64(buffer, 0);
                var id = new byte[16];
                Marshal.Copy(buffer + 8, id, 0, 16);
                return new FileIdentity(Convert.ToHexString(id), volume);
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }

        if (!GetFileInformationByHandle(h, out var info)) return null;
        ulong index = ((ulong)info.IndexHigh << 32) | info.IndexLow;
        return new FileIdentity(index.ToString("X16"), info.VolumeSerial);
    }

    /// <summary>Where the file with this identity is now, or null. Tries the volume of <paramref name="hintPath"/> first,
    /// then every other ready volume (a removable drive may have a new letter).</summary>
    public string? Locate(string hintPath)
    {
        try
        {
            var roots = new List<string>();
            var hintRoot = Path.GetPathRoot(hintPath);
            if (!string.IsNullOrEmpty(hintRoot)) roots.Add(hintRoot);
            foreach (var d in DriveInfo.GetDrives())
                if (d.IsReady && !roots.Contains(d.RootDirectory.FullName, StringComparer.OrdinalIgnoreCase)) roots.Add(d.RootDirectory.FullName);

            foreach (var root in roots)
            {
                using var volume = Open(root);
                if (volume.IsInvalid) continue;
                var volumeId = Get(volume);
                if (volumeId == null || volumeId.Value.Volume != Volume) continue;
                var found = OpenById(volume);
                if (found != null) return found;
            }
        }
        catch (Exception ex) { Log.Info("Locate by file id failed: " + ex.Message); }
        return null;
    }

    string? OpenById(SafeFileHandle volume)
    {
        var bytes = Convert.FromHexString(Id);
        var desc = new FileIdDescriptor { Size = (uint)Marshal.SizeOf<FileIdDescriptor>() };
        if (bytes.Length == 16)
        {
            desc.Type = 2;
            desc.Low = BitConverter.ToInt64(bytes, 0);
            desc.High = BitConverter.ToInt64(bytes, 8);
        }
        else
        {
            desc.Type = 0;
            desc.Low = (long)Convert.ToUInt64(Id, 16);
        }
        using var h = OpenFileById(volume, ref desc, FileReadAttributes, ShareAll, IntPtr.Zero, BackupSemantics);
        if (h.IsInvalid) return null;

        var sb = new StringBuilder(1024);
        uint n = GetFinalPathNameByHandleW(h, sb, (uint)sb.Capacity, 0 /* VOLUME_NAME_DOS */);
        if (n == 0 || n >= sb.Capacity) return null;
        var path = sb.ToString();
        if (path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) path = @"\\" + path[8..];
        else if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) path = path[4..];
        // A file in the Recycle Bin has been deleted as far as a shelf is concerned.
        if (path.Contains(@"\$Recycle.Bin\", StringComparison.OrdinalIgnoreCase)) return null;
        return path;
    }
}

/// <summary>One file as saved.</summary>
internal sealed class SavedItem
{
    public string Path { get; set; } = "";
    public string? FileId { get; set; }
    public ulong? Volume { get; set; }

    [JsonIgnore]
    public FileIdentity? Identity => FileId != null && Volume != null ? new FileIdentity(FileId, Volume.Value) : null;
}

/// <summary>A shelf as saved between launches. The frame is the card's rectangle in physical pixels.</summary>
internal sealed class SavedShelf
{
    public List<SavedItem> Items { get; set; } = new();
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public string? DockedEdge { get; set; }       // "left" | "right" | null
    public bool Expanded { get; set; }
    public string ViewMode { get; set; } = "grid"; // "grid" | "list"
}

/// <summary>Reads and writes open shelves to %APPDATA%\OpenShelf\shelves.json.</summary>
internal static class ShelfStore
{
    sealed class FileModel
    {
        public int Version { get; set; } = 1;
        public List<SavedShelf> Shelves { get; set; } = new();
    }

    static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenShelf", "shelves.json");

    public static void Save(string path, IEnumerable<SavedShelf> shelves)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(new FileModel { Shelves = shelves.ToList() }, Options);
        var temp = path + ".tmp";
        File.WriteAllText(temp, json, new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Saved shelves, or none when nothing was saved or the file is unreadable.</summary>
    public static List<SavedShelf> Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new();
            var model = JsonSerializer.Deserialize<FileModel>(File.ReadAllText(path), Options);
            return model?.Shelves ?? new();
        }
        catch (Exception ex)
        {
            Log.Error("Reading shelves.json", ex);
            return new();
        }
    }

    public static SavedItem Capture(string path) =>
        FileIdentity.Get(path) is { } id
            ? new SavedItem { Path = path, FileId = id.Id, Volume = id.Volume }
            : new SavedItem { Path = path };

    /// <summary>The file's current location: found by its id first, so a file moved or renamed on the same volume is
    /// still found, then by the saved path. Null when it is gone.</summary>
    public static string? Resolve(SavedItem item)
    {
        bool Exists(string p) => File.Exists(p) || Directory.Exists(p);
        var saved = item.Identity;
        if (saved != null)
        {
            if (Exists(item.Path))
            {
                var current = FileIdentity.Get(item.Path);
                if (current == null || current.Value == saved.Value) return item.Path;   // still the same file
            }
            var moved = saved.Value.Locate(item.Path);
            if (moved != null && Exists(moved)) return moved;
        }
        return Exists(item.Path) ? item.Path : null;
    }
}
