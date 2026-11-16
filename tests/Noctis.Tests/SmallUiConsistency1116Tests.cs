using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Localization;
using Noctis.Models;
using Noctis.Services;
using Noctis.Services.Lyrics;
using Noctis.Services.LyricsStudio;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Discord "Small ui inconsistency" (11-16, French UI): the artist About card's eighth link
/// wrapped alone, the Lyrics Studio's Settings popover was cut off on the right and partly
/// English, and the album page's Play / Shuffle / Add to Queue pills had a clipped shadow.
/// </summary>
[Collection("Localization")] // Loc.Instance is process-wide: switches culture
public class SmallUiConsistency1116Tests
{
    private readonly ITestOutputHelper _out;
    public SmallUiConsistency1116Tests(ITestOutputHelper o) => _out = o;

    private static void EnsureAppStyles()
    {
        var app = Application.Current!;
        if (app.Resources.TryGetResource("HeartFillIcon", null, out _)) return;
        app.Resources["InterSemiBold"] = FontFamily.Default;
        app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml") });
        app.Styles.Add(new StyleInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml") });
    }

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
    }

    // ── 1. About card links ──

    /// <summary>All eight link kinds an artist can have sit on one row of the side About card
    /// (340 px): 34 px chips with an 8 px trailing margin fit seven and the site wrapped alone.</summary>
    [AvaloniaFact]
    public void AboutCard_AllEightLinks_FitOneRow()
    {
        EnsureAppStyles();
        var lib = new FakeLibraryService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, new TestPersistenceService(), new FakeAnimatedCoverService());
        var vm = new ArtistDetailViewModel("Playboi Carti", lib, player);
        var view = new ArtistDetailView { DataContext = vm };
        var win = new Window { Width = 1700, Height = 900, Content = view }; // wide: About is the 340 px side card
        win.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var kinds = new[] { "spotify", "applemusic", "youtube", "soundcloud", "bandcamp", "instagram", "x", "website" };
            vm.About = new ArtistInfo { Links = kinds.Select(k => new ArtistLink { Kind = k, Url = "https://example.com/" + k }).ToList() };
            Pump(100);
            win.UpdateLayout();

