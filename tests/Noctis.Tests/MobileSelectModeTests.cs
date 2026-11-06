using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Noctis.Mobile.ViewModels;
using Noctis.Mobile.Views;
using Noctis.Models;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Beta tester 11-04: "no multi-select in albums — adding several tracks to a playlist means
/// doing it one by one". Song lists (Songs, Favorites, a playlist, an artist's songs) and the
/// album page get Apple Music's Select mode: a song's menu › Select or the list's Select
/// button, ticks on the rows, "N Selected" with Select All / Done, and an action bar above
/// the tabs. Back ends Select mode before the page goes.
/// </summary>
public class MobileSelectModeTests
{
    private static (MobileFixtures.Rig Rig, Track[] Songs) SongsRig(int count = 5)
    {
        var songs = Enumerable.Range(1, count).Select(i => MobileFixtures.Song($"Song {i}")).ToArray();
        return (MobileFixtures.MakeRig(songs), songs);
    }

    private static SongListPageViewModel OpenSongs(MobileFixtures.Rig rig)
    {
        rig.Shell.OpenSongsCommand.Execute(null);
        return (SongListPageViewModel)rig.Shell.CurrentPage!;
    }

    [Fact]
    public void SelectButton_EntersSelectMode_ATapTicksInsteadOfPlaying_AndTheMiniPlayerStepsAside()
    {
        var (rig, _) = SongsRig();
        using var _r = rig;
        var page = OpenSongs(rig);
        rig.Shell.Player.PlayTracks(page.Songs.Take(1).ToList(), 0);
        Assert.True(rig.Shell.IsMiniBarVisible);

        page.BeginSelectCommand.Execute(null);
        Assert.True(page.Selection.IsActive);
        Assert.True(rig.Shell.IsSelecting);
        Assert.Same(page.Selection, rig.Shell.ActiveSelection);
        Assert.False(rig.Shell.IsMiniBarVisible);   // the action bar takes its place
        Assert.Equal("Select Items", page.Selection.HeaderText);

        var playing = rig.Shell.Player.CurrentTrack;
        page.PlayCommand.Execute(page.Songs[2]);
        page.PlayCommand.Execute(page.Songs[4]);
        Assert.Same(playing, rig.Shell.Player.CurrentTrack);   // ticked, not played
        Assert.Equal(2, page.Selection.Count);
        Assert.Equal("2 Selected", page.Selection.HeaderText);
        Assert.True(page.Selection.IsSelected(page.Songs[2]));

        page.PlayCommand.Execute(page.Songs[2]);   // a second tap unticks
        Assert.Equal(1, page.Selection.Count);

        page.Selection.EndCommand.Execute(null);   // Done
        Assert.False(page.Selection.IsActive);
        Assert.False(rig.Shell.IsSelecting);
        Assert.True(rig.Shell.IsMiniBarVisible);
        Assert.False(page.Selection.IsSelected(page.Songs[4]));
    }

    [Fact]
    public void SongMenu_Select_StartsWithThatSongTicked_AndALongPressThenTicks()
    {
        var (rig, _) = SongsRig();
        using var _r = rig;
        var page = OpenSongs(rig);

        page.OpenSongMenuCommand.Execute(page.Songs[1]);
        Assert.True(rig.Shell.Sheet!.CanSelect);
        rig.Shell.Sheet.SelectCommand.Execute(null);
        Assert.False(rig.Shell.IsSheetOpen);
        Assert.True(page.Selection.IsActive);
        Assert.Equal(new[] { page.Songs[1] }, page.Selection.SelectedTracks);

        // In Select mode a long press (the row's ⋯ command) ticks rather than opening the menu.
        page.OpenSongMenuCommand.Execute(page.Songs[3]);
        Assert.False(rig.Shell.IsSheetOpen);
        Assert.Equal(new[] { page.Songs[1], page.Songs[3] }, page.Selection.SelectedTracks);
    }

    [Fact]
    public void Back_EndsSelectMode_BeforeThePageGoes()
    {
        var (rig, _) = SongsRig();
        using var _r = rig;
        var page = OpenSongs(rig);
        page.BeginSelectCommand.Execute(null);
        page.Selection.Toggle(page.Songs[0]);

        Assert.True(rig.Shell.TryHandleBack());
        Assert.False(page.Selection.IsActive);
        Assert.Same(page, rig.Shell.CurrentPage);   // still on the list

        Assert.True(rig.Shell.TryHandleBack());
        Assert.Null(rig.Shell.CurrentPage);
    }

