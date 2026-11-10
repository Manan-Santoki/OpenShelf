using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using OpenShelf.Core;

namespace OpenShelf.Wheel;

/// <summary>The Windows Share sheet for a set of files, shown from a tiny helper window at the pointer.</summary>
internal static class ShareSheet
{
    [ComImport, Guid("3A3DCD6C-3EAB-43DC-BCDE-45671CE800C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDataTransferManagerInterop
    {
        IntPtr GetForWindow(IntPtr appWindow, in Guid riid);
        void ShowShareUIForWindow(IntPtr appWindow);
    }

    [DllImport("combase.dll")] private static extern int RoGetActivationFactory(IntPtr classId, in Guid iid, out IntPtr factory);
    [DllImport("combase.dll", CharSet = CharSet.Unicode)] private static extern int WindowsCreateString(string source, int length, out IntPtr hstring);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(IntPtr hstring);

    private static readonly Guid InteropIid = new("3A3DCD6C-3EAB-43DC-BCDE-45671CE800C8");
    private static readonly Guid ManagerIid = new("A5CAEE9B-8708-49D1-8D36-67D25A8DA00C");

    private static Window? _helper;
    private static DataTransferManager? _manager;
    private static IReadOnlyList<IStorageItem> _items = [];
    private static string _title = "";

    /// <summary>Shows the sheet; false when it could not be shown.</summary>
    public static async Task<bool> ShowAsync(IReadOnlyList<string> paths, ScreenPoint at)
    {
        try
        {
            var items = new List<IStorageItem>();
            foreach (var path in paths)
            {
                if (Directory.Exists(path)) items.Add(await StorageFolder.GetFolderFromPathAsync(path));
                else items.Add(await StorageFile.GetFileFromPathAsync(path));
            }
            if (items.Count == 0) return false;
            _items = items;
            _title = items.Count == 1 ? items[0].Name : $"{items.Count} files";

            var hwnd = EnsureHelper(at);
            var interop = GetInterop();
            var abi = interop.GetForWindow(hwnd, ManagerIid);
            _manager?.DataRequested -= OnDataRequested;
            _manager = WinRT.MarshalInterface<DataTransferManager>.FromAbi(abi);
            Marshal.Release(abi);
            _manager.DataRequested += OnDataRequested;
            interop.ShowShareUIForWindow(hwnd);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("ShareSheet", ex);
            return false;
        }
    }

    private static void OnDataRequested(DataTransferManager sender, DataRequestedEventArgs args)
    {
        try
        {
            args.Request.Data.Properties.Title = _title;
            args.Request.Data.SetStorageItems(_items);
        }
        catch (Exception ex) { Log.Error("ShareSheet.DataRequested", ex); }
    }

    private static IDataTransferManagerInterop GetInterop()
    {
        const string name = "Windows.ApplicationModel.DataTransfer.DataTransferManager";
        Marshal.ThrowExceptionForHR(WindowsCreateString(name, name.Length, out var hstring));
        try
        {
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(hstring, InteropIid, out var factory));
            try { return (IDataTransferManagerInterop)Marshal.GetObjectForIUnknown(factory); }
            finally { Marshal.Release(factory); }
        }
        finally { WindowsDeleteString(hstring); }
    }

    /// <summary>A 1 px nearly invisible window the sheet attaches to; it must be visible and in front.</summary>
    private static nint EnsureHelper(ScreenPoint at)
    {
        if (_helper is null)
        {
            _helper = new Window
            {
                WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = System.Windows.Media.Brushes.Transparent,
                Width = 1, Height = 1, ShowInTaskbar = false, Topmost = true, ShowActivated = true,
                WindowStartupLocation = WindowStartupLocation.Manual, Opacity = 0.01,
            };
        }
        var hwnd = new WindowInteropHelper(_helper).EnsureHandle();
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, at.X, at.Y, 2, 2, 0);
        if (!_helper.IsVisible) _helper.Show();
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, at.X, at.Y, 2, 2, Native.SWP_SHOWWINDOW);
        Native.ForceForeground(hwnd);
        return hwnd;
    }
}
