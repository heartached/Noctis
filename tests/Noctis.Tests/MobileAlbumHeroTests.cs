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
using Noctis.Controls;
using Noctis.Mobile.Services;
using Noctis.Mobile.ViewModels;
using Noctis.Mobile.Views;
using Noctis.Models;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The Apple-Music-style album page (2026-10-01): full-bleed cover under the status bar fading
/// into the cover's bottom colour, glass back/… buttons, artist avatar link, the desktop's
/// quality badge, shuffle / Play / heart, playing-row bars, disc headers.
/// </summary>
public class MobileAlbumHeroTests : IDisposable
{
    private readonly List<string> _files = new();
    public void Dispose() { foreach (var f in _files) try { File.Delete(f); } catch { } }

    private string FakeCover()
    {
        var path = Path.Combine(Path.GetTempPath(), $"noctis-hero-cover-{Guid.NewGuid():N}.jpg");
        File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
        _files.Add(path);
        return path;
    }

    private static (Album Album, Track[] Tracks) Sample(string? artworkPath = null, int discs = 1)
    {
        var tracks = Enumerable.Range(1, 4).Select(i =>
        {
            var t = MobileFixtures.Song($"Song {i}");
            t.TrackNumber = discs > 1 ? (i - 1) % 2 + 1 : i;
            t.DiscNumber = discs > 1 ? (i - 1) / 2 + 1 : 1;
            return t;
        }).ToArray();
        var album = MobileFixtures.MakeAlbum("Astro", "Travis", tracks);
        album.Genre = "Hip-Hop/Rap";
        album.Year = 2018;
        album.ArtworkPath = artworkPath;
        return (album, tracks);
    }

    private static Func<PageTint> Tint(Color color) => () => new PageTint(_ => color, work => Task.FromResult(work()));

    // ---- View model -----------------------------------------------------------------------

    [Fact]
    public void MetaLine_IsGenreAndYear_AndTheQualityBadgeComesFromCore()
    {
        var (album, tracks) = Sample();
        foreach (var t in tracks)
        {
            t.Codec = "FLAC";
            t.BitsPerSample = 24;
            t.SampleRate = 96000;
        }
        using var rig = MobileFixtures.MakeRig(tracks, new[] { album });
        rig.Shell.OpenAlbumCommand.Execute(album);
        var page = Assert.IsType<AlbumPageViewModel>(rig.Shell.CurrentPage);

        Assert.Equal("Hip-Hop/Rap · 2018", page.MetaLine);
        Assert.Equal("Hi-Res Lossless", page.QualityBadge);
        Assert.Equal(album.AudioQualityBadge, page.QualityBadge);
        Assert.Equal(album.AudioQualityDetailedInfo, page.QualityDetail);
        Assert.True(page.HasQualityBadge);
    }

    [Fact]
    public async Task Heart_FavouritesTheWholeAlbum_ThenUnfavouritesIt()
    {
        var (album, tracks) = Sample();
        tracks[0].IsFavorite = true;
        using var rig = MobileFixtures.MakeRig(tracks, new[] { album });
        rig.Shell.OpenAlbumCommand.Execute(album);
        var page = Assert.IsType<AlbumPageViewModel>(rig.Shell.CurrentPage);
        Assert.False(page.IsFavourite);   // one of four

        await page.ToggleFavouriteCommand.ExecuteAsync(null);
        Assert.True(page.IsFavourite);
        Assert.All(tracks, t => Assert.True(t.IsFavorite));

        await page.ToggleFavouriteCommand.ExecuteAsync(null);
        Assert.False(page.IsFavourite);
        Assert.All(tracks, t => Assert.False(t.IsFavorite));
    }

    [Fact]
    public void DiscHeaders_OnlyWhenThereIsMoreThanOneDisc()
    {
        var (single, singleTracks) = Sample();
        var (box, boxTracks) = Sample(discs: 2);
        box.Id = Guid.NewGuid();
        using var rig = MobileFixtures.MakeRig(singleTracks.Concat(boxTracks).ToArray(), new[] { single, box });

        rig.Shell.OpenAlbumCommand.Execute(single);
        var one = Assert.IsType<AlbumPageViewModel>(rig.Shell.CurrentPage);
        Assert.DoesNotContain(one.Rows, r => r is AlbumDiscHeader);

        rig.Shell.OpenAlbumCommand.Execute(box);
        var two = Assert.IsType<AlbumPageViewModel>(rig.Shell.CurrentPage);
        Assert.Equal(new object[] { "Disc 1", 1, 2, "Disc 2", 1, 2 },
            two.Rows.Select(r => r is AlbumDiscHeader h ? (object)h.Label : ((AlbumTrackRow)r).Number));
    }

