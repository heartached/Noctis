using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Mobile.ViewModels;
using Noctis.Mobile.Views;
using Noctis.Models;
using Xunit;

namespace Noctis.Tests;

/// <summary>The Library tab root (tiles, first-launch and reconnect cards) and the list pages the tiles push.</summary>
public class MobileLibraryPagesTests
{
    private static Func<Noctis.Services.PersistenceService, Task> WithFolder(params Playlist[] playlists) => async p =>
    {
        var s = await p.LoadSettingsAsync();
        s.MusicFolders.Add("content://tree/music");
        await p.SaveSettingsAsync(s);
        if (playlists.Length > 0) await p.SavePlaylistsAsync(playlists.ToList());
    };

    private static string? TileCount(Visual view, string tile) =>
        MobileFixtures.Named<Button>(view, tile).GetVisualDescendants().OfType<TextBlock>()
            .First(t => t.Classes.Contains("tile-count")).Text;

    /// <summary>Review Focus #1: first launch — no folder, no tracks, nothing to show but the way in.</summary>
    [AvaloniaFact]
    public void EmptyLibrary_ShowsTheConnectCard_AndHidesTheTiles()
    {
        using var rig = MobileFixtures.MakeRig();
        var window = MobileFixtures.Mount(rig.Shell, out var view);

        Assert.True(MobileFixtures.Named<Border>(view, "ConnectCard").IsVisible);
        Assert.False(MobileFixtures.Named<Border>(view, "ReconnectCard").IsVisible);
        Assert.False(MobileFixtures.Named<Grid>(view, "Tiles").IsVisible);
        Assert.False(MobileFixtures.Named<StackPanel>(view, "FolderActions").IsVisible);
        Assert.False(rig.Shell.IsMiniBarVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void Tiles_ShowCounts_AndOpenTheirLists()
    {
        var a = MobileFixtures.Song("Alpha", favourite: true);
        var b = MobileFixtures.Song("Beta", daysAgo: 60);
        var c = MobileFixtures.Song("Gamma");
        var album = MobileFixtures.MakeAlbum("First", "Band", a, b);
        var mix = new Playlist { Name = "Mix", TrackIds = { c.Id, a.Id } };
        using var rig = MobileFixtures.MakeRig(new[] { a, b, c }, new[] { album }, WithFolder(mix));
        var window = MobileFixtures.Mount(rig.Shell, out var view);

        Assert.True(MobileFixtures.Named<Grid>(view, "Tiles").IsVisible);
        Assert.False(MobileFixtures.Named<Border>(view, "ConnectCard").IsVisible);
        Assert.Equal("3", TileCount(view, "SongsTile"));
        Assert.Equal("1", TileCount(view, "AlbumsTile"));
        Assert.Equal("1", TileCount(view, "FavouritesTile"));
        Assert.Equal("2", TileCount(view, "RecentlyAddedTile"));    // the 30-day window drops Beta
        Assert.Equal("1", TileCount(view, "PlaylistsTile"));

        rig.Shell.OpenFavouritesCommand.Execute(null);
        var favourites = Assert.IsType<SongListPageViewModel>(rig.Shell.CurrentPage);
        Assert.Equal(new[] { "Alpha" }, favourites.Songs.Select(t => t.Title));

        rig.Shell.NavigateBackCommand.Execute(null);
        rig.Shell.OpenPlaylistsCommand.Execute(null);
        var playlists = Assert.IsType<PlaylistListPageViewModel>(rig.Shell.CurrentPage);
        rig.Shell.OpenPlaylistCommand.Execute(playlists.Playlists.Single());
        var mixPage = Assert.IsType<SongListPageViewModel>(rig.Shell.CurrentPage);
        Assert.Equal(new[] { "Gamma", "Alpha" }, mixPage.Songs.Select(t => t.Title));   // saved order, not title order

        rig.Shell.SelectTabCommand.Execute(MobileTab.Library);
        rig.Shell.OpenAlbumsCommand.Execute(null);
        window.UpdateLayout();
        var grid = MobileFixtures.Find<AlbumGridPage>(view);
        Assert.Contains(grid.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "First");
        window.Close();
    }

    [Fact]
    public void SongList_TapPlaysFromThatRow_AndShuffleAllShuffles()
    {
        var songs = Enumerable.Range(0, 5).Select(i => MobileFixtures.Song($"S{i}")).ToArray();
        using var rig = MobileFixtures.MakeRig(songs);
        rig.Shell.OpenSongsCommand.Execute(null);
        var page = (SongListPageViewModel)rig.Shell.CurrentPage!;

        page.PlayCommand.Execute(page.Songs[2]);
        Assert.Same(page.Songs[2], rig.Shell.Player.CurrentTrack);
        Assert.Equal(new[] { "S3", "S4" }, rig.Shell.Player.UpNext.Select(t => t.Title));

        page.ShuffleAllCommand.Execute(null);
        Assert.True(rig.Shell.Player.IsShuffleEnabled);
        Assert.Equal(songs.Select(t => t.Title).OrderBy(x => x),
            rig.Shell.Player.UpNext.Append(rig.Shell.Player.CurrentTrack!).Select(t => t.Title).OrderBy(x => x));
    }

    /// <summary>Review Focus #2: a big library refreshes as one Reset (not 10,000 Adds, the
    /// desktop's flicker/stall shape) and the list realises about a screenful of rows.</summary>
    [AvaloniaFact]
    public void TenThousandSongs_RefreshIsOneReset_AndThePageRealizesAScreenful()
    {
        var songs = Enumerable.Range(0, 10_000).Select(i => MobileFixtures.Song($"Song {i:D5}")).ToArray();
        using var rig = MobileFixtures.MakeRig(songs);
        var resets = 0;
        var other = 0;
        rig.Shell.Library.Songs.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Reset) resets++; else other++;
        };

        rig.Library.RaiseLibraryUpdated();
        Assert.Equal(1, resets);
        Assert.Equal(0, other);

        var window = MobileFixtures.Mount(rig.Shell, out var view);
        rig.Shell.OpenSongsCommand.Execute(null);
        window.UpdateLayout();
        var list = MobileFixtures.Named<ItemsControl>(view, "SongList");
        Assert.InRange(list.GetRealizedContainers().Count(), 5, 200);
        window.Close();
    }

