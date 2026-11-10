using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using OpenShelf.Core;

namespace OpenShelf.Wheel;

/// <summary>Builds each wheel's entries: Convert and Tools come from the catalog, Actions are built in (SPEC §2.2).</summary>
internal sealed class WheelBuilder(IShelfService shelves, IToastService toasts, IWheelCatalog catalog)
{
    public WheelModel Build(WheelKind kind, IReadOnlyList<string> files)
    {
        switch (kind)
        {
            case WheelKind.Actions:
                return new WheelModel(kind, BuildActions());
            default:
                try
                {
                    var content = kind == WheelKind.Convert ? catalog.Convert(files) : catalog.Tools(files);
                    return new WheelModel(kind, content.Entries, content.EmptyMessage);
                }
                catch (Exception ex)
                {
                    Log.Error($"catalog {kind}", ex);
                    return new WheelModel(kind, [], "Something went wrong");
                }
        }
    }

    public void ToastEmpty(WheelModel model, ScreenPoint near) =>
        toasts.Show(model.EmptyMessage ?? (model.Kind == WheelKind.Convert ? "Nothing to convert here yet" : "Nothing to do with these files"), near);

    // ---- Actions ----

    private List<WheelEntry> BuildActions()
    {
        var items = new List<WheelEntry>
        {
            new("action.shelf", "Shelf", run => { shelves.NewShelf(run.Paths, run.Pointer); return Task.CompletedTask; }, ""),
        };
        if (shelves.HasOpenShelf)
            items.Add(new("action.addtoshelf", "Add to shelf", run =>
            {
                if (!shelves.AddToRecentShelf(run.Paths)) toasts.Show("No shelf is open", run.Pointer);
                return Task.CompletedTask;
            }, ""));
        items.Add(new("action.share", "Share", ShareAsync, ""));
        items.Add(new("action.copy", "Copy", run =>
        {
            var list = new StringCollection();
            list.AddRange(run.Paths.ToArray());
            WithClipboard(() => Clipboard.SetFileDropList(list));
            toasts.Show(run.Paths.Count == 1 ? "Copied" : $"Copied {run.Paths.Count} files", run.Pointer);
            return Task.CompletedTask;
        }, ""));
        items.Add(new("action.move", "Move", MoveAsync, ""));
        items.Add(new("action.copypath", "Copy path", run =>
        {
            WithClipboard(() => Clipboard.SetText(string.Join(Environment.NewLine, run.Paths)));
            toasts.Show(run.Paths.Count == 1 ? "Copied path" : $"Copied {run.Paths.Count} paths", run.Pointer);
            return Task.CompletedTask;
        }, ""));
        items.Add(new("action.terminal", "Terminal", run => { OpenTerminal(run.Paths[0]); return Task.CompletedTask; }, ""));
        return items;
    }

    private async Task ShareAsync(WheelRun run)
    {
        if (!await ShareSheet.ShowAsync(run.Paths, run.Pointer))
            toasts.Show("Sharing isn't available", run.Pointer);
    }

    private static void WithClipboard(Action action)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { action(); return; }
            catch (COMException) when (attempt < 4) { Thread.Sleep(40); }
        }
    }

    private static void OpenTerminal(string path)
    {
        var folder = Directory.Exists(path) ? path : Path.GetDirectoryName(path) ?? path;
        try
        {
            var psi = new ProcessStartInfo("wt.exe") { UseShellExecute = false };
            psi.ArgumentList.Add("-d");
            psi.ArgumentList.Add(folder);
            Process.Start(psi);
            Log.Write($"terminal: wt.exe in {folder}");
        }
        catch (Exception ex)
        {
            Log.Write($"wt.exe failed ({ex.Message}); falling back to powershell");
            Process.Start(new ProcessStartInfo("powershell.exe") { WorkingDirectory = folder, UseShellExecute = false });
        }
    }

    // ---- Move ----

    private async Task MoveAsync(WheelRun run)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = run.Paths.Count == 1 ? "Move to folder" : $"Move {run.Paths.Count} files to folder",
            Multiselect = false,
        };
        var first = run.Paths[0];
        var start = Path.GetDirectoryName(first);
        if (start is not null && Directory.Exists(start)) dialog.InitialDirectory = start;
        if (dialog.ShowDialog() != true || string.IsNullOrEmpty(dialog.FolderName)) return;
        var target = dialog.FolderName;

        var moved = 0;
        string? failure = null;
        var results = new List<string>();
        foreach (var path in run.Paths)
        {
            try
            {
                var dest = await Task.Run(() => MovePath(path, target));
                if (dest is null) continue;
                results.Add(dest);
                moved++;
            }
            catch (Exception ex)
            {
                failure = ex.Message;
                Log.Error($"move {path}", ex);
            }
        }
        var name = Path.GetFileName(target.TrimEnd('\\', '/'));
        if (string.IsNullOrEmpty(name)) name = target;
        if (failure is not null)
            toasts.Show(moved == 0 ? $"Could not move: {failure}" : $"Moved {moved} of {run.Paths.Count} files to {name}: {failure}", run.Pointer);
        else
            toasts.Show(moved == 1 ? $"Moved to {name}" : $"Moved {moved} files to {name}", run.Pointer);
    }

    /// <summary>Moves into <paramref name="folder"/> without overwriting; returns the new path, or null when it is already there.</summary>
    internal static string? MovePath(string source, string folder)
    {
        var isDirectory = Directory.Exists(source);
        var sourceFolder = Path.GetDirectoryName(Path.GetFullPath(source));
        if (string.Equals(sourceFolder?.TrimEnd('\\'), Path.GetFullPath(folder).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return null;

        var name = isDirectory ? Path.GetFileName(source.TrimEnd('\\', '/')) : Path.GetFileNameWithoutExtension(source);
        var ext = isDirectory ? null : Path.GetExtension(source).TrimStart('.');
        if (ext is { Length: 0 }) ext = null;
        var dest = OutputNaming.UniquePath(folder, name, ext);
        if (!isDirectory) { File.Move(source, dest); return dest; }

        try { Directory.Move(source, dest); }
        catch (IOException)
        {
            CopyDirectory(source, dest);
            Directory.Delete(source, true);
        }
        return dest;
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from)) File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        foreach (var dir in Directory.GetDirectories(from)) CopyDirectory(dir, Path.Combine(to, Path.GetFileName(dir)));
    }
}
