using OpenShelf.Core;

namespace OpenShelf.Tools.Media;

/// <summary>How a file is cut into parts.</summary>
public enum SplitMode
{
    /// <summary>2 to 20 equal parts.</summary>
    EqualParts,
    /// <summary>A part every N seconds or minutes.</summary>
    EveryInterval,
    /// <summary>At times typed as "0:30, 1:15".</summary>
    AtTimes,
}

/// <summary>One part of a split, in seconds.</summary>
public readonly record struct SplitSegment(double Start, double End)
{
    public double Duration => End - Start;
}

/// <summary>A part as the preview list shows it before exporting.</summary>
/// <param name="Number">1-based, as in "Part 1".</param>
/// <param name="Start">Seconds.</param>
/// <param name="End">Seconds.</param>
/// <param name="FileName">The file name it will be saved under (collisions already counted).</param>
public sealed record SplitPart(int Number, double Start, double End, string FileName)
{
    public double Duration => End - Start;
}

public enum SplitPlanProblem { Unreadable, OutOfRange, NoCuts, TooMany, IntervalTooShort }

/// <summary>Why a split cannot be planned; the message is fit to show.</summary>
public sealed class SplitPlanException : Exception
{
    public SplitPlanProblem Problem { get; }

    private SplitPlanException(SplitPlanProblem problem, string message) : base(message) => Problem = problem;

    public static SplitPlanException Unreadable(string text) => new(SplitPlanProblem.Unreadable, $"“{text}” isn’t a time. Use minutes and seconds, like 1:30.");
    public static SplitPlanException OutOfRange(string text) => new(SplitPlanProblem.OutOfRange, $"{text} is past the end of the file.");
    public static SplitPlanException NoCuts() => new(SplitPlanProblem.NoCuts, "Add at least one time to split at, like 0:30, 1:15.");
    public static SplitPlanException TooMany(int count) => new(SplitPlanProblem.TooMany, $"That would make {count} parts; split into 100 or fewer.");
    public static SplitPlanException IntervalTooShort() => new(SplitPlanProblem.IntervalTooShort, "Parts must be at least 1 second long.");
}

/// <summary>Works out the parts of a split before anything is exported. Port of SplitPlan in MediaEdits.swift.</summary>
public static class SplitPlan
{
    public const int MaximumParts = 100;
    public const int MinimumEqualParts = 2;
    public const int MaximumEqualParts = 20;
    /// <summary>Leftover tails shorter than this are joined to the previous part.</summary>
    public const double MinimumTail = 0.25;

    /// <summary>2-20 (clamped) equal parts; the last ends exactly at the end. Empty when the duration is unknown.</summary>
    public static IReadOnlyList<SplitSegment> EqualParts(double duration, int count)
    {
        count = Math.Clamp(count, MinimumEqualParts, MaximumEqualParts);
        if (!(duration > 0)) return [];
        var length = duration / count;
        var segments = new List<SplitSegment>(count);
        for (var index = 0; index < count; index++)
            segments.Add(new SplitSegment(index * length, index == count - 1 ? duration : (index + 1) * length));
        return segments;
    }

    /// <summary>A part every <paramref name="interval"/> seconds (at least 1); a tail under a quarter second joins the last part.</summary>
    public static IReadOnlyList<SplitSegment> Every(double interval, double duration)
    {
        if (!(interval >= 1)) throw SplitPlanException.IntervalTooShort();
        if (!(duration > 0)) return [];
        var count = (int)Math.Min(Math.Ceiling(duration / interval), int.MaxValue);
        if (count > MaximumParts) throw SplitPlanException.TooMany(count);
        var cuts = new List<double>();
        for (var i = 1; i * interval < duration; i++) cuts.Add(i * interval);
        if (cuts.Count > 0 && duration - cuts[^1] < MinimumTail) cuts.RemoveAt(cuts.Count - 1);
        return Segments(cuts, duration);
    }

    /// <summary>Cuts at comma-separated times such as "0:30, 1:15, 2:00" (semicolons and new lines work too).
    /// A time past the end is an error here; see <see cref="AtInside"/> for Same for all.</summary>
    public static IReadOnlyList<SplitSegment> At(string? text, double duration)
    {
        var cuts = new List<double>();
        foreach (var part in SplitText(text))
        {
            var seconds = MediaTime.Parse(part) ?? throw SplitPlanException.Unreadable(part);
            if (seconds >= duration) throw SplitPlanException.OutOfRange(part);
            if (seconds > 0) cuts.Add(seconds);
        }
        return Segments(Distinct(cuts), duration);
    }

    /// <summary>Cuts at the times in <paramref name="text"/> that fall inside a file <paramref name="duration"/> long, leaving out
    /// later ones: one list of times shared by files of different lengths. No parts when none fall inside.</summary>
    public static IReadOnlyList<SplitSegment> AtInside(string? text, double duration)
    {
        // Read every time as if the file were endless, so only unreadable times are a problem.
        var cuts = new List<double>();
        foreach (var part in SplitText(text))
        {
            var seconds = MediaTime.Parse(part) ?? throw SplitPlanException.Unreadable(part);
            if (seconds > 0) cuts.Add(seconds);
        }
        var inside = Distinct(cuts).Where(c => c < duration).ToList();
        if (!(duration > 0) || inside.Count == 0) return [];
        return Segments(inside, duration);
    }

    /// <summary>The preview list: each part with the name it will be saved under. <paramref name="hasVideo"/> decides the extension
    /// (see <see cref="MediaFormats.OutputExtension"/>).</summary>
    public static IReadOnlyList<SplitPart> Parts(string path, IReadOnlyList<SplitSegment> segments, bool hasVideo)
    {
        var extension = MediaFormats.OutputExtension(path, hasVideo);
        var parts = new List<SplitPart>(segments.Count);
        for (var i = 0; i < segments.Count; i++)
        {
            var name = Path.GetFileName(OutputNaming.Destination(path, $"Part {i + 1}", extension));
            parts.Add(new SplitPart(i + 1, segments[i].Start, segments[i].End, name));
        }
        return parts;
    }

    private static List<double> Distinct(List<double> cuts)
    {
        var sorted = cuts.Distinct().OrderBy(c => c).ToList();
        if (sorted.Count == 0) throw SplitPlanException.NoCuts();
        if (sorted.Count + 1 > MaximumParts) throw SplitPlanException.TooMany(sorted.Count + 1);
        return sorted;
    }

    private static IEnumerable<string> SplitText(string? text) =>
        (text ?? "").Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static IReadOnlyList<SplitSegment> Segments(IReadOnlyList<double> cuts, double duration)
    {
        var edges = new List<double> { 0 };
        edges.AddRange(cuts);
        edges.Add(duration);
        return edges.Zip(edges.Skip(1), (a, b) => new SplitSegment(a, b)).ToList();
    }
}
