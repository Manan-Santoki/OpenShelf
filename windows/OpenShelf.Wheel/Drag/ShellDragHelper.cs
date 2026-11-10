using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace OpenShelf.Wheel;

/// <summary>
/// Keeps the source's drag thumbnail alive while the pointer is over our window (WPF drop targets don't
/// talk to the shell's drag-image manager) and sets the "DropDescription" text beside it.
/// Every call is guarded and logged once; nothing here may ever break a drag.
/// </summary>
internal sealed class ShellDragHelper
{
    public const int ImageNone = 0, ImageCopy = 1;

    [ComImport, Guid("4657278B-411B-11D2-839A-00C04FD918D0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDropTargetHelper
    {
        [PreserveSig] int DragEnter(nint hwndTarget, IDataObject data, ref Native.POINT pt, int effect);
        [PreserveSig] int DragLeave();
        [PreserveSig] int DragOver(ref Native.POINT pt, int effect);
        [PreserveSig] int Drop(IDataObject data, ref Native.POINT pt, int effect);
        [PreserveSig] int Show([MarshalAs(UnmanagedType.Bool)] bool show);
    }

    private static readonly Guid ClsidDragDropHelper = new("4657278A-411B-11D2-839A-00C04FD918D0");

    private IDropTargetHelper? _helper;
    private bool _created;
    private readonly HashSet<string> _logged = [];
    private (int type, string message, string insert)? _lastDescription;
    private static ushort _dropDescriptionFormat;

    private IDropTargetHelper? Helper
    {
        get
        {
            if (_created) return _helper;
            _created = true;
            try { _helper = (IDropTargetHelper?)Activator.CreateInstance(Type.GetTypeFromCLSID(ClsidDragDropHelper)!); }
            catch (Exception ex) { Failed("create", ex); }
            return _helper;
        }
    }

    private void Failed(string what, Exception ex)
    {
        if (_logged.Add(what)) Log.Write($"drag helper {what} failed: {ex.Message}");
    }

    private void Check(string what, int hr)
    {
        if (hr < 0 && _logged.Add(what)) Log.Write($"drag helper {what} returned 0x{hr:X8}");
    }

    private static IDataObject? Com(object data) => data as IDataObject;

    public void Enter(nint hwnd, object data, int effect)
    {
        _lastDescription = null;
        try
        {
            if (Helper is not { } h || Com(data) is not { } com) return;
            var pt = Cursor();
            Check("DragEnter", h.DragEnter(hwnd, com, ref pt, effect));
        }
        catch (Exception ex) { Failed("DragEnter", ex); }
    }

    public void Over(int effect)
    {
        try
        {
            if (Helper is not { } h) return;
            var pt = Cursor();
            Check("DragOver", h.DragOver(ref pt, effect));
        }
        catch (Exception ex) { Failed("DragOver", ex); }
    }

    public void Leave()
    {
        try { if (Helper is { } h) Check("DragLeave", h.DragLeave()); }
        catch (Exception ex) { Failed("DragLeave", ex); }
    }

    public void Drop(object data, int effect)
    {
        try
        {
            if (Helper is not { } h || Com(data) is not { } com) return;
            var pt = Cursor();
            Check("Drop", h.Drop(com, ref pt, effect));
        }
        catch (Exception ex) { Failed("Drop", ex); }
    }

    public void Show(bool show)
    {
        try { if (Helper is { } h) Check("Show", h.Show(show)); }
        catch (Exception ex) { Failed("Show", ex); }
    }

    private static Native.POINT Cursor()
    {
        Native.GetCursorPos(out var p);
        return p;
    }

    // ---- DropDescription ----

    private const int MessageChars = 260;
    private const uint GMEM_MOVEABLE = 0x2, GMEM_ZEROINIT = 0x40;

    /// <summary>Sets the label next to the drag image. Type: <see cref="ImageNone"/> clears it, <see cref="ImageCopy"/> shows the copy badge.</summary>
    public void Describe(object data, int type, string message, string insert)
    {
        if (_lastDescription is { } last && last == (type, message, insert)) return;
        nint mem = 0;
        try
        {
            if (Com(data) is not { } com) return;
            if (_dropDescriptionFormat == 0) _dropDescriptionFormat = (ushort)RegisterClipboardFormatW("DropDescription");
            if (_dropDescriptionFormat == 0) return;

            mem = GlobalAlloc(GMEM_MOVEABLE | GMEM_ZEROINIT, 4 + 2 * MessageChars * 2);
            var ptr = GlobalLock(mem);
            if (ptr == 0) return;
            try
            {
                Marshal.WriteInt32(ptr, type);
                WriteText(ptr + 4, message);
                WriteText(ptr + 4 + MessageChars * 2, insert);
            }
            finally { GlobalUnlock(mem); }

            var format = new FORMATETC { cfFormat = unchecked((short)_dropDescriptionFormat), dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = -1, ptd = 0, tymed = TYMED.TYMED_HGLOBAL };
            var medium = new STGMEDIUM { tymed = TYMED.TYMED_HGLOBAL, unionmember = mem, pUnkForRelease = null };
            com.SetData(ref format, ref medium, true);
            mem = 0; // owned by the data object now
            _lastDescription = (type, message, insert);
        }
        catch (Exception ex)
        {
            Failed("DropDescription", ex);
            _lastDescription = (type, message, insert); // don't retry every mouse move
            if (mem != 0) GlobalFree(mem);
        }
    }

    private static void WriteText(nint at, string text)
    {
        var chars = (text.Length >= MessageChars ? text[..(MessageChars - 1)] : text).ToCharArray();
        if (chars.Length > 0) Marshal.Copy(chars, 0, at, chars.Length);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormatW(string name);
    [DllImport("kernel32.dll")] private static extern nint GlobalAlloc(uint flags, nuint bytes);
    [DllImport("kernel32.dll")] private static extern nint GlobalLock(nint mem);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GlobalUnlock(nint mem);
    [DllImport("kernel32.dll")] private static extern nint GlobalFree(nint mem);
}
