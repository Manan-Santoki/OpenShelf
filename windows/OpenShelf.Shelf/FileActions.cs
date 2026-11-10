using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace OpenShelf.Shelf;

/// <summary>What the shelf does to files: open, reveal in Explorer, copy, copy path, share.</summary>
internal static class FileActions
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHParseDisplayName(string name, IntPtr bindingContext, out IntPtr pidl, uint attributesIn, out uint attributesOut);
    [DllImport("shell32.dll")]
    static extern int SHOpenFolderAndSelectItems(IntPtr folder, uint count, IntPtr[]? items, uint flags);
    [DllImport("shell32.dll")]
    static extern IntPtr ILFindLastID(IntPtr pidl);
    [DllImport("ole32.dll")]
    static extern void CoTaskMemFree(IntPtr p);

    /// <summary>Opens each file with its default app.</summary>
    public static void Open(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
            catch (Exception ex) { Log.Error("Open " + path, ex); }
        }
    }

    /// <summary>Shows the files in Explorer with them selected (one Explorer window per folder).</summary>
    public static void Reveal(IEnumerable<string> paths)
    {
        foreach (var group in paths.GroupBy(p => Path.GetDirectoryName(p) ?? p, StringComparer.OrdinalIgnoreCase))
        {
            var pidls = new List<IntPtr>();
            IntPtr folder = IntPtr.Zero;
            try
            {
                if (SHParseDisplayName(group.Key, IntPtr.Zero, out folder, 0, out _) < 0 || folder == IntPtr.Zero)
                    throw new InvalidOperationException("Folder not found: " + group.Key);
                var children = new List<IntPtr>();
                foreach (var path in group)
                {
                    if (Path.GetDirectoryName(path) == null) continue;      // a drive root has no parent to select in
                    if (SHParseDisplayName(path, IntPtr.Zero, out var full, 0, out _) < 0 || full == IntPtr.Zero) continue;
                    pidls.Add(full);
                    children.Add(ILFindLastID(full));
                }
                int hr = SHOpenFolderAndSelectItems(folder, (uint)children.Count, children.Count == 0 ? null : children.ToArray(), 0);
                if (hr < 0) throw new InvalidOperationException($"SHOpenFolderAndSelectItems 0x{hr:X8}");
            }
            catch (Exception ex)
            {
                Log.Error("Reveal", ex);
                try { Process.Start("explorer.exe", $"/select,\"{group.First()}\""); } catch (Exception) { }
            }
            finally
            {
                foreach (var p in pidls) CoTaskMemFree(p);
                if (folder != IntPtr.Zero) CoTaskMemFree(folder);
            }
        }
    }

    /// <summary>Puts the files on the clipboard so they can be pasted in Explorer.</summary>
    public static bool CopyFiles(IEnumerable<string> paths)
    {
        try
        {
            var list = new System.Collections.Specialized.StringCollection();
            list.AddRange(paths.ToArray());
            System.Windows.Clipboard.SetFileDropList(list);
            return true;
        }
        catch (Exception ex) { Log.Error("Copy files", ex); return false; }
    }

    public static bool CopyPaths(IEnumerable<string> paths)
    {
        try { System.Windows.Clipboard.SetText(string.Join(Environment.NewLine, paths)); return true; }
        catch (Exception ex) { Log.Error("Copy paths", ex); return false; }
    }

    // ---- Share (the Windows share sheet) ----

    [ComImport, Guid("3A3DCD6C-3EAB-43DC-BCDE-45671CE800C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDataTransferManagerInterop
    {
        // IInspectable, declared by hand because .NET has no IInspectable interface type.
        void GetIids(out uint count, out IntPtr iids);
        void GetRuntimeClassName(out IntPtr name);
        void GetTrustLevel(out int level);
        IntPtr GetForWindow(IntPtr window, ref Guid riid);
        void ShowShareUIForWindow(IntPtr window);
    }

    [DllImport("combase.dll")]
    static extern int RoGetActivationFactory(IntPtr classId, ref Guid iid, out IntPtr factory);
    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    static extern int WindowsCreateString(string source, int length, out IntPtr hstring);
    [DllImport("combase.dll")]
    static extern int WindowsDeleteString(IntPtr hstring);

    static readonly Guid IidInterop = new("3A3DCD6C-3EAB-43DC-BCDE-45671CE800C8");
    static readonly Guid IidDataTransferManager = new("A5CAEE9B-8708-49D1-8D36-67D25A8DA00C");
    static DataTransferManager? _manager;
    static IntPtr _managerWindow;
    static List<string> _sharing = new();

    /// <summary>Opens the Windows share sheet for the files. Returns false when it isn't available.</summary>
    public static bool Share(IntPtr window, IEnumerable<string> paths)
    {
        _sharing = paths.Where(File.Exists).ToList();
        if (_sharing.Count == 0 || window == IntPtr.Zero) return false;
        IntPtr hstring = IntPtr.Zero, factory = IntPtr.Zero;
        try
        {
            if (WindowsCreateString("Windows.ApplicationModel.DataTransfer.DataTransferManager",
                    "Windows.ApplicationModel.DataTransfer.DataTransferManager".Length, out hstring) < 0) return false;
            var iid = IidInterop;
            if (RoGetActivationFactory(hstring, ref iid, out factory) < 0 || factory == IntPtr.Zero) return false;

            var interop = (IDataTransferManagerInterop)Marshal.GetObjectForIUnknown(factory);
            if (_manager == null || _managerWindow != window)
            {
                var riid = IidDataTransferManager;
                var abi = interop.GetForWindow(window, ref riid);
                _manager = WinRT.MarshalInterface<DataTransferManager>.FromAbi(abi);
                _managerWindow = window;
                _manager.DataRequested += OnDataRequested;
            }
            interop.ShowShareUIForWindow(window);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Share", ex);
            return false;
        }
        finally
        {
            if (hstring != IntPtr.Zero) WindowsDeleteString(hstring);
            if (factory != IntPtr.Zero) Marshal.Release(factory);
        }
    }

    static async void OnDataRequested(DataTransferManager sender, DataRequestedEventArgs args)
    {
        var deferral = args.Request.GetDeferral();
        try
        {
            var files = new List<IStorageItem>();
            foreach (var path in _sharing)
                files.Add(await StorageFile.GetFileFromPathAsync(path));
            args.Request.Data.Properties.Title = files.Count == 1 ? Path.GetFileName(_sharing[0]) : $"{files.Count} files";
            args.Request.Data.SetStorageItems(files);
        }
        catch (Exception ex)
        {
            Log.Error("Share data", ex);
            args.Request.FailWithDisplayText("Couldn't share these files.");
        }
        finally { deferral.Complete(); }
    }
}
