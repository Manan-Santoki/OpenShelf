using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OpenShelf.Tools.Pdf;
using Orientation = System.Windows.Controls.Orientation;

namespace OpenShelf.Tools.PdfEditors;

/// <summary>The "Split" tool: one PDF into several, every page, every N pages or by ranges, with a live preview of the parts and their
/// file names. With several PDFs a filmstrip moves between them and "Same for all" (on) applies one rule to every PDF, ranges read
/// against the longest and cut to each (Sources/OpenShelf/Tools/PDF/PDFSplitEditor.swift).</summary>
public sealed class PdfSplitWindow : PdfEditorWindow
{
    private const int PreviewRows = 60;

    private readonly SplitModel _model;
    private readonly Segmented _modes;
    private readonly TextBlock _modeNote = ToolKit.Text("Each page becomes its own PDF.", 12, "Text2", wrap: true);
    private readonly NumberBox _chunk = new(56, 2);
    private readonly Button _less;
    private readonly Button _more;
    private readonly StackPanel _chunkRow = new() { Orientation = Orientation.Horizontal };
    private readonly Grid _rangesRow;
    private readonly TextBox _ranges = new() { Style = ToolKit.Style("Field") };
    private readonly TextBlock _summaryGlyph;
    private readonly TextBlock _summaryText;
    private readonly TextBlock _total = ToolKit.Text("", 12, "Text2");
    private readonly StackPanel _parts = new();
    private readonly TextBlock _partsTitle = ToolKit.Text("", 10, "Text3", FontWeights.SemiBold);
    private readonly FilmstripView? _filmstrip;
    private bool _silent;

    public static Size ContentSizeFor(int count) => new(460, count > 1 ? 560 : 520);

    public PdfSplitWindow(EditorHost host, IReadOnlyList<PdfFileState> files) : base(host, files.Count > 1 ? "Split PDFs" : "Split PDF", ContentSizeFor(files.Count))
    {
        _model = new SplitModel(files);
        CanvasWell = false;
        SetCanvasPadding(0);

        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var top = new StackPanel();

        if (files.Count > 1)
        {
            _filmstrip = new FilmstripView(files.Select(_ => (System.Windows.Media.ImageSource?)null).ToList(), files.Select(f => f.Name).ToList(),
                withSameForAll: true, itemName: "PDF", placeholderGlyph: Parts.PageGlyph);
            _filmstrip.Attach(_model.Edits);
            Filmstrip = _filmstrip;
        }
        else
        {
            var header = Parts.Header(files[0].Name, files[0].IsUsable ? PdfText.Pages(files[0].PageCount) : files[0].IsLocked ? "Password-protected" : "Can’t be opened", null, out var picture);
            top.Children.Add(header);
            Loaded += async (_, _) =>
            {
                if (!files[0].IsUsable) return;
                try { if (await Parts.FirstPageAsync(files[0].Path) is { } page) Parts.SetPicture(picture, page, null); }
                catch (Exception) { }
            };
        }

        _modes = new Segmented(["Every page", "Every N pages", "Ranges"], 0, 12);
        _modes.Margin = new Thickness(0, files.Count > 1 ? 0 : 14, 0, 0);
        _modes.SelectionChanged += index => _model.Update(r => r with { Mode = (PdfSplitMode)index });
        top.Children.Add(_modes);

        // "pages each" stepper
        _less = ToolKit.IconButton("", "Fewer pages", (_, _) => StepChunk(-1), 26);
        _more = ToolKit.IconButton("", "More pages", (_, _) => StepChunk(1), 26);
        _chunk.ValueCommitted += value => SetChunk(value);
        ToolKit.Name(_chunk, "Pages each");
        var stepper = new StackPanel { Orientation = Orientation.Horizontal };
        _chunk.Margin = new Thickness(0, 0, 6, 0);
        stepper.Children.Add(_chunk);
        stepper.Children.Add(_less);
        stepper.Children.Add(_more);
        _chunkRow.Children.Add(ToolKit.Row("Pages each", stepper, 78));
        _chunkRow.Visibility = Visibility.Collapsed;

        _rangesRow = ToolKit.Row("Ranges", _ranges, 78);
        _rangesRow.Visibility = Visibility.Collapsed;
        ToolKit.Name(_ranges, "Page ranges");
        _ranges.TextChanged += (_, _) =>
        {
            if (!_silent) _model.Update(r => r with { RangesText = _ranges.Text });
        };

        var modeArea = new Grid { Margin = new Thickness(0, 12, 0, 0), MinHeight = 30 };
        modeArea.Children.Add(_modeNote);
        modeArea.Children.Add(_chunkRow);
        modeArea.Children.Add(_rangesRow);
        top.Children.Add(modeArea);

        var (card, text, _, glyph) = Parts.SummaryCard("");
        _summaryText = text;
        _summaryGlyph = glyph;
        var cardRow = (StackPanel)card.Child;
        _total.VerticalAlignment = VerticalAlignment.Center;
        _total.HorizontalAlignment = HorizontalAlignment.Right;
        var cardGrid = new Grid();
        cardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        cardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        card.Child = cardGrid;
        cardGrid.Children.Add(cardRow);
        Grid.SetColumn(_total, 1);
        cardGrid.Children.Add(_total);
        card.Margin = new Thickness(0, 12, 0, 0);
        top.Children.Add(card);
        _partsTitle.Margin = new Thickness(2, 14, 0, 6);
        top.Children.Add(_partsTitle);
        layout.Children.Add(top);

        var list = new ScrollViewer { Content = _parts, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 4, 0) };
        Grid.SetRow(list, 1);
        layout.Children.Add(list);
        Canvas = layout;

