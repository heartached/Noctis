using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using Noctis.Mobile.ViewModels;
using Noctis.Mobile.Views;
using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The phone artist page after Apple Music (iOS 26): the photo hero with the name and ☆ ▶ ⤨,
/// the card carousel (featured = the newest release, then the most played and the latest single
/// when they differ), Top Songs as a sideways-paged grid, the Albums / Singles &amp; EPs / Appears
/// On rails, the scroll-edge small title, and the album page's chrome.
/// </summary>
public class MobileArtistPageTests : IDisposable
{
    private readonly List<string> _files = new();

    public void Dispose()
    {
        foreach (var f in _files) try { File.Delete(f); } catch { }
    }

    private string FakeImage(string tag)
    {
        var path = Path.Combine(Path.GetTempPath(), $"noctis-artist-{tag}-{Guid.NewGuid():N}.jpg");
        File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
        _files.Add(path);
        return path;
    }

    private static Func<PageTint> Tint(Color color) => () => new PageTint(_ => color, work => Task.FromResult(work()));

    /// <summary>A release of <paramref name="count"/> songs (1–2 a single, 3–6 an EP, 7+ an album
    /// by Core's track-count rule), dated <paramref name="date"/> ("2025-09-12") or the year.</summary>
    private static (Album Album, Track[] Tracks) Release(string name, string artist, int count, int year, string? date = null, int plays = 0)
    {
        var tracks = Enumerable.Range(1, count).Select(i =>
        {
            var t = MobileFixtures.Song($"{name} {i}", artist: artist, plays: plays);
            t.TrackNumber = i;
            t.Year = year;
            t.ReleaseDate = date ?? string.Empty;
            return t;
        }).ToArray();
        var album = MobileFixtures.MakeAlbum(name, artist, tracks);
        album.Year = year;
        return (album, tracks);
    }

    private static (Track[] Tracks, Album[] Albums) Discography(params (Album Album, Track[] Tracks)[] releases) =>
        (releases.SelectMany(r => r.Tracks).ToArray(), releases.Select(r => r.Album).ToArray());

    // ---- Carousel ----------------------------------------------------------------------------

    [Fact]
    public void Cards_FeatureTheNewestRelease_ThenTheMostPlayedAndTheLatestSingle_OnlyWhenTheyDiffer()
    {
        var single = Release("Fresh", "Band", 1, 2025, "2025-09-12").Album;
        var album = Release("Big Record", "Band", 9, 2023, plays: 2).Album;
        var ep = Release("Old EP", "Band", 4, 2020, plays: 9).Album;

        // Newest is the single: it is Featured, so no separate Latest Single; the EP has the most plays.
        var cards = ArtistPageViewModel.BuildCards(new[] { single, album, ep });
        Assert.Equal(new[] { ArtistCardKind.Featured, ArtistCardKind.MostPlayed }, cards.Select(c => c.Kind));
        Assert.Same(single, cards[0].Album);
        Assert.Same(ep, cards[1].Album);
        Assert.Equal("FEATURED ALBUM · SEP 12, 2025", cards[0].Kicker);
        Assert.Equal("1 song", cards[0].SongsText);
        Assert.Equal("MOST PLAYED · 2020", cards[1].Kicker);
        Assert.Equal("4 songs", cards[1].SongsText);

        // The newest album is also the most played: one card for it, then the latest single (an EP here).
        var newAlbum = Release("New Album", "Band", 8, 2024, plays: 30).Album;
        var cards2 = ArtistPageViewModel.BuildCards(new[] { newAlbum, ep });
        Assert.Equal(new[] { ArtistCardKind.Featured, ArtistCardKind.LatestSingle }, cards2.Select(c => c.Kind));
        Assert.Same(ep, cards2[1].Album);
        Assert.Equal("LATEST EP · 2020", cards2[1].Kicker);

        // Nothing played and no single: the featured card alone; no releases, no cards.
        var quiet = Release("Quiet", "Band", 7, 2019).Album;
        Assert.Equal(new[] { ArtistCardKind.Featured }, ArtistPageViewModel.BuildCards(new[] { quiet, Release("Older", "Band", 10, 2010).Album }).Select(c => c.Kind));
        Assert.Empty(ArtistPageViewModel.BuildCards(Array.Empty<Album>()));
    }

