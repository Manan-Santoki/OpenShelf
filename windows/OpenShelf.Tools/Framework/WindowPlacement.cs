using System.Runtime.InteropServices;
using System.Windows;
using OpenShelf.Core;

namespace OpenShelf.Tools;

/// <summary>Where an editor window goes: centred on the drop point, kept inside the monitor's work area. Positions are physical pixels.</summary>
public static class WindowPlacement
{
    /// <summary>The window frame (physical pixels) of the given size centred on <paramref name="pointer"/> and kept inside <paramref name="workArea"/>.</summary>
    public static Rect Frame(ScreenPoint pointer, double width, double height, Rect workArea)
    {
        width = Math.Min(width, workArea.Width);
        height = Math.Min(height, workArea.Height);
        var x = Math.Clamp(pointer.X - width / 2, workArea.Left, workArea.Right - width);
        var y = Math.Clamp(pointer.Y - height / 2, workArea.Top, workArea.Bottom - height);
        return new Rect(Math.Round(x), Math.Round(y), Math.Round(width), Math.Round(height));
    }

    /// <summary>The work area (physical pixels) and DPI scale (1 = 96 dpi) of the monitor nearest <paramref name="pointer"/>.</summary>
    public static (Rect WorkArea, double Dpi) MonitorAt(ScreenPoint pointer)
    {
        var monitor = MonitorFromPoint(new POINT { X = pointer.X, Y = pointer.Y }, 2);   // MONITOR_DEFAULTTONEAREST
        var info = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
            return (new Rect(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight), 1);
        var scale = 1.0;
        try
        {
            if (GetDpiForMonitor(monitor, 0, out var dpiX, out _) == 0 && dpiX > 0) scale = dpiX / 96.0;
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
        return (new Rect(info.Work.Left, info.Work.Top, info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top), scale);
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int Size; public RECT Monitor; public RECT Work; public int Flags; }

    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT point, int flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
}
