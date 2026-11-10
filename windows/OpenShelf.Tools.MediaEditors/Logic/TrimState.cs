using OpenShelf.Tools.Media;

namespace OpenShelf.Tools.MediaEditors;

/// <summary>The range kept from one file, in seconds.</summary>
internal readonly record struct TrimSelection(double Start, double End);

/// <summary>Everything Trim decides, apart from drawing: each file's range, which are edited, and what "Same for all" does
/// (the same seconds cut from the start and end of each file). Off by default, as clips differ in length.</summary>
internal sealed class TrimState
{
    public TrimState(int count)
    {
        Durations = new double[count];
        Edits = new FileEdits<TrimSelection>(count, i => new TrimSelection(0, Durations[i]),
            (i, s) => Durations[i] > 0 && TrimRange.Changes(s.Start, s.End, Durations[i]), Carry, sameForAll: false);
    }

    /// <summary>Each file's length; 0 until it is known.</summary>
    public double[] Durations { get; }

    public FileEdits<TrimSelection> Edits { get; }

    public int Index => Edits.Index;
    public double Duration => Durations[Edits.Index];
    public double Start => Edits.Current.Start;
    public double End => Edits.Current.End;

    /// <summary>Learns file <paramref name="index"/>'s length and selects all of it.</summary>
    public void SetDuration(int index, double duration)
    {
        Durations[index] = duration;
        Edits.SetOnly(index, new TrimSelection(0, duration));
        // A file that finished loading after Same for all was turned on gets the shown file's cuts too.
        if (Edits.SameForAll && index != Edits.Index && Durations[Edits.Index] > 0) Edits.Current = Edits.Current;
    }

    /// <summary>Moves the start (to the tenth the field shows, at least a tenth before the end) in the shown file.</summary>
    public void SetStart(double value)
    {
        var current = Edits.Current;
        Edits.Current = current with { Start = TrimRange.ClampedStart(value, current.End) };
    }

    /// <summary>Moves the end (to a shown tenth, or the file's very end).</summary>
    public void SetEnd(double value)
    {
        var current = Edits.Current;
        Edits.Current = current with { End = TrimRange.ClampedEnd(value, current.Start, Duration) };
    }

    /// <summary>Whether the same cuts as another file's leave nothing of this one.</summary>
    public bool IsCutAway(int index) => Durations[index] > 0 && Edits[index].End - Edits[index].Start < MediaTime.Precision / 2;

    public string LengthText(int index) => MediaTime.Format(MediaTime.Length(Edits[index].Start, Edits[index].End));

    /// <summary>The range file <paramref name="to"/> gets from file <paramref name="from"/>'s: the same seconds off its start and end.</summary>
    private TrimSelection Carry(TrimSelection selection, int from, int to)
    {
        if (Durations[from] <= 0 || Durations[to] <= 0) return new TrimSelection(0, Durations[to]);
        var (head, tail) = TrimRange.Cut(selection.Start, selection.End, Durations[from]);
        if (TrimRange.CuttingStartAndEnd(head, tail, Durations[to]) is { } range) return new TrimSelection(range.Start, range.End);
        var start = Math.Min(MediaTime.Snapped(Math.Max(head, 0)), Durations[to]);
        return new TrimSelection(start, start);
    }

    /// <summary>The files to save: (index, start, end) for each edited file.</summary>
    public IReadOnlyList<(int Index, double Start, double End)> Jobs() =>
        Enumerable.Range(0, Edits.Count).Where(Edits.IsEdited).Select(i => (i, Edits[i].Start, Edits[i].End)).ToList();
}

/// <summary>Where things are on the trimming timeline, as plain arithmetic.</summary>
internal static class TimelineMath
{
    public const double HandleWidth = 12;

    public static double XAt(double seconds, double width, double duration) => duration > 0 ? seconds / duration * width : 0;

    public static double TimeAt(double x, double width, double duration) => width > 0 ? Math.Clamp(x / width, 0, 1) * duration : 0;

    public enum Part { None, Start, End, Track }

    /// <summary>What a press at <paramref name="x"/> grabs: a handle (with a generous target) or the track. The handle closer to the press
    /// wins where the two overlap (a very short selection).</summary>
    public static Part HitTest(double x, double startX, double endX)
    {
        const double slop = 8;
        var onStart = x >= startX - slop && x <= startX + HandleWidth + slop;
        var onEnd = x >= endX - HandleWidth - slop && x <= endX + slop;
        if (onStart && onEnd) return Math.Abs(x - (startX + HandleWidth / 2)) <= Math.Abs(x - (endX - HandleWidth / 2)) ? Part.Start : Part.End;
        if (onStart) return Part.Start;
        if (onEnd) return Part.End;
        return Part.Track;
    }
}
