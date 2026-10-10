using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Noctis.Mobile.ViewModels;
using Noctis.Mobile.Views;
using Noctis.Models;
using Xunit;

namespace Noctis.Tests;

/// <summary>The long-press sheet: its actions, how it is opened, and that Back closes it first.</summary>
public class MobileContextSheetTests
{
    /// <summary>HoldingRoutedEventArgs has no public constructor in Avalonia 12.1.2 (verified by
    /// metadata dump); this is the one the gesture recogniser calls.</summary>
    private static void RaiseHold(Control target)
    {
        var args = (HoldingRoutedEventArgs)Activator.CreateInstance(typeof(HoldingRoutedEventArgs),
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, binder: null,
            args: new object?[] { HoldingState.Started, new Point(10, 10), PointerType.Touch, null }, culture: null)!;
        args.RoutedEvent = InputElement.HoldingEvent;
        target.RaiseEvent(args);
    }

    private static Button FirstSongRow(ShellView view)
    {
        var list = MobileFixtures.Named<ItemsControl>(view, "SongList");
        return list.ContainerFromIndex(0)!.GetVisualDescendants().OfType<Button>().First();
    }

    /// <summary>Review Focus #5.</summary>
    [Fact]
    public void Back_ClosesTheSheetFirst_ThenTheOverlays_ThenThePages()
    {
        var t = MobileFixtures.Song("Alpha");
        using var rig = MobileFixtures.MakeRig(new[] { t });
        rig.Shell.OpenSongsCommand.Execute(null);
        rig.Shell.OpenTrackSheetCommand.Execute(t);

        Assert.True(rig.Shell.TryHandleBack());
        Assert.False(rig.Shell.IsSheetOpen);
        Assert.NotNull(rig.Shell.CurrentPage);                   // the page under the sheet stays

        rig.Shell.Player.PlayTracks(new[] { t }, 0);
        rig.Shell.OpenNowPlayingCommand.Execute(null);
        rig.Shell.OpenTrackSheetCommand.Execute(t);              // a sheet over Now Playing (B7's ⋯)
        Assert.True(rig.Shell.TryHandleBack());
        Assert.False(rig.Shell.IsSheetOpen);
        Assert.True(rig.Shell.IsNowPlayingOpen);
        Assert.True(rig.Shell.TryHandleBack());
        Assert.False(rig.Shell.IsNowPlayingOpen);
        Assert.True(rig.Shell.TryHandleBack());
        Assert.Null(rig.Shell.CurrentPage);
        Assert.False(rig.Shell.TryHandleBack());
    }

    [Fact]
    public void TrackSheet_PlayNext_And_AddToQueue_PlaceTheSong_AndClose()
    {
        var songs = Enumerable.Range(0, 4).Select(i => MobileFixtures.Song($"S{i}")).ToArray();
        var extra = MobileFixtures.Song("Extra");
        using var rig = MobileFixtures.MakeRig(songs.Append(extra).ToArray());
        rig.Shell.Player.PlayTracks(songs, 0);

        rig.Shell.OpenTrackSheetCommand.Execute(extra);
        rig.Shell.Sheet!.PlayNextCommand.Execute(null);
        Assert.False(rig.Shell.IsSheetOpen);
        Assert.Equal("Extra", rig.Shell.Player.UpNext[0].Title);

        rig.Shell.OpenTrackSheetCommand.Execute(songs[1]);
        rig.Shell.Sheet!.AddToQueueCommand.Execute(null);
        Assert.Equal("S1", rig.Shell.Player.UpNext[^1].Title);
    }