    [Fact]
    public void LeavingThePage_OrSwitchingTabs_EndsSelectMode()
    {
        var (rig, _) = SongsRig();
        using var _r = rig;
        var page = OpenSongs(rig);
        page.BeginSelectCommand.Execute(null);
        rig.Shell.NavigateBackCommand.Execute(null);
        Assert.False(page.Selection.IsActive);
        Assert.Null(rig.Shell.ActiveSelection);

        // The Favorites tab's own list (embedded, never popped) ends on a tab switch.
        rig.Shell.SelectTabCommand.Execute(MobileTab.Favorites);
        rig.Shell.FavoritesRoot.BeginSelectCommand.Execute(null);
        Assert.True(rig.Shell.IsSelecting);
        rig.Shell.SelectTabCommand.Execute(MobileTab.Home);
        Assert.False(rig.Shell.FavoritesRoot.Selection.IsActive);
        Assert.False(rig.Shell.IsSelecting);
    }

    [Fact]
    public void SelectAll_TicksEveryShownRow_ThenDeselectAll()
    {
        var (rig, _) = SongsRig(6);
        using var _r = rig;
        var page = OpenSongs(rig);
        page.BeginSelectCommand.Execute(null);
        Assert.Equal("Select All", page.Selection.SelectAllLabel);

        page.Selection.ToggleAllCommand.Execute(null);
        Assert.Equal(6, page.Selection.Count);
        Assert.True(page.Selection.AllSelected);
        Assert.Equal("Deselect All", page.Selection.SelectAllLabel);
        Assert.Equal(page.Songs, page.Selection.SelectedTracks);

        page.Selection.ToggleAllCommand.Execute(null);
        Assert.Equal(0, page.Selection.Count);
    }

    [Fact]
    public void AFilterThatHidesTickedRows_DropsTheirTicks()
    {
        var (rig, _) = SongsRig(4);
        using var _r = rig;
        var page = OpenSongs(rig);
        page.BeginSelectCommand.Execute(null);
        page.Selection.ToggleAllCommand.Execute(null);

        page.FilterText = "Song 3";
        Assert.Equal(1, page.Selection.Count);
        Assert.Equal(new[] { "Song 3" }, page.Selection.SelectedTracks.Select(t => t.Title));
    }

    [Fact]
    public void PlayNext_And_AddToQueue_TakeEverySelectedSong_InListOrder()
    {
        var (rig, _) = SongsRig(5);
        using var _r = rig;
        var page = OpenSongs(rig);
        var playing = MobileFixtures.Song("Playing");
        rig.Shell.Player.PlayTracks(new[] { playing }, 0);

        page.BeginSelectCommand.Execute(null);
        page.Selection.Toggle(page.Songs[3]);
        page.Selection.Toggle(page.Songs[1]);
        page.Selection.PlayNextCommand.Execute(null);
        Assert.Equal(new[] { page.Songs[1], page.Songs[3] }, rig.Shell.Player.UpNext.Take(2));
        Assert.False(page.Selection.IsActive);   // an action ends Select mode, as Apple's

        page.BeginSelectCommand.Execute(null);
        page.Selection.Toggle(page.Songs[4]);
        page.Selection.Toggle(page.Songs[0]);
        page.Selection.AddToQueueCommand.Execute(null);
        Assert.Equal(new[] { page.Songs[0], page.Songs[4] }, rig.Shell.Player.UpNext.TakeLast(2));
    }

    [Fact]
    public async Task Favorite_HeartsEverySelectedSong_AndAgainUnheartsThem()
    {
        var (rig, _) = SongsRig(4);
        using var _r = rig;
        var page = OpenSongs(rig);
        page.BeginSelectCommand.Execute(null);
        page.Selection.Toggle(page.Songs[0]);
        page.Selection.Toggle(page.Songs[2]);
        Assert.Equal("Favorite", page.Selection.FavouriteLabel);
        var picked = page.Selection.SelectedTracks;

        await page.Selection.ToggleFavouriteCommand.ExecuteAsync(null);
        Assert.All(picked, t => Assert.True(t.IsFavorite));
        Assert.False(page.Songs[1].IsFavorite);

        page.BeginSelectCommand.Execute(null);
        foreach (var t in picked) page.Selection.Toggle(t);
        Assert.True(page.Selection.IsAllFavourite);
        Assert.Equal("Remove from Favorites", page.Selection.FavouriteLabel);
        await page.Selection.ToggleFavouriteCommand.ExecuteAsync(null);
        Assert.All(picked, t => Assert.False(t.IsFavorite));
    }

