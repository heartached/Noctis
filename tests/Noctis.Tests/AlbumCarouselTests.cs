using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using Noctis.Controls;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.ViewModels;
using SkiaSharp;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #149: an iPod-style album carousel on the Albums page (all albums) and on an
/// artist's page (that artist's albums), switchable with the grid and persisted. Pins the
/// classic Cover Flow geometry, the browsing controls (keys, wheel, click side / centre,
/// drag snap), virtualization on a big library, keeping the centred album across reloads,
/// and the persisted view choices.
/// </summary>
public class AlbumCarouselTests
{
    private sealed class NoOpPlayHistoryService : Noctis.Services.IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    private static Album Alb(string name, int year = 2000, string? art = null) =>
        new() { Id = Guid.NewGuid(), Name = name, Artist = "Michael Jackson", Year = year, ArtworkPath = art, Tracks = new List<Track>() };

    private static List<Album> Many(int n) => Enumerable.Range(0, n).Select(i => Alb($"Album {i:D5}")).ToList();

    // ── Geometry ──────────────────────────────────────────────────────────────

    [Fact]
    public void Centre_IsFlatFullSizeAndOnTop()
    {
        var p = AlbumCarouselGeometry.At(0, 300, 6);
        Assert.Equal(0, p.X);
        Assert.Equal(1, p.Scale);
        Assert.Equal(0, p.AngleY);
        Assert.Equal(1, p.Opacity);
        Assert.True(AlbumCarouselGeometry.ZIndexAt(0) > AlbumCarouselGeometry.ZIndexAt(1));
        Assert.True(AlbumCarouselGeometry.ZIndexAt(1) > AlbumCarouselGeometry.ZIndexAt(2));
    }

    [Fact]
    public void SideCards_MirrorAndTurnToFaceTheCentre()
    {
        var l = AlbumCarouselGeometry.At(-1, 300, 6);
        var r = AlbumCarouselGeometry.At(1, 300, 6);
        Assert.Equal(-r.X, l.X, 6);
        Assert.Equal(-r.AngleY, l.AngleY, 6);
        // Same convention as the Cover Flow row: left cards positive (outer edge nearer).
        Assert.True(l.AngleY > 0);
        Assert.True(r.AngleY < 0);
        Assert.Equal(AlbumCarouselGeometry.SideAngle, l.AngleY, 6);
        Assert.Equal(AlbumCarouselGeometry.SideScale, r.Scale, 6);
    }

    [Fact]
    public void FirstSideCard_ClearsTheCentre_AndTheRestStackEdgeBehindEdge()
    {
        const double w = 300;
        // The first side card's far edge starts past the centred card's edge (no overlap)…
        var firstInnerEdge = AlbumCarouselGeometry.FirstSideX(w) - AlbumCarouselGeometry.SideHalfWidth(w, near: false);
        Assert.True(firstInnerEdge > w / 2, $"{firstInnerEdge} <= {w / 2}");
        // …while the stacked side cards overlap: each shows only a strip past its neighbour,
        // the classic iPod look (the now-playing row keeps them apart instead).
        Assert.True(AlbumCarouselGeometry.Spacing(w) < AlbumCarouselGeometry.SideHalfWidth(w, near: true));
        var x2 = AlbumCarouselGeometry.At(2, w, 6).X;
        var x3 = AlbumCarouselGeometry.At(3, w, 6).X;
        Assert.Equal(AlbumCarouselGeometry.Spacing(w), x3 - x2, 6);
    }

    [Fact]
    public void Glide_IsContinuousThroughTheCentre()
    {
        // Positions just either side of a slot give nearly the same pose: no jump mid-glide.
        foreach (var at in new[] { 0.0, 1.0, 2.0 })
        {
            var a = AlbumCarouselGeometry.At(at - 0.001, 300, 6);
            var b = AlbumCarouselGeometry.At(at + 0.001, 300, 6);
            Assert.True(Math.Abs(a.X - b.X) < 1, $"X jumps at {at}");
            Assert.True(Math.Abs(a.AngleY - b.AngleY) < 1 || at == 0, $"angle jumps at {at}");
        }
    }

