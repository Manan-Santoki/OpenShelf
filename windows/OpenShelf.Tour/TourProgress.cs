using System.Text.Json;
using OpenShelf.Core;

namespace OpenShelf.Tour;

/// <summary>
/// Follows the tour: which step is showing, which are done, and what the person just did. Fed by the wheel service's
/// activity while the tour window is open. Done steps are remembered in the preferences ("tour.progress"); Settings →
/// General → Start Over clears that value, which <see cref="Reload"/> picks up.
/// </summary>
public sealed class TourProgress
{
    public const string ProgressKey = "tour.progress";

    private readonly IPreferences _preferences;
    private readonly HashSet<TourStep> _completed = [];

    public TourProgress(IPreferences preferences)
    {
        _preferences = preferences;
        Reload();
    }

    /// <summary>Raised after any change, on the thread that caused it (the UI thread).</summary>
    public event Action? Changed;

    public TourStep Current { get; private set; } = TourStep.Welcome;
    public IReadOnlySet<TourStep> Completed => _completed;

    /// <summary>The current step's wheel is open: the next move is the drop.</summary>
    public bool IsUnderWay { get; private set; }

    /// <summary>Goes up each time a step is done, to celebrate it.</summary>
    public int Celebration { get; private set; }

    /// <summary>Whether the tour window is open; events are only followed then.</summary>
    public bool IsActive { get; set; }

    public int PracticeDoneCount => _completed.Count(s => s.IsPractice());

    /// <summary>Feeds in something the person did. Returns true when it completed the current step.</summary>
    public bool Record(WheelEvent e)
    {
        if (!IsActive) return false;
        if (Current.IsCompleted(e))
        {
            Complete(Current);
            return true;
        }
        if (!_completed.Contains(Current) && Current.IsUnderWay(e))
        {
            IsUnderWay = true;
            Changed?.Invoke();
        }
        return false;
    }

    public void Show(TourStep step)
    {
        Current = step;
        IsUnderWay = false;
        Changed?.Invoke();
    }

    public bool CanGoBack => Current != TourStep.Welcome;
    public bool CanGoNext => Current != TourStep.Finish;

    public void Next()
    {
        var index = (int)Current + 1;
        if (index < TourSteps.All.Count) Show(TourSteps.All[index]);
    }

    public void Previous()
    {
        var index = (int)Current - 1;
        if (index >= 0) Show(TourSteps.All[index]);
    }

    public void Complete(TourStep step)
    {
        IsUnderWay = false;
        if (_completed.Add(step))
        {
            Celebration++;
            Save();
        }
        Changed?.Invoke();
    }

    /// <summary>Starts over, forgetting what was done.</summary>
    public void Reset()
    {
        _completed.Clear();
        Save();
        Show(TourStep.Welcome);
    }

    /// <summary>Re-reads what was done from the preferences (Start Over in Settings clears it).</summary>
    public void Reload()
    {
        _completed.Clear();
        foreach (var step in Decode(_preferences.GetJson(ProgressKey))) _completed.Add(step);
    }

    private void Save() =>
        _preferences.SetJson(ProgressKey, Encode(_completed));

    /// <summary>{"completed":["convert","pdf"]}, in tour order.</summary>
    internal static string Encode(IEnumerable<TourStep> steps) =>
        JsonSerializer.Serialize(new Stored(steps.Where(s => s.IsPractice()).OrderBy(s => (int)s).Select(s => s.ToString().ToLowerInvariant()).ToList()));

    /// <summary>Tolerant: anything unreadable or unknown is ignored.</summary>
    internal static IReadOnlyList<TourStep> Decode(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            var stored = JsonSerializer.Deserialize<Stored>(json);
            var steps = new List<TourStep>();
            foreach (var name in stored?.Completed ?? [])
                if (Enum.TryParse<TourStep>(name, ignoreCase: true, out var step) && step.IsPractice() && !steps.Contains(step)) steps.Add(step);
            return steps;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException) { return []; }
    }

    private sealed record Stored(List<string> Completed)
    {
        public Stored() : this([]) { }
    }
}
