namespace OpenShelf.Wheel;

/// <summary>What the probe window should do next.</summary>
public enum ProbeStep
{
    /// <summary>Keep waiting for the drag to enter.</summary>
    Wait,
    /// <summary>Show the probe again under the pointer (the drag may have started after, or away from, the first one).</summary>
    Retry,
    /// <summary>Stop: hide the invisible window.</summary>
    GiveUp,
}

/// <summary>
/// The rules for an invisible probe window that has not yet received <c>DragEnter</c>. While the mouse button is down and the trigger keys
/// are still held it shows the probe again under the pointer every <see cref="RetryInterval"/>, so a drag that starts a moment late
/// (a click on an already-selected file, then a quick drag) still finds it. With the keys released (a tap, a shake) it waits
/// <see cref="GiveUpAfter"/> from the last showing and then gives up; a released button always gives up. No clock or Win32 here.
/// </summary>
public sealed class ProbeRetryPolicy
{
    public TimeSpan RetryInterval { get; init; } = TimeSpan.FromMilliseconds(400);
    public TimeSpan GiveUpAfter { get; init; } = TimeSpan.FromSeconds(2);

    private TimeSpan _shownAt;

    /// <summary>The probe was (re)shown at <paramref name="now"/>.</summary>
    public void Shown(TimeSpan now) => _shownAt = now;

    public int Retries { get; private set; }

    /// <summary>Resets the retry count for a new probe.</summary>
    public void Begin(TimeSpan now)
    {
        Retries = 0;
        _shownAt = now;
    }

    /// <summary>Decides what to do at <paramref name="now"/>; on <see cref="ProbeStep.Retry"/> the attempt restarts.</summary>
    public ProbeStep Next(TimeSpan now, bool buttonDown, bool triggerHeld)
    {
        if (!buttonDown) return ProbeStep.GiveUp;
        var waited = now - _shownAt;
        if (triggerHeld)
        {
            if (waited < RetryInterval) return ProbeStep.Wait;
            Retries++;
            _shownAt = now;
            return ProbeStep.Retry;
        }
        return waited >= GiveUpAfter ? ProbeStep.GiveUp : ProbeStep.Wait;
    }
}
