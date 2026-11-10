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
}

/// <param name="Id">"ffmpeg", "libreoffice", "pandoc", "calibre".</param>
/// <param name="Description">What it unlocks, e.g. "Video, audio and GIF formats".</param>
/// <param name="Location">Where it was found, when installed.</param>
/// <param name="CanRemove">True for copies OpenShelf installed itself.</param>
public sealed record ToolRow(string Id, string Name, string Description, bool Installed, string? Location, bool CanRemove);
