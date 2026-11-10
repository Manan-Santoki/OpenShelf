using System.Windows;
using OpenShelf.Tools.Media;
using OpenShelf.Tools.MediaEditors;

namespace OpenShelf.Tools.MediaEditors.Tests;

public class TrimStateTests
{
    private static TrimState Make(params double[] durations)
    {
        var s = new TrimState(durations.Length);
        for (var i = 0; i < durations.Length; i++) s.SetDuration(i, durations[i]);
        return s;
    }

    [Fact]
    public void StartsUnedited()
    {
        var s = Make(8, 6);
        Assert.Equal(0, s.EditedCount());
        Assert.Equal(8, s.End);
    }

    [Fact]
    public void StartAndEndClampToTenths()
    {
        var s = Make(8);
        s.SetStart(2.04);
        s.SetEnd(5.96);
        Assert.Equal(2.0, s.Start, 3);
        Assert.Equal(6.0, s.End, 3);
        s.SetStart(100);
        Assert.Equal(5.9, s.Start, 3);
        s.SetEnd(8.02);
        Assert.Equal(8, s.End);   // the very end of the file
        Assert.Single(s.Jobs());
    }

    [Fact]
    public void SameForAllCutsTheSameSecondsFromEachFile()
    {
        var s = Make(8, 6, 4);
        s.Edits.Index = 1;
        s.Edits.SameForAll = true;
        s.SetStart(1.5);
        s.SetEnd(4.5);
        Assert.Equal(new TrimSelection(1.5, 6.5), s.Edits[0]);
        Assert.Equal(new TrimSelection(1.5, 2.5), s.Edits[2]);
        Assert.Equal(3, s.Jobs().Count);
    }

    [Fact]
    public void LateLoadingFileJoinsSameForAll()
    {
        var s = new TrimState(2);
        s.SetDuration(0, 8);
        s.Edits.SameForAll = true;
        s.SetStart(2);
        s.SetDuration(1, 6);
        Assert.Equal(new TrimSelection(2, 6), s.Edits[1]);
    }

    [Fact]
    public void SameForAllLeavesNothingOfAShortFile()
    {
        var s = Make(8, 2);
        s.Edits.SameForAll = true;
        s.SetStart(3);
        Assert.True(s.IsCutAway(1));
        Assert.Single(s.Jobs());
    }

    [Fact]
    public void ResetRestoresWholeFile()
    {
        var s = Make(8);
        s.SetStart(2);
        s.Edits.Reset();
        Assert.Empty(s.Jobs());
    }
}

internal static class TrimExt
{
    public static int EditedCount(this TrimState s) => s.Edits.EditedCount;
}

public class TimelineMathTests
{
    [Fact]
    public void MapsTimeAndPosition()
    {
        Assert.Equal(150, TimelineMath.XAt(3, 300, 6));
        Assert.Equal(3, TimelineMath.TimeAt(150, 300, 6));
        Assert.Equal(6, TimelineMath.TimeAt(999, 300, 6));
        Assert.Equal(0, TimelineMath.TimeAt(10, 0, 6));
    }

    [Fact]
    public void HitTestPicksHandlesThenTrack()
    {
        Assert.Equal(TimelineMath.Part.Start, TimelineMath.HitTest(5, 0, 300));
        Assert.Equal(TimelineMath.Part.End, TimelineMath.HitTest(295, 0, 300));
        Assert.Equal(TimelineMath.Part.Track, TimelineMath.HitTest(150, 0, 300));
        // a very short selection: the nearer handle wins
        Assert.Equal(TimelineMath.Part.Start, TimelineMath.HitTest(105, 100, 108));
        Assert.Equal(TimelineMath.Part.End, TimelineMath.HitTest(100, 100, 108));
    }
}

public class SplitStateTests
{
    private static SplitState Make(params double[] durations)
    {
        var s = new SplitState(durations.Select((_, i) => $@"C:\x\clip{i}.mp4").ToList());
        for (var i = 0; i < durations.Length; i++) { s.Durations[i] = durations[i]; s.HasVideo[i] = true; }
        s.Edits.ResetAll();
        return s;
    }

