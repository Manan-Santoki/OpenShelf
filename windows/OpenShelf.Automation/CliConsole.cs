using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace OpenShelf.Automation;

/// <summary>Prints command-line help and errors from a GUI-subsystem exe by attaching to the parent console.</summary>
public static class CliConsole
{
    /// <summary>Writes <paramref name="text"/> to the console that started the exe. False when there is none
    /// (started from Explorer or a shortcut), in which case the caller may show a toast or message instead.</summary>
    public static bool TryWrite(string text, bool error = false)
    {
        try
        {
            if (!AttachConsole(ATTACH_PARENT_PROCESS)) return false;
            using var handle = CreateFileW("CONOUT$", 0x40000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (handle.IsInvalid) return false;
            using var stream = new FileStream(handle, FileAccess.Write);
            var bytes = Encoding.UTF8.GetBytes(text.TrimEnd() + Environment.NewLine);
            stream.Write(bytes);
            stream.Flush();
            return true;
        }
        catch { return false; }
    }

    private const int ATTACH_PARENT_PROCESS = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
}
