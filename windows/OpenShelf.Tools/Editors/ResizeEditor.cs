using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using OpenShelf.Tools.Imaging;

namespace OpenShelf.Tools.Editors;

/// <summary>Resizes one or several images by percentage, longest side or exact size ("Name Resized.jpg").</summary>
public sealed class ResizeEditor : EditorWindow, IEditorDemo
{
    public static readonly Size ContentSize = new(450, 440);

    private readonly IReadOnlyList<EditableImage> _images;
    private ResizeRule _rule;
    private bool _keepsProportions = true;

    private readonly Segmented _modePicker;
    private readonly LabeledSlider _percent;
    private readonly NumberBox _longSide = new(90);
    private readonly NumberBox _width = new(72);
    private readonly NumberBox _height = new(72);
    private readonly Button _lock;
    private readonly SwitchRow _enlarge;
    private readonly TextBlock _summary = ToolKit.Text("", 12, "Text2", wrap: true);
    private readonly StackPanel _modeHost = new();
    private readonly UIElement _percentRow;
    private readonly UIElement _longRow;
    private readonly UIElement _exactRow;

    public ResizeEditor(EditorHost host, IReadOnlyList<EditableImage> images) : base(host, "Resize", ContentSize)
    {
        _images = images;
        var first = images[0];
        _rule = new ResizeRule { Width = first.Width, Height = 0 };

        CanvasWell = false;
        SetCanvasPadding(0);
        var stack = new StackPanel();
        stack.Children.Add(EditorParts.Header(first.Preview, images.Count == 1 ? first.Name : $"{images.Count} images",
            $"{first.Width} × {first.Height} px{(images.Count > 1 ? " (first)" : "")}"));

        var presets = new UniformGrid { Rows = 1, Margin = new Thickness(0, 14, 0, 0) };
        void Preset(string title, Action apply)
        {
            var button = ToolKit.Preset(title, (_, _) => { apply(); Sync(); });
            button.Margin = new Thickness(0, 0, 6, 0);
            presets.Children.Add(button);
        }
        Preset("50%", () => _rule = _rule with { Mode = ResizeBy.Percent, Percent = 50 });
        Preset("1080 px", () => _rule = _rule with { Mode = ResizeBy.LongEdge, LongEdge = 1080 });
        Preset("1920 px", () => _rule = _rule with { Mode = ResizeBy.LongEdge, LongEdge = 1920 });
        Preset("3840 px", () => _rule = _rule with { Mode = ResizeBy.LongEdge, LongEdge = 3840 });
        stack.Children.Add(presets);

        _modePicker = new Segmented(["Percent", "Long side", "Exact size"], 0, 14);
        _modePicker.SelectionChanged += index =>
        {
            _rule = _rule with { Mode = (ResizeBy)index };
            Sync();
        };
        var by = ToolKit.Row("Resize by", _modePicker);
        by.Margin = new Thickness(0, 14, 0, 0);
        stack.Children.Add(by);

        _percent = new LabeledSlider("Scale", 5, 100, 50, "%");
        _percent.ValueChanged += v => { _rule = _rule with { Percent = Math.Round(v) }; Sync(true); };
        _percentRow = Spaced(_percent);

        var longLine = new StackPanel { Orientation = Orientation.Horizontal };
        longLine.Children.Add(_longSide);
        longLine.Children.Add(Unit("px"));
        _longSide.ValueCommitted += v => { _rule = _rule with { LongEdge = Math.Max(1, v) }; Sync(); };
        _longRow = Spaced(ToolKit.Row("Long side", longLine));

        var exact = new StackPanel { Orientation = Orientation.Horizontal };
        exact.Children.Add(Unit("W", 0));
        exact.Children.Add(_width);
        exact.Children.Add(Unit("H", 10));
        exact.Children.Add(_height);
        _lock = ToolKit.IconButton("", "Proportions are kept — height follows width", (_, _) => ToggleLock(), 28);
        _lock.Margin = new Thickness(8, 0, 0, 0);
        exact.Children.Add(_lock);
        _width.ValueCommitted += v => { _rule = _rule with { Width = Math.Max(1, v) }; Sync(); };
        _height.ValueCommitted += v => { _rule = _rule with { Height = Math.Max(1, v) }; Sync(); };
        _exactRow = Spaced(ToolKit.Row("Size", exact));

        _modeHost.Children.Add(_percentRow);
        _modeHost.Children.Add(_longRow);
        _modeHost.Children.Add(_exactRow);
        _modeHost.Height = 40;
        stack.Children.Add(_modeHost);

        _enlarge = new SwitchRow("Allow enlarging", false, "Off keeps images from getting bigger than they are, which only adds blur");
        _enlarge.Toggled += () =>
        {
            _rule = _rule with { AllowsEnlarging = _enlarge.IsChecked };
            _percent.Maximum = _enlarge.IsChecked ? 400 : 100;
            if (!_enlarge.IsChecked && _rule.Percent > 100) _rule = _rule with { Percent = 100 };
            Sync();
        };
        _enlarge.Margin = new Thickness(0, 10, 0, 0);
        stack.Children.Add(_enlarge);

        var card = ToolKit.Card(_summary, 12);
        card.Margin = new Thickness(0, 14, 0, 0);
        stack.Children.Add(card);
        Canvas = stack;
        Sync();
    }

