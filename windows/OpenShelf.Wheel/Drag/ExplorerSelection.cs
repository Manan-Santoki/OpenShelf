using System.Reflection;
using System.Runtime.InteropServices;

namespace OpenShelf.Wheel;

/// <summary>The files selected in the foreground Explorer window or on the desktop (Shell.Application over COM). Call on the UI (STA) thread.</summary>
internal static class ExplorerSelection
{
    private const int SWC_DESKTOP = 8, SWFO_NEEDDISPATCH = 1;

    public static IReadOnlyList<string> Get()
    {
        object? shell = null, windows = null;
        try
        {
            var foreground = Native.GetForegroundWindow();
            if (foreground == 0) return [];
            var type = Type.GetTypeFromProgID("Shell.Application");
            if (type is null) return [];
            shell = Activator.CreateInstance(type);
            if (shell is null) return [];
            windows = type.InvokeMember("Windows", BindingFlags.InvokeMethod, null, shell, null);
            if (windows is null) return [];

            var cls = Native.ClassNameOf(foreground);
            if (cls is "Progman" or "WorkerW") return FromDesktop(windows);

            IReadOnlyList<string> fallback = [];
            foreach (var window in (System.Collections.IEnumerable)windows)
            {
                try
                {
                    dynamic w = window;
                    if ((long)w.HWND != (long)foreground) continue;
                    var paths = Selected(w.Document);
                    if (paths.Count > 0) return paths;
                    fallback = paths;
                }
                catch (Exception ex) { Log.Write($"explorer window skipped: {ex.Message}"); }
            }
            return fallback;
        }
        catch (Exception ex)
        {
            Log.Error("ExplorerSelection", ex);
            return [];
        }
        finally
        {
            if (windows is not null && Marshal.IsComObject(windows)) Marshal.ReleaseComObject(windows);
            if (shell is not null && Marshal.IsComObject(shell)) Marshal.ReleaseComObject(shell);
        }
    }

    private static IReadOnlyList<string> FromDesktop(object windows)
    {
        // FindWindowSW(ref loc, ref locRoot, SWC_DESKTOP, out hwnd, SWFO_NEEDDISPATCH) returns the desktop's browser.
        object loc = 0, root = 0;
        object hwnd = 0;
        var args = new object[] { loc, root, SWC_DESKTOP, hwnd, SWFO_NEEDDISPATCH };
        var modifiers = new ParameterModifier(5);
        modifiers[0] = true; modifiers[1] = true; modifiers[3] = true;
        var browser = windows.GetType().InvokeMember("FindWindowSW", BindingFlags.InvokeMethod, null, windows, args, [modifiers], null, null);
        if (browser is null) return [];
        dynamic b = browser;
        return Selected(b.Document);
    }

    private static List<string> Selected(dynamic document)
    {
        var result = new List<string>();
        if (document is null) return result;
        dynamic items = document.SelectedItems();
        foreach (dynamic item in items)
        {
            string path = item.Path;
            if (!string.IsNullOrEmpty(path) && (System.IO.File.Exists(path) || System.IO.Directory.Exists(path))) result.Add(path);
        }
        return result;
    }
}
