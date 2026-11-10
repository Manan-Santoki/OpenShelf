using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OpenShelf.Core;
using OpenShelf.Vision;
using Orientation = System.Windows.Controls.Orientation;

namespace OpenShelf.Tools.VisionEditors;

/// <summary>
/// The "OCR" tool (SPEC §2.4): reads the text in images and scanned PDFs, saved as a searchable PDF or as text beside the originals
/// ("Name OCR.pdf" / "Name OCR.txt"). A small window with the choice, a progress bar while it reads and the file being worked on;
/// closing cancels between pages. With no Windows OCR language installed it says where to add one.
/// </summary>
public sealed class OcrEditor : EditorWindow, IEditorDemo
{
    public static readonly Size ContentSize = new(430, 392);

    /// <summary>The Settings page where recognition languages are added.</summary>
    public const string LanguageSettingsUri = "ms-settings:regionlanguage";

    private readonly IReadOnlyList<string> _paths;
    private readonly OcrRunner _runner;
    private readonly Segmented _outputPicker = new(["Searchable PDF", "Text"], 0, 16);
    private readonly ThinProgress _bar = new();
    private readonly TextBlock _detail = ToolKit.Text("", 11, "Text3");
    private readonly TextBlock _notice = ToolKit.Text("", 12, "Text", wrap: true);
    private readonly Border _noticeCard;
    private readonly Grid _status = new() { Height = 150, Margin = new Thickness(0, 12, 0, 0) };
    private readonly StackPanel _progressPanel = new() { VerticalAlignment = VerticalAlignment.Top, Visibility = Visibility.Hidden };
    private Button _settingsButton = null!;
    private OcrOutput _output = OcrOutput.SearchablePdf;
    private bool _noLanguage;

    /// <param name="runner">Replaces the recogniser (for tests and the demo).</param>
    /// <param name="isAvailable">Replaces the check for an installed OCR language.</param>
    public OcrEditor(EditorHost host, IReadOnlyList<string> paths, OcrRunner? runner = null, Func<bool>? isAvailable = null)
        : base(host, "Recognise Text", ContentSize)
    {
        _paths = paths;
        _runner = runner ?? new OcrRunner();

        CanvasWell = false;
        SetCanvasPadding(0);
        var stack = new StackPanel();
        var name = ToolKit.Text(OcrRunner.Title(paths), 13, "Text", FontWeights.SemiBold);
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        name.ToolTip = string.Join("\n", paths.Select(Path.GetFileName));
        stack.Children.Add(name);

        _outputPicker.SelectionChanged += index =>
        {
            _output = index == 1 ? OcrOutput.Text : OcrOutput.SearchablePdf;
            Refresh();
        };
        var row = ToolKit.Row("Save as", _outputPicker, 64);
        row.Margin = new Thickness(0, 14, 0, 0);
        _outputPicker.HorizontalAlignment = HorizontalAlignment.Left;
        stack.Children.Add(row);

        var note = ToolKit.Text("Read on this PC in your Windows OCR languages. PDF pages that already have text are kept.", 11, "Text3", wrap: true);
        note.Margin = new Thickness(0, 14, 0, 0);
        stack.Children.Add(note);

        // One slot for what is going on: progress while reading, an explanation when it cannot.
        _detail.Margin = new Thickness(0, 8, 0, 0);
        _progressPanel.Children.Add(_bar);
        _progressPanel.Children.Add(_detail);

        var settings = _settingsButton = ToolKit.Button("Open Language Settings", (_, _) => OpenLanguageSettings());
        settings.HorizontalAlignment = HorizontalAlignment.Left;
        settings.Margin = new Thickness(0, 8, 0, 0);
        var noticeBody = new StackPanel();
        noticeBody.Children.Add(_notice);
        noticeBody.Children.Add(settings);
        var noticeRow = new Grid();
        noticeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        noticeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var warning = ToolKit.Glyph("", 16, "Danger");
        warning.VerticalAlignment = VerticalAlignment.Top;
        warning.Margin = new Thickness(0, 1, 10, 0);
        Grid.SetColumn(noticeBody, 1);
        noticeRow.Children.Add(warning);
        noticeRow.Children.Add(noticeBody);
        _noticeCard = ToolKit.Card(noticeRow, 12);
        _noticeCard.VerticalAlignment = VerticalAlignment.Top;
        _noticeCard.Visibility = Visibility.Collapsed;
        _status.Children.Add(_progressPanel);
        _status.Children.Add(_noticeCard);
        stack.Children.Add(_status);
        Canvas = stack;

        if (!(isAvailable ?? (() => OcrService.IsAvailable))())
        {
            _noLanguage = true;
            ShowNotice(new OcrNoLanguageException().Message, offerSettings: true);
        }
        Refresh();
    }

    protected override string PrimaryTitle => "Save";
    protected override bool CanApply => !_noLanguage;

    private static void OpenLanguageSettings()
    {
        try { Process.Start(new ProcessStartInfo(LanguageSettingsUri) { UseShellExecute = true }); }
        catch (Exception) { /* nothing more to offer; the path is written in the message */ }
    }

    private void ShowNotice(string message, bool offerSettings)
    {
        _notice.Text = message;
        _noticeCard.Visibility = Visibility.Visible;
        _settingsButton.Visibility = offerSettings ? Visibility.Visible : Visibility.Collapsed;
        _progressPanel.Visibility = Visibility.Hidden;
    }

    private void ShowProgress(OcrProgress progress)
    {
        _noticeCard.Visibility = Visibility.Collapsed;
        _progressPanel.Visibility = Visibility.Visible;
        _bar.Value = progress.Fraction;
        _detail.Text = progress.Detail ?? (progress.Fraction >= 1 ? "" : "Reading…");
    }

    protected override bool PrepareToSave()
    {
        _noticeCard.Visibility = Visibility.Collapsed;
        ShowProgress(new OcrProgress(0, OcrRunner.Detail(0, _paths.Count)));
        return true;
    }

    protected override SaveOutcome Save(IProgress<double> progress, CancellationToken cancel)
    {
        var output = _output;
        try
        {
            // Save runs on a worker thread, so waiting here never blocks the window.
            var result = _runner.RunAsync(_paths, output, p => Dispatcher.InvokeAsync(() => ShowProgress(p)), cancel).GetAwaiter().GetResult();
            if (result.Saved.Count == 0)
            {
                var message = OcrRunner.FailureMessage(result);
                Dispatcher.InvokeAsync(() => ShowNotice(message, offerSettings: false));
                throw new SaveException(SaveException.Reason.Failed, message);
            }
            return OcrRunner.ToOutcome(result, output);
        }
        catch (OcrNoLanguageException e)
        {
            Dispatcher.InvokeAsync(() => { _noLanguage = true; ShowNotice(e.Message, offerSettings: true); Refresh(); });
            throw new SaveException(SaveException.Reason.Failed, e.Message, e);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SaveException)
        {
            throw;
        }
        catch (Exception e) when (e is OpenShelf.Convert.ConversionException or VisionException)
        {
            Dispatcher.InvokeAsync(() => ShowNotice(e.Message, offerSettings: false));
            throw new SaveException(SaveException.Reason.Failed, e.Message, e);
        }
    }

    void IEditorDemo.ApplyDemoState(string state)
    {
        foreach (var token in state.Split(','))
            switch (token)
            {
                case "text":
                    _outputPicker.SetSilently(1);
                    _output = OcrOutput.Text;
                    Refresh();
                    break;
                case "progress":
                    ShowProgress(new OcrProgress(0.42, OcrRunner.Detail(1, Math.Max(_paths.Count, 3))));
                    break;
            }
    }
}
