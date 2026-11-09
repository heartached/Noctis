using Avalonia.Controls;
using Noctis.Mobile.Services;
using Noctis.Mobile.ViewModels;

namespace Noctis.Mobile.Views;

public partial class SongListPage : UserControl
{
    /// <summary>The playlist cover's side: the page's width less this much each side, between
    /// <see cref="CoverMin"/> and <see cref="CoverMax"/>, and no more than
    /// <see cref="CoverViewportShare"/> of the list's height (landscape).</summary>
    private const double CoverInset = 72, CoverMin = 160, CoverMax = 300, CoverViewportShare = 0.5;

    public SongListPage()
    {
        InitializeComponent();
        // For the frame stall monitor (no-op while it is off): rows realized in a stalled frame.
        SongList.ContainerPrepared += (_, _) => FrameStallMonitor.Count("rows prepared");
        DataContextChanged += (_, _) => PlaceListTop();
        // On a size change only (rotation, the first layout), never per scroll frame.
        SongScroll.SizeChanged += (_, _) => SizeCover();
    }

    /// <summary>
    /// A playlist's page: the find field, Play / Shuffle and the count go into the scroller under
    /// the cover, so they scroll away with it (Apple's playlist page); the back row and the title
    /// stay put in the header. Other lists keep them fixed above the list.
    /// </summary>
    private void PlaceListTop()
    {
        var inScroll = DataContext is SongListPageViewModel { IsPlaylist: true };
        Panel target = inScroll ? ScrollBody : PageGrid;
        if (ReferenceEquals(ListTop.Parent, target)) return;
        (ListTop.Parent as Panel)?.Children.Remove(ListTop);
        if (inScroll) ScrollBody.Children.Insert(ScrollBody.Children.IndexOf(PlaylistCover) + 1, ListTop);
        else PageGrid.Children.Insert(PageGrid.Children.IndexOf(SongScroll), ListTop);
    }

    private void SizeCover()
    {
        var width = SongScroll.Bounds.Width;
        if (width <= 0) return;
        var side = Math.Round(Math.Min(Math.Clamp(width - 2 * CoverInset, CoverMin, CoverMax),
            Math.Max(CoverMin, SongScroll.Bounds.Height * CoverViewportShare)));
        if (PlaylistCover.Width == side) return;
        PlaylistCover.Width = side;
        PlaylistCover.Height = side;
    }
}
