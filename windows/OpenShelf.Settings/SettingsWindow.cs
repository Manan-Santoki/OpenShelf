using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using OpenShelf.Core;
using OpenShelf.Ui;

namespace OpenShelf.Settings;

internal sealed record TabDef(string Id, string Title, string Glyph);

/// <summary>The settings window: icon tabs along the top, one pane below, fixed width, height following the pane.</summary>
internal sealed class SettingsWindow : Window
{
    public const double ContentWidth = 720;

    public static readonly IReadOnlyList<TabDef> Tabs =
    [
        new("general", "General", ""),
        new("wheels", "Wheels", ""),   // drawn: see WheelIcon
        new("shelves", "Shelves", ""),
        new("output", "Output", ""),
        new("formats", "Extra Formats", ""),
        new("updates", "Updates", ""),
    ];

    private readonly Grid _root = new();
    private readonly Border _tabBar = new();
    private readonly ScrollViewer _scroll = new();
    private readonly Border _host = new();
    private readonly Dictionary<string, RadioButton> _tabButtons = [];
    private readonly Dictionary<string, Pane> _panes = [];
    private readonly Action<string> _tabChanged;
    private string _tab = Tabs[0].Id;
    private bool _fitted;

    public SettingsWindow(IPreferences preferences, AppCommands commands, Action<string> tabChanged, SettingsHooks? hooks = null)
    {
        _tabChanged = tabChanged;
        Title = "OpenShelf Settings";
        Width = ContentWidth + 16;
        Height = 520;
        ResizeMode = ResizeMode.CanMinimize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Background = Kit.Brush("Window");
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        FontFamily = Theme.TextFont;
        FontSize = 13;
        Foreground = Kit.Brush("Text");
        Resources.MergedDictionaries.Add(Kit.Dictionary);
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);

        _panes["general"] = new GeneralPane(preferences, commands, hooks ?? new SettingsHooks());
        _panes["wheels"] = new WheelsPane(preferences, commands);
        _panes["shelves"] = new ShelvesPane(preferences, commands);
        _panes["output"] = new OutputPane(preferences, commands);
        _panes["formats"] = new ToolsPane(preferences, commands);
        _panes["updates"] = new UpdatesPane(preferences, commands);

        BuildTabBar();
        _scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        _scroll.Content = _host;
        _host.Padding = new Thickness(28, 24, 28, 28);
        _host.SizeChanged += (_, e) => { if (e.HeightChanged) FitHeight(animate: true); };

        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(_scroll, 1);
        _root.Children.Add(_tabBar);
        _root.Children.Add(_scroll);
        Content = _root;