    [Fact]
    public void AlbumSheet_PlayNext_KeepsTheAlbumOrder_AndWithNothingPlayingItPlays()
    {
        var a = MobileFixtures.Song("A");
        var b = MobileFixtures.Song("B");
        var c = MobileFixtures.Song("C");
        var album = MobileFixtures.MakeAlbum("Album", "X", a, b);
        using var rig = MobileFixtures.MakeRig(new[] { a, b, c }, new[] { album });

        rig.Shell.OpenAlbumSheetCommand.Execute(album);
        rig.Shell.Sheet!.AddToQueueCommand.Execute(null);        // nothing loaded: just play it
        Assert.Same(a, rig.Shell.Player.CurrentTrack);

        rig.Shell.Player.PlayTracks(new[] { c }, 0);
        rig.Shell.OpenAlbumSheetCommand.Execute(album);
        rig.Shell.Sheet!.PlayNextCommand.Execute(null);
        Assert.Equal(new[] { "A", "B" }, rig.Shell.Player.UpNext.Select(t => t.Title));
    }

    [Fact]
    public async Task Favourite_TogglesEveryTrack_AndTheFavouritesCountFollows()
    {
        var a = MobileFixtures.Song("A");
        var b = MobileFixtures.Song("B", favourite: true);
        var album = MobileFixtures.MakeAlbum("Album", "X", a, b);
        using var rig = MobileFixtures.MakeRig(new[] { a, b }, new[] { album });

        rig.Shell.OpenAlbumSheetCommand.Execute(album);
        Assert.Equal("Favorite", rig.Shell.Sheet!.FavouriteLabel);   // not every track is a favourite yet
        await rig.Shell.Sheet.ToggleFavouriteCommand.ExecuteAsync(null);

        Assert.True(a.IsFavorite);
        Assert.True(b.IsFavorite);
        Assert.Equal(2, rig.Shell.Library.FavoriteCount);            // FavoritesChanged rebuilt the counts
        Assert.False(rig.Shell.IsSheetOpen);

        rig.Shell.OpenTrackSheetCommand.Execute(a);
        Assert.Equal("Remove from Favorites", rig.Shell.Sheet!.FavouriteLabel);
    }

    [Fact]
    public async Task AddToPlaylist_NewPlaylist_IsCreatedSavedAndCounted()
    {
        var a = MobileFixtures.Song("A");
        using var rig = MobileFixtures.MakeRig(new[] { a });

        rig.Shell.OpenTrackSheetCommand.Execute(a);
        rig.Shell.Sheet!.ShowPlaylistsCommand.Execute(null);
        Assert.True(rig.Shell.Sheet.IsPickingPlaylist);
        rig.Shell.Sheet.NewPlaylistName = "  Road trip ";
        await rig.Shell.Sheet.CreatePlaylistCommand.ExecuteAsync(null);

        Assert.False(rig.Shell.IsSheetOpen);
        Assert.Equal(1, rig.Shell.Library.PlaylistCount);
        var saved = Assert.Single(await rig.Persistence.LoadPlaylistsAsync());
        Assert.Equal("Road trip", saved.Name);
        Assert.Equal(new[] { a.Id }, saved.TrackIds);

        // Desktop parity (SidebarViewModel.AddTracksToPlaylist): a song already in the playlist is not added twice.
        rig.Shell.OpenTrackSheetCommand.Execute(a);
        await rig.Shell.Sheet!.AddToPlaylistCommand.ExecuteAsync(rig.Shell.Sheet.Playlists.Single());
        Assert.Equal(new[] { a.Id }, (await rig.Persistence.LoadPlaylistsAsync()).Single().TrackIds);
    }

    [Fact]
    public async Task AddToPlaylist_SkipsSongsAlreadyThere_AndKeepsTheNewOnesInOrder()
    {
        var a = MobileFixtures.Song("A");
        var b = MobileFixtures.Song("B");
        var album = MobileFixtures.MakeAlbum("Album", "X", a, b);
        using var rig = MobileFixtures.MakeRig(new[] { a, b }, new[] { album });
        var playlist = await rig.Shell.Library.CreatePlaylistAsync("Mix");
        await rig.Shell.Library.AddToPlaylistAsync(playlist, new[] { b });

        rig.Shell.OpenAlbumSheetCommand.Execute(album);
        await rig.Shell.Sheet!.AddToPlaylistCommand.ExecuteAsync(playlist);

        Assert.Equal(new[] { b.Id, a.Id }, (await rig.Persistence.LoadPlaylistsAsync()).Single().TrackIds);
    }

