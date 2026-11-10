using OpenShelf.Tools.Media;

namespace OpenShelf.Tools.MediaEditors;

/// <summary>The choices in the Split editor, which one file can pass to others.</summary>
internal sealed record SplitSettings(SplitMode Mode = SplitMode.EqualParts, int Parts = 2, double Interval = 30, bool IntervalInMinutes = false, string TimesText = "")
{
    public static string Title(SplitMode mode) => mode switch
    {
        SplitMode.EqualParts => "Equal parts",
        SplitMode.EveryInterval => "Every…",
        _ => "At times",
    };
}

/// <summary>What Split decides, apart from drawing: each file's parts for its settings, why they cannot be used, and which files
/// would be split. Off by default for "Same for all" (clips differ in length); with it on, times past a file's end are left out
/// rather than a problem, so one list of times serves files of different lengths.</summary>
internal sealed class SplitState
{
    private readonly IReadOnlyList<string> _paths;

    public SplitState(IReadOnlyList<string> paths)
    {
        _paths = paths;
        Durations = new double[paths.Count];
        HasVideo = new bool[paths.Count];
        Edits = new FileEdits<SplitSettings>(paths.Count, _ => new SplitSettings(), (i, s) => Segments(i, s).Count >= 2, carry: null, sameForAll: false);
    }

    public double[] Durations { get; }
    public bool[] HasVideo { get; }
    public FileEdits<SplitSettings> Edits { get; }
    public int Index => Edits.Index;
    public string PathAt(int index) => _paths[index];

    public SplitSettings Settings
    {
        get => Edits.Current;
        set => Edits.Current = value;
    }

    /// <summary>Whether times past a file's end are left out (Same for all) rather than an error.</summary>
    public bool KeepsTimesInside => Edits.SameForAll;

    /// <summary>The parts for file <paramref name="index"/> under <paramref name="settings"/>; none while they are invalid.</summary>
    public IReadOnlyList<SplitSegment> Segments(int index, SplitSettings settings)
    {
        try { return Plan(index, settings); }
        catch (SplitPlanException) { return []; }
    }

    public IReadOnlyList<SplitSegment> Segments(int index) => Segments(index, Edits[index]);
    public IReadOnlyList<SplitSegment> CurrentSegments => Segments(Edits.Index);

    private IReadOnlyList<SplitSegment> Plan(int index, SplitSettings s)
    {
        var duration = Durations[index];
        if (!(duration > 0)) return [];
        return s.Mode switch
        {
            SplitMode.EqualParts => SplitPlan.EqualParts(duration, s.Parts),
            SplitMode.EveryInterval => SplitPlan.Every(s.Interval * (s.IntervalInMinutes ? 60 : 1), duration),
            _ => KeepsTimesInside ? SplitPlan.AtInside(s.TimesText, duration) : SplitPlan.At(s.TimesText, duration),
        };
    }

    /// <summary>Why the shown file's settings cannot be used, if they cannot.</summary>
    public string? Problem()
    {
        var index = Edits.Index;
        if (!(Durations[index] > 0)) return null;
        var s = Edits.Current;
        if (s.Mode == SplitMode.AtTimes && string.IsNullOrWhiteSpace(s.TimesText)) return null;
        try
        {
            var segments = Plan(index, s);
            return KeepsTimesInside && segments.Count < 2 ? "No split falls inside this file, so it’s left whole." : null;
        }
        catch (SplitPlanException e)
        {
            return e.Message;
        }
    }

    /// <summary>The name of the first part: "Clip Part 1.mp4".</summary>
    public string PartName(int index) =>
        SplitPlan.Parts(_paths[index], [new SplitSegment(0, 1)], HasVideo[index])[0].FileName;

    /// <summary>The note under the parts list.</summary>
    public string Footer()
    {
        var count = CurrentSegments.Count;
        return count >= 2 ? $"Creates {count} files: “{PartName(Edits.Index)}”…" : " ";
    }

    /// <summary>The files to split with their parts.</summary>
    public IReadOnlyList<(int Index, IReadOnlyList<SplitSegment> Segments)> Jobs() =>
        Enumerable.Range(0, Edits.Count).Select(i => (i, Segments(i))).Where(x => x.Item2.Count >= 2).ToList();
}
