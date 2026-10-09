using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Noctis.Models;

namespace Noctis.Helpers;

/// <summary>
/// Shared bits of the album/single tile used by the Albums grid, Home, Favorites, the
/// Artist page and More-by-artist (09-13, Apple Music hover): the dots button opens the
/// tile's own menu, and the title reports when it wrapped so the heart moves from the
/// title line down to the subtitle line.
/// </summary>
public static class AlbumTile
{
    /// <summary>
    /// Dots button click: open the menu the tile already has. Views that declare a
    /// Button.ContextMenu get that menu; views that build one in code on
    /// ContextRequested (Home) get the same event a right-click would raise.
    /// </summary>
    public static void OpenMenu(object? sender)
    {
        var tile = FindTile(sender as Control);
        if (tile == null) return;

        if (tile.ContextMenu is { } menu)
        {
            menu.Open(tile);
            return;
        }

        tile.RaiseEvent(new ContextRequestedEventArgs { Source = tile });
    }

    private static Button? FindTile(Control? start)
    {
        for (var c = start; c != null; c = c.Parent as Control)
        {
            if (c is Button b && b.Classes.Contains("album-tile"))
                return b;
        }
        return null;
    }

    /// <summary>Album tracks in disc/track order: what "Play" on a tile queues from the top.
    /// Same rules as the library's album order and the album page (missing track numbers
    /// sink to the end of their disc), so the tile starts on the album page's first song.</summary>
    public static List<Track> OrderedTracks(Album album)
        => (album.Tracks ?? new List<Track>())
            .OrderBy(t => t.DiscNumber <= 0 ? 1 : t.DiscNumber)
            .ThenBy(t => t.TrackNumber <= 0 ? int.MaxValue : t.TrackNumber)
            .ThenBy(t => t.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
}

/// <summary>
/// <c>helpers:TitleWrap.Watch="True"</c> on a tile's title TextBlock sets the class
/// <c>wrapped</c> on the nearest ancestor with class <c>tile-text</c> whenever the title
/// takes more than one line, and clears it when it fits again. Styles move the heart
/// between the title line and the subtitle line on that class. SizeChanged is the only
/// hook: a title that changes line count changes height, and one that does not is
/// already right.
/// </summary>
public static class TitleWrap
{
    public static readonly AttachedProperty<bool> WatchProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, bool>("Watch", typeof(TitleWrap));

    public static bool GetWatch(TextBlock tb) => tb.GetValue(WatchProperty);
    public static void SetWatch(TextBlock tb, bool value) => tb.SetValue(WatchProperty, value);

    static TitleWrap()
    {
        WatchProperty.Changed.AddClassHandler<TextBlock>((tb, e) =>
        {
            tb.SizeChanged -= OnSizeChanged;
            if (e.NewValue is true) tb.SizeChanged += OnSizeChanged;
        });
    }

    private static void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (sender is not TextBlock tb) return;
        var wrapped = tb.TextLayout.TextLines.Count > 1;
        for (var c = tb.Parent as Control; c != null; c = c.Parent as Control)
        {
            if (!c.Classes.Contains("tile-text")) continue;
            if (wrapped) { if (!c.Classes.Contains("wrapped")) c.Classes.Add("wrapped"); }
            else c.Classes.Remove("wrapped");
            return;
        }
    }
}
