using System.Collections.Concurrent;
using System.Windows.Media;

namespace OpenShelf.Ui;

/// <summary>
/// The app's icon set: Lucide (ISC licence, see LICENSE-lucide.txt), 24 x 24 outline icons drawn with round caps and joins.
/// The path data lives in the generated <c>Lucide.g.cs</c>; there is no runtime SVG parsing, no network and no extra dependency.
/// Use <see cref="IconView"/> to draw one, or <see cref="Find"/> to look one up by name ("crop", "rotate-cw").
/// </summary>
public static partial class Icons
{
    /// <summary>The side of the grid every icon is drawn on.</summary>
    public const double Grid = 24;

    static readonly ConcurrentDictionary<string, Geometry> Cache = new(StringComparer.Ordinal);

    /// <summary>The icon called <paramref name="name"/>, or null when there is none (callers fall back to something else).</summary>
    public static Geometry? Find(string? name)
    {
        if (string.IsNullOrEmpty(name) || !PathData.ContainsKey(name)) return null;
        return Get(name);
    }

    /// <summary>All icon names, sorted.</summary>
    public static IReadOnlyList<string> Names => PathData.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();

    static Geometry Get(string name) => Cache.GetOrAdd(name, n =>
    {
        var geometry = Geometry.Parse(PathData[n]);
        geometry.Freeze();
        return geometry;
    });

    /// <summary>
    /// The stroke width, in device-independent pixels, for an icon drawn <paramref name="size"/> pixels wide. One width per size class so
    /// icons in the same toolbar or tab strip carry the same weight; the larger the icon, the heavier, but never as heavy as the 24-grid default
    /// would make the small ones.
    /// </summary>
    public static double StrokeFor(double size) => size switch
    {
        <= 14 => 1.25,
        <= 19 => 1.5,
        <= 25 => 1.75,
        <= 32 => 2.0,
        _ => 2.5,
    };
}