        SourceInitialized += (_, _) => DarkTitleBar();
        Loaded += (_, _) => AppIcon.Apply(this);
        Loaded += (_, _) => FirstFit();
        Activated += (_, _) => _panes[_tab].Sync();
        SelectTab(_tab, animate: false);
    }

    public ToolsPane Tools => (ToolsPane)_panes["formats"];

    /// <summary>Where to put the window instead of centring it (for screenshots).</summary>
    public Point? FixedPosition { get; set; }

    public string CurrentTab => _tab;

    public static string? Normalize(string? tab)
    {
        if (tab is null) return null;
        var key = tab.Trim().ToLowerInvariant().Replace(" ", "").Replace("-", "").Replace("_", "");
        if (key is "extraformats" or "tools") key = "formats";
        return Tabs.Any(t => t.Id == key) ? key : null;
    }

    private void BuildTabBar()
    {
        var strip = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 10) };
        KeyboardNavigation.SetDirectionalNavigation(strip, KeyboardNavigationMode.Cycle);
        foreach (var tab in Tabs)
        {
            FrameworkElement icon = tab.Glyph.Length == 0 ? new WheelIcon(20) { HorizontalAlignment = HorizontalAlignment.Center } : Kit.Glyph(tab.Glyph, 20);
            var label = Kit.Text(tab.Title, 12);
            // Both inherit the button's colour, which changes with the selection.
            label.ClearValue(TextBlock.ForegroundProperty);
            label.HorizontalAlignment = HorizontalAlignment.Center;
            label.Margin = new Thickness(0, 6, 0, 0);
            if (icon is TextBlock) icon.ClearValue(TextBlock.ForegroundProperty);
            var content = new StackPanel();
            content.Children.Add(icon);
            content.Children.Add(label);
            var button = new RadioButton { Style = Kit.Style("TabItemStyle"), Content = content, GroupName = "tabs", Margin = new Thickness(2, 0, 2, 0) };
            Kit.Name(button, tab.Title);
            var id = tab.Id;
            button.Checked += (_, _) => SelectTab(id, animate: true);
            _tabButtons[id] = button;
            strip.Children.Add(button);
        }
        _tabBar.Child = strip;
        _tabBar.Background = Kit.Brush("TabBar");
        _tabBar.BorderBrush = Kit.Brush("Hairline");
        _tabBar.BorderThickness = new Thickness(0, 0, 0, 1);
    }

    public void SelectTab(string id, bool animate)
    {
        if (!_panes.TryGetValue(id, out var pane)) return;
        var changed = id != _tab || _host.Child is null;
        _tab = id;
        if (_tabButtons[id].IsChecked != true) _tabButtons[id].IsChecked = true;
        if (!changed) return;
        pane.Sync();
        _host.Child = pane.Content;
        _scroll.ScrollToTop();
        _tabChanged(id);
        if (animate && IsLoaded)
        {
            Motion.Animate(pane.Content, OpacityProperty, 1, 150, from: 0);
            FitHeight(animate: true);
        }
        else if (IsLoaded) FitHeight(animate: false);
    }

    // ------------------------------------------------------------ sizing

    private void FirstFit()
    {
        UpdateLayout();
        Width = ContentWidth + (ActualWidth - _root.ActualWidth);
        UpdateLayout();
        _fitted = true;
        FitHeight(animate: false);
        if (FixedPosition is { } p) { Left = p.X; Top = p.Y; }
        else
        {
            var area = WorkArea();
            Left = area.Left + (area.Width - Width) / 2;
            Top = area.Top + (area.Height - Height) / 2;
        }
    }

    /// <summary>The usable area of the screen the window is on, in DIPs.</summary>
    private Rect WorkArea()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var handle = new WindowInteropHelper(this).Handle;
        var screen = handle != IntPtr.Zero ? System.Windows.Forms.Screen.FromHandle(handle) : System.Windows.Forms.Screen.PrimaryScreen!;
        var a = screen.WorkingArea;
        return new Rect(a.Left / dpi.DpiScaleX, a.Top / dpi.DpiScaleY, a.Width / dpi.DpiScaleX, a.Height / dpi.DpiScaleY);
    }

    private double TargetHeight()
    {
        _host.Measure(new Size(_root.ActualWidth, double.PositiveInfinity));
        var chrome = ActualHeight - _root.ActualHeight;
        var wanted = chrome + _tabBar.ActualHeight + _host.DesiredSize.Height;
        var max = WorkArea().Height - 8;
        return Math.Round(Math.Min(wanted, max));
    }

    private void FitHeight(bool animate)
    {
        if (!_fitted) return;
        var target = TargetHeight();
        if (Math.Abs(target - Height) < 0.5) return;
        var wasBottom = FixedPosition is null;
        if (animate) Motion.Animate(this, HeightProperty, target, 180, Motion.EaseOut, from: ActualHeight);
        else { BeginAnimation(HeightProperty, null); Height = target; }
        if (wasBottom && !animate) KeepOnScreen(target);
    }

    private void KeepOnScreen(double height)
    {
        var area = WorkArea();
        if (Top + height > area.Bottom) Top = Math.Max(area.Top, area.Bottom - height);
    }

    // ------------------------------------------------------------ chrome

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private void DarkTitleBar()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var on = 1;
        // 20 on Windows 10 2004 and later and Windows 11; 19 on the first Windows 10 builds that had it.
        if (DwmSetWindowAttribute(handle, 20, ref on, sizeof(int)) != 0) DwmSetWindowAttribute(handle, 19, ref on, sizeof(int));
    }
}
