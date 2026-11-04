using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Mobile.Services;
using Noctis.Mobile.ViewModels;
using Noctis.Mobile.Views;
using Noctis.Models;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Tester feedback 11-04: song rows end their artist line in the song's stars or a heart
/// (Settings › Library › Show in Song Rows: Off / Ratings, the default / Favorites), the song
/// menu rates a song (five stars and Clear), and the Songs, Albums and Artists pages share one
/// in-page search field (FindField), Albums gaining search by title and artist.
/// </summary>
public class MobileSongRowMarkTests
{
    private readonly ITestOutputHelper _o;
    public MobileSongRowMarkTests(ITestOutputHelper o) => _o = o;

    private static (Track Rated, Track Loved, Track Plain) Songs()
    {
        var rated = MobileFixtures.Song("Rated", "Adele");
        rated.Rating = 3;
        var loved = MobileFixtures.Song("Loved", "Bruno Mars", favourite: true);
        var plain = MobileFixtures.Song("Plain", "Coldplay");
        return (rated, loved, plain);
    }

    private static RowMark MarkOf(Visual root, Track track) =>
        root.GetVisualDescendants().OfType<SongRow>().Single(r => ReferenceEquals(r.DataContext, track))
            .GetVisualDescendants().OfType<RowMark>().Single();

    [AvaloniaFact]
    public void SongRows_ShowStarsByDefault_AHeartWithFavorites_NothingWhenOff_AndFollowChangesLive()
    {
        var (rated, loved, plain) = Songs();
        using var rig = MobileFixtures.MakeRig(new[] { rated, loved, plain });
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        rig.Shell.OpenSongsCommand.Execute(null);
        MobileFixtures.Land(window);
        var page = MobileFixtures.Find<SongListPage>(view);

        // Ratings (the default): three stars on the rated song only, right of the artist.
        Assert.Equal(SongRowMark.Ratings, rig.Shell.SongRowMark);
        var mark = MarkOf(page, rated);
        Assert.Equal(3, mark.StarCount);
        Assert.Equal(3 * RowMark.GlyphSize + 2 * RowMark.StarGap, mark.Bounds.Width, 1);
        Assert.False(MarkOf(page, loved).HasMark);
        Assert.Equal(0, MarkOf(page, plain).Bounds.Width);
        var artist = ((Visual)mark.Parent!).GetVisualChildren().OfType<TextBlock>().Single();
        Assert.True(mark.TranslatePoint(default, page)!.Value.X >= artist.TranslatePoint(default, page)!.Value.X + artist.Bounds.Width);

        // A rating changed elsewhere (the menu, a sync) redraws the row at once.
        rated.Rating = 5;
        window.UpdateLayout();
        Assert.Equal(5, mark.StarCount);
        Assert.Equal(5 * RowMark.GlyphSize + 4 * RowMark.StarGap, mark.Bounds.Width, 1);

        // Favorites: the heart on the favourite, no stars.
        rig.Shell.SongRowMark = SongRowMark.Favorites;
        window.UpdateLayout();
        Assert.True(MarkOf(page, loved).ShowsHeart);
        Assert.Equal(RowMark.GlyphSize, MarkOf(page, loved).Bounds.Width, 1);
        Assert.False(MarkOf(page, rated).HasMark);
        plain.IsFavorite = true;
        window.UpdateLayout();
        Assert.True(MarkOf(page, plain).ShowsHeart);

        // Off: nothing anywhere.
        rig.Shell.SongRowMark = SongRowMark.Off;
        window.UpdateLayout();
        Assert.All(page.GetVisualDescendants().OfType<RowMark>(), m => Assert.False(m.HasMark));
        window.Close();
    }

    [AvaloniaFact]
    public void AlbumTrackRows_ShowTheMarkBeforeTheMoreButton()
    {
        var (rated, _, plain) = Songs();
        var album = MobileFixtures.MakeAlbum("Record", "Adele", rated, plain);
        using var rig = MobileFixtures.MakeRig(new[] { rated, plain }, new[] { album });
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        rig.Shell.OpenAlbumCommand.Execute(album);
        MobileFixtures.Land(window);
        var page = MobileFixtures.Find<AlbumPage>(view);
        var marks = page.GetVisualDescendants().OfType<RowMark>().ToList();
        var mark = marks.Single(m => ReferenceEquals(m.DataContext, rated));
        Assert.Equal(3, mark.StarCount);
        Assert.False(marks.Single(m => ReferenceEquals(m.DataContext, plain)).HasMark);
        var more = ((Visual)mark.Parent!).GetVisualChildren().OfType<Button>().Single(b => b.Name == "RowMore");
        Assert.True(mark.TranslatePoint(default, page)!.Value.X + mark.Bounds.Width <= more.TranslatePoint(default, page)!.Value.X + 0.5);
        window.Close();
    }

