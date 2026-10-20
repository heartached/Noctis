using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Noctis.Mobile.ViewModels;
using Noctis.Mobile.Views;
using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>The Library root's lower half: chips, the Shelf and the three rails, all from the
/// library and the persisted play log.</summary>
public class MobileLibraryRailsTests
{
    private static readonly DateTime Now = DateTime.UtcNow;

    private static Func<PersistenceService, Task> WithFolder(Action<AppSettings>? edit = null, params Playlist[] playlists) => async p =>
    {
        var s = await p.LoadSettingsAsync();
        s.MusicFolders.Add("content://tree/music");
        edit?.Invoke(s);
        await p.SaveSettingsAsync(s);
        if (playlists.Length > 0) await p.SavePlaylistsAsync(playlists.ToList());
    };

    [Fact]
    public void Shelf_ListsRecentlyPlayedAlbums_NewestFirst_FromThePlayLog()
    {
        var a1 = MobileFixtures.Song("A1");
        var b1 = MobileFixtures.Song("B1");
        var c1 = MobileFixtures.Song("C1");
        var albums = new[] { MobileFixtures.MakeAlbum("Alpha", "X", a1), MobileFixtures.MakeAlbum("Beta", "Y", b1), MobileFixtures.MakeAlbum("Gamma", "Z", c1) };
        using var rig = MobileFixtures.MakeRig(new[] { a1, b1, c1 }, albums, log: h =>
        {
            h.Seed(a1, Now.AddHours(-3));
            h.Seed(b1, Now.AddHours(-2));
            h.Seed(a1, Now.AddHours(-1));
        });

        Assert.True(rig.Shell.Library.HasShelf);
        Assert.Equal(new[] { "Alpha", "Beta" }, rig.Shell.Library.Shelf.Select(a => a.Name));
    }

    [Fact]
    public void PlayingATrack_PutsItsAlbumAtTheFrontOfTheShelf()
    {
        var a1 = MobileFixtures.Song("A1");
        var c1 = MobileFixtures.Song("C1");
        var albums = new[] { MobileFixtures.MakeAlbum("Alpha", "X", a1), MobileFixtures.MakeAlbum("Gamma", "Z", c1) };
        using var rig = MobileFixtures.MakeRig(new[] { a1, c1 }, albums, log: h => h.Seed(a1, Now.AddHours(-1)));

        rig.Shell.Player.PlayTracks(new[] { c1 }, 0);
        rig.Player.RaisePositionChanged(TimeSpan.FromSeconds(1));   // its audio started: now it is a play

        Assert.Equal(new[] { "Gamma", "Alpha" }, rig.Shell.Library.Shelf.Select(a => a.Name));
    }

    [Fact]
    public void RecentlyAddedRail_OrdersAlbumsByTheirNewestTrack()
    {
        var old = MobileFixtures.Song("Old", daysAgo: 10);
        var mid = MobileFixtures.Song("Mid", daysAgo: 5);
        var fresh = MobileFixtures.Song("Fresh", daysAgo: 1);
        var albums = new[]
        {
            MobileFixtures.MakeAlbum("Oldest", "X", old),
            MobileFixtures.MakeAlbum("Mixed", "Y", mid, MobileFixtures.Song("Older", daysAgo: 20)),
            MobileFixtures.MakeAlbum("Newest", "Z", fresh),
        };
        using var rig = MobileFixtures.MakeRig(albums.SelectMany(a => a.Tracks).ToArray(), albums);

        Assert.Equal(new[] { "Newest", "Mixed", "Oldest" }, rig.Shell.Library.RecentlyAddedRail.Select(r => r.Title));
        Assert.All(rig.Shell.Library.RecentlyAddedRail, r => Assert.Equal(RailItemKind.Album, r.Kind));
    }

    /// <summary>The rail's › opens what the rail shows: its albums' songs, newest album first and
    /// each in track order. It took the last 30 days only (a full rail over "No songs" once
    /// nothing was new) and listed an album backwards (its last file scanned is the newest).</summary>
    [Fact]
    public void RecentlyAddedPage_ListsTheRailsAlbums_InTrackOrder_HoweverOld()
    {
        var one = MobileFixtures.Song("One", daysAgo: 61); one.TrackNumber = 1;
        var two = MobileFixtures.Song("Two", daysAgo: 60); two.TrackNumber = 2;
        var solo = MobileFixtures.Song("Solo", daysAgo: 90); solo.TrackNumber = 1;
        var albums = new[] { MobileFixtures.MakeAlbum("Pair", "X", one, two), MobileFixtures.MakeAlbum("Single", "Y", solo) };
        using var rig = MobileFixtures.MakeRig(new[] { solo, two, one }, albums);
        Assert.Equal(new[] { "Pair", "Single" }, rig.Shell.Library.RecentlyAddedRail.Select(r => r.Title));

        rig.Shell.OpenRecentlyAddedCommand.Execute(null);

        var page = Assert.IsType<SongListPageViewModel>(rig.Shell.CurrentPage);
        Assert.Equal(new[] { "One", "Two", "Solo" }, page.Songs.Select(t => t.Title));
    }

