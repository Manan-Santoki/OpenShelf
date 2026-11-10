using OpenShelf.Core;

namespace OpenShelf.Wheel;

/// <summary>One wedge: a title and what to do with the dragged files.</summary>
internal sealed record WheelItem(string Title, Func<IReadOnlyList<string>, ScreenPoint, Task> Run, string? Glyph = null);

internal sealed record WheelModel(string Name, IReadOnlyList<WheelItem> Items);
