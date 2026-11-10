namespace OpenShelf.Update;

public enum UpdateCheckStatus
{
    UpToDate,
    UpdateAvailable,
    /// <summary>Running from a build folder or a copy Velopack did not install; there is nothing to update.</summary>
    NotInstalled,
    Error,
}

public sealed record UpdateCheckResult(UpdateCheckStatus Status, string? Version = null, string? Message = null)
{
    public static UpdateCheckResult UpToDate { get; } = new(UpdateCheckStatus.UpToDate);

    public static UpdateCheckResult NotInstalled { get; } =
        new(UpdateCheckStatus.NotInstalled, Message: "This copy of OpenShelf was not installed with the installer, so it cannot update itself.");
}

/// <summary>Everything the service needs to say or ask. All optional; unset ones do nothing (or answer no).</summary>
public sealed class UpdatePrompts
{
    /// <summary>Tells the user something short ("OpenShelf is up to date"). Called on any thread.</summary>
    public Action<string>? Notify { get; set; }

    /// <summary>"Version x is available. Install it?" Asked after a check the user started.</summary>
    public Func<string, Task<bool>>? ConfirmInstall { get; set; }

    /// <summary>"Version x is ready. Restart now?" Asked after a background download.</summary>
    public Func<string, Task<bool>>? ConfirmRestart { get; set; }

    /// <summary>Download progress, 0 to 100.</summary>
    public Action<int>? Progress { get; set; }

    /// <summary>Last chance to save state: called just before the process is replaced by the new version.</summary>
    public Action? BeforeRestart { get; set; }
}

/// <summary>
/// Checks for, downloads and applies updates, and runs the automatic check on
/// launch and then daily. No UI: questions and messages go through <see cref="UpdatePrompts"/>.
/// </summary>
public sealed class UpdateService : IDisposable
{
    private readonly IUpdateBackend _backend;
    private readonly Func<bool> _automaticUpdates;
    private readonly SemaphoreSlim _busy = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;
    private UpdateOffer? _pending;
    private UpdateOffer? _downloaded;

    public UpdateService(IUpdateBackend backend, Func<bool> automaticUpdates)
    {
        _backend = backend;
        _automaticUpdates = automaticUpdates;
    }

    public UpdatePrompts Prompts { get; } = new();

    /// <summary>Delay before the first background check, so launch stays quick.</summary>
    public TimeSpan InitialDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Time between background checks.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Called when the background loop hits an error; a logging hook.</summary>
    public Action<Exception>? OnError { get; set; }

    public bool IsInstalled => _backend.IsInstalled;
    public string? CurrentVersion => _backend.CurrentVersion;

    /// <summary>The newest version a check has found and that has not been installed, if any.</summary>
    public string? AvailableVersion { get; private set; }

    /// <summary>Asks the backend. Reports failures as <see cref="UpdateCheckStatus.Error"/> instead of throwing (cancellation aside).</summary>
    public async Task<UpdateCheckResult> CheckNowAsync(CancellationToken cancel = default)
    {
        if (!_backend.IsInstalled)
            return UpdateCheckResult.NotInstalled;
        try
        {
            var offer = await _backend.CheckAsync(cancel).ConfigureAwait(false);
            if (offer is null)
            {
                AvailableVersion = null;
                _pending = null;
                return UpdateCheckResult.UpToDate;
            }
            AvailableVersion = offer.Version;
            _pending = offer;
            return new UpdateCheckResult(UpdateCheckStatus.UpdateAvailable, offer.Version);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult(UpdateCheckStatus.Error, Message: ex.Message);
        }
    }

    /// <summary>Downloads the update the last check found, reporting progress through <see cref="UpdatePrompts.Progress"/>.</summary>
    public async Task DownloadAsync(CancellationToken cancel = default)
    {
        var offer = _pending ?? throw new InvalidOperationException("No update has been found. Check for updates first.");
        if (_downloaded == offer)
            return;
        await _backend.DownloadAsync(offer, p => Prompts.Progress?.Invoke(Math.Clamp(p, 0, 100)), cancel).ConfigureAwait(false);
        _downloaded = offer;
    }