    [Fact]
    public void BeyondTheShownSides_CardsFadeOut()
    {
        Assert.Equal(1, AlbumCarouselGeometry.At(4, 300, 4).Opacity);
        Assert.Equal(0.5, AlbumCarouselGeometry.At(4.5, 300, 4).Opacity, 6);
        Assert.Equal(0, AlbumCarouselGeometry.At(5, 300, 4).Opacity);
    }

    [Fact]
    public void VisibleSides_GrowsWithWidth_AndIsCapped()
    {
        var narrow = AlbumCarouselGeometry.VisibleSides(700, 260);
        var wide = AlbumCarouselGeometry.VisibleSides(2400, 260);
        Assert.True(narrow >= 1);
        Assert.True(wide > narrow);
        Assert.True(AlbumCarouselGeometry.VisibleSides(100000, 100) <= AlbumCarouselGeometry.MaxSideCards);
    }

    [Fact]
    public void Approach_ConvergesAndLandsExactly()
    {
        double p = 0;
        var frames = 0;
        while (p != 5 && frames < 200) { p = AlbumCarouselGeometry.Approach(p, 5, 1 / 60.0); frames++; }
        Assert.Equal(5, p);
        // Settles in well under a second at 60 fps.
        Assert.True(frames < 60, $"{frames} frames");
        // Monotonic: never overshoots.
        Assert.True(AlbumCarouselGeometry.Approach(4.9, 5, 0.5) <= 5);
    }

    [Fact]
    public void SnapTarget_RoundsCarriesFlickAndClamps()
    {
        Assert.Equal(3, AlbumCarouselGeometry.SnapTarget(3.3, 0, 10));
        Assert.Equal(4, AlbumCarouselGeometry.SnapTarget(3.3, 4, 10));
        Assert.Equal(0, AlbumCarouselGeometry.SnapTarget(-0.4, -20, 10));
        Assert.Equal(9, AlbumCarouselGeometry.SnapTarget(9.2, 50, 10));
        Assert.Equal(0, AlbumCarouselGeometry.SnapTarget(5, 0, 0));
    }

    [Theory]
    [InlineData("Cover", AlbumCarouselStyle.Cover)]
    [InlineData("jewelcase", AlbumCarouselStyle.JewelCase)]
    [InlineData("Vinyl", AlbumCarouselStyle.Vinyl)]
    [InlineData("Cassette", AlbumCarouselStyle.Cassette)]
    [InlineData("2", AlbumCarouselStyle.Cover)]
    [InlineData("", AlbumCarouselStyle.Cover)]
    [InlineData(null, AlbumCarouselStyle.Cover)]
    [InlineData("8track", AlbumCarouselStyle.Cover)]
    public void Style_ParsesByNameOnly(string? setting, AlbumCarouselStyle expected) =>
        Assert.Equal(expected, AlbumCarouselStyles.Parse(setting));

    // ── Control ───────────────────────────────────────────────────────────────

    private static (AlbumCarousel Carousel, Window Window) Show(IList<Album> albums, double width = 1200, double height = 560)
    {
        var carousel = new AlbumCarousel { Albums = albums };
        var window = new Window { Width = width, Height = height, Content = carousel };
        window.Show();
        window.UpdateLayout();
        return (carousel, window);
    }

    private static void Settle(AlbumCarousel c)
    {
        for (var i = 0; i < 400 && Math.Abs(c.Position - c.TargetIndex) > 0; i++) c.StepGlide(1 / 60.0);
    }

