using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Path = System.IO.Path;
using OpenShelf.Core;
using OpenShelf.Ui;

namespace OpenShelf.Tour;

/// <summary>What the tour window needs from the app besides preferences and progress.</summary>
internal sealed class TourHost
{
    public required IPreferences Preferences { get; init; }
    public IShelfService? Shelves { get; init; }
    public IToastService? Toasts { get; init; }
    public Action<string?> OpenSettings { get; init; } = _ => { };
    public bool Returning { get; init; }
}

/// <summary>
/// The tour: the steps listed on the left, the current one on the right with its instructions, a practice file to
/// drag, and what happened. It never takes part in the drag it teaches: it is an ordinary window (not topmost, never
/// modal), and the file leaves it through the shell's own drag loop, so wheels open above it as they do over Explorer.
/// </summary>
internal sealed class TourWindow : Window
{
    public const double ContentWidth = 800, ContentHeight = 560;

    private readonly TourProgress _progress;
    private readonly TourSession _session;
    private readonly TourHost _host;

    private readonly StackPanel _sidebarRows = new();
    private readonly TextBlock _doneText = TourKit.Text_("", 12, TourKit.Text3);
    private readonly Border _barFill = new() { Height = 4, CornerRadius = new CornerRadius(2), Background = TourKit.Success, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Border _barTrack = new() { Height = 4, CornerRadius = new CornerRadius(2), Background = TourKit.Fill };
    private readonly ScrollViewer _scroll = new();
    private readonly Grid _footerGrid = new();
    private readonly StackPanel _dots = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _footerLeft = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _footerRight = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };

    private PracticeView? _practice;
    private TourStep _shown;
    private int _celebrationSeen;
    private bool _dragging;
    private TourButton? _default;
    private bool _built;

    public TourWindow(TourProgress progress, TourSession session, TourHost host)
    {
        _progress = progress;
        _session = session;
        _host = host;

        Title = "OpenShelf Tour";
        ResizeMode = ResizeMode.CanMinimize;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = TourKit.Window;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        FontFamily = Theme.TextFont;
        FontSize = 13;
        Foreground = TourKit.Text;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);

        Content = BuildFrame();
        AppIcons.Apply(this);
        SourceInitialized += (_, _) => DarkTitleBar();
        Loaded += (_, _) => { _built = true; };
        PreviewKeyDown += OnKey;

        _progress.Changed += OnProgressChanged;
        _session.Changed += OnSessionChanged;
        _host.Preferences.Changed += OnPreferencesChanged;
        Closed += (_, _) =>
        {
            _progress.Changed -= OnProgressChanged;
            _session.Changed -= OnSessionChanged;
            _host.Preferences.Changed -= OnPreferencesChanged;
        };
        Activated += (_, _) => { if (!_dragging) Rebuild(animate: false, keepPage: true); };