            var row = view.FindControl<Grid>("OverviewTopRow")!;
            Assert.False(row.Classes.Contains("stacked"));
            var links = view.FindControl<ItemsControl>("AboutLinks")!;
            var chips = links.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("link-circle")).ToList();
            Assert.Equal(8, chips.Count);
            var tops = chips.Select(c => Math.Round(c.TranslatePoint(default, links)!.Value.Y)).Distinct().ToList();
            var rights = chips.Select(c => c.TranslatePoint(new Point(c.Bounds.Width, 0), links)!.Value.X).ToList();
            _out.WriteLine($"row width={links.Bounds.Width} tops={string.Join(",", tops)} last right={rights.Max()}");
            Assert.Single(tops);
            Assert.True(rights.Max() <= links.Bounds.Width + 0.5, $"links run to {rights.Max()} of {links.Bounds.Width}");
        }
        finally { win.Close(); }
    }

    // ── 2. Lyrics Studio Settings popover ──

    private static LyricsStudioViewModel Studio(string root) =>
        new(Array.Empty<Track>(), new IdleEngine(root), new LyricsWriter(null!, null), new FakeLibraryService(), null, () => new AppSettings(), _ => { });

    private sealed class IdleEngine(string root) : ILyricsStudioEngine
    {
        public bool HasFfmpeg => true;
        public WhisperModelManager Models { get; } = StudioTestModel.Installed(root);
        public IDisposable OpenSession(WhisperModelSize model) => new Handle();
        public Task<LyricsStudioResult> ProcessAsync(Track track, LyricsStudioOptions options, IProgress<LyricsStudioProgress>? progress, CancellationToken ct)
            => throw new NotSupportedException();
        private sealed class Handle : IDisposable { public void Dispose() { } }
    }

    /// <summary>In French the popover's option rows are wider than Fluent's FlyoutThemeMaxWidth
    /// (456) left for the card, so its presenter's ScrollViewer cut the card's right edge and
    /// showed a horizontal scrollbar. The whole card fits now.</summary>
    [AvaloniaFact]
    public void StudioSettingsPopover_French_IsNotCutOff_AndIsTranslated()
    {
        EnsureAppStyles();
        var root = Path.Combine(Path.GetTempPath(), "noctis-studio-fr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Loc.Instance.SetCulture("fr");
            var vm = Studio(root);
            Assert.Equal("Détecter automatiquement", vm.LanguageOptions[0].ToString());
            Assert.Equal("Paramètres", Loc.T("LyricsStudio.Settings"));
            Assert.Equal("Enregistrer aussi en TTML", Loc.T("LyricsStudioSettings.SaveTtml"));

            var panel = new LyricsStudioPanel { DataContext = vm, ShowHeader = false };
            var win = new Window { Width = 1000, Height = 720, Content = panel, RequestedThemeVariant = ThemeVariant.Dark };
            win.Show();
            try
            {
                Pump(200);
                var settings = panel.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "StudioSettingsButton");
                var flyout = Assert.IsType<Flyout>(settings.Flyout);
                flyout.ShowAt(settings);
                Pump(400);

                var card = Assert.IsType<Border>(flyout.Content);
                var presenter = card.GetVisualAncestors().OfType<FlyoutPresenter>().Single();
                var scroller = presenter.GetVisualDescendants().OfType<ScrollViewer>().First();
                _out.WriteLine($"presenter={presenter.Bounds.Width} max={presenter.MaxWidth} card={card.Bounds.Width} desired={card.DesiredSize.Width} extent={scroller.Extent.Width} viewport={scroller.Viewport.Width}");
                Assert.True(scroller.Extent.Width <= scroller.Viewport.Width + 0.5,
                    $"the card is {scroller.Extent.Width} px wide in a {scroller.Viewport.Width} px viewport: its right edge is cut off");
                var right = card.TranslatePoint(new Point(card.Bounds.Width, 0), presenter)!.Value.X;
                Assert.True(right <= presenter.Bounds.Width + 0.5, $"card right {right} past presenter {presenter.Bounds.Width}");
                flyout.Hide();
            }
            finally { win.Close(); }
        }
        finally
        {
            Loc.Instance.SetCulture("en");
            try { Directory.Delete(root, true); } catch { }
        }
    }

    // ── 3. Album header pills' shadow ──

    private sealed class FakeLastFm : ILastFmService
    {
        public bool IsAuthenticated => false;
        public string? Username => null;
        public void Configure(string? sessionKey) { }
        public Task<string> GetAuthUrlAsync() => Task.FromResult(string.Empty);
        public Task<bool> CompleteAuthAsync() => Task.FromResult(false);
        public string? GetSessionKey() => null;
        public void Logout() { }
        public Task ScrobbleAsync(Track track, DateTime startedAt) => Task.CompletedTask;
        public Task UpdateNowPlayingAsync(Track track) => Task.CompletedTask;
        public Task<string?> GetAlbumDescriptionAsync(string a, string b, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<string?> GetAlbumDescriptionFullAsync(string a, string b, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task SetAlbumDescriptionOverrideAsync(string a, string b, string? d, CancellationToken ct = default) => Task.CompletedTask;
        public Task ClearAlbumDescriptionOverrideAsync(string a, string b, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class NoOpPlayHistory : IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    /// <summary>The tinted album page's Play / Shuffle / Add to Queue pills draw a soft shadow
    /// (their template's presenter, 0 2 6). Avalonia 12 TemplatedControls clip to their bounds, so
    /// the Button cut it along its box: nothing under the pill or beside it, only blocky bits in
    /// the four corners outside the round caps. The pill no longer clips; with real rendering the
    /// shadow shows just below and beside the pill.</summary>
    [AvaloniaFact]
    public void AlbumHeaderPills_ShadowIsNotCutAtThePillsBox()
    {
        EnsureAppStyles();
        var lib = new FakeLibraryService();
        var persistence = new TestPersistenceService();
        var album = new Album { Id = Guid.NewGuid(), Name = "A", Artist = "B", Tracks = new List<Track>() };
        for (var i = 1; i <= 2; i++)
            album.Tracks.Add(new Track
            {
                Id = Guid.NewGuid(), FilePath = TestPaths.Primary("pill-shadow", "A", $"{i:00} Song {i}.flac"),
                Title = $"Song {i}", Artist = "B", AlbumArtist = "B", Album = "A", TrackNumber = i,
            });
        var settings = new SettingsViewModel(persistence, lib, new NoOpPlayHistory());
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var vm = new AlbumDetailViewModel(album, player, persistence, lib, new SidebarViewModel(persistence, lib), new FakeLastFm(), settings);
        var view = new AlbumDetailView { DataContext = vm };
        var win = new Window { Width = 1280, Height = 900, Content = view };
        win.Show();
        try
        {
            vm.ApplyTint(Color.FromRgb(0xD0, 0xD0, 0xD0)); // a light tint, like the report's
            Pump(300);
            win.UpdateLayout();

            var pills = view.GetVisualDescendants().OfType<Button>()
                .Where(b => b.Classes.Contains("album-header-action") && b.GetVisualParent() is StackPanel p && p.Classes.Contains("on-tint"))
                .ToList();
            Assert.Equal(3, pills.Count);
            foreach (var pill in pills)
            {
                var presenter = pill.GetVisualDescendants().OfType<ContentPresenter>().First(c => c.Name == "PART_ContentPresenter");
                Assert.True(presenter.BoxShadow.Count > 0, "the tinted pill lost its shadow");
                Assert.False(pill.ClipToBounds, "the pill clips its own shadow at its box");
            }

            if (!HeadlessTestApp.RealRendering) return; // pixels need NOCTIS_TEST_SKIA=1
            var play = pills[0];
            var o = play.TranslatePoint(default, win)!.Value;
            int left = (int)Math.Round(o.X), top = (int)Math.Round(o.Y);
            int right = left + (int)Math.Round(play.Bounds.Width), bottom = top + (int)Math.Round(play.Bounds.Height);
            using var frame = win.CaptureRenderedFrame()!;
            using var ms = new MemoryStream();
            frame.Save(ms);
            using var bmp = SkiaSharp.SKBitmap.Decode(ms.ToArray());
            int Dark(int x, int y) => 0xD0 - bmp.GetPixel(x, y).Green;
            var bg = Dark(left - 30, top - 30);
            var below = Dark((left + right) / 2, bottom + 1);
            var side = Dark(left - 2, (top + bottom) / 2 + 2);
            _out.WriteLine($"pill {left},{top}-{right},{bottom} bg={bg} below={below} side={side}");
            Assert.InRange(bg, -2, 2);
            Assert.True(below >= 6, $"no shadow under the pill ({below})");
            Assert.True(side >= 2, $"no shadow beside the pill ({side})");
        }
        finally { win.Close(); }
    }
}