    [Fact]
    public async Task TheChoice_IsSaved_AndReadBack()
    {
        using var rig = MobileFixtures.MakeRig(seed: async p =>
        {
            var s = await p.LoadSettingsAsync();
            Assert.Equal("Ratings", s.MobileSongRowMark);   // the default
            s.MobileSongRowMark = "Favorites";
            await p.SaveSettingsAsync(s);
        });
        await rig.Shell.InitializeAsync();
        Assert.Equal(SongRowMark.Favorites, rig.Shell.SongRowMark);

        rig.Shell.SongRowMark = SongRowMark.Off;
        await rig.Shell.PendingPlayerBarSave;
        Assert.Equal("Off", (await rig.Persistence.LoadSettingsAsync()).MobileSongRowMark);
    }

    [AvaloniaFact]
    public async Task SettingsRow_OpensOffRatingsFavorites_AndPickingOneAppliesIt()
    {
        using var rig = MobileFixtures.MakeRig();
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        rig.Shell.OpenSettingsCommand.Execute(null);
        var settings = Assert.IsType<SettingsPageViewModel>(rig.Shell.CurrentPage);
        await settings.Loaded;
        MobileFixtures.Land(window);
        Assert.Equal("Ratings", MobileFixtures.Named<TextBlock>(view, "SongRowsValueLabel").Text);

        settings.ShowSongRowsMenuCommand.Execute(null);
        var menu = Assert.IsType<OptionsMenuViewModel>(rig.Shell.OptionsMenu);
        Assert.Equal(new[] { "Off", "Ratings", "Favorites" }, menu.Items.Select(i => i.Label));
        Assert.True(menu.Items[1].IsChecked);
        menu.Items[2].PickCommand.Execute(null);
        Assert.Equal(SongRowMark.Favorites, rig.Shell.SongRowMark);
        window.UpdateLayout();
        Assert.Equal("Favorites", MobileFixtures.Named<TextBlock>(view, "SongRowsValueLabel").Text);
        Assert.Equal("Show in Song Rows, Favorites", settings.SongRowsRowName);
        window.Close();
    }

    [AvaloniaFact]
    public async Task SongMenu_RatesTheSong_AndClearsIt_TheRowFollowing()
    {
        var (rated, _, plain) = Songs();
        using var rig = MobileFixtures.MakeRig(new[] { rated, plain });
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        rig.Shell.OpenSongsCommand.Execute(null);
        MobileFixtures.Land(window);

        rig.Shell.OpenTrackMenu(plain, null);
        var menu = Assert.IsType<ContextSheetViewModel>(rig.Shell.Sheet);
        Assert.True(menu.CanRate);
        Assert.False(menu.IsRated);
        Assert.All(menu.Stars, s => Assert.False(s.IsOn));
        MobileFixtures.Land(window);
        Assert.True(MobileFixtures.Named<Grid>(view, "MenuRate").IsEffectivelyVisible);
        Assert.False(MobileFixtures.Named<Button>(view, "MenuClearRating").IsEffectivelyVisible);
        await menu.RateCommand.ExecuteAsync(4);
        Assert.Null(rig.Shell.Sheet);                       // the menu closes
        Assert.Equal(4, plain.Rating);
        window.UpdateLayout();
        Assert.Equal(4, MarkOf(MobileFixtures.Find<SongListPage>(view), plain).StarCount);

        var again = ContextSheetViewModel.ForTrack(rig.Shell, plain);
        Assert.True(again.IsRated);
        Assert.Equal(new[] { true, true, true, true, false }, again.Stars.Select(s => s.IsOn));
        await again.ClearRatingCommand.ExecuteAsync(null);
        Assert.Equal(0, plain.Rating);
        window.UpdateLayout();
        Assert.False(MarkOf(MobileFixtures.Find<SongListPage>(view), plain).HasMark);

        // An album's or artist's menu does not rate (it would rate every song at once).
        var album = MobileFixtures.MakeAlbum("Record", "Adele", rated);
        Assert.False(ContextSheetViewModel.ForAlbum(rig.Shell, album).CanRate);
        window.Close();
    }