    [Fact]
    public void LibraryUpdated_RefreshesAnOpenList_AndAClosedPageStopsListening()
    {
        using var rig = MobileFixtures.MakeRig(new[] { MobileFixtures.Song("Alpha") });
        rig.Shell.OpenSongsCommand.Execute(null);
        var page = (SongListPageViewModel)rig.Shell.CurrentPage!;
        Assert.Single(page.Songs);

        rig.Library.TrackList.Add(MobileFixtures.Song("Beta"));
        rig.Library.RaiseLibraryUpdated();
        Assert.Equal(2, page.Songs.Count);

        rig.Shell.NavigateBackCommand.Execute(null);
        rig.Library.TrackList.Add(MobileFixtures.Song("Gamma"));
        rig.Library.RaiseLibraryUpdated();
        Assert.Equal(2, page.Songs.Count);
    }

    /// <summary>ScrollMemory through the real PageHost + DataTemplate (not an explicit
    /// DataContext): Songs, then an artist's list (the same page type, so the same template)
    /// and Back lands where the Songs list was left.</summary>
    [AvaloniaFact]
    public void SongList_KeepsItsScrollPlace_AcrossAPushAndBack()
    {
        var songs = Enumerable.Range(0, 500).Select(i => MobileFixtures.Song($"Song {i:D3}", artist: i % 2 == 0 ? "Even" : "Odd")).ToArray();
        using var rig = MobileFixtures.MakeRig(songs);
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        rig.Shell.OpenSongsCommand.Execute(null);
        var page = (SongListPageViewModel)rig.Shell.CurrentPage!;
        window.UpdateLayout();

        MobileFixtures.Named<ScrollViewer>(view, "SongScroll").Offset = new Vector(0, 3000);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(3000, page.ScrollOffset.Y, 1);

        rig.Shell.OpenArtistCommand.Execute("Even");   // 250 rows: long enough to inherit the offset
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, MobileFixtures.Named<ScrollViewer>(view, "SongScroll").Offset.Y, 1);   // a new page starts at the top

        rig.Shell.NavigateBackCommand.Execute(null);
        for (var i = 0; i < 3; i++) { window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }

        Assert.Same(page, rig.Shell.CurrentPage);
        Assert.Equal(3000, MobileFixtures.Named<ScrollViewer>(view, "SongScroll").Offset.Y, 1);
        window.Close();
    }

    [AvaloniaFact]
    public async Task ScanAborted_ShowsTheReconnectCard()
    {
        using var rig = MobileFixtures.MakeRig(new[] { MobileFixtures.Song("Alpha") }, seed: WithFolder());
        rig.Library.AbortScanWithRoots = new[] { "content://tree/music" };

        await rig.Shell.Library.RescanCommand.ExecuteAsync(null);
        var window = MobileFixtures.Mount(rig.Shell, out var view);

        Assert.True(rig.Shell.Library.NeedsReconnect);
        Assert.True(MobileFixtures.Named<Border>(view, "ReconnectCard").IsVisible);
        Assert.False(MobileFixtures.Named<TextBlock>(view, "StatusLine").IsVisible);   // the card carries the text
        window.Close();
    }
}
