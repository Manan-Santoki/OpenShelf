namespace OpenShelf.Shelf;

/// <summary>Sizes for each shelf state, in DIPs, and the frames of items inside the scrolling body.</summary>
internal static class ShelfLayout
{
    public const double Pad = 48;            // transparent margin around the card for the shadow
    public const double CardRadius = 20;
    public const double HeaderHeight = 40;
    public const double CellWidth = 104;     // tile (96) + 4 margin each side: 8 between tiles
    public const double CellHeight = 120;
    public const double TileWidth = 96;
    public const double TileHeight = 112;
    public const double BodyPadH = 8;
    public const double BodyPadV = 4;
    public const double RowHeight = 34;
    public const int Columns = 3;
    public const double ExpandedWidth = Columns * CellWidth + 2 * BodyPadH;
    public const double MaxCardHeight = 448;

    public static readonly (double W, double H) EmptySize = (200, 150);
    public static readonly (double W, double H) CollapsedSize = (184, 200);

    /// <summary>The card size (header included) for a state.</summary>
    public static (double W, double H) Size(int count, bool expanded, ShelfViewMode mode)
    {
        if (count <= 0) return EmptySize;
        if (!expanded) return CollapsedSize;
        return (ExpandedWidth, Math.Min(HeaderHeight + ContentHeight(count, mode), MaxCardHeight));
    }

    /// <summary>The height that fits every file in the body, before the cap.</summary>
    public static double ContentHeight(int count, ShelfViewMode mode)
    {
        if (count <= 0) return 0;
        return 2 * BodyPadV + (mode == ShelfViewMode.Grid
            ? Math.Ceiling(count / (double)Columns) * CellHeight
            : count * RowHeight);
    }

    public static bool NeedsScrolling(int count, ShelfViewMode mode) =>
        HeaderHeight + ContentHeight(count, mode) > MaxCardHeight;

    /// <summary>Each item's frame in the scrolling content's coordinates (origin at its top left).</summary>
    public static Box[] ItemFrames(int count, ShelfViewMode mode)
    {
        var frames = new Box[Math.Max(0, count)];
        for (int i = 0; i < frames.Length; i++)
        {
            frames[i] = mode == ShelfViewMode.Grid
                ? new Box(BodyPadH + (i % Columns) * CellWidth + 4, BodyPadV + (i / Columns) * CellHeight + 4, TileWidth, TileHeight)
                : new Box(BodyPadH, BodyPadV + i * RowHeight, ExpandedWidth - 2 * BodyPadH, RowHeight);
        }
        return frames;
    }
}
