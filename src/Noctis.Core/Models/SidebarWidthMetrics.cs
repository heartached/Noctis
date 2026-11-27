namespace Noctis.Models;

/// <summary>
/// Width of the expanded sidebar (Discord 11-27, Halil): its right edge drags like Spotify's
/// library, within <see cref="Min"/>..<see cref="Max"/>; narrower than that it snaps to the
/// 60 px icon rail. Hover expand, the hidden sidebar's peek, Ctrl/⌘+B and "Keep sidebar
/// expanded" all open it at the saved width.
/// </summary>
public static class SidebarWidthMetrics
{
    /// <summary>The fixed expanded width before the drag (unchanged for existing installs).</summary>
    public const double Default = 220;

    /// <summary>Narrowest expanded width: a nav row's icon and a short label still fit.</summary>
    public const double Min = 180;

    /// <summary>Widest: leaves the page room at the window's 780 px minimum width.</summary>
    public const double Max = 360;

    /// <summary>A stored or dragged width, in range and on whole pixels; NaN / infinity read
    /// as the default.</summary>
    public static double Clamp(double width) =>
        double.IsFinite(width) ? Math.Clamp(Math.Round(width), Min, Max) : Default;
}