    [AvaloniaFact]
    public void Carousel_ShowsTheCards_ATapOpensTheAlbum_AndItsPlayButtonPlaysIt()
    {
        var (tracks, albums) = Discography(Release("Fresh", "Band", 1, 2025, "2025-09-12"), Release("Big Record", "Band", 9, 2023, plays: 3));
        albums[1].Tracks[0].IsExplicit = true;
        using var rig = MobileFixtures.MakeRig(tracks, albums);
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        rig.Shell.OpenArtistCommand.Execute("Band");
        window.UpdateLayout();

        var page = MobileFixtures.Find<ArtistPage>(view);
        var cards = MobileFixtures.Named<ItemsControl>(page, "CardList");
        Assert.Equal(2, cards.GetRealizedContainers().Count());
        var titles = page.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Name == "CardTitle").Select(t => t.Text).ToList();
        Assert.Equal(new[] { "Fresh", "Big Record" }, titles);
        var badges = page.GetVisualDescendants().OfType<Border>().Where(b => b.Name == "CardExplicit").ToList();
        Assert.False(badges[0].IsVisible);
        Assert.True(badges[1].IsVisible);

        // Two cards: each is the page less the inset, the gap and the next card's peek.
        var scroll = MobileFixtures.Named<ScrollViewer>(page, "CardScroll");
        var card = (Control)cards.GetRealizedContainers().First();
        Assert.Equal(412 - ArtistPage.PageInset - ArtistPage.CardGap - ArtistPage.CardPeek, card.Bounds.Width, 1);
        Assert.True(scroll.Extent.Width > scroll.Viewport.Width);
        Assert.Equal(Avalonia.Controls.Primitives.SnapPointsType.MandatorySingle, scroll.HorizontalSnapPointsType);

        var vm = (ArtistPageViewModel)rig.Shell.CurrentPage!;
        vm.PlayCardCommand.Execute(vm.Cards[1]);
        Assert.Equal("Big Record 1", rig.Shell.Player.CurrentTrack!.Title);
        Assert.Equal("Big Record", rig.Shell.Player.SourceLabel);

