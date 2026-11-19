using OpenShelf.Core;
using System.Reflection;
using System.Runtime.InteropServices;
using WF = System.Windows.Forms;

namespace OpenShelf.Shelf;

/// <summary>Places popups the way Windows places its own menus (<c>CalculatePopupWindowPosition</c>): at the anchor, flipped to the
/// side with room and kept inside the work area, whichever edge the taskbar is on.</summary>
internal static class PopupPlacement
{
    const uint TPM_LEFTALIGN = 0x0000, TPM_BOTTOMALIGN = 0x0020, TPM_VERTICAL = 0x0040, TPM_WORKAREA = 0x10000;

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int Cx, Cy; }

    [StructLayout(LayoutKind.Sequential)]
    struct NOTIFYICONIDENTIFIER
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public Guid guidItem;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CalculatePopupWindowPosition(ref POINT anchor, ref SIZE size, uint flags, ref NativeRect exclude, out NativeRect position);

    [DllImport("shell32.dll")]
    static extern int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER identifier, out NativeRect iconLocation);

    [DllImport("user32.dll")]
    static extern IntPtr WindowFromPoint(POINT point);

    /// <summary>Top-left (physical pixels) for a popup of <paramref name="width"/> x <paramref name="height"/> opened at
    /// <paramref name="anchor"/>, kept off <paramref name="exclude"/> and inside the work area. Null if Windows can't say.</summary>
    public static (int X, int Y)? Place(ScreenPoint anchor, int width, int height, NativeRect exclude)
    {
        try
        {
            var point = new POINT { X = anchor.X, Y = anchor.Y };
            var size = new SIZE { Cx = width, Cy = height };
            // Bottom-left corner at the anchor, as a tray icon's menu opens.
            if (CalculatePopupWindowPosition(ref point, ref size, TPM_LEFTALIGN | TPM_BOTTOMALIGN | TPM_WORKAREA, ref exclude, out var result))
                return (result.Left, result.Top);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
        return null;
    }

}
