using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenShelf.Core;
using OpenShelf.Tools;
using OpenShelf.Wheel;

namespace OpenShelf.Wheel.Demo;

/// <summary>
/// Renders the wheel pages off-screen to PNG files (no window on screen, no mouse): Actions, a Convert wheel, and every page of the Tools
/// wheel for images, PDFs and video, each also with one wedge highlighted. Usage: <c>--shots &lt;folder&gt; [--scale 1|1.25|1.5|2]</c>.
/// </summary>
internal static class Shots
{
    public static void Run(string folder, double scale)
    {
        Directory.CreateDirectory(folder);
        var shelves = new FakeShelves { HasOpenShelf = true };
        var toasts = new FakeToasts();
        var tools = new ToolsCatalog(toasts, new NoProgress(), shelves);
        OpenShelf.Tools.PdfEditors.PdfTools.Register(tools);
        OpenShelf.Tools.MediaEditors.MediaTools.Register(tools);
        OpenShelf.Tools.VisionEditors.VisionTools.Register(tools);

        var builder = new WheelBuilder(shelves, toasts, new ManyCatalog());
        Save(folder, "wheel-actions", builder.Build(WheelKind.Actions, ["a.png"]), 0, null, scale);

        var convert = new WheelModel(WheelKind.Convert, new[] { "PNG", "WEBP", "AVIF", "TIFF", "BMP", "PDF", "GIF" }
            .Select(n => new WheelEntry("c." + n, n, _ => Task.CompletedTask)).ToList());
        Save(folder, "wheel-convert", convert, 0, null, scale);

        var groups = new (string Name, string[] Paths)[]
        {
            ("images", ["a.png"]), ("images-many", ["a.png", "b.png"]), ("pdf", ["a.pdf"]), ("video", ["a.mp4"]), ("audio", ["a.mp3"]),
        };
        foreach (var (name, paths) in groups)
        {
            var content = tools.For(paths);
            var model = new WheelModel(WheelKind.Tools, content.Entries);
            var pages = WheelPaging.PageCount(model.Items.Count);
            for (var page = 0; page < pages; page++)
                Save(folder, $"wheel-tools-{name}-p{page + 1}", model, page, 1, scale);
        }
    }

    /// <summary>Every icon with its name at 16, 20 and 24 px (and 16 px dimmed), rendered at <paramref name="scale"/>.</summary>
    public static void IconSheet(string file, double scale)
    {
        var names = OpenShelf.Ui.Icons.Names;
        const int columns = 4;
        var grid = new Grid { Background = new SolidColorBrush(Color.FromRgb(0x26, 0x26, 0x29)) };
        var rows = (names.Count + columns - 1) / columns;
        for (var c = 0; c < columns; c++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
        for (var r = 0; r < rows; r++) grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(40) });
        for (var i = 0; i < names.Count; i++)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            foreach (var size in new[] { 16, 20, 24 })
                row.Children.Add(new OpenShelf.Ui.IconView(names[i], size, Brushes.White) { Margin = new Thickness(0, 0, 10, 0) });
            row.Children.Add(new TextBlock { Text = names[i], Foreground = new SolidColorBrush(Color.FromRgb(0xB0, 0xB0, 0xB8)), FontSize = 11, VerticalAlignment = VerticalAlignment.Center, FontFamily = new FontFamily("Segoe UI") });
            Grid.SetColumn(row, i % columns);
            Grid.SetRow(row, i / columns);
            grid.Children.Add(row);
        }
        var w = 300 * columns;
        var h = 40 * rows;
        VisualTreeHelper.SetRootDpi(grid, new DpiScale(scale, scale));
        grid.Measure(new Size(w, h));
        grid.Arrange(new Rect(0, 0, w, h));
        grid.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Round(w * scale), (int)Math.Round(h * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(grid);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(file);
        encoder.Save(stream);
    }

    static void Save(string folder, string name, WheelModel model, int page, int? hover, double scale)
    {
        var slots = WheelPaging.Slots(model.Items.Count, page);
        var visual = new WheelVisual(model, slots, page, model.Items.Count + " files", null, scale, animateIn: false);
        visual.PlayOpen(false);
        if (hover is { } h) visual.SetHover(h);
        var host = new Grid { Width = WheelVisual.Side, Height = WheelVisual.Side, Background = new LinearGradientBrush(Color.FromRgb(0x4A, 0x5A, 0x78), Color.FromRgb(0x2C, 0x30, 0x40), 45) };
        host.Children.Add(visual.Root);
        host.Measure(new Size(WheelVisual.Side, WheelVisual.Side));
        host.Arrange(new Rect(0, 0, WheelVisual.Side, WheelVisual.Side));
        host.UpdateLayout();
        VisualTreeHelper.SetRootDpi(host, new DpiScale(scale, scale));
        var px = (int)Math.Round(WheelVisual.Side * scale);
        var bitmap = new RenderTargetBitmap(px, px, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(host);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(folder, name + ".png"));
        encoder.Save(stream);
    }

    sealed class NoProgress : IProgressService
    {
        public IProgressCard Begin(string title, int total, ScreenPoint near, CancellationTokenSource cancel) => new Card();
        sealed class Card : IProgressCard
        {
            public void Report(int completed, double? fraction = null, string? detail = null) { }
            public void Finish(string message) { }
            public void Dispose() { }
        }
    }
}