    [Fact]
    public void OnRepeatRail_IsTheDesktopsHeavyRotation()
    {
        var hot = MobileFixtures.Song("Hot");
        var once = MobileFixtures.Song("Once");
        using var rig = MobileFixtures.MakeRig(new[] { hot, once }, log: h =>
        {
            h.Seed(hot, Now.AddDays(-1));
            h.Seed(hot, Now.AddDays(-2));
            h.Seed(hot, Now.AddDays(-3));
            h.Seed(once, Now.AddDays(-1));
        });

        var item = Assert.Single(rig.Shell.Library.OnRepeatRail);
        Assert.Equal(RailItemKind.Track, item.Kind);
        Assert.Same(hot, item.Payload);
        Assert.True(rig.Shell.Library.HasOnRepeat);
    }

    [Fact]
    public void PinnedRail_ShowsPinnedAlbums_ThenPinnedPlaylists()
    {
        var a1 = MobileFixtures.Song("A1");
        var alpha = MobileFixtures.MakeAlbum("Alpha", "X", a1);
        var road = new Playlist { Name = "Road", IsPinned = true, TrackIds = { a1.Id } };
        var loose = new Playlist { Name = "Loose" };
        using var rig = MobileFixtures.MakeRig(new[] { a1 }, new[] { alpha },
            WithFolder(s => s.PinnedAlbumIds.Add(alpha.Id), road, loose));

        Assert.Equal(new[] { "Alpha", "Road" }, rig.Shell.Library.PinnedRail.Select(r => r.Title));
        Assert.Equal(new[] { RailItemKind.Album, RailItemKind.Playlist }, rig.Shell.Library.PinnedRail.Select(r => r.Kind));
        Assert.True(rig.Shell.Library.IsAlbumPinned(alpha.Id));
    }

    [Fact]
    public void OpenRailItem_OnRepeatTrack_PlaysTheRailFromIt_AndAnAlbumOpensIt()
    {
        var hot = MobileFixtures.Song("Hot");
        var warm = MobileFixtures.Song("Warm");
        var album = MobileFixtures.MakeAlbum("Alpha", "X", hot, warm);
        using var rig = MobileFixtures.MakeRig(new[] { hot, warm }, new[] { album }, log: h =>
        {
            foreach (var d in new[] { 1, 2, 3 }) h.Seed(hot, Now.AddDays(-d));
            foreach (var d in new[] { 1, 2, 3, 4 }) h.Seed(warm, Now.AddDays(-d));
        });
        var rail = rig.Shell.Library.OnRepeatRail.ToList();
        Assert.Equal(new[] { "Warm", "Hot" }, rail.Select(r => r.Title));

        rig.Shell.OpenRailItemCommand.Execute(rail[1]);
        Assert.Same(hot, rig.Shell.Player.CurrentTrack);

        rig.Shell.OpenRailItemCommand.Execute(RailItem.ForAlbum(album));
        Assert.NotNull(rig.Shell.CurrentPage);
    }

