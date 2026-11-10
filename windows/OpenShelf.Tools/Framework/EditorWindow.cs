using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using OpenShelf.Core;
using OpenShelf.Ui;

namespace OpenShelf.Tools;

/// <summary>What an editor needs from the app: the files, where they were dropped, and the services results go to.</summary>
public sealed class EditorHost
{
    public EditorHost(WheelRun run, ToolServices services)
    {
        Run = run;
        Services = services;
    }

    public WheelRun Run { get; }
    public ToolServices Services { get; }
    public IReadOnlyList<string> Paths => Run.Paths;
    public ScreenPoint Pointer => Run.Pointer;
    public object? SourceShelf => Run.SourceShelf;

    /// <summary>Puts <paramref name="saved"/> on the shelf the files came from (when the preference allows) and shows <paramref name="message"/>.</summary>
    public void Finish(string message, IReadOnlyList<string> saved)
    {
        if (saved.Count > 0) Services.Shelves.AddResults(saved, SourceShelf);
        Services.Toasts.Show(message, Pointer);
    }
}

/// <summary>
/// The window every tool editor lives in (SPEC §2.4): a dark rounded window that zooms out of the drop point, a title, a canvas, optional
/// filmstrip and controls, and Cancel / primary buttons. Esc cancels and Enter applies. Saving never overwrites: editors write suffixed
/// copies beside the originals and the window closes with a toast, passing the copies to the shelf they came from.
/// <code>
/// sealed class MyEditor : EditorWindow
/// {
///     public MyEditor(EditorHost host, IReadOnlyList&lt;EditableImage&gt; images) : base(host, "My Tool", new Size(440, 520))
///     {
///         Canvas = new ImageSurface { Source = images[0].Preview, ImageSize = images[0].PixelSize };
///         Controls = new SwitchRow("Do the thing");
///         if (images.Count > 1) { Filmstrip = new FilmstripView(thumbs, names, withSameForAll: true); Filmstrip.Attach(edits); }
///         Refresh();
///     }
///     protected override string PrimaryTitle =&gt; edits.PrimaryTitle("Do");
///     protected override bool CanApply =&gt; edits.EditedCount &gt; 0;
///     protected override SaveOutcome Save(IProgress&lt;double&gt; progress, CancellationToken cancel) =&gt; MyOperation.Run(...);
/// }
/// </code>
/// </summary>
public abstract class EditorWindow : Window
{
    private const double Gutter = 18;   // room for the drop shadow

    private readonly EditorHost _host;
    private readonly Border _chrome = new();
    private readonly Grid _body = new();
    private readonly Border _canvasWell = new();
    private readonly ContentPresenter _filmstrip = new();
    private readonly ContentPresenter _controls = new();
    private readonly TextBlock _title;
    private readonly Button _reset;
    private readonly Button _cancel;
    private readonly Button _primary;
    private readonly ScaleTransform _scale = new(1, 1);
    private readonly Size _size;
    private CancellationTokenSource? _saving;
    private bool _closing;
    private Point _anchor = new(0.5, 0.5);

    protected EditorWindow(EditorHost host, string title, Size contentSize)
    {
        _host = host;
        _size = contentSize;
        Title = title;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        FontFamily = Theme.TextFont;
        FontSize = 13;
        Foreground = ToolKit.Brush("Text");
        Width = contentSize.Width + 2 * Gutter;
        Height = contentSize.Height + 2 * Gutter;
        Resources.MergedDictionaries.Add(ToolKit.Dictionary);
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);