    /// <summary>A hold whose lift never reaches the row (the scroll recogniser captured the
    /// finger after it slid) must not swallow the next real tap.</summary>
    [AvaloniaFact]
    public void AHoldWithoutItsLift_DoesNotSwallowTheNextTap()
    {
        var a = MobileFixtures.Song("Alpha");
        using var rig = MobileFixtures.MakeRig(new[] { a });
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        rig.Shell.OpenSongsCommand.Execute(null);
        window.UpdateLayout();
        var row = FirstSongRow(view);
        var centre = row.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height / 2), window)!.Value;

        RaiseHold(row);                                              // no MouseUp reaches the row
        Assert.True(rig.Shell.IsSheetOpen);
        rig.Shell.CloseSheet();
        window.UpdateLayout();

        window.MouseDown(centre, MouseButton.Left, RawInputModifiers.None);
        window.MouseUp(centre, MouseButton.Left, RawInputModifiers.None);
        Assert.Same(a, rig.Shell.Player.CurrentTrack);
        window.Close();
    }

    [Fact]
    public async Task PinAlbum_PutsItOnThePinnedRail_AndPersists()
    {
        var a = MobileFixtures.Song("A");
        var album = MobileFixtures.MakeAlbum("Alpha", "X", a);
        using var rig = MobileFixtures.MakeRig(new[] { a }, new[] { album });

        rig.Shell.OpenAlbumSheetCommand.Execute(album);
        Assert.True(rig.Shell.Sheet!.CanPin);
        Assert.Equal("Pin to Library", rig.Shell.Sheet.PinLabel);
        await rig.Shell.Sheet.TogglePinCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "Alpha" }, rig.Shell.Library.PinnedRail.Select(r => r.Title));
        Assert.Contains(album.Id, (await rig.Persistence.LoadSettingsAsync()).PinnedAlbumIds);

        rig.Shell.OpenTrackSheetCommand.Execute(a);
        Assert.True(rig.Shell.Sheet!.CanPin);                        // songs pin too, after albums
        await rig.Shell.Sheet.TogglePinCommand.ExecuteAsync(null);
        Assert.Equal(new[] { "Alpha", "A" }, rig.Shell.Library.PinnedRail.Select(r => r.Title));
        Assert.Contains(a.Id, (await rig.Persistence.LoadSettingsAsync()).PinnedTrackIds);
    }

    [Fact]
    public async Task APinnedSong_PlaysThePinnedSongs_FromThePinnedLabel()
    {
        var a = MobileFixtures.Song("A");
        var b = MobileFixtures.Song("B");
        using var rig = MobileFixtures.MakeRig(new[] { a, b });
        await rig.Shell.Library.SetTrackPinnedAsync(a.Id, true);
        await rig.Shell.Library.SetTrackPinnedAsync(b.Id, true);

        rig.Shell.OpenRailItemCommand.Execute(rig.Shell.Library.PinnedRail.Last());

        Assert.Same(b, rig.Shell.Player.CurrentTrack);
        Assert.Equal("Pinned", rig.Shell.Player.SourceLabel);
    }

    [Fact]
    public async Task PinArtist_ShowsARoundTileOnThePinnedRail_AndOpensTheArtist()
    {
        var a = MobileFixtures.Song("A", artist: "Band");
        using var rig = MobileFixtures.MakeRig(new[] { a }, new[] { MobileFixtures.MakeAlbum("Alpha", "Band", a) });
        var artist = new Artist { Name = "Band" };
        rig.Library.ArtistList.Add(artist);

        rig.Shell.OpenArtistSheetCommand.Execute(new ArtistListItem(artist, null));
        var sheet = rig.Shell.Sheet!;
        Assert.True(sheet.CanPin);
        Assert.False(sheet.CanFavourite);                            // an artist sheet hearts nothing
        await sheet.TogglePinCommand.ExecuteAsync(null);

        var tile = Assert.Single(rig.Shell.Library.PinnedRail);
        Assert.True(tile.IsArtist);
        Assert.Contains("Band", (await rig.Persistence.LoadSettingsAsync()).PinnedArtistNames);
        rig.Shell.OpenRailItemCommand.Execute(tile);
        Assert.Equal("Band", rig.Shell.CurrentPage!.Title);

        rig.Shell.OpenRailItemSheetCommand.Execute(tile);
        Assert.Equal("Unpin", rig.Shell.Sheet!.PinLabel);
        await rig.Shell.Sheet.TogglePinCommand.ExecuteAsync(null);
        Assert.Empty(rig.Shell.Library.PinnedRail);
    }

    [Fact]
    public void GoToAlbum_And_GoToArtist_OpenPagesAndCloseTheSheet()
    {
        var a = MobileFixtures.Song("A", artist: "Band");
        var album = MobileFixtures.MakeAlbum("Alpha", "Band", a);
        using var rig = MobileFixtures.MakeRig(new[] { a }, new[] { album });

        rig.Shell.OpenTrackSheetCommand.Execute(a);
        rig.Shell.Sheet!.GoToAlbumCommand.Execute(null);
        Assert.False(rig.Shell.IsSheetOpen);
        Assert.Equal("Alpha", rig.Shell.CurrentPage!.Title);

        rig.Shell.OpenTrackSheetCommand.Execute(a);
        rig.Shell.Sheet!.GoToArtistCommand.Execute(null);
        Assert.Equal("Band", rig.Shell.CurrentPage!.Title);
    }

    [AvaloniaFact]
    public void LongPress_OnASongRow_OpensTheSheet_AndSwallowsTheTap_WhileAPlainTapStillPlays()
    {
        var a = MobileFixtures.Song("Alpha");
        using var rig = MobileFixtures.MakeRig(new[] { a });
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        rig.Shell.OpenSongsCommand.Execute(null);
        window.UpdateLayout();
        var row = FirstSongRow(view);
        var centre = row.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height / 2), window)!.Value;

        window.MouseDown(centre, MouseButton.Left, RawInputModifiers.None);
        RaiseHold(row);
        window.MouseUp(centre, MouseButton.Left, RawInputModifiers.None);

        Assert.True(rig.Shell.IsSheetOpen);
        Assert.Same(a, rig.Shell.Sheet!.Track);
        Assert.Null(rig.Shell.Player.CurrentTrack);                  // the lift after the hold is not a tap

        rig.Shell.CloseSheet();
        window.UpdateLayout();
        window.MouseDown(centre, MouseButton.Left, RawInputModifiers.None);
        window.MouseUp(centre, MouseButton.Left, RawInputModifiers.None);
        Assert.Same(a, rig.Shell.Player.CurrentTrack);
        window.Close();
    }

    [AvaloniaFact]
    public void OverflowButton_OpensTheTrackSheet_AndTheScrimClosesIt()
    {
        var a = MobileFixtures.Song("Alpha");
        using var rig = MobileFixtures.MakeRig(new[] { a });
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        rig.Shell.OpenSongsCommand.Execute(null);
        window.UpdateLayout();

        var more = FirstSongRow(view).GetVisualDescendants().OfType<Button>().First(b => b.Name == "MoreButton");
        more.Command!.Execute(more.CommandParameter);
        window.UpdateLayout();
        Assert.Same(a, rig.Shell.Sheet!.Track);
        Assert.True(view.FindControl<ContextSheet>("Sheet")!.IsVisible);
        Assert.True(MobileFixtures.Named<Button>(view, "PlayNextAction").IsEffectivelyVisible);
        Assert.True(MobileFixtures.Named<Button>(view, "PinAction").IsEffectivelyVisible);

        window.MouseDown(new Point(200, 40), MouseButton.Left, RawInputModifiers.None);   // above the card: the scrim
        Assert.False(rig.Shell.IsSheetOpen);
        window.Close();
    }
}