    [Fact]
    public async Task AddToPlaylist_OpensThePlaylistList_ForEverySelectedSong()
    {
        var songs = Enumerable.Range(1, 4).Select(i => MobileFixtures.Song($"Song {i}")).ToArray();
        var mix = new Playlist { Name = "Mix" };
        using var rig = MobileFixtures.MakeRig(songs, seed: p => p.SavePlaylistsAsync(new List<Playlist> { mix }));
        var page = OpenSongs(rig);
        page.BeginSelectCommand.Execute(null);
        page.Selection.Toggle(page.Songs[0]);
        page.Selection.Toggle(page.Songs[1]);
        page.Selection.Toggle(page.Songs[3]);

        page.Selection.AddToPlaylistCommand.Execute(null);
        Assert.True(rig.Shell.IsPlaylistPickerOpen);   // straight to the list, no menu first
        Assert.False(page.Selection.IsActive);
        var sheet = rig.Shell.Sheet!;
        Assert.Equal("3 songs", sheet.Title);
        Assert.Equal(new[] { page.Songs[0], page.Songs[1], page.Songs[3] }, sheet.Tracks);

        await sheet.AddToPlaylistCommand.ExecuteAsync(rig.Shell.Library.Playlists.Single());
        var saved = Assert.Single(await rig.Persistence.LoadPlaylistsAsync());
        Assert.Equal(new[] { page.Songs[0].Id, page.Songs[1].Id, page.Songs[3].Id }, saved.TrackIds);
    }

    [Fact]
    public async Task OnAPlaylistsPage_RemoveFromPlaylist_TakesSeveralSongsAtOnce()
    {
        var a = MobileFixtures.Song("Alpha");
        var b = MobileFixtures.Song("Beta");
        var c = MobileFixtures.Song("Gamma");
        var mix = new Playlist { Name = "Night Drive", TrackIds = { a.Id, b.Id, c.Id } };
        using var rig = MobileFixtures.MakeRig(new[] { a, b, c }, seed: p => p.SavePlaylistsAsync(new List<Playlist> { mix }));

        // Elsewhere there is nothing to remove from.
        var songsPage = OpenSongs(rig);
        songsPage.BeginSelectCommand.Execute(null);
        Assert.False(songsPage.Selection.OffersRemoveFromPlaylist);
        rig.Shell.NavigateBackCommand.Execute(null);

        rig.Shell.OpenPlaylistCommand.Execute(rig.Shell.Library.Playlists.Single());
        var page = (SongListPageViewModel)rig.Shell.CurrentPage!;
        page.BeginSelectCommand.Execute(null);
        Assert.True(page.Selection.OffersRemoveFromPlaylist);
        Assert.False(page.Selection.CanRemoveFromPlaylist);   // nothing ticked yet
        page.Selection.Toggle(a);
        page.Selection.Toggle(c);
        Assert.True(page.Selection.CanRemoveFromPlaylist);

        await page.Selection.RemoveFromPlaylistCommand.ExecuteAsync(null);
        var saved = Assert.Single(await rig.Persistence.LoadPlaylistsAsync());
        Assert.Equal(new[] { b.Id }, saved.TrackIds);
        Assert.Equal(new[] { b }, page.Songs);
        Assert.False(page.Selection.IsActive);
    }

    [Fact]
    public void SmartPlaylist_OffersNoRemove()
    {
        var a = MobileFixtures.Song("Alpha");
        var smart = new Playlist { Name = "Smart", TrackIds = { a.Id }, IsSmartPlaylist = true };
        using var rig = MobileFixtures.MakeRig(new[] { a }, seed: p => p.SavePlaylistsAsync(new List<Playlist> { smart }));
        rig.Shell.OpenPlaylistCommand.Execute(rig.Shell.Library.Playlists.Single());
        var page = (SongListPageViewModel)rig.Shell.CurrentPage!;
        page.BeginSelectCommand.Execute(null);
        page.Selection.Toggle(a);
        Assert.False(page.Selection.OffersRemoveFromPlaylist);
        Assert.False(page.Selection.CanRemoveFromPlaylist);
    }

    [Fact]
    public void Download_IsOfferedOnlyWhileSignedInToAComputer()
    {
        var (rig, _) = SongsRig(2);
        using var _r = rig;
        var page = OpenSongs(rig);
        page.BeginSelectCommand.Execute(null);
        page.Selection.Toggle(page.Songs[0]);
        Assert.False(page.Selection.OffersDownload);   // no account service at all
        Assert.False(page.Selection.CanDownload);
    }