        _model.Edits.Changed += Sync;
        Sync();
        if (_filmstrip is not null) Loaded += (_, _) => LoadFilmstripPictures();
    }

    private async void LoadFilmstripPictures()
    {
        for (var i = 0; i < _model.Files.Count; i++)
        {
            if (!_model.Files[i].IsUsable) continue;
            try
            {
                if (await Parts.FirstPageAsync(_model.Files[i].Path) is { } picture) _filmstrip!.SetThumbnail(i, picture);
            }
            catch (Exception) { }
        }
    }

    private void StepChunk(int delta) => SetChunk(_model.Rule.ChunkSize + delta);

    private void SetChunk(int value)
    {
        var clamped = Math.Clamp(value, 1, Math.Max(_model.PageLimit, 1));
        if (clamped == _model.Rule.ChunkSize)
        {
            _chunk.Value = clamped;
            return;
        }
        _model.Update(r => r with { ChunkSize = clamped });
    }

    /// <summary>Brings every control in line with the model: the shown PDF's rule, the summary and the preview.</summary>
    private void Sync()
    {
        _silent = true;
        try
        {
            var rule = _model.Rule;
            _modes.SetSilently((int)rule.Mode);
            _modeNote.Visibility = rule.Mode == PdfSplitMode.EveryPage ? Visibility.Visible : Visibility.Collapsed;
            _chunkRow.Visibility = rule.Mode == PdfSplitMode.Chunks ? Visibility.Visible : Visibility.Collapsed;
            _rangesRow.Visibility = rule.Mode == PdfSplitMode.Ranges ? Visibility.Visible : Visibility.Collapsed;
            _chunk.Value = rule.ChunkSize;
            _less.IsEnabled = rule.ChunkSize > 1;
            _more.IsEnabled = rule.ChunkSize < Math.Max(_model.PageLimit, 1);
            if (_ranges.Text != rule.RangesText) _ranges.Text = rule.RangesText;
            _ranges.ToolTip = _model.RangesPlaceholder;
            ShowSummary();
            ShowPreview();
        }
        finally { _silent = false; }
        Refresh();
    }

    private void ShowSummary()
    {
        var summary = _model.Summary();
        _summaryText.Text = summary.Text;
        Parts.Tint(_summaryText, summary.Tone);
        _summaryGlyph.Text = summary.Tone switch
        {
            Tone.Warning => "",
            Tone.Muted => "",
            Tone.Info => "",
            _ => "",
        };
        _summaryGlyph.Foreground = summary.Tone == Tone.Warning ? ToolKit.Brush("Danger") : ToolKit.Brush("Accent");
        _total.Text = _model.TotalText ?? "";
    }

    private void ShowPreview()
    {
        _parts.Children.Clear();
        var parts = _model.Preview();
        _partsTitle.Text = parts.Count == 0 ? "" : "NEW FILES";
        if (parts.Count == 0) return;
        var rows = new StackPanel();
        for (var i = 0; i < parts.Count && i < PreviewRows; i++)
        {
            var row = new Grid { Margin = new Thickness(10, 6, 10, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var pages = ToolKit.Text(parts[i].Pages, 12, "Text2");
            var name = ToolKit.Text(parts[i].FileName, 12);
            name.ToolTip = parts[i].FileName;
            Grid.SetColumn(name, 1);
            row.Children.Add(pages);
            row.Children.Add(name);
            if (i > 0) rows.Children.Add(new Border { Height = 1, Background = ToolKit.Brush("Hairline") });
            rows.Children.Add(row);
        }
        if (parts.Count > PreviewRows)
        {
            rows.Children.Add(new Border { Height = 1, Background = ToolKit.Brush("Hairline") });
            var more = ToolKit.Text($"… and {parts.Count - PreviewRows} more", 12, "Text3");
            more.Margin = new Thickness(10, 6, 10, 6);
            rows.Children.Add(more);
        }
        _parts.Children.Add(ToolKit.Card(rows, 0));
    }

    protected override string PrimaryTitle => _model.PrimaryTitle;
    protected override bool CanApply => _model.CanApply;
    protected override bool HasReset => true;

    protected override void ResetEdits() => _model.Edits.Reset();

    public override void ApplyDemoState(string state)
    {
        foreach (var token in state.Split(','))
        {
            if (token == "every") _modes.SelectedIndex = 0;
            else if (token == "chunks") { _modes.SelectedIndex = 1; SetChunk(3); }
            else if (token == "ranges") _modes.SelectedIndex = 2;
            else if (token.StartsWith("text=")) _ranges.Text = token[5..].Replace('_', ' ').Replace(';', ',');
            else if (token == "next" && _filmstrip is not null) _filmstrip.Selection += 1;
            else if (token == "alone") _model.Edits.SameForAll = false;
        }
    }

    protected override SaveOutcome SaveCore(IProgress<double> progress, CancellationToken cancel) => _model.Save(progress, cancel);
}
