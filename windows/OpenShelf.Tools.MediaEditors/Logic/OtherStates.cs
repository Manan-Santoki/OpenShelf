using System.Globalization;
using System.Windows;
using OpenShelf.Tools.Imaging;
using OpenShelf.Tools.Media;
using ImageCropMath = OpenShelf.Tools.Imaging.CropMath;

namespace OpenShelf.Tools.MediaEditors;

/// <summary>Speed: a rate from 0.25x to 4x, whether the sound keeps its pitch, and what the total length becomes.</summary>
internal sealed class SpeedState
{
    public static readonly IReadOnlyList<double> Presets = [0.25, 0.5, 0.75, 1.25, 1.5, 2, 4];
    public const double Step = 0.05;
    public const double DefaultRate = 2;

    public double Rate { get; private set; } = DefaultRate;
    public bool KeepsPitch { get; set; } = true;
    public double TotalDuration { get; set; }

    /// <summary>Sets the rate on the slider's grid, inside 0.25x to 4x.</summary>
    public void SetRate(double value) =>
        Rate = Math.Round(Math.Clamp(value, MediaFormats.MinimumSpeed, MediaFormats.MaximumSpeed) / Step, MidpointRounding.AwayFromZero) * Step;

    public bool CanSave => Math.Abs(Rate - 1) > 0.001;

    public void Reset()
    {
        Rate = DefaultRate;
        KeepsPitch = true;
    }

    /// <summary>"0:30.0 → 0:15.0"; a blank until the lengths are known.</summary>
    public string DurationSummary => TotalDuration > 0 ? $"{MediaTime.Format(TotalDuration)} → {MediaTime.Format(TotalDuration / Rate)}" : " ";

    /// <summary>"2×", "0.5×", "1.25×".</summary>
    public static string Label(double rate) => MediaFormats.SpeedSuffix(rate).Replace('x', '×');

    public static bool IsPreset(double rate, double preset) => Math.Abs(rate - preset) < 0.001;
}

/// <summary>Video Crop: each video's crop rectangle in its own upright pixels, kept as fractions when shared ("the top half").</summary>
internal sealed class CropState
{
    public const double MinimumSize = 16;

    public CropState(int count)
    {
        Bounds = new Rect[count];
        Loaded = new bool[count];
        for (var i = 0; i < count; i++) Bounds[i] = new Rect(0, 0, 1, 1);
        Edits = new FileEdits<Rect>(count, i => Bounds[i], (i, crop) => Loaded[i] && crop != Bounds[i],
            carry: (crop, from, to) => ImageCropMath.Mapped(ImageCropMath.Unit(crop, Bounds[from]), Bounds[to], Ratio.Value), sameForAll: true);
    }

    public Rect[] Bounds { get; }
    public bool[] Loaded { get; }
    public FileEdits<Rect> Edits { get; }
    public CropRatio Ratio { get; set; } = CropRatio.Free;

    public Rect CurrentBounds => Bounds[Edits.Index];

    /// <summary>Learns file <paramref name="index"/>'s upright size and starts its crop as the whole frame.</summary>
    public void SetSize(int index, int width, int height)
    {
        Bounds[index] = new Rect(0, 0, width, height);
        Loaded[index] = true;
        Edits.SetOnly(index, Bounds[index]);
    }

    public void Snap(CropRatio ratio)
    {
        Ratio = ratio;
        if (ratio.Value is not { } value) return;
        var c = Edits.Current;
        Edits.Current = ImageCropMath.Largest(value, new Point(c.Left + c.Width / 2, c.Top + c.Height / 2), CurrentBounds);
    }

    public void SetWidth(int width) => Edits.Current = ImageCropMath.Resized(Edits.Current, width, null, Ratio.Value, CurrentBounds, MinimumSize);
    public void SetHeight(int height) => Edits.Current = ImageCropMath.Resized(Edits.Current, null, height, Ratio.Value, CurrentBounds, MinimumSize);

    public void Reset()
    {
        Ratio = CropRatio.Free;
        Edits.Reset();
    }

    /// <summary>The crop of file <paramref name="index"/> as fractions of its frame.</summary>
    public CropFraction Fraction(int index)
    {
        var b = Bounds[index];
        var r = Edits[index];
        return new CropFraction(r.X / b.Width, r.Y / b.Height, r.Width / b.Width, r.Height / b.Height);
    }

    public IReadOnlyList<int> Jobs() => Enumerable.Range(0, Edits.Count).Where(Edits.IsEdited).ToList();
}

/// <summary>Rotate: one turn or flip per video, on for all by default.</summary>
internal sealed class RotateState
{
    public RotateState(int count)
    {
        Failed = new bool[count];
        Edits = new FileEdits<VideoRotation>(count, _ => VideoRotation.Right, (i, _) => !Failed[i], carry: null, sameForAll: true);
    }

    public bool[] Failed { get; }
    public FileEdits<VideoRotation> Edits { get; }

    /// <summary>Turns the shown video, or every video while they share one turn.</summary>
    public void Choose(VideoRotation rotation) => Edits.Current = rotation;

    public static string Caption(VideoRotation rotation) => rotation.IsLossless()
        ? "Turning keeps the original quality; only the orientation changes."
        : "Flipping re-encodes the video.";

    /// <summary>Every video that can be read gets turned.</summary>
    public IReadOnlyList<int> Jobs() => Enumerable.Range(0, Edits.Count).Where(Edits.IsEdited).ToList();
}

/// <summary>Merge: the files in play order. Reorder, remove (never below two), reset.</summary>
internal sealed class MergeState
{
    public sealed class Item(string path)
    {
        public Guid Id { get; } = Guid.NewGuid();
        public string Path { get; } = path;
        public string Name => System.IO.Path.GetFileName(Path);
        public double? Duration { get; set; }
    }