    [Fact]
    public void PlayingRow_IsMarked_AndAnimatesOnlyWhilePlayingAndInView()
    {
        var (album, tracks) = Sample();
        using var rig = MobileFixtures.MakeRig(tracks, new[] { album });
        rig.Shell.OpenAlbumCommand.Execute(album);
        var page = Assert.IsType<AlbumPageViewModel>(rig.Shell.CurrentPage);

        page.PlayTrackCommand.Execute(page.Tracks[2]);
        Assert.Equal(new[] { false, false, true, false }, page.Tracks.Select(r => r.IsCurrent));
        Assert.True(page.Tracks[2].IsAnimating);
        Assert.False(page.Tracks[2].ShowsNumber);

        rig.Shell.Player.TogglePlayPauseCommand.Execute(null);   // pause
        Assert.True(page.Tracks[2].IsCurrent);
        Assert.False(page.Tracks[2].IsAnimating);

        rig.Shell.Player.TogglePlayPauseCommand.Execute(null);   // play
        Assert.True(page.Tracks[2].IsAnimating);
        rig.Shell.IsNowPlayingOpen = true;                         // the page is covered
        Assert.False(page.Tracks[2].IsAnimating);
        rig.Shell.IsNowPlayingOpen = false;
        Assert.True(page.Tracks[2].IsAnimating);
    }

    [Fact]
    public void ArtistAvatar_IsTheArtistsCover_LikeTheArtistsList()
    {
        var (album, tracks) = Sample("C:/covers/astro.jpg");
        using var rig = MobileFixtures.MakeRig(tracks, new[] { album });
        rig.Shell.OpenAlbumCommand.Execute(album);
        var page = Assert.IsType<AlbumPageViewModel>(rig.Shell.CurrentPage);
        Assert.Equal("C:/covers/astro.jpg", page.ArtistArtworkPath);
        Assert.True(page.HasArtistArtwork);
    }

    // ---- View -----------------------------------------------------------------------------

    /// <summary>ShellView pads the tab content by the status bar; the album page takes the
    /// strip back so the cover starts at the very top, and keeps its buttons below the bar.</summary>
    [AvaloniaFact]
    public void Cover_RunsUnderTheStatusBar_ButtonsStayBelowIt()
    {
        var (album, tracks) = Sample();
        using var rig = MobileFixtures.MakeRig(tracks, new[] { album });
        rig.Shell.SafeArea = new Thickness(0, 40, 0, 0);
        var window = MobileFixtures.Mount(rig.Shell, out var view);

        rig.Shell.OpenAlbumCommand.Execute(album);
        window.UpdateLayout();

        var page = MobileFixtures.Find<AlbumPage>(view);
        var hero = MobileFixtures.Named<Panel>(page, "Hero");
        Assert.Equal(0, hero.TranslatePoint(new Point(0, 0), view)!.Value.Y, 1);
        Assert.Equal(412, hero.Bounds.Width, 1);
        Assert.Equal(hero.Bounds.Width, hero.Bounds.Height, 1);   // square
        var back = MobileFixtures.Named<Button>(page, "BackButton");
        Assert.InRange(back.TranslatePoint(new Point(0, 0), view)!.Value.Y, 40, 60);
        Assert.IsType<GlassPanel>(back.GetVisualParent());
        window.Close();
    }

    /// <summary>Avalonia 12 clips every TemplatedControl by default, so the shell's page host
    /// cut the cover's status-bar strip off on the device. The page lifts that clip only while
    /// it is shown.</summary>
    [AvaloniaFact]
    public void PageHostClip_IsLiftedWhileTheAlbumShows_AndRestoredAfter()
    {
        var (album, tracks) = Sample();
        using var rig = MobileFixtures.MakeRig(tracks, new[] { album });
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        var host = MobileFixtures.Named<ContentControl>(view, "PageHost");
        Assert.True(host.ClipToBounds);

        rig.Shell.OpenAlbumsCommand.Execute(null);
        rig.Shell.OpenAlbumCommand.Execute(album);
        window.UpdateLayout();
        Assert.False(host.ClipToBounds);

        rig.Shell.NavigateBackCommand.Execute(null);
        window.UpdateLayout();
        Assert.True(host.ClipToBounds);
        window.Close();
    }

    [AvaloniaFact]
    public void BackAndMore_NavigateBack_AndOpenTheAlbumSheet()
    {
        var (album, tracks) = Sample();
        using var rig = MobileFixtures.MakeRig(tracks, new[] { album });
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        rig.Shell.OpenAlbumsCommand.Execute(null);
        rig.Shell.OpenAlbumCommand.Execute(album);
        window.UpdateLayout();
        var page = MobileFixtures.Find<AlbumPage>(view);

        MobileFixtures.Named<Button>(page, "MoreButton").Command!.Execute(null);
        Assert.True(rig.Shell.IsSheetOpen);
        rig.Shell.CloseSheet();

        var back = MobileFixtures.Named<Button>(page, "BackButton");
        back.Command!.Execute(back.CommandParameter);
        Assert.IsNotType<AlbumPageViewModel>(rig.Shell.CurrentPage);
        window.Close();
    }

