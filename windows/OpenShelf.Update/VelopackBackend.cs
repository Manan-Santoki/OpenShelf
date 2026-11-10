using Velopack;
using Velopack.Locators;
using Velopack.Sources;

namespace OpenShelf.Update;

/// <summary>Velopack-backed <see cref="IUpdateBackend"/>. Releases come from GitHub Releases, or any <see cref="IUpdateSource"/>.</summary>
public sealed class VelopackBackend : IUpdateBackend
{
    /// <summary>The repository the Windows releases are published to.</summary>
    public const string DefaultRepository = "https://github.com/Manan-Santoki/openshelf";

    private readonly UpdateManager? _manager;

    public VelopackBackend(string repositoryUrl = DefaultRepository)
        : this(new GithubSource(repositoryUrl, accessToken: null, prerelease: RunningPrerelease()))
    {
    }

    /// <summary>A beta copy ("0.1.0-beta.1") follows pre-releases, so it sees the next beta and the final release;
    /// a released copy only ever sees released versions.</summary>
    public static bool RunningPrerelease() => IsPrerelease(
        System.Reflection.Assembly.GetEntryAssembly()?
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion);

    public static bool IsPrerelease(string? version)
    {
        if (string.IsNullOrEmpty(version)) return false;
        var core = version.Split('+')[0];   // drop build metadata such as the commit id
        return core.Contains('-');
    }

    public VelopackBackend(IUpdateSource source, IVelopackLocator? locator = null)
    {
        try
        {
            _manager = new UpdateManager(source, options: null, locator: locator);
        }
        catch (InvalidOperationException)
        {
            // Velopack has no locator: VelopackApp.Run() never ran in this process (a test host, a tool).
            // Treat that like any other copy that was not installed.
            _manager = null;
        }
    }

    public bool IsInstalled => _manager?.IsInstalled == true;

    public string? CurrentVersion => IsInstalled ? _manager!.CurrentVersion?.ToString() : null;

    public async Task<UpdateOffer?> CheckAsync(CancellationToken cancel)
    {
        // Velopack's check has no token; honour ours at the edges.
        cancel.ThrowIfCancellationRequested();
        var info = await _manager!.CheckForUpdatesAsync().ConfigureAwait(false);
        cancel.ThrowIfCancellationRequested();
        return info is null ? null : new UpdateOffer(info.TargetFullRelease.Version.ToString(), info);
    }

    public Task DownloadAsync(UpdateOffer offer, Action<int> progress, CancellationToken cancel)
        => _manager!.DownloadUpdatesAsync((UpdateInfo)offer.Handle!, progress, cancel);

    public void ApplyAndRestart(UpdateOffer offer)
        => _manager!.ApplyUpdatesAndRestart(((UpdateInfo)offer.Handle!).TargetFullRelease);
}
