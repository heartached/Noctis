using CommunityToolkit.Mvvm.Input;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Cover Flow is an overlay on the current section, so the section's own action buttons
/// must not show through it. Songs was already gated; Folders / Playlists / Favorites
/// leaked (the "folder options still show in cover flow" report).
/// </summary>
public class TopBarCoverFlowGatingTests
{
    private static readonly RelayCommand Noop = new(() => { });

    [Fact]
    public void Folders_playlists_favorites_actions_hide_in_cover_flow_and_return_after()
    {
        var bar = new TopBarViewModel();
        bar.ShowFoldersActions(Noop, Noop, Noop);
        bar.ShowPlaylistActions(Noop, Noop, Noop, Noop, "Default");
        bar.ShowFavoritesActions(Noop, Noop);

        Assert.True(bar.FoldersActionsVisible);
        Assert.True(bar.PlaylistActionsVisible);
        Assert.True(bar.FavoritesActionsVisible);

        bar.ShowViewModeToggle(Noop, Noop, isCoverFlowMode: true);

        Assert.False(bar.FoldersActionsVisible);
        Assert.False(bar.PlaylistActionsVisible);
        Assert.False(bar.FavoritesActionsVisible);
        // The flags themselves stay set, so leaving Cover Flow brings the buttons back.
        Assert.True(bar.HasFoldersActions);

        bar.ShowViewModeToggle(Noop, Noop, isCoverFlowMode: false);

        Assert.True(bar.FoldersActionsVisible);
        Assert.True(bar.PlaylistActionsVisible);
        Assert.True(bar.FavoritesActionsVisible);
    }

    /// <summary>GitHub #164: on Albums the top-right view icon is the grid / album carousel
    /// toggle (the queue's Cover Flow showed the playing album's tracks, or nothing); every
    /// other page keeps the Cover Flow icon.</summary>
    [Fact]
    public void Albums_page_swaps_the_cover_flow_icon_for_the_album_carousel_toggle()
    {
        var bar = new TopBarViewModel();
        bar.ShowViewModeToggle(Noop, Noop, isCoverFlowMode: false);
        Assert.True(bar.CoverFlowToggleVisible);
        Assert.False(bar.AlbumsViewToggleVisible);

        bar.ShowReleaseTypeChips(new(), Noop);
        Assert.False(bar.CoverFlowToggleVisible);
        Assert.True(bar.AlbumsViewToggleVisible);

        // Cover Flow (opened from the View menu) shows neither; leaving the page restores the icon.
        bar.ShowViewModeToggle(Noop, Noop, isCoverFlowMode: true);
        Assert.False(bar.CoverFlowToggleVisible);
        Assert.False(bar.AlbumsViewToggleVisible);
        bar.ShowViewModeToggle(Noop, Noop, isCoverFlowMode: false);
        bar.HideReleaseTypeChips();
        Assert.True(bar.CoverFlowToggleVisible);
        Assert.False(bar.AlbumsViewToggleVisible);
    }

    [Fact]
    public void Album_quality_pill_segments_follow_the_label()
    {
        var bar = new TopBarViewModel();
        Assert.True(bar.AlbumQualityAll);

        bar.QualityFilterLabel = "Lossless";
        Assert.False(bar.AlbumQualityAll);
        Assert.True(bar.AlbumQualityLossless);
        Assert.False(bar.AlbumQualityHiRes);

        bar.QualityFilterLabel = "Hi-Res";
        Assert.True(bar.AlbumQualityHiRes);
    }

    [Fact]
    public void Artist_sort_chip_shows_and_hides()
    {
        var bar = new TopBarViewModel();
        bar.ShowArtistSort(Noop, "Song count", "songs", ascending: false);

        Assert.True(bar.HasArtistSort);
        Assert.Equal("Song count", bar.ArtistSortLabel);
        Assert.Equal("songs", bar.ArtistSortMode);
        Assert.True(bar.ArtistSortDescending);

        bar.HideArtistSort();
        Assert.False(bar.HasArtistSort);
    }
}
