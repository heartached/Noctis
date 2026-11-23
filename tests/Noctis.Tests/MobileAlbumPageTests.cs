using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using Noctis.Mobile.ViewModels;
using Noctis.Mobile.Views;
using Noctis.Models;
using Xunit;

namespace Noctis.Tests;

/// <summary>The phone album page: header, meta line, rows, transport and the cover tint.</summary>
public class MobileAlbumPageTests : IDisposable
{
    private readonly List<string> _files = new();
    public void Dispose() { foreach (var f in _files) try { File.Delete(f); } catch { } }

    /// <summary>PageTint only needs the cover path to exist; the fake extractor supplies the colour.</summary>
    private string FakeCover()
    {
        var path = Path.Combine(Path.GetTempPath(), $"noctis-cover-{Guid.NewGuid():N}.jpg");
        File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
        _files.Add(path);
        return path;
    }

    private static (Album Album, Track[] Tracks) Sample(string? artworkPath = null)
    {
        var one = MobileFixtures.Song("Opening");
        one.TrackNumber = 1;
        var two = MobileFixtures.Song("Loud One");
        two.TrackNumber = 2;
        two.IsExplicit = true;
        var album = MobileFixtures.MakeAlbum("Night Drive", "The Band", one, two);
        album.Genre = "Rock";
        album.Year = 2020;
        album.ArtworkPath = artworkPath;
        return (album, new[] { one, two });
    }

