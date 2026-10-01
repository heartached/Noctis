using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Noctis.Localization;

namespace Noctis.Views;

/// <summary>Where files dragged in from outside the app land with "Import dropped files" off.</summary>
public enum DropZone { None, Play, Library, NewPlaylist }

/// <summary>What dropping files from outside the app does (routed by MainWindow).</summary>
public enum DropAction { PlayOrQueue, Import, AddToLibrary, NewPlaylist, AddToPlaylist }

/// <summary>
/// The drag overlay for files dragged in from outside the app: the import card, or (GitHub
/// #108, "Import dropped files" off) the drop zones with the one under the pointer lit.
/// </summary>
public partial class DropOverlay : UserControl
{
    /// <summary>"Add to queue": three list lines with a plus (GitHub #90).</summary>
    private static readonly Geometry QueueIconData = Geometry.Parse(
        "M3 5h13v2H3z M3 10h13v2H3z M3 15h8v2H3z M17 12h2v4h4v2h-4v4h-2v-4h-4v-2h4z");

    /// <summary>The play glyph from the XAML, kept so the icon can switch back to it.</summary>
    private Geometry? _playIconData;

    public DropOverlay()
    {
        InitializeComponent();
    }

    /// <summary>The zone lit right now.</summary>
    public DropZone Highlighted { get; private set; }

    /// <summary>"Import dropped files" on: the one import card (GitHub #71).</summary>
    public void ShowImport()
    {
        ImportCard.IsVisible = true;
        ZonesCard.IsVisible = false;
        Highlight(DropZone.None);
    }

    /// <summary>
    /// "Import dropped files" off: the zones. <paramref name="startsPlayback"/> — nothing is
    /// loaded, so the first zone plays the drop rather than queueing it (GitHub #86 / #90).
    /// <paramref name="contentLeft"/> is where the page starts right of the sidebar; the
    /// zones center on the page, clear of the rail.
    /// </summary>
    public void ShowZones(bool startsPlayback, string playlistName, double contentLeft)
    {
        ImportCard.IsVisible = false;
        ZonesCard.IsVisible = true;
        _playIconData ??= PlayZoneIcon.Data;
        PlayZoneIcon.Data = startsPlayback ? _playIconData : QueueIconData;
        PlayZoneTitle.Text = Loc.T(startsPlayback ? "Main.DropZonePlay" : "Main.DropZoneQueue");
        PlaylistZoneName.Text = playlistName;
        ZonesCard.Margin = new Thickness(Math.Max(0, contentLeft), 0, 0, 0);
    }

    /// <summary>The zone under the pointer; none outside the tiles or while the import card
    /// shows. <paramref name="positionIn"/> maps the pointer into a visual's coordinates
    /// (DragEventArgs.GetPosition).</summary>
    public DropZone ZoneAt(Func<Visual, Point> positionIn)
    {
        if (!ZonesCard.IsVisible) return DropZone.None;
        foreach (var (tile, zone) in Tiles())
        {
            if (new Rect(tile.Bounds.Size).Contains(positionIn(tile)))
                return zone;
        }
        return DropZone.None;
    }

    public void Highlight(DropZone zone)
    {
        Highlighted = zone;
        foreach (var (tile, z) in Tiles())
            tile.Classes.Set("active", z == zone);
    }

    private IEnumerable<(Border Tile, DropZone Zone)> Tiles()
    {
        yield return (PlayZone, DropZone.Play);
        yield return (LibraryZone, DropZone.Library);
        yield return (PlaylistZone, DropZone.NewPlaylist);
    }

    /// <summary>
    /// A sidebar playlist under the pointer takes the drop in either mode. Otherwise "Import
    /// dropped files" on imports as before; off, the zone decides, and a drop outside every
    /// zone plays / queues, as it did before the zones existed.
    /// </summary>
    public static DropAction ActionFor(bool importDroppedMedia, DropZone zone, bool overPlaylist) =>
        overPlaylist ? DropAction.AddToPlaylist
        : importDroppedMedia ? DropAction.Import
        : zone switch
        {
            DropZone.Library => DropAction.AddToLibrary,
            DropZone.NewPlaylist => DropAction.NewPlaylist,
            _ => DropAction.PlayOrQueue,
        };
}
