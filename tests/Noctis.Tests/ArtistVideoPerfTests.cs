using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Controls;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;
using static Noctis.Services.ITunesArtworkService;

namespace Noctis.Tests;

/// <summary>
/// Owner 11-26 "the app feels slow and choppy" with Settings › Artwork › Artist videos on.
/// Pins: at most one player, none off the artist page (PageDeck navigation, Back), none under
/// the Settings sheet, the fetch cancelled when the page is left, a frame repaints the video
/// alone (no layout, no OpacityMask layer over it), and the slow-machine stop.
/// The numbers are in <see cref="ArtistVideoPerfProbeTests"/>.
/// </summary>
public class ArtistVideoPerfTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NoctisTests", "avperf-" + Guid.NewGuid().ToString("N"));
    private readonly PersistenceService _persistence;
    private DateTime _clock = new(2026, 11, 26, 12, 0, 0, DateTimeKind.Utc);
    private Func<string, CancellationToken, Task<bool>>? _download;

    public ArtistVideoPerfTests()
    {
        _persistence = new PersistenceService(_dir);
        VideoBackdrop.SkipDecoderForTests = true;
    }

    public void Dispose()
    {
        VideoBackdrop.SkipDecoderForTests = false;
        VideoBackdrop.ClearKnownSlowForTests();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private ArtistVideoAutoFetcher Fetcher()
        => new(
            (name, _, _, _, _) => Task.FromResult(new ArtistVideoLookup(ArtistVideoOutcome.Found, 1000 + Math.Abs(name.GetHashCode() % 1000),
                new AnimatedArtworkVariant("720p", "https://mvod.itunes.apple.com/w.m3u8", 1280, 720, "avc1.64001f", 3_000_000, true))),
            async (_, part, ct) =>
            {
                if (_download != null) return await _download(part, ct);
                await File.WriteAllBytesAsync(part, new byte[100], ct);
                return true;
            },
            new AnimatedArtworkCache(_persistence, Path.Combine(_dir, ArtistVideoAutoFetcher.BlockListFile), () => _clock),
            Path.Combine(_dir, ArtistVideoAutoFetcher.MissLedgerFile),
            Path.Combine(_dir, ArtistVideoAutoFetcher.IndexFile),
            new PoliteGate(TimeSpan.Zero, () => _clock, (_, _) => Task.CompletedTask),
            new SemaphoreSlim(1, 1),
            () => _clock);

    private static void EnsureAppStyles()
    {
        var app = Application.Current!;
        if (app.Resources.TryGetResource("HeartFillIcon", null, out _)) return;
        app.Resources["InterSemiBold"] = FontFamily.Default;
        app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis/"))
        { Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml") });
        app.Styles.Add(new StyleInclude(new Uri("avares://Noctis/"))
        { Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml") });
    }

    private async Task<SettingsViewModel> SettingsOnAsync(FakeLibraryService lib)
    {
        await _persistence.SaveSettingsAsync(new AppSettings { EnableAnimatedCovers = true, ArtistVideos = true });
        var settings = new SettingsViewModel(_persistence, lib, new FakeHistoryLog());
        await settings.LoadAsync();
        return settings;
    }

    private static async Task Settle(ArtistDetailViewModel? page)
    {
        if (page != null) await page.ArtistVideoTask;
        Dispatcher.UIThread.RunJobs();
    }

    private sealed class OtherPage { }

    /// <summary>MainWindowViewModel.OnCurrentViewChanged's part for artist pages, over a PageDeck
    /// with the app's kind of templates (artist pages recycled, like the XAML DataTemplate).</summary>
    private sealed class Shell
    {
        public readonly PageDeck Deck = new();
        public object? Current;
        public void Show(object page)
        {
            if (Current is ArtistDetailViewModel left && !ReferenceEquals(left, page)) { left.IsActive = false; left.IsCovered = false; }
            Current = page;
            Deck.Page = page;
            if (page is ArtistDetailViewModel shown) shown.IsActive = true;
        }
    }

    [AvaloniaFact]
    public async Task Navigation_KeepsAtMostOnePlayer_AndNoneOffTheArtistPage()
    {
        EnsureAppStyles();
        var lib = new FakeLibraryService();
        var settings = await SettingsOnAsync(lib);
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, _persistence, new FakeAnimatedCoverService());
        var fetcher = Fetcher();
        ArtistDetailViewModel Page(string name)
        {
            var p = new ArtistDetailViewModel(name, lib, player, settings: settings) { ArtistVideoFetcherProvider = () => fetcher };
            p.StartArtistVideo(); // the constructor's start ran before the provider was swapped in
            return p;
        }

        var shell = new Shell();
        shell.Deck.DataTemplates.Add(new FuncDataTemplate<ArtistDetailViewModel>((_, _) => new ArtistDetailView(), supportsRecycling: true));
        shell.Deck.DataTemplates.Add(new FuncDataTemplate<OtherPage>((_, _) => new TextBlock { Text = "album" }));
        var win = new Window { Width = 1280, Height = 900, Content = shell.Deck };
        win.Show();
        var baseline = VideoBackdrop.ActiveCount;

        var a = Page("Artist A");
        shell.Show(a);
        await Settle(a);
        Assert.NotNull(a.ArtistVideoPath);
        Assert.Equal(baseline + 1, VideoBackdrop.ActiveCount);

        // Similar artist B over A (A kept in history; the same view recycled): still one.
        var b = Page("Artist B");
        shell.Show(b);
        await Settle(b);
        Assert.Null(a.ArtistVideoPath);
        Assert.NotNull(b.ArtistVideoPath);
        Assert.Equal(baseline + 1, VideoBackdrop.ActiveCount);

        // An album page: none.
        shell.Show(new OtherPage());
        await Settle(null);
        Assert.Null(b.ArtistVideoPath);
        Assert.Equal(baseline, VideoBackdrop.ActiveCount);

        // Back to B, back to A: one each time, never a second one.
        shell.Show(b);
        await Settle(b);
        Assert.Equal(baseline + 1, VideoBackdrop.ActiveCount);
        shell.Show(a);
        await Settle(a);
        Assert.Equal(baseline + 1, VideoBackdrop.ActiveCount);
        Assert.Null(b.ArtistVideoPath);

        // The Settings sheet over A: the player holds still (paused, no frames, no blur retakes).
        var view = (ArtistDetailView)shell.Deck.ShownView!;
        var hero = view.FindControl<VideoBackdrop>("HeroVideo")!;
        view.OnHeroWindowActiveChanged(true);
        Assert.False(hero.IsPaused);
        a.IsCovered = true;
        Assert.True(hero.IsPaused);
        a.IsCovered = false;
        Assert.False(hero.IsPaused);

        win.Close();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(baseline, VideoBackdrop.ActiveCount);
    }

    [AvaloniaFact]
    public void MainWindowViewModel_CoversTheShownArtistPage_WhileSettingsIsOpen()
    {
        // The two hooks (OnIsSettingsModalOpenChanged, OnCurrentViewChanged) as source: the shell
        // needs the whole app to construct.
        var src = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Noctis", "ViewModels", "MainWindowViewModel.cs"));
        Assert.Contains("if (CurrentView is ArtistDetailViewModel coveredArtist) coveredArtist.IsCovered = value;", src);
        Assert.Contains("artistPage.IsCovered = IsSettingsModalOpen;", src);
        Assert.Contains("if (oldValue is ArtistDetailViewModel uncoveredArtist) uncoveredArtist.IsCovered = false;", src);
    }

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "Noctis.sln"))) d = d.Parent;
        return d!.FullName;
    }

    [AvaloniaFact]
    public async Task LeavingThePage_CancelsTheFetch_AndNothingLandsAfter()
    {
        var lib = new FakeLibraryService();
        var settings = await SettingsOnAsync(lib);
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, _persistence, new FakeAnimatedCoverService());
        var started = new TaskCompletionSource();
        var cancelled = false;
        _download = async (part, ct) =>
        {
            started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { cancelled = true; throw; }
            return true;
        };
        var page = new ArtistDetailViewModel("Artist C", lib, player, settings: settings);
        var fetcher = Fetcher();
        page.ArtistVideoFetcherProvider = () => fetcher;
        page.StartArtistVideo();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        page.IsActive = false; // navigated on before the download finished
        await page.ArtistVideoTask.WaitAsync(TimeSpan.FromSeconds(5));
        Dispatcher.UIThread.RunJobs();
        Assert.True(cancelled);
        Assert.Null(page.ArtistVideoPath);
        Assert.Empty(Directory.Exists(fetcher.Cache.Folder) ? Directory.GetFiles(fetcher.Cache.Folder) : Array.Empty<string>());
    }

    private static WriteableBitmap Frame() =>
        new(new PixelSize(64, 36), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);

    private static (ArtistDetailView View, ArtistDetailViewModel Vm, Window Win) Mount(double height = 900, int albums = 0)
    {
        EnsureAppStyles();
        var lib = new FakeLibraryService();
        for (var a = 0; a < albums; a++)
        {
            var id = Guid.NewGuid();
            var album = new Album { Id = id, Name = $"Album {a}", Artist = "Chase Atlantic", Year = 2010 + a, Tracks = new List<Track>() };
            for (var i = 1; i <= 8; i++)
                album.Tracks.Add(new Track
                {
                    Id = Guid.NewGuid(), Title = $"Song {a}.{i}", Artist = "Chase Atlantic", AlbumArtist = "Chase Atlantic",
                    Album = album.Name, AlbumId = id, TrackNumber = i, Year = album.Year, Duration = TimeSpan.FromMinutes(3),
                });
            album.TrackCount = 8;
            ((List<Album>)lib.Albums).Add(album);
        }
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, new TestPersistenceService(), new FakeAnimatedCoverService());
        var vm = new ArtistDetailViewModel("Chase Atlantic", lib, player);
        var view = new ArtistDetailView { DataContext = vm };
        var win = new Window { Width = 1280, Height = height, Content = view };
        win.Show();
        Dispatcher.UIThread.RunJobs();
        return (view, vm, win);
    }

    [AvaloniaFact]
    public void AVideoFrame_RepaintsTheVideoAlone_NoLayoutPass()
    {
        var (view, vm, win) = Mount();
        var hero = view.FindControl<VideoBackdrop>("HeroVideo")!;
        var stills = view.FindControl<Panel>("HeroStills")!;
        vm.ArtistVideoPath = Path.Combine(Path.GetTempPath(), "NoctisTests", "no-such-artist-video.mp4");
        Dispatcher.UIThread.RunJobs();
        hero.PresentForTests(Frame()); // the first frame cross-fades the stills out
        Dispatcher.UIThread.RunJobs();
        win.UpdateLayout();

        var otherChanges = new List<string>();
        void OnChange(object? s, AvaloniaPropertyChangedEventArgs e) => otherChanges.Add($"{s?.GetType().Name}.{e.Property.Name}");
        var heroGrid = view.FindControl<Grid>("Hero")!;
        stills.PropertyChanged += OnChange;
        heroGrid.PropertyChanged += OnChange;
        // Collapsed parts of the page are never measured; only what a frame invalidates counts.
        HashSet<Layoutable> NeedingLayout() => view.GetSelfAndVisualDescendants().OfType<Layoutable>()
            .Where(c => !c.IsMeasureValid || !c.IsArrangeValid).ToHashSet();
        var idle = NeedingLayout();
        var before = hero.FramesPresented;
        for (var i = 0; i < 10; i++)
        {
            hero.PresentForTests(Frame());
            Dispatcher.UIThread.RunJobs();
            // Nothing asks for a layout pass: no measure or arrange made invalid anywhere.
            var invalid = NeedingLayout();
            invalid.ExceptWith(idle);
            Assert.True(invalid.Count == 0, $"needs layout after a frame: {string.Join(", ", invalid.Select(c => c.GetType().Name))}");
        }
        Assert.Equal(before + 10, hero.FramesPresented);
        Assert.Empty(otherChanges);
        win.Close();
    }

    [AvaloniaFact]
    public void TheHeroFade_IsAGradient_NotAnOpacityMaskOverTheVideo()
    {
        var (view, _, win) = Mount();
        var heroGrid = view.FindControl<Grid>("Hero")!;
        var video = view.FindControl<VideoBackdrop>("HeroVideo")!;
        var layers = heroGrid.Children.ToList();
        // Nothing drawn over the video makes an offscreen layer per frame.
        foreach (var layer in layers.Skip(layers.IndexOf(video)))
            foreach (var v in layer.GetSelfAndVisualDescendants())
            {
                Assert.Null(v.OpacityMask);
                Assert.Null(v.Effect);
            }
        var fade = view.FindControl<Border>("HeroPageFade")!;
        var brush = Assert.IsType<LinearGradientBrush>(fade.Background);
        Assert.Equal(new[] { 0.4, 0.82, 1.0 }, brush.GradientStops.Select(s => s.Offset));
        win.Close();
    }

    [Fact]
    public void HeroFadeBrush_MatchesTheOldMask_ForOpaqueAndSeeThroughPageColours()
    {
        var opaque = Assert.IsType<LinearGradientBrush>(ArtistDetailView.HeroFadeBrush(new SolidColorBrush(Color.Parse("#252525"))));
        Assert.Equal(new byte[] { 0, 0xB0, 0xFF }, opaque.GradientStops.Select(s => s.Color.A));
        Assert.All(opaque.GradientStops, s => Assert.Equal(Color.FromRgb(0x25, 0x25, 0x25), Color.FromRgb(s.Color.R, s.Color.G, s.Color.B)));
        Assert.Null(ArtistDetailView.HeroAfterFadeBrush(new SolidColorBrush(Color.Parse("#252525"))));
        // A see-through page (Smoke's #99, Liquid Glass's 35% brush): the hero still ends in the
        // opaque colour (owner 11-27: its bottom edge cut across the page under Liquid Glass),
        // and the strip under the hero takes that colour back out.
        foreach (var page in new[] { new SolidColorBrush(Color.Parse("#992A2D38")), new SolidColorBrush(Color.Parse("#252525"), 0.35) })
        {
            var fade = Assert.IsType<LinearGradientBrush>(ArtistDetailView.HeroFadeBrush(page));
            Assert.Equal(new byte[] { 0, 0xB0, 0xFF }, fade.GradientStops.Select(s => s.Color.A));
            Assert.Equal(1, fade.Opacity);
            var after = Assert.IsType<LinearGradientBrush>(ArtistDetailView.HeroAfterFadeBrush(page));
            Assert.Equal(fade.GradientStops[^1].Color, after.GradientStops[0].Color);
            Assert.Equal(0, after.GradientStops[^1].Color.A);
        }
    }

    [AvaloniaFact]
    public void TheHero_PausesWhenLessThanAThirdIsOnScreen()
    {
        var (view, _, win) = Mount(height: 500, albums: 6);
        view.OnHeroWindowActiveChanged(true);
        var video = view.FindControl<VideoBackdrop>("HeroVideo")!;
        var sv = view.FindControl<ScrollViewer>("PageScrollViewer")!;
        sv.Offset = new Vector(0, 250); // 150 of 400 px still showing (37%)
        Dispatcher.UIThread.RunJobs();
        view.UpdateHeroVideoPause();
        Assert.False(video.IsPaused);
        sv.Offset = new Vector(0, 300); // 100 of 400 (25%)
        Dispatcher.UIThread.RunJobs();
        view.UpdateHeroVideoPause();
        Assert.True(video.IsPaused);
        win.Close();
    }

    [AvaloniaFact]
    public void TheHeroVideo_IsLoggedAsArtistVideo_CappedAt30Fps_AndStopsWhenTooSlow()
    {
        var (view, _, win) = Mount();
        var video = view.FindControl<VideoBackdrop>("HeroVideo")!;
        Assert.Equal("ArtistVideo", video.LogName);
        Assert.True(video.StopWhenSlow);
        Assert.Equal(30, video.MaxFps);
        Assert.Equal(VideoBackdrop.MaxLongSide, video.MaxDecodeSide); // 960 px decode
        win.Close();
    }

    [AvaloniaFact]
    public void AClipMarkedTooSlow_IsNotStartedAgain()
    {
        var file = Path.Combine(_dir, "slow.mp4");
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(file, new byte[10]);
        var video = new VideoBackdrop();
        var win = new Window { Content = video };
        win.Show();
        var baseline = VideoBackdrop.ActiveCount;
        video.Source = file;
        Assert.Equal(baseline + 1, VideoBackdrop.ActiveCount);
        video.Source = null;
        Assert.Equal(baseline, VideoBackdrop.ActiveCount);

        VideoBackdrop.MarkSlow(file);
        Assert.True(VideoBackdrop.IsKnownSlow(file));
        video.Source = file;
        Assert.Equal(baseline, VideoBackdrop.ActiveCount);
        win.Close();
    }

    [Fact]
    public void SlowStep_StopsOnlyAfterThreeSlowSecondsInARow()
    {
        const long f = 1000; // ticks per second
        var m = new VideoBackdrop.SlowMeter();
        double? Feed(double fps, double seconds, long from)
        {
            double? hit = null;
            var step = (long)(f / fps);
            for (var t = from; t < from + (long)(seconds * f); t += step)
                hit ??= VideoBackdrop.SlowStep(ref m, t, f);
            return hit;
        }
        // 24 fps for 5 s: fine. Two slow seconds then a good one: the run starts over.
        Assert.Null(Feed(24, 5, 1));
        Assert.Null(Feed(6, 2.2, 5001));
        Assert.Null(Feed(24, 1.2, 7201));
        // Three slow seconds in a row: the rate comes back, once.
        var slow = Feed(6, 4.5, 8401);
        Assert.NotNull(slow);
        Assert.InRange(slow!.Value, 4, 8);
        Assert.Null(Feed(6, 5, 12901));
    }
}