    [AvaloniaFact]
    public void AlbumPage_ShowsHeaderMetaAndNumberedRows_WithTheExplicitBadge()
    {
        var (album, tracks) = Sample();
        using var rig = MobileFixtures.MakeRig(tracks, new[] { album });
        var window = MobileFixtures.Mount(rig.Shell, out var view);

        rig.Shell.OpenAlbumCommand.Execute(album);
        window.UpdateLayout();

        var page = MobileFixtures.Find<AlbumPage>(view);
        Assert.Equal("Night Drive", MobileFixtures.Named<TextBlock>(page, "AlbumTitle").Text);
        Assert.Equal("Rock · 2020", MobileFixtures.Named<TextBlock>(page, "MetaLine").Text);
        var rows = MobileFixtures.Named<ItemsControl>(page, "TrackList");
        Assert.Equal(2, rows.GetRealizedContainers().Count());
        var second = rows.ContainerFromIndex(1)!;
        Assert.Contains(second.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "2");
        Assert.True(second.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("explicit-badge")).IsEffectivelyVisible);
        Assert.False(rows.ContainerFromIndex(0)!.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("explicit-badge")).IsEffectivelyVisible);
        Assert.Equal("2 songs, 3 minutes", MobileFixtures.Named<TextBlock>(page, "FooterSongs").Text);
        window.Close();
    }

    /// <summary>A box set must not realise all its rows at page open: TrackList virtualises
    /// even though it sits under the header inside the page's own ScrollViewer.</summary>
    [AvaloniaFact]
    public void LongAlbum_RealizesOnlyAScreenfulOfRows()
    {
        var tracks = Enumerable.Range(1, 300).Select(i =>
        {
            var t = MobileFixtures.Song($"Track {i:D3}");
            t.TrackNumber = i;
            return t;
        }).ToArray();
        var album = MobileFixtures.MakeAlbum("Box Set", "The Band", tracks);
        using var rig = MobileFixtures.MakeRig(tracks, new[] { album });
        var window = MobileFixtures.Mount(rig.Shell, out var view);

        rig.Shell.OpenAlbumCommand.Execute(album);
        window.UpdateLayout();

        var rows = MobileFixtures.Named<ItemsControl>(MobileFixtures.Find<AlbumPage>(view), "TrackList");
        Assert.InRange(rows.GetRealizedContainers().Count(), 5, 40);
        window.Close();
    }

    [Fact]
    public void Play_Shuffle_AndARowTap_QueueTheAlbum()
    {
        var (album, tracks) = Sample();
        using var rig = MobileFixtures.MakeRig(tracks, new[] { album });
        rig.Shell.OpenAlbumCommand.Execute(album);
        var page = Assert.IsType<AlbumPageViewModel>(rig.Shell.CurrentPage);

        page.PlayTrackCommand.Execute(page.Tracks[1]);
        Assert.Same(tracks[1], rig.Shell.Player.CurrentTrack);

        page.PlayCommand.Execute(null);
        Assert.Same(tracks[0], rig.Shell.Player.CurrentTrack);
        Assert.Equal(new[] { tracks[1] }, rig.Shell.Player.UpNext);

        page.ShuffleCommand.Execute(null);
        Assert.True(rig.Shell.Player.IsShuffleEnabled);
    }

    /// <summary>Discord (Halil, "deluxe track linking"): a deluxe edition linked to its original
    /// (Link Tracks From, on the computer) lists the original's songs at their places with its own
    /// bonus track after them, as the desktop album page does; Play, the heart and the album menu
    /// act on that same list. The original album keeps its own songs.</summary>
    [Fact]
    public async Task LinkedAlbum_ListsPlaysAndFavouritesTheLinkedSongs_InAlbumOrder()
    {
        var (original, tracks) = Sample();
        var bonus = MobileFixtures.Song("Bonus");
        bonus.TrackNumber = 3;
        var deluxe = MobileFixtures.MakeAlbum("Night Drive (Deluxe)", "The Band", bonus);
        Noctis.Services.AlbumLinks.Attach(new Dictionary<Guid, Album> { [original.Id] = original, [deluxe.Id] = deluxe },
            new[] { new AlbumLink { AlbumId = deluxe.Id, SourceAlbumId = original.Id } });
        var all = new[] { tracks[0], tracks[1], bonus };
        using var rig = MobileFixtures.MakeRig(all, new[] { original, deluxe });

        rig.Shell.OpenAlbumCommand.Execute(deluxe);
        var page = Assert.IsType<AlbumPageViewModel>(rig.Shell.CurrentPage);

        Assert.Equal(all, page.Tracks.Select(r => r.Track));
        Assert.Equal(new[] { 1, 2, 3 }, page.Tracks.Select(r => r.Number));
        page.PlayCommand.Execute(null);
        Assert.Same(tracks[0], rig.Shell.Player.CurrentTrack);
        Assert.Equal(new[] { tracks[1], bonus }, rig.Shell.Player.UpNext);
        page.PlayTrackCommand.Execute(page.Tracks[2]);
        Assert.Same(bonus, rig.Shell.Player.CurrentTrack);

        Assert.Equal(all, ContextSheetViewModel.ForAlbum(rig.Shell, deluxe).Tracks);

        await page.ToggleFavouriteCommand.ExecuteAsync(null);
        Assert.True(page.IsFavourite);
        Assert.All(all, t => Assert.True(t.IsFavorite));

        Assert.Equal(new[] { tracks[0], tracks[1] }, original.EditionTracks);
    }

    /// <summary>Review Focus #3: no cover, genre "Unknown", year 0, no codec.</summary>
    [AvaloniaFact]
    public void AlbumWithoutArtwork_HasNoTint_AndACleanMetaLine()
    {
        var bare = MobileFixtures.Song("Untitled");
        var album = MobileFixtures.MakeAlbum("Demo", "Someone", bare);   // Genre defaults to "Unknown", Year 0
        using var rig = MobileFixtures.MakeRig(new[] { bare }, new[] { album },
            tint: () => new PageTint(_ => Colors.Red, work => Task.FromResult(work())));   // would tint, if asked
        var window = MobileFixtures.Mount(rig.Shell, out var view);

        rig.Shell.OpenAlbumCommand.Execute(album);
        window.UpdateLayout();

        var vm = Assert.IsType<AlbumPageViewModel>(rig.Shell.CurrentPage);
        Assert.Equal(string.Empty, vm.MetaLine);
        Assert.False(vm.HasMetaLine);
        Assert.False(vm.Tint.HasTint);
        var page = MobileFixtures.Find<AlbumPage>(view);
        Assert.Null(page.Background);
        Assert.False(page.Classes.Contains("tinted"));
        Assert.True(MobileFixtures.Named<PathIcon>(page, "ArtPlaceholder").IsEffectivelyVisible);
        Assert.False(MobileFixtures.Named<TextBlock>(page, "MetaLine").IsVisible);
        window.Close();
    }

    /// <summary>A light cover no longer gives a flat light page with black text (the washed-out
    /// beige page): the page is a deep shade of the cover's colour, Apple's brown page under the
    /// beige Silk Sonic cover, with white text; the cover's own colour is kept for the fade.</summary>
    [AvaloniaFact]
    public void LightCover_DeepensIntoADarkPage_WithWhiteText()
    {
        var beige = Color.FromRgb(240, 236, 228);
        var (album, tracks) = Sample(FakeCover());
        using var rig = MobileFixtures.MakeRig(tracks, new[] { album },
            tint: () => new PageTint(_ => beige, work => Task.FromResult(work())));
        var window = MobileFixtures.Mount(rig.Shell, out var view);

        rig.Shell.OpenAlbumCommand.Execute(album);
        window.UpdateLayout();

        var page = MobileFixtures.Find<AlbumPage>(view);
        var vm = Assert.IsType<AlbumPageViewModel>(rig.Shell.CurrentPage);
        var deep = Noctis.Services.DominantColorExtractor.DeepPageColor(beige);
        Assert.True(page.Classes.Contains("tinted"));
        Assert.False(page.Classes.Contains("light"));
        Assert.Equal(deep, ((ISolidColorBrush)page.Background!).Color);
        Assert.Equal(beige, vm.Tint.CoverColor);
        var title = MobileFixtures.Named<TextBlock>(page, "AlbumTitle");
        Assert.Equal(Colors.White, ((ISolidColorBrush)title.Foreground!).Color);
        Assert.True(PageTint.ContrastRatio(Colors.White, deep) >= 7, $"white on {deep}");
        window.Close();
    }

    /// <summary>A mid grey cover: the page is its deep shade, and both the title and the secondary
    /// text clear WCAG AA (4.5:1) on it, the secondary text dimmer than the title.</summary>
    [AvaloniaFact]
    public void MidGreyCover_PageTextClearsWcagAA()
    {
        var mid = Color.FromRgb(0x96, 0x96, 0x96);
        var (album, tracks) = Sample(FakeCover());
        using var rig = MobileFixtures.MakeRig(tracks, new[] { album },
            tint: () => new PageTint(_ => mid, work => Task.FromResult(work())));
        var window = MobileFixtures.Mount(rig.Shell, out var view);

        rig.Shell.OpenAlbumCommand.Execute(album);
        window.UpdateLayout();

        var page = MobileFixtures.Find<AlbumPage>(view);
        var pageColour = ((ISolidColorBrush)page.Background!).Color;
        Assert.Equal(Noctis.Services.DominantColorExtractor.DeepPageColor(mid), pageColour);
        Assert.False(page.Classes.Contains("light"));
        var title = ((ISolidColorBrush)MobileFixtures.Named<TextBlock>(page, "AlbumTitle").Foreground!).Color;
        Assert.Equal(Colors.White, title);
        Assert.True(PageTint.ContrastRatio(title, pageColour) >= 4.5);
        var meta = ((ISolidColorBrush)MobileFixtures.Named<TextBlock>(page, "MetaLine").Foreground!).Color;
        Assert.NotEqual(title, meta);
        Assert.True(PageTint.ContrastRatio(meta, pageColour) >= 4.5, $"meta {meta} on {pageColour}");
        window.Close();
    }

    [AvaloniaFact]
    public async Task StaleTint_NeverPaintsTheNextAlbum()
    {
        var pending = new Queue<TaskCompletionSource<Color?>>();
        var tint = new PageTint(_ => null, _ =>
        {
            var tcs = new TaskCompletionSource<Color?>();
            pending.Enqueue(tcs);
            return tcs.Task;
        });

        tint.Load(FakeCover());          // album A: slow
        tint.Load(FakeCover());          // album B: the page moved on
        var first = pending.Dequeue();
        var second = pending.Dequeue();
        second.SetResult(Colors.Red);
        first.SetResult(Colors.Blue);    // A's colour lands last
        await tint.Ready;

        Assert.Equal(Colors.Red, ((ISolidColorBrush)tint.Background!).Color);
    }

    [Fact]
    public void AlbumPage_FollowsALibraryRefresh_ForTheSameAlbum()
    {
        var (album, tracks) = Sample();
        using var rig = MobileFixtures.MakeRig(tracks, new[] { album });
        rig.Shell.OpenAlbumCommand.Execute(album);
        var page = Assert.IsType<AlbumPageViewModel>(rig.Shell.CurrentPage);

        // A rescan rebuilds Album instances with the same id.
        var extra = MobileFixtures.Song("Bonus");
        extra.TrackNumber = 3;
        var rebuilt = MobileFixtures.MakeAlbum("Night Drive", "The Band", tracks[0], tracks[1], extra);
        rebuilt.Id = album.Id;
        ((List<Album>)rig.Library.Albums).Clear();
        ((List<Album>)rig.Library.Albums).Add(rebuilt);
        rig.Library.RaiseLibraryUpdated();

        Assert.Same(rebuilt, page.Album);
        Assert.Equal(3, page.Tracks.Count);
    }

    [Fact]
    public void OpenAlbum_FromTheGrid_PushesTheAlbumPage_AndTheArtistLinkOpensTheArtist()
    {
        var (album, tracks) = Sample();
        using var rig = MobileFixtures.MakeRig(tracks, new[] { album });
        rig.Shell.OpenAlbumsCommand.Execute(null);
        rig.Shell.OpenAlbumCommand.Execute(album);
        var page = Assert.IsType<AlbumPageViewModel>(rig.Shell.CurrentPage);
        Assert.Equal("Night Drive", page.Title);

        page.OpenArtistCommand.Execute(null);
        Assert.Equal("The Band", rig.Shell.CurrentPage!.Title);
        Assert.Equal(3, rig.Shell.Pages.Count);
    }
}