    private static void Key(AlbumCarousel c, Key key) =>
        c.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, Source = c });

    [AvaloniaFact]
    public void BigLibrary_OnlyBuildsCardsNearTheCentre()
    {
        var (c, w) = Show(Many(20000));
        Assert.InRange(c.RealizedCount, 3, 2 * AlbumCarouselGeometry.MaxSideCards + 3);
        Key(c, Avalonia.Input.Key.End);
        Settle(c);
        Assert.Equal(19999, c.TargetIndex);
        Assert.InRange(c.RealizedCount, 2, AlbumCarouselGeometry.MaxSideCards + 2);
        Assert.All(c.RealizedCards.Keys, i => Assert.True(i >= 19999 - AlbumCarouselGeometry.MaxSideCards - 2));
        w.Close();
    }

    [AvaloniaFact]
    public void Keys_BrowseOneAlbumAtATime_AndEnterOpensTheCentredAlbum()
    {
        var albums = Many(30);
        var (c, w) = Show(albums);
        Album? opened = null;
        c.OpenCommand = new RelayCommand<Album>(a => opened = a);

        Key(c, Avalonia.Input.Key.Right);
        Key(c, Avalonia.Input.Key.Right);
        Assert.Equal(2, c.TargetIndex);
        Assert.Same(albums[2], c.CenterAlbum);
        Settle(c);
        Assert.Equal("Album 00002", c.CaptionTitle);

        Key(c, Avalonia.Input.Key.Left);
        Settle(c);
        Assert.Equal(1, c.TargetIndex);

        Key(c, Avalonia.Input.Key.Home);
        Key(c, Avalonia.Input.Key.Left); // clamped at the first album
        Assert.Equal(0, c.TargetIndex);

        Key(c, Avalonia.Input.Key.Enter);
        Assert.Same(albums[0], opened);
        w.Close();
    }

    [AvaloniaFact]
    public void ClickingASideAlbum_CentresIt_ClickingTheCentre_OpensIt()
    {
        var albums = Many(12);
        var (c, w) = Show(albums);
        Album? opened = null;
        c.OpenCommand = new RelayCommand<Album>(a => opened = a);

        c.Click(c.RealizedCards[3]);
        Assert.Null(opened);
        Assert.Equal(3, c.TargetIndex);
        Settle(c);

        c.Click(c.RealizedCards[3]);
        Assert.Same(albums[3], opened);
        w.Close();
    }

    [AvaloniaFact]
    public void MouseWheel_StepsOneAlbumPerNotch()
    {
        var (c, w) = Show(Many(10));
        var centre = new Point(600, 200);
        w.MouseWheel(centre, new Vector(0, -1));
        Assert.Equal(1, c.TargetIndex);
        w.MouseWheel(centre, new Vector(0, -1));
        w.MouseWheel(centre, new Vector(0, 1));
        Assert.Equal(1, c.TargetIndex);
        w.Close();
    }

    [AvaloniaFact]
    public void Drag_MovesTheRowAndSnapsToAnAlbum()
    {
        var (c, w) = Show(Many(40));
        var start = new Point(600, 200);
        w.MouseDown(start, MouseButton.Left);
        for (var x = 600; x >= 300; x -= 30)
            w.MouseMove(new Point(x, 200));
        Thread.Sleep(120); // stop before letting go: no flick
        w.MouseMove(new Point(300, 200));
        Thread.Sleep(120);
        w.MouseUp(new Point(300, 200), MouseButton.Left);
        Assert.True(c.TargetIndex > 0, "dragging left moves on");
        Assert.Equal(Math.Round(c.Position, MidpointRounding.AwayFromZero), c.TargetIndex);
        w.Close();
    }

    [AvaloniaFact]
    public void Reload_KeepsTheCentredAlbum_AndCenterAlbumRestoresAPlace()
    {
        var albums = Many(50);
        var source = new System.Collections.ObjectModel.ObservableCollection<Album>(albums);
        var (c, w) = Show(source);
        c.MoveTo(20);
        Settle(c);

        // A re-sort / library refresh: same album lands elsewhere — the row follows it.
        source.Move(20, 5);
        Assert.Equal(5, c.TargetIndex);
        Assert.Same(albums[20], c.CenterAlbum);

        // The host hands back a remembered album (back from the album page).
        c.CenterAlbum = albums[40];
        Assert.Equal(source.IndexOf(albums[40]), c.TargetIndex);
        w.Close();
    }

    [AvaloniaFact]
    public void EveryStyle_SizesItsCard()
    {
        foreach (var style in Enum.GetValues<AlbumCarouselStyle>())
        {
            var card = new AlbumCarouselCard { CaseStyle = style, CoverSize = 200, Album = Alb("Bad") };
            card.Measure(Size.Infinity);
            var f = AlbumCarouselCard.SizeFactor(style);
            Assert.Equal(200 * f.Width, card.DesiredSize.Width, 1);
            Assert.Equal(200 * f.Height, card.DesiredSize.Height, 1);
            // The cover always sits inside the card.
            Assert.True(new Rect(card.CardSize).Contains(card.CoverRect.Deflate(0.5)), style.ToString());
        }
    }

    // ── View models ───────────────────────────────────────────────────────────

    private static (LibraryAlbumsViewModel Vm, SettingsViewModel Settings) AlbumsVm(params Album[] libraryAlbums)
    {
        var lib = new FakeLibraryService();
        ((List<Album>)lib.Albums).AddRange(libraryAlbums);
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var settings = new SettingsViewModel(persistence, lib, new NoOpPlayHistoryService());
        return (new LibraryAlbumsViewModel(lib, player, new SidebarViewModel(persistence, lib), settings), settings);
    }

    [AvaloniaFact]
    public void AlbumsPage_ViewAndStyle_PersistThroughSettings()
    {
        var (vm, settings) = AlbumsVm(Alb("Bad"), Alb("Thriller"), Alb("Invincible"));
        Assert.True(vm.IsGridView);

        vm.SetAlbumViewCommand.Execute("carousel");
        Assert.True(vm.IsCarouselView);
        Assert.Equal("carousel", settings.AlbumViewMode);

        vm.SetCarouselStyleCommand.Execute("Vinyl");
        Assert.Equal(AlbumCarouselStyle.Vinyl, vm.CarouselStyle);
        Assert.Equal("Vinyl", settings.AlbumCarouselStyle);

        // A pick made on the artist page (shared setting) is followed here.
        settings.AlbumCarouselStyle = "Cassette";
        Assert.Equal(AlbumCarouselStyle.Cassette, vm.CarouselStyle);

        vm.SetAlbumViewCommand.Execute("bogus");
        Assert.True(vm.IsGridView);
        Assert.Equal("grid", settings.AlbumViewMode);
    }

    [AvaloniaFact]
    public void AlbumsPage_CarouselBrowsesTheGridsAlbumsInGridOrder()
    {
        var (vm, _) = AlbumsVm(Alb("Bad", 1987), Alb("Thriller", 1982), Alb("Invincible", 2001));
        vm.Refresh();
        for (var i = 0; i < 200 && vm.FilteredAlbumRows.Count == 0; i++)
        {
            Thread.Sleep(10);
            Dispatcher.UIThread.RunJobs();
        }
        var grid = vm.FilteredAlbumRows.OfType<AlbumRow>().SelectMany(r => r.Albums).ToList();
        Assert.Equal(3, grid.Count);
        Assert.Equal(grid, vm.CarouselAlbums);
    }

    [AvaloniaFact]
    public void AlbumsPage_CarouselMode_HidesTheGridAndShowsTheCarousel()
    {
        var app = Application.Current!;
        if (!app.Resources.TryGetResource("HeartFillIcon", null, out _))
        {
            app.Resources["InterSemiBold"] = FontFamily.Default;
            app.Resources.MergedDictionaries.Add(new Avalonia.Markup.Xaml.Styling.ResourceInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml") });
            app.Styles.Add(new Avalonia.Markup.Xaml.Styling.StyleInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml") });
        }
        var (vm, _) = AlbumsVm(Alb("Bad", 1987), Alb("Thriller", 1982), Alb("Invincible", 2001));
        var view = new Noctis.Views.LibraryAlbumsView { DataContext = vm };
        var window = new Window { Width = 1300, Height = 800, Content = view };
        window.Show();
        vm.Refresh();
        for (var i = 0; i < 200 && vm.FilteredAlbumRows.Count == 0; i++)
        {
            Thread.Sleep(10);
            Dispatcher.UIThread.RunJobs();
        }

        var grid = view.FindControl<ListBox>("AlbumListBox")!;
        var carousel = view.FindControl<AlbumCarousel>("AlbumCarousel")!;
        Assert.True(grid.IsVisible);
        Assert.False(carousel.IsVisible);

        vm.SetAlbumViewCommand.Execute("carousel");
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Assert.False(grid.IsVisible);
        Assert.True(carousel.IsVisible);
        Assert.Equal(3, carousel.RealizedCards.Count);

        // The centred album is remembered on the view model (back from the album page).
        carousel.MoveTo(2);
        Assert.Same(vm.CarouselAlbums[2], vm.CarouselCenterAlbum);
        window.Close();
    }

    private static void WaitUntil(Func<bool> done)
    {
        for (var i = 0; i < 300 && !done(); i++)
        {
            Thread.Sleep(10);
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>GitHub #164: the Albums page's top-right icon flips grid and carousel, with
    /// nothing playing, and the carousel holds every album (not the playing album's tracks)
    /// in the grid's sort and filter.</summary>
    [AvaloniaFact]
    public void AlbumsPage_Toggle_ShowsEveryAlbumWithNothingPlaying_InTheGridsSortAndFilter()
    {
        var (vm, settings) = AlbumsVm(Alb("Bad", 1987), Alb("Thriller", 1982), Alb("Invincible", 2001));
        vm.IsActive = true;
        WaitUntil(() => vm.CarouselAlbums.Count == 3);

        vm.ToggleAlbumViewCommand.Execute(null);
        Assert.True(vm.IsCarouselView);
        Assert.Equal("carousel", settings.AlbumViewMode);
        Assert.Equal(3, vm.CarouselAlbums.Count);

        vm.SetAlbumSortCommand.Execute("year");
        vm.SetAlbumSortCommand.Execute("ascending");
        WaitUntil(() => vm.CarouselAlbums.Select(a => a.Name).SequenceEqual(new[] { "Thriller", "Bad", "Invincible" }));
        Assert.Equal(new[] { "Thriller", "Bad", "Invincible" }, vm.CarouselAlbums.Select(a => a.Name));

        vm.ApplyFilter("thr");
        WaitUntil(() => vm.CarouselAlbums.Count == 1);
        Assert.Equal("Thriller", Assert.Single(vm.CarouselAlbums).Name);

        vm.ToggleAlbumViewCommand.Execute(null);
        Assert.True(vm.IsGridView);
        Assert.Equal("grid", settings.AlbumViewMode);
    }

    [AvaloniaFact]
    public void ArtistPage_AlbumsViewToggle_IsSharedByEveryArtist()
    {
        var lib = new FakeLibraryService();
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var settings = new SettingsViewModel(persistence, lib, new NoOpPlayHistoryService());
        settings.AlbumCarouselStyle = "JewelCase";

        var mj = new ArtistDetailViewModel("Michael Jackson", lib, player, settings: settings);
        Assert.False(mj.IsAlbumsCarousel);
        Assert.Equal(AlbumCarouselStyle.JewelCase, mj.CarouselStyle);
        mj.ToggleAlbumsViewCommand.Execute(null);
        Assert.True(mj.IsAlbumsCarousel);
        Assert.Equal("carousel", settings.ArtistAlbumsViewMode);
        Assert.Equal(Noctis.Localization.Loc.T("ArtistDetail.ShowGrid"), mj.AlbumsViewToggleLabel);

        var other = new ArtistDetailViewModel("Prince", lib, player, settings: settings);
        Assert.True(other.IsAlbumsCarousel);
    }

    // ── GitHub #233: reflections, glow, per-album styles ─────────────────────

    private static void Frames(int n)
    {
        for (var i = 0; i < n; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    [AvaloniaFact]
    public void EveryStyle_StandsOnAReflectionStripUnderTheWholeCard()
    {
        foreach (var style in Enum.GetValues<AlbumCarouselStyle>())
        {
            Assert.True(AlbumCarouselCard.ReflectionFactor(style) > 0.25, style.ToString());
            var card = new AlbumCarouselCard { CaseStyle = style, CoverSize = 200, Album = Alb("Bad") };
            card.Measure(Size.Infinity);
            card.Arrange(new Rect(card.DesiredSize));
            var strip = card.ReflectionBounds;
            // As wide as the composed card (case, disc, shell), right under it.
            // (Within a pixel: layout rounding.)
            Assert.Equal(card.CardSize.Width, strip.Width, 1.0);
            Assert.Equal(card.CardSize.Height + 200 * AlbumCarouselCard.ReflectionGap, strip.Y, 1.0);
            Assert.Equal(200 * AlbumCarouselCard.ReflectionHeight, strip.Height, 1.0);
        }
    }

    [Fact]
    public void ReflectionCover_CropsLikeUniformToFill()
    {
        Assert.Equal(new Rect(0, 0, 500, 500), AlbumCarouselCard.UniformToFillSource(new Size(500, 500), new Size(200, 200)));
        // A wide image shows its middle square.
        Assert.Equal(new Rect(100, 0, 400, 400), AlbumCarouselCard.UniformToFillSource(new Size(600, 400), new Size(100, 100)));
    }

    [AvaloniaFact]
    public void Glide_DoesNotRedrawReflections()
    {
        var (c, w) = Show(Many(5));
        c.CaseStyle = AlbumCarouselStyle.Vinyl;
        Frames(4);
        var cards = c.RealizedCards.Values.ToList();
        Assert.Equal(5, cards.Count);
        Assert.All(cards, card => Assert.True(card.ReflectionRenders > 0));
        var before = cards.Select(card => card.ReflectionRenders).ToList();

        c.MoveTo(4);
        for (var i = 0; i < 40; i++)
        {
            c.StepGlide(1 / 60.0);
            Frames(1);
        }
        Assert.Equal(4, c.Position);
        // Moving cards is transforms only: no reflection was re-recorded.
        Assert.Equal(before, cards.Select(card => card.ReflectionRenders).ToList());
        w.Close();
    }

    [AvaloniaFact]
    public void Glow_SitsBehindTheCentredAlbum_AndDimsBetweenTwo()
    {
        var (c, w) = Show(Many(9));
        c.SnapTo(4);
        Frames(2);
        var centre = c.RealizedCards[4];
        var cardRect = new Rect(Canvas.GetLeft(centre), Canvas.GetTop(centre), centre.CardSize.Width, centre.CardSize.Height);
        Assert.Equal(1, c.GlowOpacity, 3);
        Assert.Equal(cardRect.Center.X, c.GlowRect.Center.X, 1);
        Assert.Equal(cardRect.Center.Y, c.GlowRect.Center.Y, 1);
        Assert.True(c.GlowRect.Width > cardRect.Width && c.GlowRect.Height > cardRect.Height, "the glow reaches past the card");

        // Half way to the next album the glow is at half strength, never on a side album.
        c.MoveTo(5);
        while (c.Position < 4.5) c.StepGlide(1 / 120.0);
        Assert.InRange(c.GlowOpacity, 0.45, 0.7);
        Assert.InRange(c.GlowRect.Center.X, cardRect.Center.X - 1, cardRect.Center.X + 1);
        Settle(c);
        Assert.Equal(1, c.GlowOpacity, 3);

        c.GlowEnabled = false;
        Assert.Equal(0, c.GlowOpacity);
        w.Close();
    }

    [AvaloniaFact]
    public void Glow_MovesOnlyWhatItMust_WhileGliding()
    {
        var (c, w) = Show(Many(30));
        Frames(3);
        var renders = c.GlowRenders;
        c.MoveTo(20);
        for (var i = 0; i < 60; i++)
        {
            c.StepGlide(1 / 60.0);
            Frames(1);
        }
        // One ellipse per frame at most (same style, same accent: usually none at all).
        Assert.InRange(c.GlowRenders - renders, 0, 60);
        w.Close();
    }

    [AvaloniaFact]
    public void MixedStyles_EachCardWearsItsOwn_OnOneFloor()
    {
        var albums = Many(7);
        var (c, w) = Show(albums);
        c.CaseStyle = AlbumCarouselStyle.JewelCase;
        c.StyleOverrides = new Dictionary<Guid, AlbumCarouselStyle>
        {
            [albums[2].Id] = AlbumCarouselStyle.Cover,
            [albums[3].Id] = AlbumCarouselStyle.Vinyl,
            [albums[4].Id] = AlbumCarouselStyle.Cassette,
        };
        c.SnapTo(3);
        w.UpdateLayout();

        Assert.Equal(AlbumCarouselStyle.JewelCase, c.RealizedCards[1].CaseStyle);
        Assert.Equal(AlbumCarouselStyle.Cover, c.RealizedCards[2].CaseStyle);
        Assert.Equal(AlbumCarouselStyle.Vinyl, c.RealizedCards[3].CaseStyle);
        Assert.Equal(AlbumCarouselStyle.Cassette, c.RealizedCards[4].CaseStyle);
        // Different heights, one floor line (so the reflections line up).
        var floors = c.RealizedCards.Values.Select(card => Canvas.GetTop(card) + card.CardSize.Height).Distinct().ToList();
        Assert.All(floors, f => Assert.Equal(floors[0], f, 1));
        // The centred record's sleeve is centred, its disc hangs to the right.
        var vinyl = c.RealizedCards[3];
        Assert.Equal(c.Bounds.Width / 2, Canvas.GetLeft(vinyl) + vinyl.CoverSize / 2, 1);
        // Clicking still centres the right album.
        c.Click(c.RealizedCards[4]);
        Assert.Equal(4, c.TargetIndex);

        // Back to the global style: the override map empties.
        c.StyleOverrides = new Dictionary<Guid, AlbumCarouselStyle>();
        Assert.All(c.RealizedCards.Values, card => Assert.Equal(AlbumCarouselStyle.JewelCase, card.CaseStyle));
        w.Close();
    }

    [AvaloniaFact]
    public async Task PerAlbumStyle_IsSavedByAlbumId_AndSurvivesARestart()
    {
        var dir = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
        var persistence = new Noctis.Services.PersistenceService(dir);
        var lib = new FakeLibraryService();
        var settings = new SettingsViewModel(persistence, lib, new NoOpPlayHistoryService());
        await settings.LoadAsync();
        var bad = Alb("Bad");
        Assert.Null(settings.GetAlbumCarouselStyleOverride(bad.Id));
        Assert.True(settings.AlbumCarouselGlow);

        settings.SetAlbumCarouselStyleOverride(bad.Id, AlbumCarouselStyle.Cassette);
        settings.AlbumCarouselGlow = false;
        await settings.SaveAsync();

        // A rescan rebuilds the Album object; the id (artist + title) stays.
        var again = new SettingsViewModel(persistence, lib, new NoOpPlayHistoryService());
        await again.LoadAsync();
        Assert.Equal(AlbumCarouselStyle.Cassette, again.GetAlbumCarouselStyleOverride(bad.Id));
        Assert.Equal(AlbumCarouselStyle.Cassette, again.AlbumCarouselStyleOverrideMap()[bad.Id]);
        Assert.False(again.AlbumCarouselGlow);

        // Default = follow the global style again.
        again.SetAlbumCarouselStyleOverride(bad.Id, null);
        Assert.Null(again.GetAlbumCarouselStyleOverride(bad.Id));
        Assert.Empty(again.AlbumCarouselStyleOverrideMap());
    }

    [AvaloniaFact]
    public void AlbumMenu_CarouselStyle_ChecksTheAlbumsPick()
    {
        var host = new Window();
        var builder = new AlbumContextMenuBuilder();
        builder.Build("Remove", host, v2: true);
        var album = Alb("Bad");
        builder.BindCarouselStyle(album, null, visible: true);
        Assert.True(builder.CarouselStyle.IsVisible);
        Assert.Equal(1 + Enum.GetValues<AlbumCarouselStyle>().Length, builder.CarouselStyleRows.Length);
        Assert.Equal(new object?[] { true, false, false, false, false }, builder.CarouselStyleRows.Select(r => r.Tag));
        Assert.NotNull(builder.CarouselStyleRows[0].Icon);

        builder.BindCarouselStyle(album, AlbumCarouselStyle.Vinyl, visible: true);
        Assert.Equal(new object?[] { false, false, false, true, false }, builder.CarouselStyleRows.Select(r => r.Tag));
        var vinylRow = builder.CarouselStyleRows[1 + (int)AlbumCarouselStyle.Vinyl];
        var choice = Assert.IsType<AlbumCarouselStyleOverrides.Choice>(vinylRow.CommandParameter);
        Assert.Same(album, choice.Album);
        Assert.Equal(AlbumCarouselStyle.Vinyl, choice.Style);
        Assert.Null(((AlbumCarouselStyleOverrides.Choice)builder.CarouselStyleRows[0].CommandParameter!).Style);

        builder.BindCarouselStyle(album, null, visible: false);
        Assert.False(builder.CarouselStyle.IsVisible);
    }

    // ── Opt-in renders for eyeballing the case styles ─────────────────────────

    /// <summary>Saves one PNG per style. Runs only with NOCTIS_TEST_SKIA=1 and
    /// NOCTIS_CAROUSEL_SHOTS set to an output folder.</summary>
    [AvaloniaFact]
    public void Styles_SaveRenders()
    {
        var dir = Environment.GetEnvironmentVariable("NOCTIS_CAROUSEL_SHOTS");
        if (!HeadlessTestApp.RealRendering || string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        var artDir = Path.Combine(Path.GetTempPath(), "NoctisCarouselArt");
        Directory.CreateDirectory(artDir);
        var palette = new[] { SKColors.OrangeRed, SKColors.SteelBlue, SKColors.Goldenrod, SKColors.SeaGreen, SKColors.MediumPurple, SKColors.Crimson, SKColors.Teal };
        var albums = new List<Album>();
        for (var i = 0; i < 15; i++)
        {
            var path = Path.Combine(artDir, $"art{i}.png");
            using (var bmp = new SKBitmap(512, 512))
            using (var canvas = new SKCanvas(bmp))
            {
                var c1 = palette[i % palette.Length];
                var c2 = palette[(i + 3) % palette.Length];
                using var paint = new SKPaint
                {
                    Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(512, 512), new[] { c1, c2 }, SKShaderTileMode.Clamp),
                };
                canvas.DrawRect(0, 0, 512, 512, paint);
                using var white = new SKPaint { Color = SKColors.White.WithAlpha(200), IsAntialias = true };
                canvas.DrawCircle(256, 220, 110, white);
                using var dark = new SKPaint { Color = SKColors.Black.WithAlpha(160) };
                canvas.DrawRect(40, 400, 432, 60, dark);
                using var data = SKImage.FromBitmap(bmp).Encode(SKEncodedImageFormat.Png, 90);
                using var fs = File.Create(path);
                data.SaveTo(fs);
            }
            albums.Add(Alb($"Album Number {i}", 1980 + i, path));
        }

        void Shot(string name, AlbumCarouselStyle style, Action<AlbumCarousel>? setup = null, double width = 1280)
        {
            var carousel = new AlbumCarousel { Albums = albums, CaseStyle = style };
            var window = new Window
            {
                Width = width, Height = 560, Content = carousel,
                Background = new SolidColorBrush(Color.Parse("#141414")),
                RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark,
            };
            window.Show();
            carousel.SnapTo(7);
            setup?.Invoke(carousel);
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 900)
            {
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Thread.Sleep(10);
            }
            window.CaptureRenderedFrame()!.Save(Path.Combine(dir, $"carousel-{name}.png"));
            window.Close();
        }

        foreach (var style in Enum.GetValues<AlbumCarouselStyle>())
            Shot(style.ToString(), style);
        Shot("Cover-NoGlow", AlbumCarouselStyle.Cover, c => c.GlowEnabled = false);
        // GitHub #233: one row, every case side by side (global Jewel Case).
        var mixed = new Dictionary<Guid, AlbumCarouselStyle>
        {
            [albums[5].Id] = AlbumCarouselStyle.Cassette,
            [albums[6].Id] = AlbumCarouselStyle.Cover,
            [albums[7].Id] = AlbumCarouselStyle.Vinyl,
            [albums[8].Id] = AlbumCarouselStyle.Cassette,
            [albums[9].Id] = AlbumCarouselStyle.Cover,
        };
        Shot("Mixed", AlbumCarouselStyle.JewelCase, c => c.StyleOverrides = mixed);
        Shot("Mixed-CentreCassette", AlbumCarouselStyle.JewelCase, c => { c.StyleOverrides = mixed; c.SnapTo(8); });
        Shot("Mixed-Narrow", AlbumCarouselStyle.Cassette, c => c.StyleOverrides = mixed, width: 820);
    }
}