    /// <summary>Installs the downloaded update and restarts. Returns false if nothing was downloaded.</summary>
    public bool ApplyAndRestart()
    {
        if (_downloaded is null)
            return false;
        try { Prompts.BeforeRestart?.Invoke(); }
        catch { /* saving must not block the update */ }
        _backend.ApplyAndRestart(_downloaded);
        return true;
    }

    /// <summary>
    /// The "Check for Updates…" command: check, say what happened, and on a yes download and restart.
    /// </summary>
    public async Task<UpdateCheckResult> CheckInteractivelyAsync(CancellationToken cancel = default)
    {
        if (!await _busy.WaitAsync(0, cancel).ConfigureAwait(false))
            return new UpdateCheckResult(UpdateCheckStatus.Error, Message: "An update check is already running.");
        try
        {
            var result = await CheckNowAsync(cancel).ConfigureAwait(false);
            switch (result.Status)
            {
                case UpdateCheckStatus.UpToDate:
                    Prompts.Notify?.Invoke($"OpenShelf is up to date ({CurrentVersion})");
                    break;
                case UpdateCheckStatus.NotInstalled:
                    Prompts.Notify?.Invoke("Updates only work in the installed app; this copy runs from a folder");
                    break;
                case UpdateCheckStatus.Error:
                    Prompts.Notify?.Invoke($"Couldn't check for updates: {result.Message}");
                    break;
                case UpdateCheckStatus.UpdateAvailable:
                    var yes = Prompts.ConfirmInstall is { } ask && await ask(result.Version!).ConfigureAwait(false);
                    if (yes)
                    {
                        try
                        {
                            await DownloadAsync(cancel).ConfigureAwait(false);
                            ApplyAndRestart();
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            Prompts.Notify?.Invoke($"Couldn't install the update: {ex.Message}");
                            return new UpdateCheckResult(UpdateCheckStatus.Error, result.Version, ex.Message);
                        }
                    }
                    break;
            }
            return result;
        }
        finally
        {
            _busy.Release();
        }
    }

    /// <summary>
    /// One background pass: does nothing when automatic updates are off or the app is not installed;
    /// otherwise downloads a new version quietly and asks whether to restart into it. A "not now"
    /// leaves it staged; Velopack applies it the next time the app starts.
    /// </summary>
    public async Task RunBackgroundCheckAsync(CancellationToken cancel = default)
    {
        if (!_automaticUpdates() || !_backend.IsInstalled)
            return;
        if (!await _busy.WaitAsync(0, cancel).ConfigureAwait(false))
            return;
        try
        {
            var result = await CheckNowAsync(cancel).ConfigureAwait(false);
            if (result.Status != UpdateCheckStatus.UpdateAvailable)
                return;
            await DownloadAsync(cancel).ConfigureAwait(false);
            if (Prompts.ConfirmRestart is { } ask && await ask(result.Version!).ConfigureAwait(false))
                ApplyAndRestart();
        }
        finally
        {
            _busy.Release();
        }
    }

    /// <summary>Starts the automatic checks: once after <see cref="InitialDelay"/>, then every <see cref="Interval"/>. Idempotent; does nothing when not installed.</summary>
    public void Start()
    {
        if (_loop is not null || !_backend.IsInstalled)
            return;
        var token = _stop.Token;
        _loop = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(InitialDelay, token).ConfigureAwait(false);
                while (!token.IsCancellationRequested)
                {
                    try { await RunBackgroundCheckAsync(token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                    catch (Exception ex) { OnError?.Invoke(ex); }
                    await Task.Delay(Interval, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Stopped.
            }
        }, token);
    }

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }
}
