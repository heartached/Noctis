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
    public void Chips_SwapTheLowerHalfForAnEmbeddedList_AndBack()
    {
        var a1 = MobileFixtures.Song("A1");
        using var rig = MobileFixtures.MakeRig(new[] { a1 }, new[] { MobileFixtures.MakeAlbum("Alpha", "X", a1) }, WithFolder());
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        Assert.True(MobileFixtures.Named<ScrollViewer>(view, "LibraryScroll").IsVisible);
        Assert.True(MobileFixtures.Named<Button>(view, "AllMusicChip").Classes.Contains("selected"));

        rig.Shell.SelectLibraryChipCommand.Execute(LibraryChip.Albums);
        window.UpdateLayout();

        var embedded = Assert.IsType<AlbumGridPageViewModel>(rig.Shell.LibraryChipPage);
        Assert.True(embedded.IsEmbedded);
        Assert.Null(rig.Shell.CurrentPage);                                // embedded, not pushed
        Assert.False(MobileFixtures.Named<ScrollViewer>(view, "LibraryScroll").IsVisible);
        Assert.True(MobileFixtures.Named<ContentControl>(view, "ChipHost").IsVisible);
        var grid = MobileFixtures.Find<AlbumGridPage>(view);
        Assert.DoesNotContain(grid.GetVisualDescendants().OfType<Button>(),
            b => b.Classes.Contains("icon") && b.IsEffectivelyVisible);     // no back chevron when embedded
        Assert.Contains(grid.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Alpha");

        rig.Shell.SelectLibraryChipCommand.Execute(LibraryChip.AllMusic);
        window.UpdateLayout();
        Assert.Null(rig.Shell.LibraryChipPage);
        Assert.True(MobileFixtures.Named<ScrollViewer>(view, "LibraryScroll").IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void ShelfToggle_SwitchesBetweenTheRailAndTheList()
    {
        var a1 = MobileFixtures.Song("A1");
        using var rig = MobileFixtures.MakeRig(new[] { a1 }, new[] { MobileFixtures.MakeAlbum("Alpha", "X", a1) }, WithFolder(),
            log: h => h.Seed(a1, Now.AddHours(-1)));
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        Assert.True(MobileFixtures.Named<ScrollViewer>(view, "ShelfGrid").IsVisible);
        Assert.False(MobileFixtures.Named<ItemsControl>(view, "ShelfList").IsVisible);

        rig.Shell.Library.ToggleShelfLayoutCommand.Execute(null);
        window.UpdateLayout();

        Assert.False(MobileFixtures.Named<ScrollViewer>(view, "ShelfGrid").IsVisible);
        Assert.True(MobileFixtures.Named<ItemsControl>(view, "ShelfList").IsVisible);
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