        _shown = _progress.Current;
        Rebuild(animate: false);
    }

    /// <summary>Where to put the window instead of centring it (screenshots).</summary>
    public Point? FixedPosition
    {
        set
        {
            if (value is not { } p) return;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = p.X; Top = p.Y;
        }
    }

    // ------------------------------------------------------------ frame

    private UIElement BuildFrame()
    {
        var root = new Grid { Width = ContentWidth, Height = ContentHeight };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var sidebar = BuildSidebar();
        root.Children.Add(sidebar);

        var main = new Grid();
        main.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        _scroll.Focusable = false;
        main.Children.Add(_scroll);

        var footer = new Border { Height = 60, BorderBrush = TourKit.Hairline, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(24, 0, 24, 0), Background = TourKit.Window };
        _footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_dots, 1);
        Grid.SetColumn(_footerRight, 2);
        _footerRight.HorizontalAlignment = HorizontalAlignment.Right;
        _footerGrid.Children.Add(_footerLeft);
        _footerGrid.Children.Add(_dots);
        _footerGrid.Children.Add(_footerRight);
        footer.Child = _footerGrid;
        Grid.SetRow(footer, 1);
        main.Children.Add(footer);
        Grid.SetColumn(main, 1);
        root.Children.Add(main);
        return root;
    }

    private UIElement BuildSidebar()
    {
        var panel = new DockPanel { Background = TourKit.Sidebar };
        var border = new Border { Background = TourKit.Sidebar, BorderBrush = TourKit.Hairline, BorderThickness = new Thickness(0, 0, 1, 0), Child = panel };

        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(18, 22, 18, 0) };
        var badge = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(8), Background = TourKit.Accent, Child = WheelMark(18) };
        header.Children.Add(badge);
        var titles = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(TourKit.Text_("OpenShelf", 14, weight: FontWeights.SemiBold));
        titles.Children.Add(TourKit.Text_("Guided tour", 11, TourKit.Text3));
        header.Children.Add(titles);
        DockPanel.SetDock(header, Dock.Top);
        panel.Children.Add(header);

        var progress = new StackPanel { Margin = new Thickness(18, 20, 18, 14) };
        progress.Children.Add(_doneText);
        _barTrack.Margin = new Thickness(0, 7, 0, 0);
        _barTrack.Child = _barFill;
        progress.Children.Add(_barTrack);
        DockPanel.SetDock(progress, Dock.Top);
        panel.Children.Add(progress);

        var skip = new TourButton("Skip Tour", ButtonStyle.Link, "Close the tour. It’s in the tray menu and Settings → General whenever you want it.")
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(14, 0, 0, 14),
        };
        skip.Click += CloseTour;
        DockPanel.SetDock(skip, Dock.Bottom);
        panel.Children.Add(skip);

        _sidebarRows.Margin = new Thickness(10, 0, 10, 0);
        panel.Children.Add(_sidebarRows);
        return border;
    }

    private void BuildSidebarRows()
    {
        _sidebarRows.Children.Clear();
        foreach (var step in TourSteps.All) _sidebarRows.Children.Add(SidebarRow(step));
        var done = _progress.PracticeDoneCount;
        var total = TourSteps.Practice.Count;
        _doneText.Text = done == 0 ? $"{total} practice steps" : $"{done} of {total} done";
        _barFill.Width = 194.0 * done / total;
        _barTrack.Width = 194;
    }

    private UIElement SidebarRow(TourStep step)
    {
        var current = _progress.Current == step;
        var done = _progress.Completed.Contains(step);
        var row = new Border
        {
            Height = 32,
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(8, 0, 8, 0),
            Cursor = Cursors.Hand,
            Background = current ? TourKit.AccentSoft : Brushes.Transparent,
            Focusable = true,
        };
        TourKit.Name(row, step.Title() + (done ? ", done" : ""));
        var line = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var disc = new Grid { Width = 20, Height = 20, VerticalAlignment = VerticalAlignment.Center };
        if (done)
        {
            disc.Children.Add(new Ellipse { Fill = TourKit.Success });
            disc.Children.Add(TourKit.Glyph("check", 10, B(0x12, 0x2A, 0x1E)));
        }
        else
        {
            disc.Children.Add(new Ellipse { Stroke = current ? TourKit.Accent : Theme.White(0.28), StrokeThickness = 1.5 });
            disc.Children.Add(TourKit.Glyph(step.Glyph(), 9, current ? TourKit.Accent : TourKit.Text3));
        }
        line.Children.Add(disc);
        var label = TourKit.Text_(step.Title(), 13, current ? TourKit.Text : TourKit.Text2, current ? FontWeights.SemiBold : FontWeights.Normal);
        label.Margin = new Thickness(10, 0, 0, 0);
        label.VerticalAlignment = VerticalAlignment.Center;
        line.Children.Add(label);
        row.Child = line;
        row.MouseEnter += (_, _) => { if (_progress.Current != step) row.Background = TourKit.Fill; };
        row.MouseLeave += (_, _) => row.Background = _progress.Current == step ? TourKit.AccentSoft : Brushes.Transparent;
        row.MouseLeftButtonUp += (_, _) => _progress.Show(step);
        row.KeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Space) { _progress.Show(step); e.Handled = true; } };
        return row;
    }

    /// <summary>A small wheel: a ring, a hub and three spokes.</summary>
    private static FrameworkElement WheelMark(double size)
    {
        var grid = new Grid { Width = size, Height = size, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        grid.Children.Add(new Ellipse { Stroke = Brushes.White, StrokeThickness = 1.8 });
        var c = size / 2;
        foreach (var angle in new[] { -90.0, 30, 150 })
        {
            var rad = angle * Math.PI / 180;
            grid.Children.Add(new Line { X1 = c + Math.Cos(rad) * size * 0.16, Y1 = c + Math.Sin(rad) * size * 0.16, X2 = c + Math.Cos(rad) * size * 0.45, Y2 = c + Math.Sin(rad) * size * 0.45, Stroke = Brushes.White, StrokeThickness = 1.6 });
        }
        grid.Children.Add(new Ellipse { Width = size * 0.22, Height = size * 0.22, Fill = Brushes.White });
        return grid;
    }

    private static Brush B(byte r, byte g, byte b) { var brush = new SolidColorBrush(Color.FromRgb(r, g, b)); brush.Freeze(); return brush; }

    // ------------------------------------------------------------ updating

    private void OnProgressChanged()
    {
        var stepChanged = _progress.Current != _shown;
        Rebuild(animate: stepChanged, keepPage: !stepChanged);
    }

    private void OnSessionChanged() { if (!_dragging) Rebuild(animate: false, keepPage: _practice is not null); else _practice?.Update(); }

    private void OnPreferencesChanged() { if (!_dragging) Rebuild(animate: false); }

    /// <summary>
    /// Rebuilds the sidebar, footer and (unless <paramref name="keepPage"/>) the page. A practice page that is already
    /// showing is updated in place instead, so the file being dragged never disappears under the pointer.
    /// </summary>
    private void Rebuild(bool animate, bool keepPage = false)
    {
        BuildSidebarRows();
        BuildFooter();
        var step = _progress.Current;
        if (keepPage && _practice is not null && _practice.Step == step)
        {
            _practice.Update();
        }
        else
        {
            _shown = step;
            var page = BuildPage(step);
            _scroll.Content = page;
            _scroll.ScrollToTop();
            if (animate && _built) TourKit.SlideIn(page);
        }
        if (_progress.Celebration != _celebrationSeen)
        {
            _celebrationSeen = _progress.Celebration;
            _practice?.Celebrate();
        }
    }

    private void BuildFooter()
    {
        var step = _progress.Current;
        var done = _progress.Completed.Contains(step);
        _footerLeft.Children.Clear();
        _footerRight.Children.Clear();
        _dots.Children.Clear();
        _default = null;

        if (step != TourStep.Welcome)
        {
            var back = new TourButton("Back", tip: "Ctrl+Left");
            back.Click += _progress.Previous;
            _footerLeft.Children.Add(back);
        }

        foreach (var s in TourSteps.All)
        {
            var isCurrent = s == step;
            var dot = new Border
            {
                Width = isCurrent ? 18 : 7,
                Height = 7,
                CornerRadius = new CornerRadius(3.5),
                Margin = new Thickness(3, 0, 3, 0),
                Background = isCurrent ? TourKit.Accent : _progress.Completed.Contains(s) ? TourKit.Success : Theme.White(0.22),
                Cursor = Cursors.Hand,
                ToolTip = s.Title(),
            };
            var target = s;
            dot.MouseLeftButtonUp += (_, _) => _progress.Show(target);
            _dots.Children.Add(dot);
        }

        switch (step)
        {
            case TourStep.Welcome:
                var notNow = new TourButton("Not Now");
                notNow.Click += CloseTour;
                var start = new TourButton("Start the Tour", ButtonStyle.Primary);
                start.Click += _progress.Next;
                start.Margin = new Thickness(10, 0, 0, 0);
                _footerRight.Children.Add(notNow);
                _footerRight.Children.Add(start);
                _default = start;
                break;
            case TourStep.Finish:
                var finish = new TourButton("Done", ButtonStyle.Primary);
                finish.Click += CloseTour;
                _footerRight.Children.Add(finish);
                _default = finish;
                break;
            default:
                if (done)
                {
                    var next = new TourButton("Continue", ButtonStyle.Primary);
                    next.Click += _progress.Next;
                    _footerRight.Children.Add(next);
                    _default = next;
                }
                else
                {
                    var skip = new TourButton("Skip for Now", tip: "Go on without trying this one. You can come back to it from the list.");
                    skip.Click += _progress.Next;
                    _footerRight.Children.Add(skip);
                }
                break;
        }
    }

    private void CloseTour() => Close();

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Left && _progress.CanGoBack) { _progress.Previous(); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.None && e.Key == Key.Enter && _default is not null && Keyboard.FocusedElement is not TourButton)
        {
            _default.Invoke();
            e.Handled = true;
        }
    }

    // ------------------------------------------------------------ pages

    private FrameworkElement BuildPage(TourStep step)
    {
        _practice = null;
        var page = new StackPanel { Margin = new Thickness(32, 36, 32, 16) };
        page.Children.Add(Header(step));
        FrameworkElement body = step switch
        {
            TourStep.Welcome => WelcomeBody(),
            TourStep.Finish => FinishBody(),
            _ => (_practice = new PracticeView(this, step)).Root,
        };
        body.Margin = new Thickness(0, 18, 0, 0);
        page.Children.Add(body);
        return page;
    }

    private FrameworkElement Header(TourStep step)
    {
        var header = new StackPanel();
        if (step.IsPractice())
        {
            var index = TourSteps.Practice.ToList().IndexOf(step);
            var kicker = TourKit.Text_($"STEP {index + 1} OF {TourSteps.Practice.Count}", 11, TourKit.Accent, FontWeights.SemiBold);
            header.Children.Add(kicker);
        }
        var title = TourKit.Text_(step == TourStep.Welcome ? TourInstructions.WelcomeTitle(_host.Returning) : step.Title(), 26, weight: FontWeights.Bold);
        title.FontFamily = Theme.Font;
        title.Margin = new Thickness(0, 6, 0, 0);
        header.Children.Add(title);
        var summary = TourKit.Text_(step.Summary(), 14, TourKit.Text2, wrap: true);
        summary.Margin = new Thickness(0, 6, 0, 0);
        header.Children.Add(summary);
        return header;
    }

    private FrameworkElement WelcomeBody()
    {
        var body = new StackPanel();
        body.Children.Add(TourKit.Text_("Everything starts with a drag. Start dragging any file, keep holding, and tap a key:", 13, wrap: true));
        var triggers = _host.Preferences.Triggers;
        var rows = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
        void Row(WheelKind kind, string title, string detail)
        {
            var card = new Border { Background = TourKit.Fill, CornerRadius = new CornerRadius(12), Padding = new Thickness(12), Margin = new Thickness(0, 0, 0, 10) };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var keys = new Border { Background = TourKit.AccentSoft, CornerRadius = new CornerRadius(9), Height = 40, Padding = new Thickness(12, 0, 12, 0), HorizontalAlignment = HorizontalAlignment.Left };
            keys.Child = TourKit.Text_(TourInstructions.Describe(kind, triggers), 15, TourKit.Accent, FontWeights.SemiBold);
            ((TextBlock)keys.Child).VerticalAlignment = VerticalAlignment.Center;
            grid.Children.Add(keys);
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(TourKit.Text_(title, 13, weight: FontWeights.SemiBold));
            text.Children.Add(TourKit.Text_(detail, 12, TourKit.Text2, wrap: true));
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);
            card.Child = grid;
            TourKit.Name(card, $"{title}: {TourInstructions.Describe(kind, triggers)}. {detail}");
            rows.Children.Add(card);
        }
        Row(WheelKind.Convert, "Convert", "Turn it into JPG, PNG, PDF, DOCX, MP3, ZIP…");
        Row(WheelKind.Tools, "Tools", "Crop, annotate, compress, trim, split PDFs…");
        Row(WheelKind.Actions, "Actions", "Put it on a shelf, share, copy, move…");
        body.Children.Add(rows);

        var ready = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        if (_session.IsReady)
        {
            ready.Children.Add(TourKit.Glyph("check", 13, TourKit.Success));
            ready.Children.Add(Padded(TourKit.Text_("Practice files are ready. Nothing of yours is touched, and it takes about three minutes.", 12, TourKit.Text2)));
        }
        else if (_session.Failure is { } failure)
        {
            ready.Children.Add(TourKit.Glyph("triangle-exclamation-point", 13, TourKit.Danger));
            ready.Children.Add(Padded(TourKit.Text_(failure, 12, TourKit.Text2)));
        }
        else
        {
            ready.Children.Add(TourKit.Glyph("refresh-cw", 13, TourKit.Text3));
            ready.Children.Add(Padded(TourKit.Text_("Making practice files…", 12, TourKit.Text2)));
        }
        body.Children.Add(ready);
        return body;

        static FrameworkElement Padded(TextBlock t) { t.Margin = new Thickness(8, 0, 0, 0); return t; }
    }

    private FrameworkElement FinishBody()
    {
        var body = new StackPanel();
        var top = new StackPanel { Orientation = Orientation.Horizontal };
        var seal = TourKit.Glyph("circle-check", 40, TourKit.Success);   // completed
        top.Children.Add(seal);
        var summary = TourKit.Text_(TourInstructions.FinishSummary(_progress.PracticeDoneCount, TourSteps.Practice.Count), 13, wrap: true);
        summary.Width = 440;
        summary.Margin = new Thickness(14, 0, 0, 0);
        summary.VerticalAlignment = VerticalAlignment.Center;
        top.Children.Add(summary);
        body.Children.Add(top);
        if (_progress.Celebration > 0 && _progress.Current == TourStep.Finish) TourKit.Pop(seal);

        var grid = new UniformGrid { Columns = 2, Margin = new Thickness(0, 18, 0, 0) };
        foreach (var step in TourSteps.Practice)
        {
            var done = _progress.Completed.Contains(step);
            var item = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4), Cursor = Cursors.Hand, Background = Brushes.Transparent };
            item.Children.Add(TourKit.Glyph(done ? "check" : "circle-dashed", 14, done ? TourKit.Success : TourKit.Text3));
            var label = TourKit.Text_(step.Title(), 13, done ? TourKit.Text : TourKit.Text2);
            label.Margin = new Thickness(8, 0, 0, 0);
            item.Children.Add(label);
            item.ToolTip = done ? "Done. Try it again." : "Try this one.";
            TourKit.Name(item, step.Title() + (done ? ", done" : ", not tried"));
            var target = step;
            item.MouseLeftButtonUp += (_, _) => _progress.Show(target);
            grid.Children.Add(item);
        }
        body.Children.Add(grid);

        body.Children.Add(new Border { Height = 1, Background = TourKit.Hairline, Margin = new Thickness(0, 14, 0, 14) });

        FrameworkElement Labeled(string label, params UIElement[] controls)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            var text = TourKit.Text_(label, 13, TourKit.Text2);
            text.Width = 130;
            text.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(text);
            foreach (var c in controls) { if (c is FrameworkElement fe) fe.Margin = new Thickness(0, 0, 8, 0); row.Children.Add(c); }
            return row;
        }

        var formats = new TourButton("Extra Formats…", tip: "Install ffmpeg, LibreOffice or Pandoc for more formats.");
        formats.Click += () => _host.OpenSettings("formats");
        var keys = new TourButton("Wheel Keys…", tip: "Change the keys that open each wheel, and the shortcut.");
        keys.Click += () => _host.OpenSettings("wheels");
        body.Children.Add(Labeled("Keep going", formats, keys));

        var practice = new List<UIElement>();
        if (_session.IsReady)
        {
            var show = new TourButton("Show in Explorer");
            show.Click += () => Reveal(_session.Folder, folder: true);
            var remove = new TourButton("Remove", tip: "Moves the practice folder, and everything made in it, to the Recycle Bin.");
            remove.Click += _session.RemovePracticeFiles;
            practice.Add(show);
            practice.Add(remove);
        }
        else
        {
            var again = new TourButton("Make Them Again");
            again.Click += _session.Restore;
            practice.Add(again);
        }
        body.Children.Add(Labeled("Practice files", [.. practice]));

        var login = new TourSwitch("Open OpenShelf at login") { IsOn = _host.Preferences.OpenAtLogin };
        login.Toggled += on =>
        {
            try { _host.Preferences.OpenAtLogin = on; }
            catch (Exception) { login.IsOn = _host.Preferences.OpenAtLogin; }
        };
        body.Children.Add(login);
        return body;
    }

    // ------------------------------------------------------------ the practice page

    /// <summary>A practice step's page: numbered instructions, a tip, the file to drag, a status line, what's been made.</summary>
    private sealed class PracticeView
    {
        private readonly TourWindow _w;
        private readonly Border _banner = new() { Height = 44, CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 0, 14, 0) };
        private readonly Grid _bannerIcon = new() { Width = 22, VerticalAlignment = VerticalAlignment.Center };
        private readonly ContentControl _bannerText = new() { VerticalAlignment = VerticalAlignment.Center };
        private readonly StackPanel _results = new();
        private Border _tile = new();
        private TextBlock _nudge = new();
        private readonly Ellipse _pulse = new() { Width = 9, Height = 9, Fill = TourKit.Accent, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        private readonly StackPanel _tray = new() { Margin = new Thickness(28, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top };
        private bool? _tileReady;
        private bool _wasDone;
        private string _bannerKey = "";

        public PracticeView(TourWindow window, TourStep step)
        {
            _w = window;
            Step = step;
            Root = new StackPanel();

            var top = new Grid();
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var left = new StackPanel();
            var instructions = TourInstructions.Steps(step, _w._host.Preferences.Triggers, _w._host.Preferences.SelectionHotKey);
            for (var i = 0; i < instructions.Count; i++) left.Children.Add(InstructionRow(i + 1, instructions[i]));
            if (TourInstructions.Tip(step) is { } tip)
            {
                var box = new Border { Background = TourKit.Fill, CornerRadius = new CornerRadius(10), Padding = new Thickness(10), Margin = new Thickness(0, 2, 0, 0) };
                var line = new Grid();
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                line.Children.Add(TourKit.Glyph("lightbulb", 14, B(0xF5, 0xC8, 0x42)));
                var text = TourKit.Rich(tip, 12, TourKit.Text2);
                text.Margin = new Thickness(8, 0, 0, 0);
                Grid.SetColumn(text, 1);
                line.Children.Add(text);
                ((FrameworkElement)line.Children[0]).VerticalAlignment = VerticalAlignment.Top;
                box.Child = line;
                left.Children.Add(box);
            }
            if (step == TourStep.Shelf && _w._host.Shelves is { } shelves)
            {
                var show = new TourButton("Show My Shelves", ButtonStyle.Secondary, "Bring any open shelves to the front.") { HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 0) };
                show.Click += shelves.ShowAll;
                left.Children.Add(show);
            }
            top.Children.Add(left);

            if (step.File() is not null)
            {
                Grid.SetColumn(_tray, 1);
                top.Children.Add(_tray);
            }
            Root.Children.Add(top);

            _banner.Margin = new Thickness(0, 14, 0, 0);
            var bannerLine = new StackPanel { Orientation = Orientation.Horizontal };
            _bannerIcon.Margin = new Thickness(0, 0, 10, 0);
            bannerLine.Children.Add(_bannerIcon);
            bannerLine.Children.Add(_bannerText);
            _banner.Child = bannerLine;
            Root.Children.Add(_banner);
            _results.Margin = new Thickness(0, 14, 0, 0);
            Root.Children.Add(_results);

            _wasDone = _w._progress.Completed.Contains(step);
            Update();
        }

        public TourStep Step { get; }
        public StackPanel Root { get; }

        private bool IsDone => _w._progress.Completed.Contains(Step);

        public void Celebrate()
        {
            if (IsDone && !_wasDone && _bannerIcon.Children.Count > 0 && _bannerIcon.Children[0] is FrameworkElement check) TourKit.Pop(check);
            _wasDone = IsDone;
        }

        /// <summary>Refreshes everything that changes while the page stays: the banner, the nudge, the files made.</summary>
        public void Update()
        {
            if (Step.File() is { } file && _tileReady != _w._session.IsReady)
            {
                _tileReady = _w._session.IsReady;
                (_tile, _nudge) = BuildTile(file, Step);
                _nudge.HorizontalAlignment = HorizontalAlignment.Center;
                _nudge.Margin = new Thickness(0, 0, 0, 8);
                _tray.Children.Clear();
                _tray.Children.Add(_nudge);
                _tray.Children.Add(_tile);
            }
            var done = IsDone;
            var underWay = _w._progress.IsUnderWay;
            UpdateBanner(done, underWay);
            _nudge.Opacity = !done && !underWay ? 1 : 0;
            if (_tile.Child is not null) _tile.BorderBrush = !done && !underWay ? Theme.Brush(Theme.Accent, 0.65) : Theme.Brush(Theme.Accent, 0.22);
            UpdateResults();
        }

        private void UpdateBanner(bool done, bool underWay)
        {
            var key = $"{done}|{underWay}";
            if (key == _bannerKey) return;
            _bannerKey = key;
            _bannerIcon.Children.Clear();
            if (done)
            {
                _bannerIcon.Children.Add(TourKit.Glyph("circle-check", 20, TourKit.Success));
                _banner.Background = TourKit.SuccessSoft;
            }
            else if (underWay)
            {
                _bannerIcon.Children.Add(TourKit.Glyph("circle-arrow-down", 20, TourKit.Accent));   // down arrow
                _banner.Background = TourKit.AccentSoft;
            }
            else
            {
                _bannerIcon.Children.Add(_pulse);
                _banner.Background = TourKit.Fill;
                if (Motion.Enabled)
                {
                    _pulse.RenderTransformOrigin = new Point(0.5, 0.5);
                    var scale = new ScaleTransform(1, 1);
                    _pulse.RenderTransform = scale;
                    var grow = new System.Windows.Media.Animation.DoubleAnimation(0.8, 1.35, TimeSpan.FromMilliseconds(900)) { AutoReverse = true, RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever };
                    scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
                    scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
                }
            }
            var text = TourInstructions.Status(Step, done, underWay);
            _bannerText.Content = TourKit.Rich(text, 13, done || underWay ? TourKit.Text : TourKit.Text2, weight: done || underWay ? FontWeights.SemiBold : FontWeights.Normal);
            TourKit.Name(_banner, text.Replace("**", ""));
        }

        private string _resultsKey = "";

        private void UpdateResults()
        {
            var files = _w._session.Results;
            var key = string.Join("|", files);
            if (key == _resultsKey) return;
            _resultsKey = key;
            _results.Children.Clear();
            if (files.Count == 0) return;
            var head = new Grid();
            head.Children.Add(TourKit.Text_("Made so far", 11, TourKit.Text3, FontWeights.SemiBold));
            var reveal = new TourButton("Show in Explorer", ButtonStyle.Link) { HorizontalAlignment = HorizontalAlignment.Right };
            var first = files[0];
            reveal.Click += () => Reveal(first);
            head.Children.Add(reveal);
            _results.Children.Add(head);
            var chips = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            foreach (var file in files.Take(4)) chips.Children.Add(Chip(file));
            _results.Children.Add(chips);
        }

        private UIElement Chip(string path)
        {
            var chip = new Border
            {
                Height = 30,
                CornerRadius = new CornerRadius(15),
                Background = TourKit.Fill,
                Padding = new Thickness(10, 0, 12, 0),
                Margin = new Thickness(0, 0, 8, 0),
                Cursor = Cursors.Hand,
                ToolTip = $"Show {Path.GetFileName(path)} in Explorer",
            };
            var line = new StackPanel { Orientation = Orientation.Horizontal };
            line.Children.Add(TourKit.Glyph(GlyphFor(path), 13, TourKit.Text2));
            var name = TourKit.Text_(Path.GetFileName(path), 11, TourKit.Text);
            name.Margin = new Thickness(7, 0, 0, 0);
            name.MaxWidth = 150;
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            name.VerticalAlignment = VerticalAlignment.Center;
            line.Children.Add(name);
            chip.Child = line;
            TourKit.Name(chip, Path.GetFileName(path));
            chip.MouseEnter += (_, _) => chip.Background = TourKit.FillHover;
            chip.MouseLeave += (_, _) => chip.Background = TourKit.Fill;
            var drag = new DragSource(_w, path);
            drag.Attach(chip, onClick: () => Reveal(path));
            return chip;
        }

        private (Border Tile, TextBlock Nudge) BuildTile(PracticeFile file, TourStep step)
        {
            var nudge = TourKit.Text_(step == TourStep.Keyboard ? "Select me in Explorer" : "Drag me", 11, TourKit.Accent, FontWeights.SemiBold);
            var path = _w._session.PathOf(file);

            var column = new StackPanel { Width = 150 };
            var picture = new Grid { Width = 96, Height = 96, HorizontalAlignment = HorizontalAlignment.Center };
            column.Children.Add(picture);
            var name = TourKit.Text_(TourInstructions.FileName(file), 12, TourKit.Text, FontWeights.Medium);
            name.HorizontalAlignment = HorizontalAlignment.Center;
            name.Margin = new Thickness(0, 8, 0, 0);
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            column.Children.Add(name);
            var caption = TourKit.Text_(TourInstructions.Caption(file), 11, TourKit.Text3);
            caption.HorizontalAlignment = HorizontalAlignment.Center;
            caption.Margin = new Thickness(0, 2, 0, 0);
            column.Children.Add(caption);

            if (_w._session.IsReady) picture.Children.Add(Picture(file, path));
            else picture.Children.Add(new ProgressRing());

            var tile = new Border
            {
                Width = 180,
                Padding = new Thickness(14),
                CornerRadius = new CornerRadius(14),
                Background = TourKit.Fill,
                BorderThickness = new Thickness(1.5),
                BorderBrush = Theme.Brush(Theme.Accent, 0.65),
                Child = column,
                Cursor = _w._session.IsReady ? Cursors.Hand : Cursors.Arrow,
                ToolTip = _w._session.IsReady ? $"Drag {TourInstructions.FileName(file)}, then tap the keys while you’re still holding it." : null,
            };
            TourKit.Name(tile, $"Practice file {TourInstructions.FileName(file)}. Drag it.");
            if (_w._session.IsReady)
            {
                var drag = new DragSource(_w, path);
                drag.Attach(tile, onClick: null, doubleClick: () => OpenFile(path));
            }
            return (tile, nudge);
        }

        private static UIElement Picture(PracticeFile file, string path)
        {
            if (file == PracticeFile.Photo && File.Exists(path))
            {
                try
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.UriSource = new Uri(path);
                    bitmap.DecodePixelWidth = 288;
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.EndInit();
                    bitmap.Freeze();
                    return new Border
                    {
                        CornerRadius = new CornerRadius(6),
                        Background = new ImageBrush(bitmap) { Stretch = Stretch.UniformToFill },
                        Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 8, ShadowDepth = 2, Opacity = 0.35, Direction = 270 },
                    };
                }
                catch (Exception) { /* fall through to the generic tile */ }
            }
            var (label, color) = file switch
            {
                PracticeFile.Notes => ("MD", Color.FromRgb(0x6E, 0x8B, 0xFF)),
                PracticeFile.Report => ("PDF", Color.FromRgb(0xE0, 0x5A, 0x4F)),
                PracticeFile.Clip => ("AVI", Color.FromRgb(0xB0, 0x6B, 0xE8)),
                _ => ("TXT", Color.FromRgb(0x8A, 0x93, 0xA3)),
            };
            var page = new Border
            {
                Width = 76,
                Height = 96,
                CornerRadius = new CornerRadius(6),
                Background = Theme.Brush(color, 0.2),
                BorderBrush = Theme.Brush(color, 0.7),
                BorderThickness = new Thickness(1.5),
            };
            var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var glyph = TourKit.Glyph(file switch { PracticeFile.Clip => "film", PracticeFile.Report => "file-text", _ => "file-text" }, 26, Theme.Brush(color));
            stack.Children.Add(glyph);
            var tag = TourKit.Text_(label, 12, Theme.Brush(color), FontWeights.Bold);
            tag.HorizontalAlignment = HorizontalAlignment.Center;
            tag.Margin = new Thickness(0, 6, 0, 0);
            stack.Children.Add(tag);
            page.Child = stack;
            return page;
        }

        private UIElement InstructionRow(int number, TourInstruction instruction)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var disc = new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(10), Background = TourKit.Accent, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 0) };
            disc.Child = new TextBlock { Text = number.ToString(), FontSize = 11, FontWeight = FontWeights.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(disc);
            var content = new StackPanel();
            var text = TourKit.Rich(instruction.Text, 13, TourKit.Text);
            text.Margin = new Thickness(0, 1, 0, 0);
            content.Children.Add(text);
            if (instruction.Keys.Count > 0)
            {
                var caps = TourKit.Keycaps(instruction.Keys);
                caps.Margin = new Thickness(0, 8, 0, 0);
                caps.HorizontalAlignment = HorizontalAlignment.Left;
                content.Children.Add(caps);
            }
            if (instruction.Action != InstructionAction.None)
            {
                var button = new TourButton(instruction.ActionTitle ?? "", ButtonStyle.Secondary) { HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0), Height = 26 };
                button.Click += () =>
                {
                    if (instruction.Action == InstructionAction.SetShortcut) _w._host.OpenSettings("wheels");
                    else if (_w._session.IsReady && Step.File() is { } f) Reveal(_w._session.PathOf(f));
                };
                content.Children.Add(button);
            }
            Grid.SetColumn(content, 1);
            row.Children.Add(content);
            return row;
        }
    }

    private static string GlyphFor(string path) => FileKinds.Of(path) switch
    {
        FileKind.Image => "image",
        FileKind.Video => "film",
        FileKind.Audio => "audio-lines",
        FileKind.Pdf => "file-text",
        _ => "file-text",
    };

    /// <summary>A spinner for "making practice files".</summary>
    private sealed class ProgressRing : Grid
    {
        public ProgressRing()
        {
            Width = 28; Height = 28;
            HorizontalAlignment = HorizontalAlignment.Center; VerticalAlignment = VerticalAlignment.Center;
            var arc = new System.Windows.Shapes.Path
            {
                Stroke = TourKit.Accent, StrokeThickness = 3, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                Data = Geometry.Parse("M 14,2 A 12,12 0 1 1 2,14"),
                RenderTransformOrigin = new Point(0.5, 0.5),
            };
            var rotate = new RotateTransform();
            arc.RenderTransform = rotate;
            Children.Add(arc);
            if (Motion.Enabled)
                rotate.BeginAnimation(RotateTransform.AngleProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 360, TimeSpan.FromSeconds(1)) { RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever });
        }
    }

    // ------------------------------------------------------------ files and drags

    private static void Reveal(string path, bool folder = false)
    {
        try
        {
            if (folder) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            else Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception) { /* Explorer missing is not something to report */ }
    }

    private static void OpenFile(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception) { /* no app for this type: nothing to do */ }
    }

    private void Notify(string message)
    {
        try
        {
            var centre = PointToScreen(new Point(ActualWidth / 2, ActualHeight / 2));
            _host.Toasts?.Show(message, new ScreenPoint((int)centre.X, (int)centre.Y));
        }
        catch (Exception) { /* window gone */ }
    }

    /// <summary>
    /// Makes an element a file drag source the way an Explorer icon is: press, move past the system drag distance, and the
    /// shell's own drag loop takes over (Copy or Link only, so the practice file is never moved away). If the shell call
    /// fails, WPF's drag with the same file list is the fallback.
    /// </summary>
    private sealed class DragSource(TourWindow window, string path)
    {
        private Point _start;
        private bool _pressed;

        public void Attach(UIElement element, Action? onClick, Action? doubleClick = null)
        {
            element.PreviewMouseLeftButtonDown += (_, e) =>
            {
                if (e.ClickCount == 2) { doubleClick?.Invoke(); return; }
                _pressed = true;
                _start = e.GetPosition(window);
                element.CaptureMouse();
            };
            element.PreviewMouseMove += (_, e) =>
            {
                if (!_pressed || e.LeftButton != MouseButtonState.Pressed) return;
                var now = e.GetPosition(window);
                if (Math.Abs(now.X - _start.X) < SystemParameters.MinimumHorizontalDragDistance
                    && Math.Abs(now.Y - _start.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                _pressed = false;
                element.ReleaseMouseCapture();
                Start(element);
            };
            element.PreviewMouseLeftButtonUp += (_, _) =>
            {
                var click = _pressed && onClick is not null;
                _pressed = false;
                element.ReleaseMouseCapture();
                if (click) onClick!();
            };
        }

        private void Start(UIElement element)
        {
            if (!File.Exists(path)) { window.Notify("That file is gone. Practice files are made again in a moment."); return; }
            window._dragging = true;
            try
            {
                var hwnd = new WindowInteropHelper(window).Handle;
                if (!ShellDrag.TryDrag(hwnd, [path], out _, out _))
                {
                    var data = new DataObject(DataFormats.FileDrop, new[] { path });
                    DragDrop.DoDragDrop(element, data, DragDropEffects.Copy | DragDropEffects.Link);
                }
            }
            finally
            {
                window._dragging = false;
                window.Dispatcher.BeginInvoke(() => window.Rebuild(animate: false, keepPage: true));
            }
        }
    }

    // ------------------------------------------------------------ chrome

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private void DarkTitleBar()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var on = 1;
        if (DwmSetWindowAttribute(handle, 20, ref on, sizeof(int)) != 0) DwmSetWindowAttribute(handle, 19, ref on, sizeof(int));
    }
}
