using ImageMagick;
using OpenShelf.Core;

namespace OpenShelf.Tools.Tests;

/// <summary>A scratch folder that goes away with the test.</summary>
public sealed class TempFolder : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "openshelf-tools-" + Guid.NewGuid().ToString("N"));

    public TempFolder() => Directory.CreateDirectory(Path);

    public string File(string name) => System.IO.Path.Combine(Path, name);

    /// <summary>Writes a picture: a solid colour with a red square in the top-left quarter.</summary>
    public string Image(string name, int width, int height, MagickFormat? format = null, Action<MagickImage>? tweak = null, string background = "#3366CC")
    {
        var path = File(name);
        using var image = new MagickImage(new MagickColor(background), (uint)width, (uint)height);
        using (var corner = new MagickImage(MagickColors.Red, (uint)(width / 2), (uint)(height / 2)))
            image.Composite(corner, 0, 0, CompositeOperator.Over);
        tweak?.Invoke(image);
        if (format is { } f) image.Write(path, f); else image.Write(path);
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

public static class Pixels
{
    public static IMagickColor<byte> At(string path, int x, int y)
    {
        using var image = new MagickImage(path);
        using var pixels = image.GetPixels();
        var pixel = pixels.GetPixel(x, y);
        return pixel.ToColor()!;
    }

    public static (uint Width, uint Height) SizeOf(string path)
    {
        var info = new MagickImageInfo(path);
        return (info.Width, info.Height);
    }

    public static bool IsNear(IMagickColor<byte> color, byte r, byte g, byte b, int tolerance = 12) =>
        Math.Abs(color.R - r) <= tolerance && Math.Abs(color.G - g) <= tolerance && Math.Abs(color.B - b) <= tolerance;
}

/// <summary>Runs WPF drawing code on a thread that can use it.</summary>
public static class Sta
{
    public static void Run(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception e) { failure = e; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new Exception("STA test failed: " + failure.Message, failure);
    }
}

public sealed class FakeToasts : IToastService
{
    public List<string> Messages { get; } = [];
    public void Show(string message, ScreenPoint near) => Messages.Add(message);
}

public sealed class FakeShelves : IShelfService
{
    public List<(IReadOnlyList<string> Paths, object? Shelf)> Added { get; } = [];
    public void NewShelf(IReadOnlyList<string> paths, ScreenPoint near) { }
    public bool AddToRecentShelf(IReadOnlyList<string> paths) => false;
    public bool HasOpenShelf => false;
    public void ShowAll() { }
    public void CloseAll() { }
    public IReadOnlyList<string> SelectionForWheel() => [];
    public object? DraggingShelf => null;
    public void AddResults(IReadOnlyList<string> paths, object? sourceShelf) => Added.Add((paths, sourceShelf));
    public void Flush() { }
}

public sealed class FakeProgress : IProgressService
{
    public List<string> Titles { get; } = [];
    public List<string> Finished { get; } = [];
    public IProgressCard Begin(string title, int total, ScreenPoint near, CancellationTokenSource cancel)
    {
        Titles.Add(title);
        return new Card(this);
    }

    private sealed class Card(FakeProgress owner) : IProgressCard
    {
        public void Report(int completed, double? fraction = null, string? detail = null) { }
        public void Finish(string message) => owner.Finished.Add(message);
        public void Dispose() { }
    }
}

public static class TestImages
{
    /// <summary>Makes every pixel half see-through.</summary>
    public static void HalfClear(MagickImage image)
    {
        image.Alpha(AlphaAction.Set);
        image.Evaluate(Channels.Alpha, EvaluateOperator.Multiply, 0.5);
    }

    /// <summary>Marks the picture as stored sideways: viewers turn it 90 degrees to the right.</summary>
    public static void Turned(MagickImage image)
    {
        var exif = new ExifProfile();
        exif.SetValue(ExifTag.Orientation, (ushort)6);
        image.SetProfile(exif);
        image.Orientation = Orientation.RightTop;
    }
}

