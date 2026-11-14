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
}
