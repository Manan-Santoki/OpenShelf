namespace OpenShelf.Settings;

/// <summary>What the Extra Formats tab needs from the tool installer (implemented elsewhere).</summary>
public interface IToolInstallerUi
{
    IReadOnlyList<ToolRow> Tools { get; }

    /// <summary>Raised when <see cref="Tools"/> changed (after an install or removal, or a re-scan). May come from any thread.</summary>
    event Action? Changed;

    /// <summary>Downloads and installs. Reports 0..1; throws with a message fit for the user on failure; honours cancellation.</summary>
    Task InstallAsync(string id, IProgress<double> progress, CancellationToken ct);

    Task RemoveAsync(string id);

    /// <summary>True when the last install of <paramref name="id"/> failed because every download source was down and winget is on this PC:
    /// the row then offers "Try winget".</summary>
    bool OffersWinget(string id) => false;

    /// <summary>What an install of <paramref name="id"/> is doing right now, or null when none runs (the pane then says "Downloading…").</summary>
    InstallPhase? PhaseOf(string id) => null;

    /// <summary>Installs through winget (the person chose "Try winget"). Same contract as <see cref="InstallAsync"/>.</summary>
    Task InstallWithWingetAsync(string id, IProgress<double> progress, CancellationToken ct) => InstallAsync(id, progress, ct);
}

/// <summary>The stages of an install, so the Extra Formats row says what is really going on.</summary>
public enum InstallPhase { Downloading, Verifying, Unpacking, Finishing, Winget }

/// <param name="Id">"ffmpeg", "libreoffice", "pandoc", "calibre".</param>
/// <param name="Description">What it unlocks, e.g. "Video, audio and GIF formats".</param>
/// <param name="Location">Where it was found, when installed.</param>
/// <param name="CanRemove">True for copies OpenShelf installed itself.</param>
public sealed record ToolRow(string Id, string Name, string Description, bool Installed, string? Location, bool CanRemove);
