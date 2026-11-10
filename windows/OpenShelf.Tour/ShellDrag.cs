using System.Runtime.InteropServices;

namespace OpenShelf.Tour;

/// <summary>
/// Starts a real shell file drag out of the tour window: the same call the shelf uses (OpenShelf.Shelf's ShellDrag),
/// so Explorer's own drag image shows and the wheels open exactly as they do for a drag from Explorer.
/// </summary>
internal static class ShellDrag
{
    static readonly Guid BhidDataObject = new("B8C0BD9F-ED24-455c-83E6-D5390C4FE8C4");
    static readonly Guid IidDataObject = new("0000010e-0000-0000-C000-000000000046");

    [ComImport, Guid("B63EA76D-1F85-456F-A19C-48159EFA858B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItemArray
    {
        [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetPropertyStore(int flags, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetPropertyDescriptionList(IntPtr keyType, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetAttributes(int flags, int mask, out int attrs);
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetItemAt(int index, out IntPtr item);
        [PreserveSig] int EnumItems(out IntPtr enumerator);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHParseDisplayName(string name, IntPtr pbc, out IntPtr pidl, uint sfgaoIn, out uint sfgaoOut);
    [DllImport("shell32.dll")]
    static extern int SHCreateShellItemArrayFromIDLists(uint cidl, IntPtr[] rgpidl, out IShellItemArray ppsiItemArray);
    [DllImport("shell32.dll")]
    static extern int SHDoDragDrop(IntPtr hwnd, IntPtr pdtobj, IntPtr pdsrc, uint okEffects, out uint effect);
    [DllImport("ole32.dll")]
    static extern void CoTaskMemFree(IntPtr p);

    public const uint Copy = 1, Move = 2, Link = 4;

    /// <summary>
    /// Runs the shell's drag loop for these files. Practice files are only ever offered as Copy or Link, so a drop
    /// target can never move them away. Returns false when a shell call failed, with <paramref name="error"/> set.
    /// </summary>
    public static bool TryDrag(IntPtr hwnd, IReadOnlyList<string> paths, out uint effect, out string? error)
    {
        effect = 0;
        error = null;
        var pidls = new List<IntPtr>();
        IShellItemArray? array = null;
        IntPtr data = IntPtr.Zero;
        try
        {
            foreach (var path in paths)
            {
                int hr = SHParseDisplayName(path, IntPtr.Zero, out var pidl, 0, out _);
                if (hr < 0 || pidl == IntPtr.Zero) throw new InvalidOperationException($"SHParseDisplayName 0x{hr:X8} for {path}");
                pidls.Add(pidl);
            }
            if (pidls.Count == 0) return false;

            int h1 = SHCreateShellItemArrayFromIDLists((uint)pidls.Count, pidls.ToArray(), out array);
            if (h1 < 0 || array == null) throw new InvalidOperationException($"SHCreateShellItemArrayFromIDLists 0x{h1:X8}");

            var bhid = BhidDataObject;
            var iid = IidDataObject;
            int h2 = array.BindToHandler(IntPtr.Zero, ref bhid, ref iid, out data);
            if (h2 < 0 || data == IntPtr.Zero) throw new InvalidOperationException($"BindToHandler 0x{h2:X8}");

            int h3 = SHDoDragDrop(hwnd, data, IntPtr.Zero, Copy | Link, out effect);
            if (h3 < 0) throw new InvalidOperationException($"SHDoDragDrop 0x{h3:X8}");
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            if (data != IntPtr.Zero) Marshal.Release(data);
            if (array != null) Marshal.ReleaseComObject(array);
            foreach (var p in pidls) CoTaskMemFree(p);
        }
    }
}
