using OpenShelf.Core;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace OpenShelf.Shelf;

/// <summary>Notification-area icon with the app menu. Used on the WPF UI thread.</summary>
public sealed class TrayService : ITrayService
{
    readonly IShelfService _shelves;
    readonly Action _quit;
    WF.NotifyIcon? _notify;
    SD.Icon? _icon;
    WF.ContextMenuStrip? _menu;

    public TrayService(IShelfService shelves, Action quit)
    {
        _shelves = shelves;
        _quit = quit;
    }

    public void Start()
    {
        if (_notify != null) return;
        try
        {
            _icon = DrawIcon();
            _menu = new WF.ContextMenuStrip();
            _menu.Items.Add("New Shelf", null, (_, _) => Run("New Shelf",
                () => _shelves.NewShelf(Array.Empty<string>(), Native.CursorPosition())));
            _menu.Items.Add("Show Shelves", null, (_, _) => Run("Show Shelves", _shelves.ShowAll));
            _menu.Items.Add("Close All Shelves", null, (_, _) => Run("Close All Shelves", _shelves.CloseAll));
            _menu.Items.Add(new WF.ToolStripSeparator());
            _menu.Items.Add("Quit", null, (_, _) => Run("Quit", _quit));

            _notify = new WF.NotifyIcon
            {
                Icon = _icon,
                Text = "OpenShelf",
                ContextMenuStrip = _menu,
                Visible = true,
            };
            Log.Info("Tray icon started");
        }
        catch (Exception ex) { Log.Error("Tray start", ex); }
    }

    static void Run(string what, Action action)
    {
        try { action(); }
        catch (Exception ex) { Log.Error("Tray menu " + what, ex); }
    }

    /// <summary>White ring with a centre dot on a transparent 32x32 canvas.</summary>
    static SD.Icon DrawIcon()
    {
        using var bmp = new SD.Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = SD.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(SD.Color.Transparent);
            using var pen = new SD.Pen(SD.Color.White, 3f);
            g.DrawEllipse(pen, 4f, 4f, 24f, 24f);
            g.FillEllipse(SD.Brushes.White, 12f, 12f, 8f, 8f);
        }
        var hIcon = bmp.GetHicon();
        try
        {
            using var temp = SD.Icon.FromHandle(hIcon);
            return (SD.Icon)temp.Clone();
        }
        finally
        {
            Native.DestroyIcon(hIcon);
        }
    }

    public void Dispose()
    {
        try
        {
            if (_notify != null)
            {
                _notify.Visible = false;
                _notify.Dispose();
                _notify = null;
            }
            _menu?.Dispose();
            _menu = null;
            _icon?.Dispose();
            _icon = null;
        }
        catch (Exception ex) { Log.Error("Tray dispose", ex); }
    }
}