    [AvaloniaFact]
    public void TitleBlock_ShowsTheAvatarLink_MetaLine_AndTheQualityBadge()
    {
        var (album, tracks) = Sample(FakeCover());
        foreach (var t in tracks) t.Codec = "FLAC";
        using var rig = MobileFixtures.MakeRig(tracks, new[] { album }, tint: Tint(Color.FromRgb(0xC8, 0xA4, 0x78)));
        var window = MobileFixtures.Mount(rig.Shell, out var view);

        rig.Shell.OpenAlbumCommand.Execute(album);
        window.UpdateLayout();

        var page = MobileFixtures.Find<AlbumPage>(view);
        Assert.True(MobileFixtures.Named<Border>(page, "ArtistAvatar").IsEffectivelyVisible);
        Assert.Equal("Travis", MobileFixtures.Named<TextBlock>(page, "ArtistName").Text);
        Assert.Equal("Hip-Hop/Rap · 2018", MobileFixtures.Named<TextBlock>(page, "MetaLine").Text);
        Assert.True(MobileFixtures.Named<Button>(page, "QualityBadge").IsEffectivelyVisible);
        Assert.Equal("Lossless", MobileFixtures.Named<TextBlock>(page, "QualityBadgeText").Text);
        Assert.Contains(MobileFixtures.Named<StackPanel>(page, "MetaRow").GetVisualDescendants().OfType<TextBlock>(),
            t => t.Text == " · " && t.IsEffectivelyVisible);
        window.Close();
    }

    /// <summary>Play is a dark pill with light text on a light tint, a light pill on a dark tint.</summary>
    [AvaloniaTheory]
    [InlineData(0xF0, 0xE8, 0xD8, true)]
    [InlineData(0x1C, 0x24, 0x4A, false)]
    public void PlayPill_ContrastsWithTheTint(byte r, byte g, byte b, bool darkPill)
    {
        var (album, tracks) = Sample(FakeCover());
        using var rig = MobileFixtures.MakeRig(tracks, new[] { album }, tint: Tint(Color.FromRgb(r, g, b)));
        var window = MobileFixtures.Mount(rig.Shell, out var view);

        rig.Shell.OpenAlbumCommand.Execute(album);
        window.UpdateLayout();

        var play = MobileFixtures.Named<Button>(MobileFixtures.Find<AlbumPage>(view), "PlayButton");
        Assert.Equal(darkPill ? Colors.Black : Colors.White, ((ISolidColorBrush)play.Background!).Color);
        Assert.Equal(darkPill ? Colors.White : Colors.Black, ((ISolidColorBrush)play.Foreground!).Color);
        window.Close();
    }

