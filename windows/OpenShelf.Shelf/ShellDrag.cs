using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace OpenShelf.Shelf;

/// <summary>Shell drag and drop: native drag-out with the Explorer drag image, and the drop-target helper.</summary>
internal static class ShellDrag
{
    static readonly Guid BhidDataObject = new("B8C0BD9F-ED24-455c-83E6-D5390C4FE8C4");
    static readonly Guid IidDataObject = new("0000010e-0000-0000-C000-000000000046");
    static bool _loggedDragFailure, _loggedDescription;

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
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern uint RegisterClipboardFormatW(string name);
    [DllImport("kernel32.dll")]
    static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll")]
    static extern IntPtr GlobalLock(IntPtr h);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GlobalUnlock(IntPtr h);
    [DllImport("kernel32.dll")]
    static extern IntPtr GlobalFree(IntPtr h);

    /// <summary>
    /// Runs the shell's own drag loop for these files (standard Explorer drag image).
    /// Returns false when any shell call failed so the caller can fall back. Effect: 1 copy, 2 move, 4 link.
    /// </summary>
    public static bool TryDrag(IntPtr hwnd, IReadOnlyList<string> paths, out uint effect)
    {
        effect = 0;
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

            int h3 = SHDoDragDrop(hwnd, data, IntPtr.Zero, 1 | 2 | 4, out effect);
            if (h3 < 0) throw new InvalidOperationException($"SHDoDragDrop 0x{h3:X8}");
            return true;
        }
        catch (Exception ex)
        {
            if (!_loggedDragFailure) { _loggedDragFailure = true; Log.Error("Shell drag failed, using WPF drag", ex); }
            return false;
        }
        finally
        {
            if (data != IntPtr.Zero) Marshal.Release(data);
            if (array != null) Marshal.ReleaseComObject(array);
            foreach (var p in pidls) CoTaskMemFree(p);
        }
    }

    /// <summary>Writes a "DropDescription" ("Add to Shelf", copy icon) onto the dragged data object. Never throws.</summary>
    public static void SetDropDescription(object data, string message, string insert)
    {
        IntPtr h = IntPtr.Zero;
        try
        {
            if (data is not IDataObject com) return;
            const int size = 4 + 260 * 2 + 260 * 2;
            h = GlobalAlloc(0x42, (UIntPtr)size);
            if (h == IntPtr.Zero) return;
            var p = GlobalLock(h);
            Marshal.WriteInt32(p, 0, 1); // DROPIMAGE_COPY
            WriteString(p + 4, message);
            WriteString(p + 4 + 520, insert);
            GlobalUnlock(h);

            var fmt = new FORMATETC
            {
                cfFormat = unchecked((short)RegisterClipboardFormatW("DropDescription")),
                ptd = IntPtr.Zero, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = -1, tymed = TYMED.TYMED_HGLOBAL,
            };
            var medium = new STGMEDIUM { tymed = TYMED.TYMED_HGLOBAL, unionmember = h, pUnkForRelease = null };
            com.SetData(ref fmt, ref medium, true);
            h = IntPtr.Zero; // ownership passed
        }
        catch (Exception ex)
        {
            if (!_loggedDescription) { _loggedDescription = true; Log.Info("DropDescription not set: " + ex.Message); }
        }
        finally
        {
            if (h != IntPtr.Zero) GlobalFree(h);
        }
    }

    static void WriteString(IntPtr dest, string s)
    {
        if (s.Length > 259) s = s[..259];
        for (int i = 0; i < s.Length; i++) Marshal.WriteInt16(dest, i * 2, s[i]);
        Marshal.WriteInt16(dest, s.Length * 2, 0);
    }
}

/// <summary>Wraps the shell IDropTargetHelper so the source's drag image stays visible over a window.</summary>
internal sealed class DropTargetHelper : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X, Y; }

    [ComImport, Guid("4657278B-411B-11D2-839A-00C04FD918D0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDropTargetHelper
    {
        [PreserveSig] int DragEnter(IntPtr hwndTarget, [MarshalAs(UnmanagedType.Interface)] IDataObject data, ref POINT pt, int effect);
        [PreserveSig] int DragLeave();
        [PreserveSig] int DragOver(ref POINT pt, int effect);
        [PreserveSig] int Drop([MarshalAs(UnmanagedType.Interface)] IDataObject data, ref POINT pt, int effect);
        [PreserveSig] int Show([MarshalAs(UnmanagedType.Bool)] bool show);
    }

    IDropTargetHelper? _helper;
    bool _tried;

    IDropTargetHelper? Get()
    {
        if (_tried) return _helper;
        _tried = true;
        try
        {
            var type = Type.GetTypeFromCLSID(new Guid("4657278A-411B-11D2-839A-00C04FD918D0"));
            if (type != null) _helper = (IDropTargetHelper?)Activator.CreateInstance(type);
        }
        catch (Exception ex) { Log.Info("IDropTargetHelper unavailable: " + ex.Message); }
        return _helper;
    }

    public void DragEnter(IntPtr hwnd, object data, int x, int y, int effect)
    {
        try { if (data is IDataObject d && Get() is { } h) { var pt = new POINT { X = x, Y = y }; h.DragEnter(hwnd, d, ref pt, effect); } }
        catch (Exception) { }
    }

    public void DragOver(int x, int y, int effect)
    {
        try { if (_helper != null) { var pt = new POINT { X = x, Y = y }; _helper.DragOver(ref pt, effect); } }
        catch (Exception) { }
    }

    public void DragLeave()
    {
        try { _helper?.DragLeave(); } catch (Exception) { }
    }

    public void Drop(object data, int x, int y, int effect)
    {
        try { if (data is IDataObject d && _helper != null) { var pt = new POINT { X = x, Y = y }; _helper.Drop(d, ref pt, effect); } }
        catch (Exception) { }
    }

    public void Dispose()
    {
        try { if (_helper != null) Marshal.ReleaseComObject(_helper); } catch (Exception) { }
        _helper = null;
    }
}
