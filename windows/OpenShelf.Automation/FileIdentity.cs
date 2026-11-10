using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace OpenShelf.Automation;

/// <summary>Windows file ids and the "still being written" check.</summary>
internal static class FileIdentity
{
    /// <summary>The NTFS/ReFS file index, which survives renames; null when it cannot be read.</summary>
    public static ulong? IdOf(string path)
    {
        try
        {
            // Access 0 reads attributes only and never conflicts with a writer.
            using var handle = CreateFileW(path, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
            if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var info)) return null;
            return ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
        }
        catch { return null; }
    }

    /// <summary>True when another process has the file open for writing (a download in progress).</summary>
    public static bool IsBusy(string path)
    {
        try
        {
            // Asking for FileShare.Read fails while any writer holds the file; readers are not disturbed.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return false;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return false; } // not ours to wait for
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME { public uint Low; public uint High; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public FILETIME CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleFileInformation info);
}