    [AvaloniaFact]
    public void RecentlyPlayed_ShowsThePlayedAlbums_AndItsHeaderOpensTheGrid()
    {
        var a1 = MobileFixtures.Song("A1");
        using var rig = MobileFixtures.MakeRig(new[] { a1 }, new[] { MobileFixtures.MakeAlbum("Alpha", "X", a1) }, WithFolder(),
            log: h => h.Seed(a1, Now.AddHours(-1)));
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        Assert.True(MobileFixtures.Named<StackPanel>(view, "RecentlyPlayedSection").IsVisible);
        Assert.Equal(new[] { "Alpha" }, rig.Shell.Library.RecentlyPlayedRail.Select(r => r.Title));

        MobileFixtures.Named<Button>(view, "RecentlyPlayedHeader").Command!.Execute(null);
        window.UpdateLayout();

        var page = Assert.IsType<RailGridPageViewModel>(rig.Shell.CurrentPage);
        Assert.Equal("Recently Played", page.Title);
        Assert.Contains(MobileFixtures.Find<RailGridPage>(view).GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Alpha");
        window.Close();
    }

    [Fact]
    public async Task ConnectCard_StaysHiddenUntilSettingsHaveLoaded()
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
        try
        {
            var library = new LibraryViewModel(new FakeLibraryService(), new PersistenceService(root),
                new MobileFixtures.NoPicker(), marshal: a => a());

            // Before InitializeAsync nothing is known about folders: a user who has some must
            // not see the "Add your music" card flash on every launch.
            Assert.False(library.ShowConnectCard);

            await library.InitializeAsync();
            Assert.True(library.IsLoaded);
            Assert.True(library.ShowConnectCard);
        }
        finally
        {
            try { System.IO.Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private sealed class ThrowingSettingsPersistence : TestPersistenceService
    {
        public override Task<AppSettings> LoadSettingsAsync() => throw new InvalidOperationException("settings unreadable");
    }

    /// <summary>A throw during the startup load must not leave an empty library with no way in.</summary>
    [Fact]
    public async Task InitializeAsync_ThatThrows_StillMarksLoaded_SoTheConnectCardShows()
    {
        using var persistence = new ThrowingSettingsPersistence();
        var library = new LibraryViewModel(new FakeLibraryService(), persistence, new MobileFixtures.NoPicker(), marshal: a => a());

        await Assert.ThrowsAsync<InvalidOperationException>(library.InitializeAsync);

        Assert.True(library.IsLoaded);
        Assert.True(library.ShowConnectCard);
    }

    /// <summary>A rescan publishes rebuilt Album objects every 1.5 s. An unchanged Recently Added
    /// rail must not be reset each time (a Reset re-fades every tile: the rail went blank again and
    /// again on the phone), and a kept tile must still open the album the library holds now.</summary>
    [Fact]
    public void RecentlyAddedRail_IsNotRebuilt_WhenARefreshOnlyRebuildsTheAlbumObjects()
    {
        var a1 = MobileFixtures.Song("A1", daysAgo: 1);
        var b1 = MobileFixtures.Song("B1", daysAgo: 2);
        var alpha = MobileFixtures.MakeAlbum("Alpha", "X", a1);
        var beta = MobileFixtures.MakeAlbum("Beta", "Y", b1);
        using var rig = MobileFixtures.MakeRig(new[] { a1, b1 }, new[] { alpha, beta });
        var rail = rig.Shell.Library.RecentlyAddedRail;
        Assert.Equal(new[] { "Alpha", "Beta" }, rail.Select(r => r.Title));
        var resets = 0;
        rail.CollectionChanged += (_, _) => resets++;

        // The same albums, as new objects with the same ids (what RebuildIndexesAsync produces).
        var albums = (List<Album>)rig.Library.Albums;
        var alphaNow = new Album { Id = alpha.Id, Name = "Alpha", Artist = "X", Tracks = alpha.Tracks.ToList(), TrackCount = 1 };
        var betaNow = new Album { Id = beta.Id, Name = "Beta", Artist = "Y", Tracks = beta.Tracks.ToList(), TrackCount = 1 };
        albums.Clear();
        albums.AddRange(new[] { alphaNow, betaNow });
        rig.Library.RaiseLibraryUpdated();

        Assert.Equal(0, resets);
        rig.Shell.OpenRailItemCommand.Execute(rail[0]);
        Assert.Same(alphaNow, Assert.IsType<AlbumPageViewModel>(rig.Shell.CurrentPage).Album);

        // A real change still lands.
        albums.Remove(betaNow);
        rig.Library.RaiseLibraryUpdated();
        Assert.Equal(new[] { "Alpha" }, rail.Select(r => r.Title));
    }

    /// <summary>The Albums grid, same rule: rebuilt-but-equal albums keep the rows (a reset blanked
    /// every cover during a rescan), a tile still opens the current album, a real change lands.</summary>
    [Fact]
    public void AlbumGrid_KeepsItsRows_WhenARefreshOnlyRebuildsTheAlbumObjects()
    {
        var a1 = MobileFixtures.Song("A1");
        var b1 = MobileFixtures.Song("B1");
        var alpha = MobileFixtures.MakeAlbum("Alpha", "X", a1);
        var beta = MobileFixtures.MakeAlbum("Beta", "Y", b1);
        using var rig = MobileFixtures.MakeRig(new[] { a1, b1 }, new[] { alpha, beta });
        var grid = new AlbumGridPageViewModel(rig.Shell);
        var resets = 0;
        grid.Rows.CollectionChanged += (_, _) => resets++;

        var albums = (List<Album>)rig.Library.Albums;
        var alphaNow = new Album { Id = alpha.Id, Name = "Alpha", Artist = "X", Tracks = alpha.Tracks.ToList(), TrackCount = 1 };
        var betaNow = new Album { Id = beta.Id, Name = "Beta", Artist = "Y", Tracks = beta.Tracks.ToList(), TrackCount = 1 };
        albums.Clear();
        albums.AddRange(new[] { alphaNow, betaNow });
        rig.Library.RaiseLibraryUpdated();

        Assert.Equal(0, resets);
        rig.Shell.OpenAlbumCommand.Execute(grid.Rows[0].Albums[0]);
        Assert.Same(alphaNow, Assert.IsType<AlbumPageViewModel>(rig.Shell.CurrentPage).Album);

        betaNow.Name = "Beta (Deluxe)";
        rig.Library.RaiseLibraryUpdated();
        Assert.Equal(new[] { "Alpha", "Beta (Deluxe)" }, grid.Rows.SelectMany(r => r.Albums).Select(a => a.Name));
    }

    [Fact]
    public void BuildLastPlayed_KeepsTheNewestOfEachTrack()
    {
        var a = MobileFixtures.Song("A");
        var b = MobileFixtures.Song("B");
        var c = MobileFixtures.Song("C");

        Assert.Equal(new[] { a, b, c }, HomeRowsBuilder.BuildLastPlayed(new[] { a, b, a, c }, max: 6));
        Assert.Equal(new[] { a, b }, HomeRowsBuilder.BuildLastPlayed(new[] { a, b, a, c }, max: 2));
    }
}