    [Fact]
    public void DefaultIsTwoEqualParts()
    {
        var s = Make(10);
        Assert.Equal(2, s.CurrentSegments.Count);
        Assert.Equal(5, s.CurrentSegments[0].End);
        Assert.Contains("Creates 2 files", s.Footer());
        Assert.Contains("clip0 Part 1.mp4", s.Footer());
    }

    [Fact]
    public void EveryIntervalAndMinutes()
    {
        var s = Make(100);
        s.Settings = s.Settings with { Mode = SplitMode.EveryInterval, Interval = 30 };
        Assert.Equal(4, s.CurrentSegments.Count);
        s.Settings = s.Settings with { Interval = 1, IntervalInMinutes = true };
        Assert.Equal(2, s.CurrentSegments.Count);
    }

    [Fact]
    public void AtTimesReportsProblems()
    {
        var s = Make(60);
        s.Settings = new SplitSettings(SplitMode.AtTimes, TimesText: "");
        Assert.Null(s.Problem());
        Assert.Empty(s.CurrentSegments);
        s.Settings = s.Settings with { TimesText = "0:30, 1:15" };
        Assert.Contains("past the end", s.Problem());
        s.Settings = s.Settings with { TimesText = "abc" };
        Assert.Contains("isn’t a time", s.Problem());
        s.Settings = s.Settings with { TimesText = "0:20, 0:40" };
        Assert.Null(s.Problem());
        Assert.Equal(3, s.CurrentSegments.Count);
    }

    [Fact]
    public void SameForAllKeepsTimesInsideEachFile()
    {
        var s = Make(60, 20);
        s.Edits.SameForAll = true;
        s.Settings = new SplitSettings(SplitMode.AtTimes, TimesText: "0:30");
        Assert.Equal(2, s.Segments(0).Count);
        Assert.Empty(s.Segments(1));
        s.Edits.Index = 1;
        Assert.Contains("left whole", s.Problem());
        Assert.Single(s.Jobs());
    }

    [Fact]
    public void WithoutSameForAllFilesKeepTheirOwn()
    {
        var s = Make(60, 20);
        s.Settings = new SplitSettings(Parts: 4);
        Assert.Equal(2, s.Segments(1).Count);
        Assert.Equal(2, s.Jobs().Count);
    }
}

public class OtherStateTests
{
    [Fact]
    public void SpeedRateSnapsAndClamps()
    {
        var s = new SpeedState();
        Assert.Equal(2, s.Rate);
        s.SetRate(0.1);
        Assert.Equal(0.25, s.Rate, 6);
        s.SetRate(9);
        Assert.Equal(4, s.Rate, 6);
        s.SetRate(1.27);
        Assert.Equal(1.25, s.Rate, 6);
        s.SetRate(1);
        Assert.False(s.CanSave);
        Assert.Equal("1.25×", SpeedState.Label(1.25));
        Assert.Equal("0.5×", SpeedState.Label(0.5));
    }

    [Fact]
    public void SpeedSummaryShowsNewLength()
    {
        var s = new SpeedState { TotalDuration = 30 };
        Assert.Equal("0:30.0 → 0:15.0", s.DurationSummary);
        s.Reset();
        Assert.True(s.KeepsPitch);
    }

    [Fact]
    public void CropIsSharedAsFractions()
    {
        var s = new CropState(2);
        s.SetSize(0, 1000, 500);
        s.SetSize(1, 200, 400);
        s.Edits.Index = 0;
        s.Edits.Current = new Rect(0, 0, 1000, 250);
        Assert.Equal(new Rect(0, 0, 200, 200), s.Edits[1]);
        Assert.Equal(2, s.Jobs().Count);
        var f = s.Fraction(1);
        Assert.Equal(0.5, f.Height, 6);
        s.Edits.SameForAll = false;
        s.Edits.Index = 1;
        s.Edits.Current = new Rect(0, 0, 200, 400);
        Assert.Single(s.Jobs());
    }

