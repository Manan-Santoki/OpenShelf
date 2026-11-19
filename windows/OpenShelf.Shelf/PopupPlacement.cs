using OpenShelf.Core;
using System.Reflection;
using System.Runtime.InteropServices;
using WF = System.Windows.Forms;

namespace OpenShelf.Shelf;

/// <summary>Places popups the way Windows places its own menus: <c>CalculatePopupWindowPosition</c> with an area to keep clear,
/// so a tray menu never covers the taskbar or the hidden-icons panel it was opened from, whichever edge the taskbar is on.</summary>
internal static class PopupPlacement
{
    const uint TPM_LEFTALIGN = 0x0000, TPM_BOTTOMALIGN = 0x0020, TPM_VERTICAL = 0x0040, TPM_WORKAREA = 0x10000;
    const uint GA_ROOT = 2;

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
            // Left edge at the pointer, bottom edge above it, moving vertically first: a native tray menu.
            if (CalculatePopupWindowPosition(ref point, ref size, TPM_LEFTALIGN | TPM_BOTTOMALIGN | TPM_VERTICAL | TPM_WORKAREA, ref exclude, out var result))
                return (result.Left, result.Top);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
        return null;
    }

    /// <summary>The area a tray menu must not cover: the whole window showing <paramref name="icon"/> (the taskbar, or the
    /// hidden-icons panel when the icon lives there), else the icon itself, else null.</summary>
    public static NativeRect? TrayArea(WF.NotifyIcon icon)
    {
        try
        {
            if (Identify(icon) is not { } id) return null;
            if (Shell_NotifyIconGetRect(ref id, out var rect) != 0) return null;
            var centre = new POINT { X = (rect.Left + rect.Right) / 2, Y = (rect.Top + rect.Bottom) / 2 };
            var root = Native.GetAncestor(WindowFromPoint(centre), GA_ROOT);
            if (root != IntPtr.Zero && Native.GetWindowRect(root, out var host) && Contains(host, rect) && Area(host) < Area(Screen(centre)) / 2)
                return host;
            return rect;
        }
        catch (Exception ex) { Log.Info("Tray icon rect: " + ex.Message); }
        return null;
    }

    static NOTIFYICONIDENTIFIER? Identify(WF.NotifyIcon icon)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = typeof(WF.NotifyIcon);
        var window = (type.GetField("_window", flags) ?? type.GetField("window", flags))?.GetValue(icon) as WF.NativeWindow;
        var idValue = (type.GetField("_id", flags) ?? type.GetField("id", flags))?.GetValue(icon);
        if (window is null || window.Handle == IntPtr.Zero || idValue is null) return null;
        return new NOTIFYICONIDENTIFIER
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONIDENTIFIER>(),
            hWnd = window.Handle,
            uID = Convert.ToUInt32(idValue),
        };
    }

    static bool Contains(NativeRect outer, NativeRect inner) =>
        inner.Left >= outer.Left && inner.Top >= outer.Top && inner.Right <= outer.Right && inner.Bottom <= outer.Bottom;

    static long Area(NativeRect r) => (long)Math.Max(0, r.Right - r.Left) * Math.Max(0, r.Bottom - r.Top);

    static NativeRect Screen(POINT p)
    {
        var bounds = WF.Screen.FromPoint(new System.Drawing.Point(p.X, p.Y)).Bounds;
        return new NativeRect { Left = bounds.Left, Top = bounds.Top, Right = bounds.Right, Bottom = bounds.Bottom };
    }
}
