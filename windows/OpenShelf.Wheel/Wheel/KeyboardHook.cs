using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OpenShelf.Wheel;

/// <summary>
/// A low-level keyboard hook installed only while a wheel is open. The handler runs inside the hook callback on the
/// installing (UI) thread and must be quick; it returns true to swallow the key.
/// </summary>
internal sealed class KeyboardHook : IDisposable
{
    private readonly Native.LowLevelKeyboardProc _proc;
    private nint _hook;

    /// <summary>(virtual key, isDown) → swallow?</summary>
    public Func<int, bool, bool>? Handler { get; set; }

    public bool IsInstalled => _hook != 0;

    public KeyboardHook() => _proc = Callback;

    public void Install()
    {
        if (_hook != 0) return;
        using var process = Process.GetCurrentProcess();
        using var module = process.MainModule;
        _hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _proc, Native.GetModuleHandle(module?.ModuleName), 0);
        if (_hook == 0) Log.Write($"SetWindowsHookEx failed: {Marshal.GetLastWin32Error()}");
        else Log.Write("keyboard hook installed");
    }

    public void Uninstall()
    {
        if (_hook == 0) return;
        Native.UnhookWindowsHookEx(_hook);
        _hook = 0;
        Log.Write("keyboard hook removed");
    }

    private nint Callback(int code, nint wParam, nint lParam)
    {
        if (code >= 0)
        {
            try
            {
                var message = (int)wParam;
                var down = message is Native.WM_KEYDOWN or Native.WM_SYSKEYDOWN;
                var up = message is Native.WM_KEYUP or Native.WM_SYSKEYUP;
                if ((down || up) && Handler is { } handler)
                {
                    var data = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
                    if (handler((int)data.vkCode, down)) return 1;
                }
            }
            catch (Exception ex) { Log.Error("keyboard hook", ex); }
        }
        return Native.CallNextHookEx(_hook, code, wParam, lParam);
    }

    public void Dispose() => Uninstall();
}
