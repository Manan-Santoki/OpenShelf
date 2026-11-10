using System.Windows;
using System.Windows.Controls;
using OpenShelf.Tools.Pdf;
using Orientation = System.Windows.Controls.Orientation;

namespace OpenShelf.Tools.PdfEditors;

/// <summary>The PDF "Compress" tool: light, balanced or strong copies of one or more PDFs, with the estimated size. The estimate is a
/// real compression of the first PDF done in memory, scaled to the batch (Sources/OpenShelf/Tools/PDF/PDFCompressEditor.swift).</summary>
public sealed class PdfCompressWindow : PdfEditorWindow
{
    public static readonly Size ContentSize = new(450, 400);

    private readonly CompressModel _model;
    private readonly Segmented _levels;
    private readonly TextBlock _summary = ToolKit.Text("", 12, "Text2", wrap: true);
    private readonly TextBlock _estimateText;
    private readonly TextBlock _savingText;
    private readonly TextBlock _note = ToolKit.Status(2);
    private readonly System.Windows.Controls.ContentControl _picture;
    private CancellationTokenSource? _estimating;
    private string? _notice;
    private string? _nothingSaved;

    public PdfCompressWindow(EditorHost host, IReadOnlyList<PdfFileState> files) : base(host, "Compress PDF", ContentSize)
    {
        _model = new CompressModel(files);
        CanvasWell = false;
        SetCanvasPadding(0);
        FitHeightToContent();

        var stack = new StackPanel();
        stack.Children.Add(Parts.Header(_model.Title, _model.Subtitle, files.Count > 1 ? files.Count : null, out _picture));

        _levels = new Segmented(Enum.GetValues<PdfCompressionLevel>().Select(l => l.Title()).ToList(), (int)_model.Level, 24);
        _levels.Margin = new Thickness(0, 14, 0, 0);
        _levels.SelectionChanged += index =>
        {
            _model.Level = (PdfCompressionLevel)index;
            Changed();
        };
        stack.Children.Add(_levels);

        var info = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        var infoGlyph = ToolKit.Icon("info", 12, "Text3");
        infoGlyph.VerticalAlignment = VerticalAlignment.Top;
        infoGlyph.Margin = new Thickness(0, 2, 8, 0);
        _summary.MaxWidth = ContentSize.Width - 32 - 24;
        info.Children.Add(infoGlyph);
        info.Children.Add(_summary);
        stack.Children.Add(info);

        var (card, text, detail, _) = Parts.SummaryCard("minimize-2");
        _estimateText = text;
        _savingText = detail;
        card.Margin = new Thickness(0, 14, 0, 0);
        stack.Children.Add(card);

        _note.Margin = new Thickness(0, 10, 0, 0);
        stack.Children.Add(_note);
        Canvas = stack;

        _summary.Text = _model.Level.Summary();
        UpdateText();
        Loaded += (_, _) =>
        {
            LoadPicture();
            UpdateEstimate();
        };
        Closed += (_, _) => _estimating?.Cancel();
    }

    private async void LoadPicture()
    {
        if (!_model.Files[0].IsUsable) return;
        try
        {
            if (await Parts.FirstPageAsync(_model.Files[0].Path) is { } picture)
                Parts.SetPicture(_picture, picture, _model.Files.Count > 1 ? _model.Files.Count : null);
        }
        catch (Exception) { }
    }

    private void Changed()
    {
        _notice = null;
        _summary.Text = _model.Level.Summary();
        UpdateEstimate();
        Refresh();
    }

    private void UpdateText()
    {
        _estimateText.Text = _model.EstimateText;
        _estimateText.FontWeight = _model.Estimate is null ? FontWeights.Normal : FontWeights.SemiBold;
        _savingText.Text = _model.SavingText;
        _savingText.Foreground = _model.Estimate is not null && _model.SavingPercent <= 0 ? ToolKit.Brush("Danger") : ToolKit.Brush("Text3");

        var problem = _model.Files.FirstOrDefault(f => !f.IsUsable)?.Problem;
        _note.Text = _notice ?? problem ?? "";
        _note.Foreground = _notice is not null || problem is not null ? ToolKit.Brush("Danger") : ToolKit.Brush("Text3");
    }

    private async void UpdateEstimate()
    {
        _estimating?.Cancel();
        var cancel = _estimating = new CancellationTokenSource();
        _model.StartEstimate();
        UpdateText();
        Refresh();
        var first = _model.Files[0];
        if (!first.IsUsable)
        {
            _model.FailEstimate();
            UpdateText();
            return;
        }
        try
        {
            await Task.Delay(250, cancel.Token);
            var result = await PdfCompressor.EstimateAsync(first.Path, _model.Level, null, cancel.Token);
            if (cancel.IsCancellationRequested) return;
            _model.SetEstimate(result.CompressedSize);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception) { _model.FailEstimate(); }
        UpdateText();
        Refresh();
    }

    protected override string PrimaryTitle => "Compress";
    protected override bool CanApply => !_model.IsKnownNotSmaller && _model.Files.All(f => f.IsUsable);
    protected override bool HasReset => true;

    protected override void ResetEdits()
    {
        _model.Level = PdfCompressionLevel.Balanced;
        _levels.SetSilently((int)_model.Level);
        Changed();
    }

    public override void ApplyDemoState(string state)
    {
        foreach (var token in state.Split(','))
            switch (token)
            {
                case "strong":
                    _levels.SelectedIndex = 2;
                    break;
                case "light":
                    _levels.SelectedIndex = 0;
                    break;
            }
    }

    protected override SaveOutcome SaveCore(IProgress<double> progress, CancellationToken cancel)
    {
        var outcome = _model.Save(progress, cancel, out var nothing);
        _nothingSaved = nothing;
        return outcome;
    }

    protected override void NothingSaved(SaveOutcome outcome)
    {
        _notice = _nothingSaved;
        UpdateText();
    }
}
