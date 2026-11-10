using OpenShelf.Core;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace OpenShelf.Shelf;

/// <summary>Notification-area icon with the app menu (SPEC 4). Used on the WPF UI thread.</summary>
public sealed class TrayService : ITrayService
{
    readonly IShelfService _shelves;
    readonly AppCommands _commands;
    WF.NotifyIcon? _notify;
    SD.Icon? _icon;

    public TrayService(IShelfService shelves, AppCommands commands)
    {
        _shelves = shelves;
        _commands = commands;
    }

    /// <summary>The entries of the menu, in order (SPEC 4). Internal so the demo and tests can show or inspect it.</summary>
    internal IReadOnlyList<MenuEntry> BuildMenu() => new[]
    {
        new MenuEntry("New Shelf", () => Run("New Shelf", () => _shelves.NewShelf(Array.Empty<string>(), Native.CursorPosition()))),
        new MenuEntry("Show Shelves", () => Run("Show Shelves", _shelves.ShowAll)),
        new MenuEntry("Close All Shelves", () => Run("Close All Shelves", _shelves.CloseAll)),
        MenuEntry.Line(),
        new MenuEntry("Open Wheel for Selection", () => Run("Open Wheel", _commands.OpenWheelForSelection)),
        new MenuEntry("Take the Tour…", () => Run("Take the Tour", _commands.TakeTour)),
        new MenuEntry("Settings…", () => Run("Settings", _commands.OpenSettings)),
        new MenuEntry("Check for Updates…", () => Run("Check for Updates", _commands.CheckForUpdates)),
        MenuEntry.Line(),
        new MenuEntry("Quit", () => Run("Quit", () => { _shelves.Flush(); _commands.Quit(); })),
    };

    public void Start()
    {
        if (_notify != null) return;
        try
        {
            _icon = DrawIcon();
            _notify = new WF.NotifyIcon { Icon = _icon, Text = "OpenShelf", Visible = true };
            _notify.MouseUp += (_, e) =>
            {
                try
                {
                    if (e.Button == WF.MouseButtons.Left) _shelves.ShowAll();
                    else if (e.Button == WF.MouseButtons.Right) DarkMenu.Open(BuildMenu(), Native.CursorPosition());
                }
                catch (Exception ex) { Log.Error("Tray click", ex); }
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

    /// <summary>White ring with a centre dot on a transparent canvas.</summary>
    static SD.Icon DrawIcon()
    {
        int size = 16;
        try
        {
            size = Native.GetSystemMetricsForDpi(49 /* SM_CXSMICON */, Native.GetDpiForSystem());
            if (size < 16) size = 16;
        }
        catch (Exception) { }

        using var bmp = new SD.Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = SD.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            g.Clear(SD.Color.Transparent);
            float stroke = Math.Max(2f, size / 12f);
            float inset = 0.5f + stroke / 2f;
            float d = size - 2 * inset;
            using var pen = new SD.Pen(SD.Color.White, stroke);
            g.DrawEllipse(pen, inset, inset, d, d);
            float dot = size * 0.28f;
            g.FillEllipse(SD.Brushes.White, (size - dot) / 2f, (size - dot) / 2f, dot, dot);
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
            DarkMenu.Current?.Dismiss();
            if (_notify != null)
            {
                _notify.Visible = false;
                _notify.Dispose();
                _notify = null;
            }
            _icon?.Dispose();
            _icon = null;
        }
        catch (Exception ex) { Log.Error("Tray dispose", ex); }
    }
}
