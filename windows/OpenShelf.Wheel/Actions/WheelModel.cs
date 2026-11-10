using OpenShelf.Core;

namespace OpenShelf.Wheel;

/// <summary>One wheel's entries in display order. <paramref name="EmptyMessage"/> is toasted when there are none.</summary>
internal sealed record WheelModel(WheelKind Kind, IReadOnlyList<WheelEntry> Items, string? EmptyMessage = null)
{
    public string Name => WheelCycle.Title(Kind);
    public bool HasOptions => Items.Any(i => i.HasOptions);
}