    [Fact]
    public void CropRatioSnapsToLargestRect()
    {
        var s = new CropState(1);
        s.SetSize(0, 1600, 900);
        s.Snap(OpenShelf.Tools.Imaging.CropRatio.Square);
        Assert.Equal(900, s.Edits.Current.Width, 3);
        Assert.Equal(900, s.Edits.Current.Height, 3);
    }

    [Fact]
    public void RotateAppliesToAllUnlessTurnedOff()
    {
        var s = new RotateState(3);
        s.Choose(VideoRotation.Left);
        Assert.All(s.Edits.All, r => Assert.Equal(VideoRotation.Left, r));
        s.Edits.SameForAll = false;
        s.Edits.Index = 1;
        s.Choose(VideoRotation.UpsideDown);
        Assert.Equal(VideoRotation.Left, s.Edits[0]);
        s.Failed[2] = true;
        Assert.Equal(2, s.Jobs().Count);
        Assert.Contains("only the orientation", RotateState.Caption(VideoRotation.Right));
        Assert.Contains("re-encodes", RotateState.Caption(VideoRotation.FlipHorizontal));
    }

    [Fact]
    public void MergeReorderRemoveReset()
    {
        var s = new MergeState([@"C:\a.mp4", @"C:\b.mp4", @"C:\c.mp4"]);
        s.Items[0].Duration = 1.5;
        s.Move(0, 2);
        Assert.Equal(["b.mp4", "c.mp4", "a.mp4"], s.Items.Select(i => i.Name));
        s.Remove(s.Items[0].Id);
        Assert.Equal(2, s.Items.Count);
        Assert.False(s.CanRemove);
        s.Remove(s.Items[0].Id);
        Assert.Equal(2, s.Items.Count);
        s.Reset();
        Assert.Equal(3, s.Items.Count);
        Assert.Equal("a.mp4", s.Items[0].Name);
        Assert.Equal(1.5, s.TotalDuration);
        Assert.Equal("Merged.mp4", s.OutputName);
        s.IsVideo = false;
        Assert.Equal("Merged.m4a", s.OutputName);
    }

    [Fact]
    public void ReorderTargetIndexIsClamped()
    {
        Assert.Equal(0, ReorderMath.TargetIndex(-20, 38, 4));
        Assert.Equal(1, ReorderMath.TargetIndex(40, 38, 4));
        Assert.Equal(3, ReorderMath.TargetIndex(900, 38, 4));
    }

    private static VideoSourceInfo Source(long bytes) =>
        new(1920, 1080, 0, 60, bytes) { FrameRate = 30, BitRate = (int)(bytes * 8 / 60) };

    [Fact]
    public void CompressEstimateAndAdvice()
    {
        var big = new CompressState([@"C:\a.mp4"], 200_000_000) { Sources = [Source(200_000_000)] };
        Assert.True(big.IsSmaller);
        Assert.True(big.CanCompress);
        Assert.InRange(big.SavingPercent(big.Estimate!.Value), 1, 99);
        Assert.Contains("smaller", big.SavingText);
        Assert.Null(big.SizeAdvice);

        var small = new CompressState([@"C:\a.mp4"], 100_000) { Sources = [Source(100_000)] };
        Assert.False(small.CanCompress);
        Assert.Equal("Already as small as it gets.", small.SizeAdvice);
        Assert.Contains("no smaller", small.SavingText);
        Assert.Equal("Already as small as it gets — nothing saved.", CompressState.NotSmaller(1));
        Assert.Contains("These 3 videos", CompressState.NotSmaller(3));
    }

    [Fact]
    public void CompressSummaryBeforeSourcesLoad()
    {
        var s = new CompressState([@"C:\a.mp4"], 1_500_000);
        Assert.Equal("Estimating…", s.EstimateText);
        Assert.False(s.CanCompress);
        s.LoadError = "x";
        Assert.Equal("x", s.EstimateText);
        Assert.Equal("MP4", s.OutputSummary);
        Assert.Equal("Original 1.5 MB", s.OriginalSummary);
        Assert.Equal("a.mp4", s.Title);
    }
}