        // ---- title bar
        _title = ToolKit.Text(title, 13, "Text", FontWeights.SemiBold);
        _title.HorizontalAlignment = HorizontalAlignment.Center;
        _title.VerticalAlignment = VerticalAlignment.Center;
        _title.IsHitTestVisible = false;
        var close = ToolKit.IconButton("", "Close without saving (Esc)", (_, _) => CancelEditing(), 24);
        close.FontSize = 10;
        close.HorizontalAlignment = HorizontalAlignment.Left;
        close.VerticalAlignment = VerticalAlignment.Center;
        close.Margin = new Thickness(10, 0, 0, 0);
        var titleBar = new Grid { Height = 42, Background = Brushes.Transparent };
        titleBar.Children.Add(_title);
        titleBar.Children.Add(close);
        titleBar.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };

        // ---- content
        _canvasWell.Background = ToolKit.Brush("Well");
        _canvasWell.BorderBrush = ToolKit.Brush("Hairline");
        _canvasWell.BorderThickness = new Thickness(1);
        _canvasWell.CornerRadius = new CornerRadius(12);
        _canvasWell.ClipToBounds = true;
        _filmstrip.Margin = new Thickness(0, 12, 0, 0);
        _controls.Margin = new Thickness(0, 12, 0, 0);
        _body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(_filmstrip, 1);
        Grid.SetRow(_controls, 2);
        _body.Children.Add(_canvasWell);
        _body.Children.Add(_filmstrip);
        _body.Children.Add(_controls);
        _body.Margin = new Thickness(16, 0, 16, 14);

        // ---- footer
        _reset = ToolKit.Button("Reset", (_, _) => { ResetEdits(); Refresh(); }, "GhostBtn");
        _reset.Visibility = Visibility.Collapsed;
        _cancel = ToolKit.Button("Cancel", (_, _) => CancelEditing());
        _cancel.IsCancel = false;
        _primary = ToolKit.Button("Apply", (_, _) => Apply(), "AccentBtn");
        _primary.MinWidth = 110;
        _primary.IsDefault = false;
        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        _cancel.Margin = new Thickness(0, 0, 8, 0);
        right.Children.Add(_cancel);
        right.Children.Add(_primary);
        var footer = new Grid { Margin = new Thickness(16, 12, 16, 12) };
        _reset.HorizontalAlignment = HorizontalAlignment.Left;
        footer.Children.Add(_reset);
        footer.Children.Add(right);
        var footerBar = new Border { Child = footer, BorderBrush = ToolKit.Brush("Hairline"), BorderThickness = new Thickness(0, 1, 0, 0) };

        var rows = new Grid();
        rows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        rows.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        rows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(_body, 1);
        Grid.SetRow(footerBar, 2);
        rows.Children.Add(titleBar);
        rows.Children.Add(_body);
        rows.Children.Add(footerBar);

        _chrome.Child = rows;
        _chrome.Margin = new Thickness(Gutter);
        _chrome.Background = ToolKit.Brush("Window");
        _chrome.BorderBrush = ToolKit.Brush("HairlineStrong");
        _chrome.BorderThickness = new Thickness(1);
        _chrome.CornerRadius = new CornerRadius(14);
        _chrome.Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Direction = 270, Opacity = 0.5, Color = Colors.Black };
        _chrome.RenderTransform = _scale;
        _chrome.RenderTransformOrigin = new Point(0.5, 0.5);
        _chrome.Clip = null;
        Content = _chrome;

        SourceInitialized += (_, _) => Place();
        Loaded += (_, _) => { Refresh(); ZoomIn(); };
        PreviewKeyDown += OnPreviewKeyDown;
        Closed += (_, _) => _saving?.Cancel();
    }

    // ---------------------------------------------------------------- what a subclass provides

    protected EditorHost Host => _host;

    /// <summary>The main area: an <see cref="ImageSurface"/> or any element. It sits in a dark well that takes the remaining height.</summary>
    protected UIElement? Canvas
    {
        get => _canvasWell.Child;
        set => _canvasWell.Child = value;
    }

    /// <summary>Whether the canvas sits in a dark rounded well (pictures do); editors made of plain controls turn it off.</summary>
    protected bool CanvasWell
    {
        set
        {
            _canvasWell.Background = value ? ToolKit.Brush("Well") : Brushes.Transparent;
            _canvasWell.BorderBrush = value ? ToolKit.Brush("Hairline") : Brushes.Transparent;
        }
    }

    /// <summary>Lets a canvas fill the well edge to edge.</summary>
    protected void SetCanvasPadding(double padding) => _canvasWell.Padding = new Thickness(padding);

    /// <summary>The controls under the canvas (and filmstrip).</summary>
    protected UIElement? Controls
    {
        get => _controls.Content as UIElement;
        set => _controls.Content = value;
    }

    /// <summary>The filmstrip for several files, between the canvas and the controls.</summary>
    protected FilmstripView? Filmstrip
    {
        get => _filmstrip.Content as FilmstripView;
        set => _filmstrip.Content = value;
    }

    /// <summary>Text of the primary button: "Crop 3 Images". Re-read on <see cref="Refresh"/>.</summary>
    protected abstract string PrimaryTitle { get; }

    /// <summary>Whether there is something to save; the button is disabled otherwise. Re-read on <see cref="Refresh"/>.</summary>
    protected abstract bool CanApply { get; }

    /// <summary>Whether the Reset button shows.</summary>
    protected virtual bool HasReset => false;

    /// <summary>Resets the shown file, or every file while they share one edit.</summary>
    protected virtual void ResetEdits() { }

    /// <summary>Whether Esc has a more local meaning right now, such as ending text entry; the editor then handles it itself.</summary>
    protected virtual bool EscapeIsHandledByEditor => false;

    /// <summary>Called just before saving; return false to stop (for example while text is still being typed).</summary>
    protected virtual bool PrepareToSave() => true;

    /// <summary>Whether <see cref="Save"/> must run on a thread that can draw with WPF (annotations).</summary>
    protected virtual bool NeedsStaThread => false;

    /// <summary>Writes the results. Runs on a worker thread; must not touch the UI. Files untouched by the edits are counted in the
    /// outcome, not saved. Throw <see cref="SaveException"/> for failures a person can act on.</summary>
    protected abstract SaveOutcome Save(IProgress<double> progress, CancellationToken cancel);

    /// <summary>A save wrote nothing (every file was left as it was). The window stays open; override to explain in the window.</summary>
    protected virtual void NothingSaved(SaveOutcome outcome) { }

    /// <summary>Re-reads the primary button's text and enabled state, and the Reset button.</summary>
    protected void Refresh()
    {
        _primary.Content = IsBusy ? "Saving…" : PrimaryTitle;
        _primary.IsEnabled = !IsBusy && CanApply;
        _reset.Visibility = HasReset ? Visibility.Visible : Visibility.Collapsed;
        _reset.IsEnabled = !IsBusy;
    }

    protected bool IsBusy { get; private set; }

    /// <summary>Changes the window's title (shown in the bar).</summary>
    protected void SetTitle(string title)
    {
        Title = title;
        _title.Text = title;
    }

    // ---------------------------------------------------------------- keys

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (EscapeIsHandledByEditor) return;
            e.Handled = true;
            CancelEditing();
        }
        else if (e.Key == Key.Enter && !Keyboard.IsKeyDown(Key.LeftAlt) && !Keyboard.IsKeyDown(Key.RightAlt))
        {
            if (EscapeIsHandledByEditor) return;                                  // the editor is taking text: Enter ends it
            var focused = Keyboard.FocusedElement;
            if (focused is Button b && b != _primary) return;                     // Enter presses the focused button
            if (focused is TextBox { AcceptsReturn: true }) return;
            if (focused is ComboBox { IsDropDownOpen: true }) return;
            if (focused is TextBox box)
            {
                // Commit what was typed before applying.
                (box as NumberBox)?.Commit();
                Keyboard.ClearFocus();
            }
            e.Handled = true;
            Apply();
        }
    }

    // ---------------------------------------------------------------- saving

    /// <summary>Applies the edits: saves in the background, then closes with a toast, or reports why not.</summary>
    protected async void Apply()
    {
        if (IsBusy || !CanApply || _closing) return;
        if (!PrepareToSave()) return;
        IsBusy = true;
        Refresh();
        var cancel = _saving = new CancellationTokenSource();
        var progress = new Progress<double>(_ => { });
        try
        {
            var outcome = await RunAsync(() => Save(progress, cancel.Token));
            IsBusy = false;
            if (outcome.SavedAnything)
            {
                _host.Finish(outcome.Message, outcome.Saved);
                CloseAnimated();
            }
            else
            {
                _host.Services.Toasts.Show(outcome.Message, _host.Pointer);
                NothingSaved(outcome);
            }
        }
        catch (OperationCanceledException)
        {
            IsBusy = false;
        }
        catch (Exception e)
        {
            IsBusy = false;
            var saved = (e as SaveException)?.SavedBefore ?? [];
            var message = e is SaveException or IOException ? e.Message : "The file couldn’t be saved.";
            _host.Finish("Couldn’t save · " + message, saved);
        }
        finally
        {
            IsBusy = false;
            Refresh();
        }
    }

    private Task<SaveOutcome> RunAsync(Func<SaveOutcome> work)
    {
        if (!NeedsStaThread) return Task.Run(work);
        var completion = new TaskCompletionSource<SaveOutcome>();
        var thread = new Thread(() =>
        {
            try { completion.SetResult(work()); }
            catch (Exception e) { completion.SetException(e); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    // ---------------------------------------------------------------- appearing and going

    private void Place()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var (work, dpi) = WindowPlacement.MonitorAt(_host.Pointer);
        var frame = WindowPlacement.Frame(_host.Pointer, Width * dpi, Height * dpi, work);
        SetWindowPos(handle, new IntPtr(-1), (int)frame.X, (int)frame.Y, (int)frame.Width, (int)frame.Height, 0x0010 /* NOACTIVATE */);
        // The scale grows out of the drop point, in the chrome's own units.
        var x = (_host.Pointer.X - frame.X) / dpi - Gutter;
        var y = (_host.Pointer.Y - frame.Y) / dpi - Gutter;
        _anchor = new Point(Math.Clamp(x / _size.Width, -0.2, 1.2), Math.Clamp(y / _size.Height, -0.2, 1.2));
        _chrome.RenderTransformOrigin = _anchor;
    }

    private void ZoomIn()
    {
        Motion.Animate(_scale, ScaleTransform.ScaleXProperty, 1, 420, Motion.Spring, from: 0.3);
        Motion.Animate(_scale, ScaleTransform.ScaleYProperty, 1, 420, Motion.Spring, from: 0.3);
        Motion.Animate(_chrome, OpacityProperty, 1, 160, from: 0);
        Activate();
        Focus();
        // Windows may refuse focus to a background app; this nudges it forward.
        Topmost = false;
        Topmost = true;
    }

    /// <summary>Closes without saving.</summary>
    protected void CancelEditing() => CloseAnimated();

    private void CloseAnimated()
    {
        if (_closing) return;
        _closing = true;
        _saving?.Cancel();
        IsEnabled = false;
        Motion.Animate(_scale, ScaleTransform.ScaleXProperty, 0.3, 200, Motion.EaseInOut);
        Motion.Animate(_scale, ScaleTransform.ScaleYProperty, 0.3, 200, Motion.EaseInOut);
        Motion.Animate(_chrome, OpacityProperty, 0, 190, completed: Close);
    }

    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
}
