using System.Runtime.InteropServices;
using OpenShelf.Core;

namespace OpenShelf.Wheel;

internal static class Native
{
    public const int VK_LBUTTON = 0x01, VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C;
    public const int SM_CXDRAG = 68, SM_CYDRAG = 69;
    public const int GWL_EXSTYLE = -20;
    public const long WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000, WS_EX_TRANSPARENT = 0x20;
    public const uint SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;
    public static readonly nint HWND_TOPMOST = new(-1);
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(nint hWnd, nint after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern nint GetWindowLongPtr(nint hWnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] public static extern nint SetWindowLongPtr(nint hWnd, int index, nint value);
    [DllImport("user32.dll")] public static extern nint MonitorFromPoint(POINT pt, uint flags);
    [DllImport("shcore.dll")] public static extern int GetDpiForMonitor(nint monitor, int type, out uint dpiX, out uint dpiY);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO { public int Size; public RECT Monitor, Work; public int Flags; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool GetMonitorInfo(nint monitor, ref MONITORINFO info);

    /// <summary>Work area (physical px) of the monitor nearest the point, or null.</summary>
    public static RECT? WorkAreaAt(ScreenPoint pt)
    {
        try
        {
            var mon = MonitorFromPoint(new POINT { X = pt.X, Y = pt.Y }, MONITOR_DEFAULTTONEAREST);
            var info = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
            if (mon != 0 && GetMonitorInfo(mon, ref info)) return info.Work;
        }
        catch (Exception ex) { Log.Error("WorkAreaAt", ex); }
        return null;
    }

    // ---- keyboard hook, hot key, foreground ----
    public const int WH_KEYBOARD_LL = 13;
    public const int WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_SYSKEYDOWN = 0x104, WM_SYSKEYUP = 0x105, WM_HOTKEY = 0x312;
    public const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_WIN = 8, MOD_NOREPEAT = 0x4000;
    public static readonly nint HWND_MESSAGE = new(-3);

    public delegate nint LowLevelKeyboardProc(int code, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public nint dwExtraInfo; }

    [DllImport("user32.dll", SetLastError = true)] public static extern nint SetWindowsHookEx(int idHook, LowLevelKeyboardProc proc, nint module, uint threadId);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] public static extern nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool RegisterHotKey(nint hWnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool UnregisterHotKey(nint hWnd, int id);
    [DllImport("user32.dll")] public static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool SetForegroundWindow(nint hWnd);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool AttachThreadInput(uint from, uint to, [MarshalAs(UnmanagedType.Bool)] bool attach);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(nint hWnd, System.Text.StringBuilder sb, int max);

    public static string ClassNameOf(nint hwnd)
    {
        var sb = new System.Text.StringBuilder(256);
        return GetClassName(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : "";
    }

    /// <summary>Brings a window to the foreground even when the process did not just receive input.</summary>
    public static void ForceForeground(nint hwnd)
    {
        try
        {
            var fg = GetForegroundWindow();
            var fgThread = fg == 0 ? 0 : GetWindowThreadProcessId(fg, out _);
            var me = GetCurrentThreadId();
            var attached = fgThread != 0 && fgThread != me && AttachThreadInput(me, fgThread, true);
            SetForegroundWindow(hwnd);
            if (attached) AttachThreadInput(me, fgThread, false);
        }
        catch (Exception ex) { Log.Error("ForceForeground", ex); }
    }

    public static Modifiers HeldModifiers()
    {
        var m = Modifiers.None;
        if (IsDown(VK_SHIFT)) m |= Modifiers.Shift;
        if (IsDown(VK_CONTROL)) m |= Modifiers.Control;
        if (IsDown(VK_MENU)) m |= Modifiers.Alt;
        if (IsDown(VK_LWIN) || IsDown(VK_RWIN)) m |= Modifiers.Win;
        return m;
    }

    public static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    public static ScreenPoint CursorPos()
    {
        GetCursorPos(out var p);
        return new ScreenPoint(p.X, p.Y);
    }

    /// <summary>Scale factor (1.0 = 96 dpi) of the monitor under the physical point.</summary>
    public static double ScaleAt(ScreenPoint pt)
    {
        try
        {
            var mon = MonitorFromPoint(new POINT { X = pt.X, Y = pt.Y }, MONITOR_DEFAULTTONEAREST);
            if (mon != 0 && GetDpiForMonitor(mon, 0, out var dx, out _) == 0 && dx > 0) return dx / 96.0;
        }
        catch (Exception ex) { Log.Error("ScaleAt", ex); }
        return 1.0;
    }
}
