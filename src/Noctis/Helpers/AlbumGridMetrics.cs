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
    /// Artwork edge for a given usable width and column count. Each tile carries 8px
    /// of chrome (2px margin + 2px padding per side); 80 is the legibility floor.
    /// </summary>
    public static double ComputeTileSize(double usableWidth, int columns)
        => Math.Max(80, usableWidth / columns - 8);
}
