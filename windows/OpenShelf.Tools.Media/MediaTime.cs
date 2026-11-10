using System.Globalization;

namespace OpenShelf.Tools.Media;

/// <summary>"m:ss.s" formatting and parsing for media times. Times are shown, and chosen, to a tenth of a second.
/// Port of MediaTime in MediaEditorSupport.swift.</summary>
public static class MediaTime
{
    /// <summary>The step editors snap times to.</summary>
    public const double Precision = 0.1;

    /// <summary>"1:02.5". Rounds first, so 59.97 reads "1:00.0" rather than "0:60.0".</summary>
    public static string Format(double seconds)
    {
        var tenths = (long)Math.Round(Math.Max(double.IsFinite(seconds) ? seconds : 0, 0) * 10, MidpointRounding.AwayFromZero);
        return string.Create(CultureInfo.InvariantCulture, $"{tenths / 600}:{tenths / 10 % 60:00}.{tenths % 10}");
    }

    /// <summary><paramref name="seconds"/> rounded to the tenth <see cref="Format"/> shows.</summary>
    public static double Snapped(double seconds) => Math.Round(seconds / Precision, MidpointRounding.AwayFromZero) * Precision;

    /// <summary>The length between two times as their formatted values show it, so "0:01.0 - 0:04.0" always reads as "0:03.0" long.</summary>
    public static double Length(double start, double end) => Snapped(end) - Snapped(start);

    /// <summary>Accepts "1:02.5", "62.5", "1:02" and "1:02:03"; null for anything else (including negative or non-finite numbers).</summary>
    public static double? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var parts = text.Trim().Split(':');
        if (parts.Length is < 1 or > 3) return null;
        var numbers = new double[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i].Trim();
            if (part.Length == 0 || part[0] is '+' or '-') return null;
            if (!double.TryParse(part, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out numbers[i]) || !double.IsFinite(numbers[i])) return null;
        }
        return numbers.Length switch
        {
            1 => numbers[0],
            2 => numbers[0] * 60 + numbers[1],
            _ => numbers[0] * 3600 + numbers[1] * 60 + numbers[2],
        };
    }
}

/// <summary>The arithmetic of Trim, including "same seconds off the start and end" for Same for all.
/// Port of TrimModel's static helpers.</summary>
public static class TrimRange
{
    /// <summary>A start on the tenth of a second the fields show, at least a tenth before <paramref name="end"/>.</summary>
    public static double ClampedStart(double value, double end) =>
        Math.Max(Math.Min(MediaTime.Snapped(value), MediaTime.Snapped(end) - MediaTime.Precision), 0);

    /// <summary>An end on a tenth. Anything that shows as the last tenth is the file's very end: the file rarely ends on one,
    /// and its last frames should not be lost.</summary>
    public static double ClampedEnd(double value, double start, double duration)
    {
        var snapped = MediaTime.Snapped(value);
        var end = snapped >= MediaTime.Snapped(duration) ? duration : snapped;
        return Math.Max(end, Math.Min(start + MediaTime.Precision, duration));
    }

    /// <summary>Whether trimming a file <paramref name="duration"/> long to start-end would change it and leave something of it.</summary>
    public static bool Changes(double start, double end, double duration) =>
        end - start >= MediaTime.Precision / 2 && (start > 0 || end < duration);

    /// <summary>What is left of a file <paramref name="duration"/> long after cutting <paramref name="head"/> seconds from its start
    /// and <paramref name="tail"/> from its end (the same seconds for every file with Same for all), on the tenths the fields
    /// show or the file's very end; null when nothing would be left.</summary>
    public static (double Start, double End)? CuttingStartAndEnd(double head, double tail, double duration)
    {
        var start = Math.Max(MediaTime.Snapped(head), 0);
        var end = duration - Math.Max(tail, 0);
        if (end - start < MediaTime.Precision / 2) return null;
        return (start, ClampedEnd(end, start, duration));
    }

    /// <summary>The seconds a chosen range cuts from the start and from the end of a file: feed them to
    /// <see cref="CuttingStartAndEnd"/> for every other file.</summary>
    public static (double Head, double Tail) Cut(double start, double end, double duration) => (start, duration - end);
}

/// <summary>Batch bookkeeping the editors share when given several files.</summary>
public static class MediaBatch
{
    /// <summary>How far through <paramref name="count"/> exports the whole batch is when the one at <paramref name="index"/> is
    /// <paramref name="fraction"/> done: halfway through the second of four is 0.375.</summary>
    public static double Overall(double fraction, int index, int count) =>
        count <= 0 ? 1 : (index + Math.Clamp(fraction, 0, 1)) / count;
}

/// <summary>An <see cref="IProgress{T}"/> that calls back on the reporting thread (unlike Progress&lt;T&gt;, which posts).</summary>
internal sealed class DirectProgress(Action<double> report) : IProgress<double>
{
    public void Report(double value) => report(value);
}