    private readonly IReadOnlyList<string> _original;

    public MergeState(IReadOnlyList<string> paths)
    {
        _original = paths;
        Items = paths.Select(p => new Item(p)).ToList();
    }

    public List<Item> Items { get; private set; }
    public bool IsVideo { get; set; } = true;

    public double TotalDuration => Items.Sum(i => i.Duration ?? 0);
    public string OutputName => IsVideo ? "Merged.mp4" : "Merged.m4a";
    public bool CanMerge => Items.Count >= 2;
    public bool CanRemove => Items.Count > 2;

    public void Move(int from, int to)
    {
        if (from < 0 || from >= Items.Count) return;
        to = Math.Clamp(to, 0, Items.Count - 1);
        if (from == to) return;
        var item = Items[from];
        Items.RemoveAt(from);
        Items.Insert(to, item);
    }

    public void Remove(Guid id)
    {
        if (!CanRemove) return;
        Items.RemoveAll(i => i.Id == id);
    }

    public void Reset()
    {
        var durations = Items.GroupBy(i => i.Path).ToDictionary(g => g.Key, g => g.First().Duration);
        Items = _original.Select(p => new Item(p) { Duration = durations.GetValueOrDefault(p) }).ToList();
    }

    public IReadOnlyList<string> Paths => Items.Select(i => i.Path).ToList();
}

/// <summary>Where a dragged row lands in a list of equal-height rows.</summary>
internal static class ReorderMath
{
    public static int TargetIndex(double y, double rowHeight, int count) =>
        count <= 0 ? 0 : Math.Clamp((int)Math.Floor(y / Math.Max(rowHeight, 1)), 0, count - 1);
}

/// <summary>Video Compress: resolution and quality, with the estimated size summed over the videos.</summary>
internal sealed class CompressState
{
    private readonly IReadOnlyList<string> _paths;

    public CompressState(IReadOnlyList<string> paths, long originalBytes)
    {
        _paths = paths;
        OriginalBytes = originalBytes;
    }

    public CompressResolution Resolution { get; set; } = CompressResolution.P1080;
    public CompressQuality Quality { get; set; } = CompressQuality.High;
    public IReadOnlyList<VideoSourceInfo>? Sources { get; set; }
    public bool HevcAvailable { get; set; } = true;
    public string? LoadError { get; set; }
    /// <summary>Set after a compress that saved nothing; cleared when the settings change.</summary>
    public string? Notice { get; set; }
    public long OriginalBytes { get; }
    public int Count => _paths.Count;

    public string Title => Count == 1 ? Path.GetFileName(_paths[0]) : $"{Count} videos";

    public string OriginalSummary => Count == 1 && Sources is { Count: 1 } one
        ? $"{one[0].DisplaySize.Width} × {one[0].DisplaySize.Height} · {Bytes(OriginalBytes)}"
        : $"Original {Bytes(OriginalBytes)}";

    public long? Estimate => Sources?.Sum(s => VideoCompressionPlan.Estimate(s, Resolution, Quality, HevcAvailable));

    public string OutputSummary
    {
        get
        {
            if (Count != 1 || Sources is not { Count: 1 } one) return "MP4";
            var size = new VideoCompressionPlan(one[0], Resolution, Quality, HevcAvailable).DisplaySize;
            return $"MP4 · {size.Width} × {size.Height}";
        }
    }

    public bool IsSmaller => Estimate is { } e && e < OriginalBytes;

    /// <summary>How much smaller the estimate is: at least 1% when it is smaller at all.</summary>
    public int SavingPercent(long estimate) =>
        OriginalBytes <= 0 ? 0 : Math.Max(1, 100 - (int)Math.Ceiling((double)estimate / OriginalBytes * 100));

    /// <summary>Why Compress is off when these settings would not save anything.</summary>
    public string? SizeAdvice
    {
        get
        {
            if (Sources is null || IsSmaller) return null;
            foreach (var resolution in Enum.GetValues<CompressResolution>())
            foreach (var quality in Enum.GetValues<CompressQuality>())
                if (Sources.Sum(s => VideoCompressionPlan.Estimate(s, resolution, quality, HevcAvailable)) < OriginalBytes)
                    return "Wouldn’t get smaller at these settings. Try a lower resolution or quality.";
            return Sources.Count == 1 ? "Already as small as it gets." : "These videos are already as small as they get.";
        }
    }

    public bool CanCompress => IsSmaller;

    public string EstimateText => Estimate is not { } e ? (LoadError ?? "Estimating…")
        : Count == 1 ? $"≈ {Bytes(e)}" : $"≈ {Bytes(e)} for {Count} videos";

    public string SavingText => Estimate is not { } e ? "" : IsSmaller ? $"  ({SavingPercent(e)}% smaller)" : "  (no smaller)";

    /// <summary>The message after a compress that kept nothing.</summary>
    public static string NotSmaller(int count) => count == 1
        ? "Already as small as it gets — nothing saved."
        : $"These {count} videos are already as small as they get — nothing saved.";

    public void Reset()
    {
        Resolution = CompressResolution.P1080;
        Quality = CompressQuality.High;
        Notice = null;
    }

    public static string Bytes(long bytes)
    {
        string[] units = ["bytes", "KB", "MB", "GB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1000 && unit < units.Length - 1) { value /= 1000; unit++; }
        return unit == 0 ? $"{bytes} bytes" : string.Create(CultureInfo.CurrentCulture, $"{value:0.#} {units[unit]}");
    }
}