    [AvaloniaFact]
    public void FindInAlbums_FiltersByTitleOrArtist_IgnoringAccents()
    {
        var albums = new[]
        {
            MobileFixtures.MakeAlbum("Renaissance", "Beyoncé", MobileFixtures.Song("a")),
            MobileFixtures.MakeAlbum("25", "Adele", MobileFixtures.Song("b")),
            MobileFixtures.MakeAlbum("Parachutes", "Coldplay", MobileFixtures.Song("c")),
        };
        using var rig = MobileFixtures.MakeRig(albums.SelectMany(a => a.Tracks).ToArray(), albums);
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        rig.Shell.OpenAlbumsCommand.Execute(null);
        var page = Assert.IsType<AlbumGridPageViewModel>(rig.Shell.CurrentPage);
        MobileFixtures.Land(window);
        var grid = MobileFixtures.Find<AlbumGridPage>(view);
        Assert.Equal(3, page.Albums.Count);

        MobileFixtures.Named<TextBox>(grid, "FindBox").Text = "beyonce";   // typed, by artist
        window.UpdateLayout();
        Assert.Equal(new[] { "Renaissance" }, page.Albums.Select(a => a.Name));
        Assert.True(MobileFixtures.Named<Button>(grid, "ClearFindButton").IsEffectivelyVisible);

        page.Query = "PARACH";                                                  // by title
        Assert.Equal(new[] { "Parachutes" }, page.Albums.Select(a => a.Name));

        page.Query = "zz";
        window.UpdateLayout();
        Assert.Empty(page.Rows);
        Assert.True(MobileFixtures.Named<TextBlock>(grid, "NoAlbumResultsText").IsEffectivelyVisible);

        MobileFixtures.Named<Button>(grid, "ClearFindButton").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        window.UpdateLayout();
        Assert.Equal(string.Empty, page.Query);
        Assert.Equal(3, page.Albums.Count);
        Assert.False(MobileFixtures.Named<TextBlock>(grid, "NoAlbumResultsText").IsEffectivelyVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void SongsAlbumsAndArtists_ShareOneSearchField_WithTheSameSizeAndSpacing()
    {
        var song = MobileFixtures.Song("One", "Adele");
        var album = MobileFixtures.MakeAlbum("25", "Adele", song);
        using var rig = MobileFixtures.MakeRig(new[] { song }, new[] { album });
        rig.Library.ArtistList.Add(new Artist { Id = Guid.NewGuid(), Name = "Adele", AlbumCount = 1, TrackCount = 1 });
        var window = MobileFixtures.Mount(rig.Shell, out var view);

        FindField FieldOn<TPage>() where TPage : Visual
        {
            MobileFixtures.Land(window);
            var page = view.GetVisualDescendants().OfType<TPage>().Last(p => p.IsEffectivelyVisible);
            return Assert.Single(page.GetVisualDescendants().OfType<FindField>(), f => f.IsEffectivelyVisible);
        }

        rig.Shell.OpenSongsCommand.Execute(null);
        var songs = FieldOn<SongListPage>();
        Assert.Empty(MobileFixtures.Find<SongListPage>(view).GetVisualDescendants().OfType<Button>().Where(b => b.Name == "ListSearchButton"));
        rig.Shell.NavigateBackCommand.Execute(null);
        rig.Shell.OpenAlbumsCommand.Execute(null);
        var albums = FieldOn<AlbumGridPage>();
        rig.Shell.NavigateBackCommand.Execute(null);
        rig.Shell.OpenArtistsCommand.Execute(null);
        var artists = FieldOn<ArtistListPage>();

        foreach (var field in new[] { songs, albums })
        {
            Assert.Equal(artists.Margin, field.Margin);
            Assert.Equal(artists.Bounds.Width, field.Bounds.Width, 1);
            Assert.Equal(artists.Bounds.Height, field.Bounds.Height, 1);
        }
        Assert.Equal("Search in this list", songs.Placeholder);
        Assert.Equal("Find in Albums", albums.Placeholder);
        Assert.Equal("Find in Artists", artists.Placeholder);
        window.Close();
    }

    // ── Eyeball shots: NOCTIS_TEST_SKIA=1 and NOCTIS_RATINGS_SHOTS=<dir> ──

    private static void Pump(int ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(5);
        }
        Dispatcher.UIThread.RunJobs();
        Motion.FinishAll();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Shots_RatingsFavoritesAndTheThreeSearchFields()
    {
        var dir = Environment.GetEnvironmentVariable("NOCTIS_RATINGS_SHOTS");
        if (!HeadlessTestApp.RealRendering || string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);

        var app = Application.Current!;
        app.Resources["InterSemiBold"] = FontFamily.Default;
        try { FontManager.Current.AddFontCollection(new Avalonia.Fonts.Inter.InterFontCollection()); }
        catch (ArgumentException) { }
        var icons = new ResourceInclude(new Uri("avares://Noctis.Mobile/")) { Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml") };
        var styles = new StyleInclude(new Uri("avares://Noctis.Mobile/")) { Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml") };
        app.Resources.MergedDictionaries.Add(icons);
        app.Styles.Add(styles);
        var theme = new MobileTheme();
        try
        {
            foreach (var name in new[] { "Light", "Dark" })
            {
                theme.Apply(app, name, MobileTheme.DefaultAccent);
                var tracks = new[]
                {
                    ("Hello", "Adele", 5, true), ("Grenade", "Bruno Mars", 4, false), ("Yellow", "Coldplay", 0, true),
                    ("Halo", "Beyoncé", 3, false), ("Clocks", "Coldplay", 0, false), ("Someone Like You", "Adele", 2, true),
                    ("Viva la Vida", "Coldplay", 1, false), ("Uptown Funk", "Mark Ronson feat. Bruno Mars", 5, true),
                }.Select(x =>
                {
                    var t = MobileFixtures.Song(x.Item1, x.Item2, favourite: x.Item4);
                    t.Rating = x.Item3;
                    return t;
                }).ToArray();
                var albums = new[]
                {
                    MobileFixtures.MakeAlbum("25", "Adele", tracks[0], tracks[5]),
                    MobileFixtures.MakeAlbum("Doo-Wops & Hooligans", "Bruno Mars", tracks[1]),
                    MobileFixtures.MakeAlbum("Parachutes", "Coldplay", tracks[2]),
                    MobileFixtures.MakeAlbum("I Am... Sasha Fierce", "Beyoncé", tracks[3]),
                };
                using var rig = MobileFixtures.MakeRig(tracks, albums);
                foreach (var artist in new[] { "Adele", "Beyoncé", "Bruno Mars", "Coldplay" })
                    rig.Library.ArtistList.Add(new Artist { Id = Guid.NewGuid(), Name = artist, AlbumCount = 1, TrackCount = 2 });
                var window = MobileFixtures.Mount(rig.Shell, out _, 412, 915);
                try
                {
                    rig.Shell.OpenSongsCommand.Execute(null);
                    Pump(300);
                    Save(window, Path.Combine(dir, $"ratings-songs-ratings-{name}.png"));
                    rig.Shell.SongRowMark = SongRowMark.Favorites;
                    Pump(200);
                    Save(window, Path.Combine(dir, $"ratings-songs-favorites-{name}.png"));
                    rig.Shell.SongRowMark = SongRowMark.Ratings;
                    rig.Shell.OpenTrackMenu(tracks[1], null);
                    Pump(400);
                    Save(window, Path.Combine(dir, $"ratings-menu-{name}.png"));
                    rig.Shell.CloseSheet();
                    rig.Shell.NavigateBackCommand.Execute(null);

                    rig.Shell.OpenAlbumsCommand.Execute(null);
                    Pump(300);
                    Save(window, Path.Combine(dir, $"ratings-search-albums-{name}.png"));
                    rig.Shell.NavigateBackCommand.Execute(null);
                    rig.Shell.OpenArtistsCommand.Execute(null);
                    Pump(300);
                    Save(window, Path.Combine(dir, $"ratings-search-artists-{name}.png"));
                    rig.Shell.NavigateBackCommand.Execute(null);
                    rig.Shell.OpenSongsCommand.Execute(null);
                    Pump(300);
                    Save(window, Path.Combine(dir, $"ratings-search-songs-{name}.png"));
                    rig.Shell.NavigateBackCommand.Execute(null);
                    rig.Shell.OpenAlbumCommand.Execute(albums[0]);
                    Pump(300);
                    Save(window, Path.Combine(dir, $"ratings-album-{name}.png"));
                }
                finally
                {
                    window.Close();
                }
            }
        }
        finally
        {
            theme.Remove(app);
            app.Styles.Remove(styles);
            app.Resources.MergedDictionaries.Remove(icons);
        }
    }

    private void Save(Window window, string path)
    {
        window.CaptureRenderedFrame()!.Save(path);
        _o.WriteLine(path);
    }
}
