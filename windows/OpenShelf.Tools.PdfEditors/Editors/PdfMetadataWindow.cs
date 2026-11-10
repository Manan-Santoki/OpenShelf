using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using OpenShelf.Tools.Pdf;
using Orientation = System.Windows.Controls.Orientation;

namespace OpenShelf.Tools.PdfEditors;

/// <summary>The PDF "Metadata" tool: edit one PDF's document info, or strip it from several
/// (Sources/OpenShelf/Tools/PDF/PDFMetadataEditor.swift).</summary>
public sealed class PdfMetadataWindow : PdfEditorWindow
{
    public static readonly Size ContentSize = new(470, 620);

    private readonly MetadataModel _model;
    private readonly Dictionary<string, TextBox> _boxes = [];
    private Button? _removeAll;
    private bool _silent;
    private ContentControl? _picture;

    public PdfMetadataWindow(EditorHost host, IReadOnlyList<PdfFileState> files) : base(host, "PDF Metadata", ContentSize)
    {
        _model = new MetadataModel(files);
        CanvasWell = false;
        SetCanvasPadding(0);

        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.Children.Add(Parts.Header(_model.Title, _model.Detail, files.Count > 1 ? files.Count : null, out _picture));

        var body = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
        if (_model.Warning is { } warning)
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
            var glyph = ToolKit.Icon("lock", 12, "Danger");
            glyph.Margin = new Thickness(0, 0, 8, 0);
            line.Children.Add(glyph);
            line.Children.Add(ToolKit.Text(warning, 12, "Danger"));
            body.Children.Add(line);
        }
        if (_model.ShowsForm) body.Children.Add(SingleForm());
        else if (!_model.IsSingle) body.Children.Add(BatchList());
        var scroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 4, 0) };
        Grid.SetRow(scroll, 1);
        layout.Children.Add(scroll);
        Canvas = layout;

        Loaded += (_, _) => LoadPicture();
    }

    private async void LoadPicture()
    {
        if (_picture is null || !_model.Files[0].IsUsable) return;
        try
        {
            if (await Parts.FirstPageAsync(_model.Files[0].Path) is { } picture)
                Parts.SetPicture(_picture, picture, _model.Files.Count > 1 ? _model.Files.Count : null);
        }
        catch (Exception) { }
    }

    private UIElement SingleForm()
    {
        var form = new StackPanel();
        AddField(form, "Title", f => f.Title, (f, v) => f with { Title = v });
        AddField(form, "Author", f => f.Author, (f, v) => f with { Author = v });
        AddField(form, "Subject", f => f.Subject, (f, v) => f with { Subject = v });
        AddField(form, "Keywords", f => f.Keywords, (f, v) => f with { Keywords = v }, "Comma separated");
        AddField(form, "Creator", f => f.Creator, (f, v) => f with { Creator = v });
        AddField(form, "Producer", f => f.Producer, (f, v) => f with { Producer = v });

        form.Children.Add(new Border { Height = 1, Background = ToolKit.Brush("Hairline"), Margin = new Thickness(0, 6, 0, 8) });
        if (_model.SingleInfo is { } info)
        {
            form.Children.Add(ReadOnly("Created", info.Created is { } c ? Format(c) : "—"));
            form.Children.Add(ReadOnly("Modified", info.Modified is { } m ? Format(m) : "—"));
            form.Children.Add(ReadOnly("Encrypted", info.IsEncrypted ? "Yes" : "No"));
        }

        _removeAll = ToolKit.Button("Remove All", (_, _) =>
        {
            _model.RemoveAll();
            LoadBoxes();
            Refresh();
        });
        _removeAll.HorizontalAlignment = HorizontalAlignment.Right;
        _removeAll.Margin = new Thickness(0, 10, 0, 0);
        _removeAll.ToolTip = "Clear every field; saving then writes a clean copy";
        _removeAll.IsEnabled = !_model.Fields.IsEmpty;
        form.Children.Add(_removeAll);
        LoadBoxes();
        return form;
    }

    private void AddField(StackPanel form, string label, Func<PdfMetadataFields, string> get, Func<PdfMetadataFields, string, PdfMetadataFields> set, string? prompt = null)
    {
        var box = new TextBox { Style = ToolKit.Style("Field"), Tag = get, IsEnabled = _model.Files[0].IsUsable, MaxLength = 1000 };
        if (prompt is not null) box.ToolTip = prompt;
        ToolKit.Name(box, label);
        box.TextChanged += (_, _) =>
        {
            if (_silent) return;
            _model.Fields = set(_model.Fields, box.Text);
            if (_removeAll is not null) _removeAll.IsEnabled = !_model.Fields.IsEmpty;
            Refresh();
        };
        _boxes[label] = box;
        var row = ToolKit.Row(label, box, 74);
        row.Margin = new Thickness(0, 0, 0, 10);
        form.Children.Add(row);
        box.Text = get(_model.Fields);
    }

    private void LoadBoxes()
    {
        _silent = true;
        try
        {
            foreach (var box in _boxes.Values) box.Text = ((Func<PdfMetadataFields, string>)box.Tag)(_model.Fields);
        }
        finally { _silent = false; }
        if (_removeAll is not null) _removeAll.IsEnabled = !_model.Fields.IsEmpty;
    }

    private static UIElement ReadOnly(string label, string value)
    {
        var text = ToolKit.Text(value, 12, "Text");
        text.VerticalAlignment = VerticalAlignment.Center;
        var row = ToolKit.Row(label, text, 74);
        row.Margin = new Thickness(0, 0, 0, 4);
        return row;
    }

    private UIElement BatchList()
    {
        var list = new StackPanel();
        for (var i = 0; i < _model.Files.Count; i++)
        {
            var file = _model.Files[i];
            var (status, warn) = MetadataModel.Status(file);
            var row = new Grid { Margin = new Thickness(10, 7, 10, 7) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var name = ToolKit.Text(file.Name, 12);
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            name.ToolTip = file.Name;
            var state = ToolKit.Text(status, 11, warn ? "Danger" : "Text3");
            state.Margin = new Thickness(10, 0, 0, 0);
            Grid.SetColumn(state, 1);
            row.Children.Add(name);
            row.Children.Add(state);
            if (i > 0) list.Children.Add(new System.Windows.Controls.Border { Height = 1, Background = ToolKit.Brush("Hairline") });
            list.Children.Add(row);
        }
        return ToolKit.Card(list, 0);
    }

    private static string Format(DateTimeOffset date) => date.LocalDateTime.ToString("MMM d, yyyy, h:mm tt", CultureInfo.CurrentCulture);

    protected override string PrimaryTitle => _model.PrimaryTitle;
    protected override bool CanApply => _model.CanSave;
    protected override bool HasReset => _model.ShowsForm;

    protected override void ResetEdits()
    {
        _model.Reset();
        LoadBoxes();
    }

    public override void ApplyDemoState(string state)
    {
        foreach (var token in state.Split(','))
            switch (token)
            {
                case "edit":
                    _boxes["Title"].Text = "Quarterly report, final";
                    _boxes["Keywords"].Text = "finance,  2024,q3";
                    break;
                case "clear":
                    _model.RemoveAll();
                    LoadBoxes();
                    Refresh();
                    break;
            }
    }

    protected override SaveOutcome SaveCore(IProgress<double> progress, CancellationToken cancel) => _model.Save(progress, cancel);
}