    [AvaloniaFact]
    public void PlayingRow_ShowsBouncingBars_InPlaceOfItsNumber()
    {
        var (album, tracks) = Sample();
        using var rig = MobileFixtures.MakeRig(tracks, new[] { album });
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        rig.Shell.OpenAlbumCommand.Execute(album);
        var vm = Assert.IsType<AlbumPageViewModel>(rig.Shell.CurrentPage);
        vm.PlayTrackCommand.Execute(vm.Tracks[1]);
        window.UpdateLayout();

        var list = MobileFixtures.Named<ItemsControl>(MobileFixtures.Find<AlbumPage>(view), "TrackList");
        Panel Bars(int i) => list.ContainerFromIndex(i)!.GetVisualDescendants().OfType<Panel>().First(p => p.Name == "EqBars");
        TextBlock Number(int i) => list.ContainerFromIndex(i)!.GetVisualDescendants().OfType<TextBlock>().First(t => t.Name == "RowNumber");
        Assert.True(Bars(1).IsEffectivelyVisible);
        Assert.True(Bars(1).Classes.Contains("playing"));
        Assert.False(Number(1).IsEffectivelyVisible);
        Assert.False(Bars(0).IsEffectivelyVisible);
        Assert.True(Number(0).IsEffectivelyVisible);

        rig.Shell.Player.TogglePlayPauseCommand.Execute(null);
        Assert.False(Bars(1).Classes.Contains("playing"));
        Assert.True(Bars(1).IsEffectivelyVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void Heart_FillsWhenEveryTrackIsAFavourite()
    {
        var (album, tracks) = Sample();
        foreach (var t in tracks) t.IsFavorite = true;
        using var rig = MobileFixtures.MakeRig(tracks, new[] { album });
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        rig.Shell.OpenAlbumCommand.Execute(album);
        window.UpdateLayout();

        var page = MobileFixtures.Find<AlbumPage>(view);
        Assert.True(MobileFixtures.Named<PathIcon>(page, "HeartFilled").IsEffectivelyVisible);
        Assert.False(MobileFixtures.Named<PathIcon>(page, "HeartOutline").IsEffectivelyVisible);
        window.Close();
    }

    /// <summary>The page colour fades in behind the status bar only once the cover has scrolled
    /// away; over the cover the clock sits on the art.</summary>
    [AvaloniaFact]
    public void TopScrim_FadesInOnceTheCoverHasScrolledAway()
    {
        var tracks = Enumerable.Range(1, 40).Select(i => { var t = MobileFixtures.Song($"T{i}"); t.TrackNumber = i; return t; }).ToArray();
        var album = MobileFixtures.MakeAlbum("Long", "Band", tracks);
        album.ArtworkPath = FakeCover();
        using var rig = MobileFixtures.MakeRig(tracks, new[] { album }, tint: Tint(Color.FromRgb(0x30, 0x40, 0x60)));
        rig.Shell.SafeArea = new Thickness(0, 40, 0, 0);
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        rig.Shell.OpenAlbumCommand.Execute(album);
        window.UpdateLayout();

        var page = MobileFixtures.Find<AlbumPage>(view);
        var scrim = MobileFixtures.Named<Border>(page, "TopScrim");
        Assert.Equal(0, scrim.Opacity);
        Assert.NotNull(scrim.Background);
        Assert.NotNull(MobileFixtures.Named<Border>(page, "HeroFade").Background);

        MobileFixtures.Named<ScrollViewer>(page, "AlbumScroll").Offset = new Vector(0, 600);
        window.UpdateLayout();
        Assert.Equal(1, scrim.Opacity);
        window.Close();
    }

    // ---- Status bar -----------------------------------------------------------------------

    private sealed class RecordingTheme : IThemeHost
    {
        public List<bool?> Icons { get; } = new();
        public void ApplyTheme(string appearance, string darkTheme, string accentHex) { }
        public void SetStatusBarIcons(bool? dark) => Icons.Add(dark);
    }

    /// <summary>Under the bar at rest: the cover's top rows. Scrolled into the fade, the page
    /// colour takes over; with the scrim fully in, it is the page colour alone.</summary>
    [Fact]
    public void StatusBarBackdrop_FollowsTheCoverRows_ThenThePageColour()
    {
        var rows = Enumerable.Repeat(0.9, 32).Concat(Enumerable.Repeat(0.02, 32)).ToArray();   // white top, black bottom
        var tint = Color.FromRgb(0x10, 0x10, 0x10);
        var page = Noctis.Services.DominantColorExtractor.GetRelativeLuminance(tint);

        Assert.Equal(0.9, AlbumPage.StatusBarBackdropLuminance(rows, 400, 0.45, 0, 40, tint, 0), 3);
        Assert.Equal(page, AlbumPage.StatusBarBackdropLuminance(rows, 400, 0.45, 600, 40, tint, 0), 3);   // past the cover
        Assert.Equal(page, AlbumPage.StatusBarBackdropLuminance(rows, 400, 0.45, 0, 40, tint, 1), 3);     // scrim in
        Assert.Equal(page, AlbumPage.StatusBarBackdropLuminance(null, 400, 0.45, 0, 40, tint, 0), 3);     // rows unknown
        Assert.True(PageTint.PrefersDarkText(AlbumPage.StatusBarBackdropLuminance(rows, 400, 0.45, 0, 40, tint, 0)));
    }

    /// <summary>The page asks for icons readable on its tint (light on a dark page), and hands
    /// the bar back to the theme under Now Playing and when it leaves.</summary>
    [AvaloniaFact]
    public void StatusBarIcons_FollowThePage_AndReturnToTheTheme()
    {
        var theme = new RecordingTheme();
        var (album, tracks) = Sample(FakeCover());
        using var rig = MobileFixtures.MakeRig(tracks, new[] { album }, tint: Tint(Color.FromRgb(0x14, 0x18, 0x30)), theme: theme);
        rig.Shell.SafeArea = new Thickness(0, 40, 0, 0);
        var window = MobileFixtures.Mount(rig.Shell, out _);
        rig.Shell.OpenAlbumsCommand.Execute(null);
        rig.Shell.OpenAlbumCommand.Execute(album);
        window.UpdateLayout();
        Assert.Equal(false, theme.Icons[^1]);    // light icons on the dark page

        rig.Shell.IsNowPlayingOpen = true;
        Assert.Null(theme.Icons[^1]);
        rig.Shell.IsNowPlayingOpen = false;
        Assert.Equal(false, theme.Icons[^1]);

        rig.Shell.NavigateBackCommand.Execute(null);
        window.UpdateLayout();
        Assert.Null(theme.Icons[^1]);
        window.Close();
    }
}
