using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The Songs section's corner controls (filter · sort · view options · Shuffle) are gated on
/// TopBarViewModel.PageActionsVisible. This replays the exact call sequences
/// MainWindowViewModel runs when leaving Songs and coming back, so a regression in the
/// show/hide ordering shows up here rather than as "the buttons vanished".
/// </summary>
public class TopBarPageActionsTests
{
    private readonly ITestOutputHelper _out;
    public TopBarPageActionsTests(ITestOutputHelper output) => _out = output;

    private static readonly ICommand Noop = new RelayCommand(() => { });

    /// <summary>MainWindowViewModel.SetupSongsTopBarActions, minus the view-model wiring.</summary>
    private static void SetupSongs(TopBarViewModel bar)
    {
        bar.HidePageActions();
        bar.HideSongsFilters();
        bar.ShowPageActions(Noop, Noop, false, true, "Title", Noop, Noop, Noop, Noop);
        bar.ShowSongsFilters("2,144 songs · 138 hours", string.Empty, Noop);
    }

    /// <summary>MainWindowViewModel.ClearAllTopBarActions.</summary>
    private static void ClearAll(TopBarViewModel bar)
    {
        bar.HidePageActions();
        bar.HideSongsFilters();
        bar.HidePlaylistActions();
        bar.HideArtistActions();
        bar.HideFavoritesActions();
        bar.HideFoldersActions();
        bar.HideArtistSort();
        bar.HideViewModeToggle();
    }

    /// <summary>MainWindowViewModel.SetupGlobalViewModeToggle.</summary>
    private static void SetupViewMode(TopBarViewModel bar, bool coverFlow) =>
        bar.ShowViewModeToggle(Noop, Noop, coverFlow);

    [Fact]
    public void LeavingSongsForAnotherSectionAndComingBack_KeepsTheCornerControls()
    {
        var bar = new TopBarViewModel();

        // Land on Songs.
        ClearAll(bar);
        SetupViewMode(bar, coverFlow: false);
        SetupSongs(bar);
        Assert.True(bar.PageActionsVisible);

        // Away to Albums (eligible section, no page actions of its own).
        ClearAll(bar);
        SetupViewMode(bar, coverFlow: false);
        Assert.False(bar.PageActionsVisible);

        // Back to Songs.
        ClearAll(bar);
        SetupViewMode(bar, coverFlow: false);
        SetupSongs(bar);

        _out.WriteLine($"HasPageActions={bar.HasPageActions} IsCoverFlowMode={bar.IsCoverFlowMode} PageActionsVisible={bar.PageActionsVisible}");
        Assert.True(bar.PageActionsVisible);
        Assert.True(bar.SongsFiltersVisible);
    }

    [Fact]
    public void LeavingSongsForANonSectionViewAndComingBack_KeepsTheCornerControls()
    {
        var bar = new TopBarViewModel();
        ClearAll(bar);
        SetupViewMode(bar, coverFlow: false);
        SetupSongs(bar);

        // Away to a detail page (Queue / Album detail): not toggle-eligible, so the
        // view-mode toggle is not re-shown.
        ClearAll(bar);
        Assert.False(bar.PageActionsVisible);

        // Back via history (RestoreNavigationEntry → RestoreTopBarActionsForView).
        ClearAll(bar);
        SetupViewMode(bar, coverFlow: false);
        SetupSongs(bar);

        _out.WriteLine($"HasPageActions={bar.HasPageActions} IsCoverFlowMode={bar.IsCoverFlowMode} PageActionsVisible={bar.PageActionsVisible}");
        Assert.True(bar.PageActionsVisible);
    }

    [Fact]
    public void CoverFlowRoundTrip_RestoresTheCornerControls()
    {
        var bar = new TopBarViewModel();
        ClearAll(bar);
        SetupViewMode(bar, coverFlow: false);
        SetupSongs(bar);

        // Enter Cover Flow: the page actions are suppressed, not cleared.
        bar.IsCoverFlowMode = true;
        Assert.False(bar.PageActionsVisible);

        // Exit: ExitCoverFlowMode clears the flag, then restores the section's actions.
        bar.IsCoverFlowMode = false;
        SetupViewMode(bar, coverFlow: false);
        SetupSongs(bar);

        _out.WriteLine($"HasPageActions={bar.HasPageActions} IsCoverFlowMode={bar.IsCoverFlowMode} PageActionsVisible={bar.PageActionsVisible}");
        Assert.True(bar.PageActionsVisible);
    }

    /// <summary>Discord (v1.6.8, artist page): opening search pushed the whole Artist page,
    /// hero included, ~56 px down and left an empty strip above it. The search pill's page
    /// inset (MainWindow.InitializeSearchCapsuleInset) only applies under a top bar; pages
    /// without one start with a full-bleed hero that the pill floats over.</summary>
    [Fact]
    public void OpenSearch_OnPagesWithoutATopBar_DoesNotReserveTheBand()
    {
        var bar = new TopBarViewModel { CurrentTabName = "Artists", IsSearchVisible = true };

        // MainWindowViewModel.OpenArtistDiscography: ClearAllTopBarActions + RefreshBackButton.
        ClearAll(bar);
        bar.ShowBackButton("Back", Noop);
        bar.IsSearchOpen = true;
        Assert.False(bar.HasBarContent);
        Assert.False(bar.ReservesSearchBand);

        // MainWindowViewModel.OpenAlbumDetail.
        ClearAll(bar);
        bar.ShowAlbumDetailBackButton("Back", Noop);
        Assert.True(bar.IsSearchOpen);
        Assert.False(bar.ReservesSearchBand);
    }

    /// <summary>The 10-24 inset itself stays: under a bar the pill covers the Songs column
    /// headers, and More By (titled bar) keeps it too.</summary>
    [Fact]
    public void OpenSearch_UnderATopBar_StillReservesTheBand()
    {
        var bar = new TopBarViewModel { CurrentTabName = "Songs", IsSearchVisible = true };
        ClearAll(bar);
        SetupViewMode(bar, coverFlow: false);
        SetupSongs(bar);
        bar.IsSearchOpen = true;
        Assert.True(bar.ReservesSearchBand);

        // MainWindowViewModel.OpenMoreByArtistPage.
        ClearAll(bar);
        bar.ShowArtistActions(Noop, Noop);
        bar.ShowBackButton("Back", Noop, "More By Artist");
        Assert.True(bar.ReservesSearchBand);

        bar.IsSearchOpen = false;
        Assert.False(bar.ReservesSearchBand);
    }
}
