namespace Noctis.Helpers;

/// <summary>
/// Shared column/tile math for the album-cover grids (Albums, Favorites).
/// In Automatic mode the grid keeps the classic five covers per row, so covers
/// scale with the window; with a custom cover size the column count is derived
/// from the view width instead, so covers stay near the chosen size on any
/// window — the ultrawide fix.
/// </summary>
public static class AlbumGridMetrics
{
    /// <summary>Classic layout: five covers per row, whatever the window width.</summary>
    public const int ClassicColumns = 5;

    /// <summary>Bounds of the cover-size slider (px of artwork edge the user aims for).</summary>
    public const double MinTargetSize = 140;
    public const double MaxTargetSize = 320;

    /// <summary>Column floor/ceiling so tiny windows and extreme slider values stay usable.</summary>
    public const int MinColumns = 2;
    public const int MaxColumns = 20;

    /// <summary>
    /// Columns for a grid of covers: in auto mode the classic five, otherwise the
    /// count whose resulting tile size lands nearest the user's target.
    /// </summary>
    public static int ComputeColumns(double usableWidth, bool autoSize, double targetSize)
    {
        if (autoSize || !double.IsFinite(usableWidth) || usableWidth <= 0)
            return ClassicColumns;

        var target = double.IsFinite(targetSize)
            ? Math.Clamp(targetSize, MinTargetSize, MaxTargetSize)
            : MaxTargetSize;
        return Math.Clamp(
            (int)Math.Round(usableWidth / target, MidpointRounding.AwayFromZero),
            MinColumns, MaxColumns);
    }

    /// <summary>
    /// Space each tile adds around its cover with "Wider spacing between albums" on: 3px margin
    /// + 6px padding per side (GitHub #210). Two neighbours' chrome make the 18px gap between
    /// covers, the same gap the rows keep between one caption and the next cover.
    /// </summary>
    public const double TileChrome = 18;

    /// <summary>
    /// Space each tile adds around its cover by default: 4px per side, so neighbouring covers
    /// sit 8px apart as they did in 1.6.9 (2px margin + 2px padding then; now all padding, which
    /// is also the selection plate's breathing room, #209).
    /// </summary>
    public const double ClassicTileChrome = 8;

    /// <summary>
    /// Artwork edge for a given usable width and column count, less each tile's chrome
    /// (<see cref="ClassicTileChrome"/>, or <see cref="TileChrome"/> with wide spacing);
    /// 80 is the legibility floor.
    /// </summary>
    public static double ComputeTileSize(double usableWidth, int columns, bool wideSpacing = false)
        => Math.Max(80, usableWidth / columns - (wideSpacing ? TileChrome : ClassicTileChrome));

    /// <summary>One side of a tile's horizontal chrome: margin + padding (3 + 6 with wide
    /// spacing, 0 + 4 by default; twice these make <see cref="TileChrome"/> / <see cref="ClassicTileChrome"/>).</summary>
    private const double TileMarginSide = 3, TilePaddingSide = 6;
    private const double ClassicTileMarginSide = 0, ClassicTilePaddingSide = 4;

    /// <summary>
    /// Artwork edge for a row that WRAPS its tiles (Home's album rows, a WrapPanel) rather than
    /// splitting the width evenly (the Albums grid's UniformGrid). Layout rounding snaps each
    /// side's margin and padding to whole device pixels and rounds each tile up, so at 125%
    /// the wide chrome is 19.2px, not 18, and five tiles from <see cref="ComputeTileSize"/>
    /// overflow the row and the fifth wraps (GitHub #242). This budgets the chrome as rounded
    /// up at <paramref name="layoutScale"/> plus one device pixel per tile, so
    /// <paramref name="columns"/> tiles always fit <paramref name="rowWidth"/>.
    /// </summary>
    public static double ComputeWrappedTileSize(double rowWidth, int columns, bool wideSpacing, double layoutScale)
    {
        var scale = double.IsFinite(layoutScale) && layoutScale > 0 ? layoutScale : 1;
        double Up(double v) => Math.Ceiling(v * scale) / scale;
        var chrome = wideSpacing
            ? 2 * (Up(TileMarginSide) + Up(TilePaddingSide))
            : 2 * (Up(ClassicTileMarginSide) + Up(ClassicTilePaddingSide));
        return Math.Max(80, rowWidth / columns - 1 / scale - chrome);
    }
}
