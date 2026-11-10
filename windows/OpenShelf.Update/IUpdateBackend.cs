namespace OpenShelf.Update;

/// <summary>An update a check found. <see cref="Handle"/> is opaque to everything but the backend that made it.</summary>
public sealed record UpdateOffer(string Version, object? Handle = null);

/// <summary>
/// The package manager underneath <see cref="UpdateService"/>. The real one is
/// <see cref="VelopackBackend"/>; tests supply a fake.
/// </summary>
public interface IUpdateBackend
{
    /// <summary>False when running from a build folder or a portable copy that Velopack did not install.</summary>
    bool IsInstalled { get; }

    /// <summary>The running version, or null when not installed.</summary>
    string? CurrentVersion { get; }

    /// <summary>The newest release if it is newer than the running one, else null.</summary>
    Task<UpdateOffer?> CheckAsync(CancellationToken cancel);

    /// <summary>Downloads the offer; <paramref name="progress"/> gets 0 to 100.</summary>
    Task DownloadAsync(UpdateOffer offer, Action<int> progress, CancellationToken cancel);

    /// <summary>Installs a downloaded offer and starts the new version. Does not return when it works.</summary>
    void ApplyAndRestart(UpdateOffer offer);
}