        vm.OpenCardCommand.Execute(vm.Cards[0]);
        Assert.Same(albums[0], Assert.IsType<AlbumPageViewModel>(rig.Shell.CurrentPage).Album);
        window.Close();
    }

    // ---- Top Songs ----------------------------------------------------------------------------

    [AvaloniaFact]
    public void TopSongs_RankByPlays_InColumnsOfFour_AndARowPlaysTheListFromItsRank()
    {
        var songs = Enumerable.Range(1, 6).Select(i => MobileFixtures.Song($"Song {i}", artist: "Band", plays: 10 - i)).ToArray();
        var album = MobileFixtures.MakeAlbum("Record", "Band", songs);
        album.Year = 2021;
        foreach (var s in songs) s.Year = 2021;
        using var rig = MobileFixtures.MakeRig(songs, new[] { album });
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        rig.Shell.OpenArtistCommand.Execute("Band");
        window.UpdateLayout();

        var vm = (ArtistPageViewModel)rig.Shell.CurrentPage!;
        Assert.Equal(new[] { "Song 1", "Song 2", "Song 3", "Song 4", "Song 5", "Song 6" }, vm.TopSongs.Select(s => s.Track.Title));
        Assert.Equal(new[] { 4, 2 }, vm.TopSongPages.Select(p => p.Rows.Count));
        Assert.Equal(new[] { true, true, true, false, true, false }, vm.TopSongs.Select(s => s.ShowsRule));
        Assert.Equal("Record · 2021", vm.TopSongs[0].Subtitle);

        var page = MobileFixtures.Find<ArtistPage>(view);
        var pages = MobileFixtures.Named<ItemsControl>(page, "TopSongPages");
        var first = (Control)pages.GetRealizedContainers().First();
        Assert.Equal(412 - ArtistPage.PageInset - ArtistPage.ColumnGap - ArtistPage.ColumnPeek, first.Bounds.Width, 1);
        var grid = MobileFixtures.Named<ScrollViewer>(page, "TopSongScroll");
        Assert.True(grid.Extent.Width > grid.Viewport.Width);   // the next column peeks in

        vm.PlayTopSongCommand.Execute(vm.TopSongs[2]);
        Assert.Equal("Song 3", rig.Shell.Player.CurrentTrack!.Title);
        Assert.Equal(new[] { "Song 4", "Song 5", "Song 6" }, rig.Shell.Player.UpNext.Select(t => t.Title));
        Assert.Equal("Band", rig.Shell.Player.SourceLabel);
        window.UpdateLayout();

        // The playing song: Apple's bars over its darkened cover.
        Assert.True(vm.TopSongs[2].IsCurrent);
        var scrims = page.GetVisualDescendants().OfType<Border>().Where(b => b.Name == "PlayingScrim").ToList();
        Assert.Equal(new[] { false, false, true, false }, scrims.Take(4).Select(b => b.IsVisible));
        window.Close();
    }

    [Fact]
    public void TopSongsChevron_OpensEveryOneOfTheArtistsSongs_UnderTheArtistsName()
    {
        var songs = Enumerable.Range(1, 25).Select(i => MobileFixtures.Song($"Song {i:00}", artist: "Band", plays: i)).ToArray();
        using var rig = MobileFixtures.MakeRig(songs, new[] { MobileFixtures.MakeAlbum("Record", "Band", songs) });
        rig.Shell.OpenArtistCommand.Execute("Band");
        var vm = (ArtistPageViewModel)rig.Shell.CurrentPage!;
        Assert.Equal(ArtistPageViewModel.TopSongCount, vm.TopSongs.Count);

        vm.OpenAllSongsCommand.Execute(null);

        var list = Assert.IsType<SongListPageViewModel>(rig.Shell.CurrentPage);
        Assert.Equal("Band", list.Title);
        Assert.Equal(25, list.SongCount);
        Assert.Equal("Song 25", list.Songs[0].Title);            // most played first
        list.PlayCommand.Execute(list.Songs[1]);
        Assert.Equal("Band", rig.Shell.Player.SourceLabel);
    }

    // ---- Rails ----------------------------------------------------------------------------------

    [AvaloniaFact]
    public void Rails_SplitAlbumsFromSinglesAndEps_AndAppearsOnListsOthersAlbumsCreditingTheArtist()
    {
        var lp = Release("Long Player", "Band", 9, 2022);
        var single = Release("One Song", "Band", 1, 2024);
        var ep = Release("Four Songs", "Band", 4, 2023);
        var guest = MobileFixtures.Song("Duet", artist: "Host feat. Band");
        var hostSolo = MobileFixtures.Song("Solo", artist: "Host");
        var hostAlbum = MobileFixtures.MakeAlbum("Host Album", "Host", guest, hostSolo);
        hostAlbum.Year = 2021;
        var (tracks, albums) = Discography(lp, single, ep);
        using var rig = MobileFixtures.MakeRig(tracks.Concat(new[] { guest, hostSolo }).ToArray(), albums.Append(hostAlbum).ToArray());
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        rig.Shell.OpenArtistCommand.Execute("Band");
        window.UpdateLayout();

        var vm = (ArtistPageViewModel)rig.Shell.CurrentPage!;
        Assert.Equal(new[] { "Long Player" }, vm.Albums.Select(r => r.Album.Name));
        Assert.Equal("2022", vm.Albums[0].Subtitle);
        Assert.Equal(new[] { "One Song", "Four Songs" }, vm.Singles.Select(r => r.Album.Name));   // newest first
        Assert.Equal(new[] { "Host Album" }, vm.AppearsOn.Select(r => r.Album.Name));
        Assert.Equal("Host", vm.AppearsOn[0].Subtitle);
        Assert.DoesNotContain(vm.AppearsOn, r => r.Album.Artist == "Band");

        var page = MobileFixtures.Find<ArtistPage>(view);
        Assert.True(MobileFixtures.Named<StackPanel>(page, "AlbumsSection").IsVisible);
        Assert.True(MobileFixtures.Named<StackPanel>(page, "SinglesSection").IsVisible);
        Assert.True(MobileFixtures.Named<StackPanel>(page, "AppearsOnSection").IsVisible);

        vm.OpenSinglesCommand.Execute(null);
        var grid = Assert.IsType<RailGridPageViewModel>(rig.Shell.CurrentPage);
        Assert.Equal("Singles & EPs", grid.Title);
        Assert.Equal(new[] { "One Song", "Four Songs" }, grid.Items.Select(i => i.Title));
        window.Close();
    }

    // ---- Hero ----------------------------------------------------------------------------------

    [AvaloniaFact]
    public void Hero_IsThePhotoOnThePhone_ElseTheNewestCoverUntilTheLookupFindsOne()
    {
        var cover = FakeImage("cover");
        var photo = FakeImage("photo");
        var (tracks, albums) = Discography(Release("Record", "Band", 8, 2020));
        albums[0].ArtworkPath = cover;
        var photos = new MobileFixtures.FakeArtistPhotos();
        photos.Online["Band"] = photo;
        var tinted = new List<string?>();
        using var rig = MobileFixtures.MakeRig(tracks, albums, photos: photos,
            tint: () => new PageTint(p => { tinted.Add(p); return Colors.DarkRed; }, work => Task.FromResult(work())));
        var window = MobileFixtures.Mount(rig.Shell, out var view);

        rig.Shell.OpenArtistCommand.Execute("Band");
        window.UpdateLayout();
        var vm = (ArtistPageViewModel)rig.Shell.CurrentPage!;
        Assert.Equal(cover, vm.HeroArtworkPath);                 // the cover stands in while Deezer is asked
        Assert.False(vm.HasPhoto);
        Assert.Equal(new[] { "Band" }, photos.Asked);

        photos.Complete();
        Assert.Equal(photo, vm.HeroArtworkPath);
        Assert.True(vm.HasPhoto);
        Assert.Equal(new[] { cover, photo }, tinted);            // the page colour follows the photo
        window.Close();

        // Next time the photo is on the phone: shown at once, nothing asked.
        rig.Shell.OpenArtistCommand.Execute("Band");
        var again = (ArtistPageViewModel)rig.Shell.CurrentPage!;
        Assert.Equal(photo, again.HeroArtworkPath);
        Assert.Single(photos.Asked);
    }

    [Fact]
    public void ClosingThePage_CancelsItsPhotoLookup()
    {
        var (tracks, albums) = Discography(Release("Record", "Band", 8, 2020));
        var photos = new MobileFixtures.FakeArtistPhotos();
        using var rig = MobileFixtures.MakeRig(tracks, albums, photos: photos);
        rig.Shell.OpenArtistCommand.Execute("Band");
        rig.Shell.NavigateBackCommand.Execute(null);
        Assert.Equal(new[] { "Band" }, photos.Cancelled);
    }

    [AvaloniaFact]
    public void Hero_RunsUnderTheStatusBar_WithTheNameAndButtonsOnItsLowerPart()
    {
        var (tracks, albums) = Discography(Release("Record", "Band", 8, 2020));
        albums[0].ArtworkPath = FakeImage("cover");
        using var rig = MobileFixtures.MakeRig(tracks, albums, tint: Tint(Color.FromRgb(0xC8, 0x10, 0x2E)));
        rig.Shell.SafeArea = new Thickness(0, 40, 0, 0);
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        var host = MobileFixtures.Named<ContentControl>(view, "PageHost");
        rig.Shell.OpenArtistCommand.Execute("Band");
        window.UpdateLayout();

        var page = MobileFixtures.Find<ArtistPage>(view);
        var hero = MobileFixtures.Named<Panel>(page, "Hero");
        Assert.Equal(0, hero.TranslatePoint(new Point(0, 0), view)!.Value.Y, 1);
        Assert.Equal(hero.Bounds.Width, hero.Bounds.Height, 1);   // square, as the album cover
        Assert.False(host.ClipToBounds);
        var back = MobileFixtures.Named<Button>(page, "BackButton");
        Assert.InRange(back.TranslatePoint(new Point(0, 0), view)!.Value.Y, 40, 60);

        // The big name and the three buttons sit over the photo's faded lower part, centred.
        var name = MobileFixtures.Named<TextBlock>(page, "ArtistName");
        var buttons = new[] { "FavouriteButton", "PlayButton", "ShuffleButton" }.Select(n => MobileFixtures.Named<Button>(page, n)).ToList();
        var heroBottom = hero.Bounds.Height;
        var nameBox = BoundsIn(name, view);
        Assert.InRange(nameBox.Top, heroBottom * (1 - ArtistPage.HeroFadeShare), heroBottom);
        foreach (var b in buttons)
        {
            var box = BoundsIn(b, view);
            Assert.True(box.Bottom <= heroBottom, $"{b.Name} bottom {box.Bottom} > {heroBottom}");
            Assert.True(box.Top > nameBox.Bottom);
            Assert.Equal(54, box.Width, 1);
        }
        Assert.Equal(view.Bounds.Width / 2, BoundsIn(buttons[1], view).Center.X, 1);
        Assert.Equal(view.Bounds.Width / 2, nameBox.Center.X, 1);

        // The buttons wear a shade of the page colour: lighter than this red, white glyphs.
        var red = Color.FromRgb(0xC8, 0x10, 0x2E);
        var fill = ((ISolidColorBrush)buttons[1].Background!).Color;
        Assert.Equal(ArtistPage.Shade(red, ArtistPage.ButtonShade), fill);
        Assert.True(ArtistPage.Lightness(fill) > ArtistPage.Lightness(red));
        Assert.Equal(Colors.White, ((ISolidColorBrush)name.Foreground!).Color);
        Assert.Equal(Colors.White, ((ISolidColorBrush)buttons[1].Foreground!).Color);

        rig.Shell.NavigateBackCommand.Execute(null);
        window.UpdateLayout();
        Assert.True(host.ClipToBounds);
        window.Close();
    }

    [Fact]
    public void Shades_AreDeeperOnALightPage_AndLighterOnADarkOne()
    {
        var yellow = Color.FromRgb(0xF2, 0xB9, 0x2C);   // dark text: deeper buttons
        Assert.True(PageTint.PrefersDarkText(yellow));
        Assert.True(ArtistPage.Lightness(ArtistPage.Shade(yellow, -ArtistPage.ButtonShade)) < ArtistPage.Lightness(yellow));
        var navy = Color.FromRgb(0x1B, 0x24, 0x3A);
        Assert.True(ArtistPage.Lightness(ArtistPage.Shade(navy, ArtistPage.ButtonShade)) > ArtistPage.Lightness(navy));
        // Hue survives the shade.
        var shaded = ArtistPage.Shade(Color.FromRgb(0xC8, 0x10, 0x2E), 0.1);
        Assert.True(shaded.R > shaded.G && shaded.R > shaded.B);
    }

    // ---- Scroll ----------------------------------------------------------------------------------

    [AvaloniaFact]
    public void SmallTitle_FadesInAsTheBigNamePassesUnderTheButtons()
    {
        var songs = Enumerable.Range(1, 30).Select(i => MobileFixtures.Song($"Song {i}", artist: "Band")).ToArray();
        using var rig = MobileFixtures.MakeRig(songs, new[] { MobileFixtures.MakeAlbum("Record", "Band", songs) });
        rig.Shell.SafeArea = new Thickness(0, 40, 0, 0);
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        rig.Shell.OpenArtistCommand.Execute("Band");
        window.UpdateLayout();

        var page = MobileFixtures.Find<ArtistPage>(view);
        var big = MobileFixtures.Named<TextBlock>(page, "ArtistName");
        var small = MobileFixtures.Named<TextBlock>(page, "SmallTitle");
        var scroll = MobileFixtures.Named<ScrollViewer>(page, "ArtistScroll");
        Assert.Equal(1, big.Opacity);
        Assert.Equal(0, small.Opacity);
        Assert.Equal(0, MobileFixtures.Named<Border>(page, "TopScrim").Opacity);

        var barBottom = 40 + HeroChrome.TopBarHeight;
        scroll.Offset = new Vector(0, big.TranslatePoint(new Point(0, 0), view)!.Value.Y - barBottom + big.Bounds.Height + 4);
        window.UpdateLayout();
        Assert.Equal(0, big.Opacity, 2);
        Assert.Equal(1, small.Opacity, 2);
        Assert.Equal(1, MobileFixtures.Named<Border>(page, "TopScrim").Opacity, 2);
        var smallBox = BoundsIn(small, view);
        Assert.Equal(view.Bounds.Width / 2, smallBox.Center.X, 1);
        Assert.Equal("Band", small.Text);

        scroll.Offset = default;
        window.UpdateLayout();
        Assert.Equal(1, big.Opacity, 2);
        Assert.Equal(0, small.Opacity, 2);
        window.Close();
    }

    // ---- Buttons ----------------------------------------------------------------------------------

    [AvaloniaFact]
    public void Star_TogglesTheFavouriteArtist_InTheDesktopsFile()
    {
        var (tracks, albums) = Discography(Release("Record", "Band", 8, 2020));
        using var rig = MobileFixtures.MakeRig(tracks, albums);
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        rig.Shell.OpenArtistCommand.Execute("Band");
        window.UpdateLayout();
        var page = MobileFixtures.Find<ArtistPage>(view);
        var outline = MobileFixtures.Named<PathIcon>(page, "StarOutline");
        var filled = MobileFixtures.Named<PathIcon>(page, "StarFilled");
        Assert.True(outline.IsVisible);
        Assert.False(filled.IsVisible);

        MobileFixtures.Named<Button>(page, "FavouriteButton").Command!.Execute(null);
        window.UpdateLayout();

        Assert.False(outline.IsVisible);
        Assert.True(filled.IsVisible);
        var reloaded = new FavoriteArtistsService(Path.Combine(rig.Persistence.DataDirectory, "favorite_artists.json"));
        Assert.True(reloaded.IsFavorite("band"));

        MobileFixtures.Named<Button>(page, "FavouriteButton").Command!.Execute(null);
        Assert.False(new FavoriteArtistsService(Path.Combine(rig.Persistence.DataDirectory, "favorite_artists.json")).IsFavorite("Band"));
        window.Close();
    }

    [Fact]
    public void PlayAndShuffle_QueueTheArtistsSongs_NewestReleaseFirst()
    {
        var (tracks, albums) = Discography(Release("Old", "Band", 7, 2010), Release("New", "Band", 7, 2022));
        using var rig = MobileFixtures.MakeRig(tracks, albums);
        rig.Shell.OpenArtistCommand.Execute("Band");
        var vm = (ArtistPageViewModel)rig.Shell.CurrentPage!;

        vm.PlayCommand.Execute(null);
        Assert.Equal("New 1", rig.Shell.Player.CurrentTrack!.Title);
        Assert.Equal("Band", rig.Shell.Player.SourceLabel);
        Assert.Equal(13, rig.Shell.Player.UpNext.Count);

        vm.ShuffleCommand.Execute(null);
        Assert.True(rig.Shell.Player.IsShuffleEnabled);
    }

    [AvaloniaFact]
    public void More_OpensTheArtistSheet_HeadedByThePhoto()
    {
        var photo = FakeImage("photo");
        var (tracks, albums) = Discography(Release("Record", "Band", 8, 2020));
        var photos = new MobileFixtures.FakeArtistPhotos();
        photos.Cached["Band"] = photo;
        using var rig = MobileFixtures.MakeRig(tracks, albums, photos: photos);
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        rig.Shell.OpenArtistCommand.Execute("Band");
        window.UpdateLayout();

        MobileFixtures.Named<Button>(MobileFixtures.Find<ArtistPage>(view), "MoreButton").Command!.Execute(null);

        Assert.True(rig.Shell.IsSheetOpen);
        Assert.True(rig.Shell.Sheet!.IsArtist);
        Assert.Equal("Band", rig.Shell.Sheet.Title);
        Assert.Equal(photo, rig.Shell.Sheet.ArtworkPath);
        window.Close();
    }

    [AvaloniaFact]
    public void ArtistWithoutArtwork_ShowsThePlaceholderHero_NoCards_AndNoTint()
    {
        var lone = MobileFixtures.Song("Lone", artist: "Solo");
        using var rig = MobileFixtures.MakeRig(new[] { lone });           // no album at all
        var window = MobileFixtures.Mount(rig.Shell, out var view);

        rig.Shell.OpenArtistCommand.Execute("Solo");
        window.UpdateLayout();

        var vm = (ArtistPageViewModel)rig.Shell.CurrentPage!;
        Assert.Null(vm.HeroArtworkPath);
        Assert.False(vm.HasCards);
        Assert.False(vm.Tint.HasTint);
        Assert.Equal(new[] { "Lone" }, vm.TopSongs.Select(s => s.Track.Title));
        var page = MobileFixtures.Find<ArtistPage>(view);
        Assert.True(MobileFixtures.Named<PathIcon>(page, "HeroPlaceholder").IsEffectivelyVisible);
        Assert.False(MobileFixtures.Named<ScrollViewer>(page, "CardScroll").IsVisible);
        Assert.False(MobileFixtures.Named<StackPanel>(page, "AlbumsSection").IsVisible);
        window.Close();
    }

    [Fact]
    public void SongsBy_IncludesAFeatureOnAnotherAlbum_Once()
    {
        var own = MobileFixtures.Song("Own", artist: "Guest");
        var feature = MobileFixtures.Song("Duet", artist: "Guest");
        var host = MobileFixtures.MakeAlbum("Host Album", "Host", feature);
        var mine = MobileFixtures.MakeAlbum("Guest Album", "Guest", own);
        using var rig = MobileFixtures.MakeRig(new[] { own, feature }, new[] { host, mine });

        Assert.Equal(new[] { "Own", "Duet" }, MobileLibrary.SongsBy(rig.Library, "Guest").Select(t => t.Title));
        Assert.Equal(new[] { "Duet" }, MobileLibrary.SongsBy(rig.Library, "Host").Select(t => t.Title));
    }

    [Fact]
    public void TheAlbumPagesArtistLink_OpensTheArtistPage()
    {
        var (tracks, albums) = Discography(Release("Record", "Band", 8, 2020));
        using var rig = MobileFixtures.MakeRig(tracks, albums);
        rig.Shell.OpenAlbumCommand.Execute(albums[0]);
        ((AlbumPageViewModel)rig.Shell.CurrentPage!).OpenArtistCommand.Execute(null);

        var artist = Assert.IsType<ArtistPageViewModel>(rig.Shell.CurrentPage);
        Assert.Equal("Band", artist.Name);
    }

    private static Rect BoundsIn(Control control, Visual root)
    {
        var origin = control.TranslatePoint(new Point(0, 0), root)!.Value;
        return new Rect(origin, control.Bounds.Size);
    }
}