    private static UIElement Spaced(UIElement element)
    {
        ((FrameworkElement)element).Margin = new Thickness(0, 12, 0, 0);
        return element;
    }

    private static TextBlock Unit(string text, double left = 6)
    {
        var label = ToolKit.Text(text, 12, "Text2");
        label.VerticalAlignment = VerticalAlignment.Center;
        label.Margin = new Thickness(left, 0, 6, 0);
        return label;
    }

    private void ToggleLock()
    {
        _keepsProportions = !_keepsProportions;
        if (_keepsProportions) _rule = _rule with { Height = 0 };
        else _rule = _rule with { Height = (int)EffectiveRule.TargetSize(_images[0].PixelSize).Height };
        Sync();
    }

    /// <summary>The rule as it will be applied: with proportions kept, height follows width.</summary>
    private ResizeRule EffectiveRule => _rule.Mode == ResizeBy.Exact && _keepsProportions ? _rule with { Height = 0 } : _rule;

    private int ChangingCount => _images.Count(i => EffectiveRule.Changes(i.PixelSize));

    /// <summary>Brings every control and the summary in line with the rule.</summary>
    private void Sync(bool fromSlider = false)
    {
        _modePicker.SetSilently((int)_rule.Mode);
        _percentRow.Visibility = _rule.Mode == ResizeBy.Percent ? Visibility.Visible : Visibility.Collapsed;
        _longRow.Visibility = _rule.Mode == ResizeBy.LongEdge ? Visibility.Visible : Visibility.Collapsed;
        _exactRow.Visibility = _rule.Mode == ResizeBy.Exact ? Visibility.Visible : Visibility.Collapsed;
        if (!fromSlider) _percent.SetSilently(_rule.Percent);
        _longSide.Value = _rule.LongEdge;
        var first = _images[0].PixelSize;
        _width.Value = _rule.Width;
        _height.Value = _keepsProportions ? (int)EffectiveRule.TargetSize(first).Height : _rule.Height;
        _height.IsEnabled = !_keepsProportions;
        _lock.Content = _keepsProportions ? "" : "";
        _lock.ToolTip = _keepsProportions ? "Proportions are kept — height follows width" : "Set width and height freely";
        _enlarge.SetSilently(_rule.AllowsEnlarging);

        var rule = EffectiveRule;
        var target = rule.TargetSize(first);
        var line = $"{first.Width:0} × {first.Height:0}  →  {target.Width:0} × {target.Height:0} px";
        if (target == first) line += "  (no change)";
        if (_images.Count > 1)
        {
            var changing = ChangingCount;
            var unchanged = _images.Count - changing;
            line += unchanged == 0
                ? "\nThe same rule applies to each image."
                : $"\n{changing} of {_images.Count} will be resized · {unchanged} {rule.UnchangedLabel}";
        }
        _summary.Text = line;
        Refresh();
    }

    protected override string PrimaryTitle => "Resize";
    protected override bool CanApply => ChangingCount > 0;
    protected override bool HasReset => true;

    protected override void ResetEdits()
    {
        _rule = new ResizeRule { Width = _images[0].Width, Height = 0 };
        _keepsProportions = true;
        Sync();
    }

    void IEditorDemo.ApplyDemoState(string state)
    {
        foreach (var token in state.Split(','))
            switch (token)
            {
                case "long": _rule = _rule with { Mode = ResizeBy.LongEdge, LongEdge = 1080 }; Sync(); break;
                case "exact": _rule = _rule with { Mode = ResizeBy.Exact, Width = 800 }; Sync(); break;
            }
    }

    protected override SaveOutcome Save(IProgress<double> progress, CancellationToken cancel) =>
        ResizeOperation.Run(_images.Select(i => (i.Path, i.PixelSize)).ToList(), EffectiveRule, progress, cancel);
}