    [Fact]
    public void AlbumPage_MenuSelect_TicksAndTheRowsToggle()
    {
        var a = MobileFixtures.Song("A");
        var b = MobileFixtures.Song("B");
        var c = MobileFixtures.Song("C");
        var album = MobileFixtures.MakeAlbum("Album", "X", a, b, c);
        using var rig = MobileFixtures.MakeRig(new[] { a, b, c }, new[] { album });
        rig.Shell.OpenAlbumCommand.Execute(album);
        var page = (AlbumPageViewModel)rig.Shell.CurrentPage!;

        // The album's ⋯ offers Select (nothing ticked yet) …
        page.MoreCommand.Execute(null);
        Assert.True(rig.Shell.Sheet!.CanSelect);
        rig.Shell.Sheet.SelectCommand.Execute(null);
        Assert.True(page.Selection.IsActive);
        Assert.Equal(0, page.Selection.Count);
        page.Selection.EndCommand.Execute(null);

        // … and a song's menu › Select starts with that song.
        page.OpenSongMenuCommand.Execute(b);
        rig.Shell.Sheet!.SelectCommand.Execute(null);
        Assert.Equal(new[] { b }, page.Selection.SelectedTracks);

        page.PlayTrackCommand.Execute(page.Tracks[2]);   // a row tap ticks
        Assert.Null(rig.Shell.Player.CurrentTrack);
        Assert.Equal(new[] { b, c }, page.Selection.SelectedTracks);

        page.Selection.AddToQueueCommand.Execute(null);   // nothing playing: they play, in album order
        Assert.Same(b, rig.Shell.Player.CurrentTrack);
    }

    [Fact]
    public void ATick_NotifiesOnlyThatSong_SelectAllNotifiesEveryRow()
    {
        var (rig, _) = SongsRig(3);
        using var _r = rig;
        var page = OpenSongs(rig);
        var seen = new List<Guid?>();
        page.Selection.Changed += id => seen.Add(id);

        page.BeginSelectCommand.Execute(null);
        page.Selection.Toggle(page.Songs[1]);
        page.Selection.ToggleAllCommand.Execute(null);
        Assert.Equal(new Guid?[] { null, page.Songs[1].Id, null }, seen);
    }

    // ── The views ──

    [AvaloniaFact]
    public void Views_ChecksShowOnTheRows_TheBarsSwap_AndTheActionBarStandsAboveTheTabs()
    {
        var (rig, _) = SongsRig(6);
        using var _r = rig;
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        var page = OpenSongs(rig);
        MobileFixtures.Land(window);
        var pageView = MobileFixtures.PageView(view, page);

        var checks = pageView.GetVisualDescendants().OfType<SelectCheck>().ToList();
        Assert.NotEmpty(checks);
        Assert.All(checks, c => Assert.Equal(0, c.Bounds.Width));   // no room taken outside Select mode
        Assert.False(MobileFixtures.Named<SelectionHeader>(pageView, "SelectBar").IsVisible);

        page.BeginSelectCommand.Execute(null);
        page.Selection.Toggle(page.Songs[0]);
        window.UpdateLayout();

        checks = pageView.GetVisualDescendants().OfType<SelectCheck>().ToList();
        Assert.All(checks, c => Assert.True(c.Bounds.Width > 0));
        Assert.Single(checks, c => c.IsChecked);
        Assert.True(MobileFixtures.Named<SelectionHeader>(pageView, "SelectBar").IsEffectivelyVisible);
        Assert.Equal("1 Selected", MobileFixtures.Named<TextBlock>(pageView, "SelectionCount").Text);
        Assert.All(pageView.GetVisualDescendants().OfType<SongRow>(), r => Assert.False(r.IsMenuVisible));

        var bar = view.FindControl<Control>("SelectionBar")!;
        Assert.True(bar.IsEffectivelyVisible);
        Assert.False(MobileFixtures.Named<Button>(view, "SelectRemoveFromPlaylist").IsVisible);   // not a playlist
        var tabs = view.FindControl<Control>("TabCapsule")!;
        Assert.True(bar.Bounds.Bottom <= tabs.Bounds.Top + 0.5);

        page.Selection.EndCommand.Execute(null);
        window.UpdateLayout();
        Assert.False(bar.IsVisible);
        Assert.All(pageView.GetVisualDescendants().OfType<SelectCheck>(), c => Assert.False(c.IsChecked));
        window.Close();
    }
}
