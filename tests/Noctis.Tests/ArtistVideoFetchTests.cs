using Avalonia;
using Avalonia.Controls;
using Rectangle = Avalonia.Controls.Shapes.Rectangle;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
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
/// Settings › Artwork › Artist videos: the cache (keyed by Apple artist id, in the album loops'
/// size-capped folder), the miss memory and the block list, the two switches, the artist page's
/// state and the hero's layers. No network: the lookup and download are faked.
/// </summary>
public class ArtistVideoFetchTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NoctisTests", "artistvideo-" + Guid.NewGuid().ToString("N"));
    private readonly PersistenceService _persistence;
    private DateTime _clock = new(2026, 11, 26, 12, 0, 0, DateTimeKind.Utc);
    private readonly List<TimeSpan> _waits = new();
    private int _lookups, _downloads;
    private Func<string, long, ArtistVideoLookup> _answer = (_, _) => Found(879496517);
    private int _bytes = 100;

    public ArtistVideoFetchTests() => _persistence = new PersistenceService(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static ArtistVideoLookup Found(long id) => new(ArtistVideoOutcome.Found, id,
        new AnimatedArtworkVariant("1080p", "https://mvod.itunes.apple.com/w.m3u8", 1920, 1080, "avc1.640028", 4_223_697, true));

    private ArtistVideoAutoFetcher Fetcher(PoliteGate? gate = null, SemaphoreSlim? one = null)
        => new(
            (name, known, g, ct) => { _lookups++; return Task.FromResult(_answer(name, known)); },
            async (_, part, ct) => { _downloads++; await File.WriteAllBytesAsync(part, new byte[_bytes], ct); return true; },
            new AnimatedArtworkCache(_persistence, Path.Combine(_dir, ArtistVideoAutoFetcher.BlockListFile), () => _clock),
            Path.Combine(_dir, ArtistVideoAutoFetcher.MissLedgerFile),
            Path.Combine(_dir, ArtistVideoAutoFetcher.IndexFile),
            gate ?? new PoliteGate(AnimatedArtworkAutoFetcher.RequestInterval, () => _clock, (t, _) => { _waits.Add(t); _clock += t; return Task.CompletedTask; }),
            one ?? new SemaphoreSlim(1, 1),
            () => _clock);

    private static readonly Guid Chase = LibraryService.ComputeArtistId("Chase Atlantic");

    // ── Cache ──

    [Fact]
    public async Task Found_LandsInTheAlbumLoopCache_KeyedByAppleArtistId_AndARevisitAsksNothing()
    {
        var fetcher = Fetcher();
        var path = await fetcher.FetchAsync(Chase, "Chase Atlantic", long.MaxValue, CancellationToken.None);

        var cacheFolder = new AnimatedArtworkCache(_persistence, null).Folder;
        Assert.Equal(Path.Combine(cacheFolder, "artist_879496517.mp4"), path);
        Assert.True(File.Exists(path));
        Assert.Empty(Directory.GetFiles(cacheFolder, "*.part"));
        Assert.Equal(879496517, fetcher.KnownAppleId(Chase));

        // A new session (the index is on disk): the cached file, no lookup, no download.
        var again = Fetcher();
        Assert.Equal(path, await again.FetchAsync(Chase, "Chase Atlantic", long.MaxValue, CancellationToken.None));
        Assert.Equal((1, 1), (_lookups, _downloads));
    }

    [Fact]
    public async Task AnEvictedVideo_IsFetchedAgain_WithTheRememberedIdAndNoSearch()
    {
        var fetcher = Fetcher();
        var path = (await fetcher.FetchAsync(Chase, "Chase Atlantic", long.MaxValue, CancellationToken.None))!;
        File.Delete(path);

        long knownSeen = -1;
        _answer = (_, known) => { knownSeen = known; return Found(879496517); };
        Assert.Equal(path, await fetcher.FetchAsync(Chase, "Chase Atlantic", long.MaxValue, CancellationToken.None));
        Assert.Equal(879496517, knownSeen);
    }

    [Fact]
    public async Task SharesTheAlbumLoopsCap_TheLeastRecentlyOpenedGoesFirst()
    {
        var cache = new AnimatedArtworkCache(_persistence, null, () => _clock);
        Directory.CreateDirectory(cache.Folder);
        var albumLoop = cache.PathFor(Guid.NewGuid());
        File.WriteAllBytes(albumLoop, new byte[100]);
        File.SetLastWriteTimeUtc(albumLoop, _clock - TimeSpan.FromDays(3));

        _clock += TimeSpan.FromMinutes(1);
        // Cap 150 bytes: the album loop (100, older) + the artist video (100) do not fit.
        var path = await Fetcher().FetchAsync(Chase, "Chase Atlantic", capBytes: 150, CancellationToken.None);

        Assert.True(File.Exists(path));
        Assert.False(File.Exists(albumLoop));
        Assert.Equal(100, cache.SizeBytes());

        // Settings › Clear empties artist videos too.
        cache.Clear();
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task ACapSmallerThanTheVideo_LeavesNothingBehind()
    {
        Assert.Null(await Fetcher().FetchAsync(Chase, "Chase Atlantic", capBytes: 10, CancellationToken.None));
        Assert.Equal(0, new AnimatedArtworkCache(_persistence, null).SizeBytes());
    }

    // ── Misses and blocks ──

    [Theory]
    [InlineData(ArtistVideoOutcome.NoArtist)]
    [InlineData(ArtistVideoOutcome.NoVideo)]
    public async Task AMiss_IsNotAskedAgainFor14Days(ArtistVideoOutcome miss)
    {
        _answer = (_, _) => new ArtistVideoLookup(miss);
        var fetcher = Fetcher();
        Assert.Null(await fetcher.FetchAsync(Chase, "Chase Atlantic", long.MaxValue, CancellationToken.None));
        Assert.Null(await fetcher.FetchAsync(Chase, "Chase Atlantic", long.MaxValue, CancellationToken.None));
        // Remembered on disk, too.
        Assert.Null(await Fetcher().FetchAsync(Chase, "Chase Atlantic", long.MaxValue, CancellationToken.None));
        Assert.Equal(1, _lookups);

        _clock += AnimatedArtworkAutoFetcher.MissLifetime + TimeSpan.FromDays(1);
        _answer = (_, _) => Found(879496517);
        Assert.NotNull(await fetcher.FetchAsync(Chase, "Chase Atlantic", long.MaxValue, CancellationToken.None));
        Assert.Equal(2, _lookups);
    }

    [Fact]
    public async Task Offline_IsNotRemembered()
    {
        _answer = (_, _) => new ArtistVideoLookup(ArtistVideoOutcome.Failed);
        var fetcher = Fetcher();
        await fetcher.FetchAsync(Chase, "Chase Atlantic", long.MaxValue, CancellationToken.None);
        await fetcher.FetchAsync(Chase, "Chase Atlantic", long.MaxValue, CancellationToken.None);
        Assert.Equal(2, _lookups);
    }

    [Fact]
    public async Task Remove_DeletesTheFile_AndTheArtistIsNeverFetchedAgain()
    {
        var fetcher = Fetcher();
        var path = (await fetcher.FetchAsync(Chase, "Chase Atlantic", long.MaxValue, CancellationToken.None))!;

        await fetcher.RemoveAsync(Chase);

        Assert.False(File.Exists(path));
        Assert.True(fetcher.Cache.IsBlocked(Chase));
        Assert.Null(fetcher.CachedPath(Chase));
        // A new session reads the block list back.
        Assert.Null(await Fetcher().FetchAsync(Chase, "Chase Atlantic", long.MaxValue, CancellationToken.None));
        Assert.Equal(1, _lookups);
        // Another artist is unaffected.
        _answer = (_, _) => Found(1);
        Assert.NotNull(await fetcher.FetchAsync(LibraryService.ComputeArtistId("Dua Lipa"), "Dua Lipa", long.MaxValue, CancellationToken.None));
    }

    [Fact]
    public async Task Lookups_QueueOneAtATime_AndWaitTheSharedSpacing()
    {
        var one = new SemaphoreSlim(1, 1);
        await one.WaitAsync(); // the album page's fetch holds the turn
        var gate = new PoliteGate(AnimatedArtworkAutoFetcher.RequestInterval, () => _clock,
            (t, _) => { _waits.Add(t); _clock += t; return Task.CompletedTask; });
        await gate.WaitTurnAsync(CancellationToken.None); // ... and just asked Apple something
        var fetcher = Fetcher(gate, one);
        var pending = fetcher.FetchAsync(Chase, "Chase Atlantic", long.MaxValue, CancellationToken.None);
        await Task.Delay(50);
        Assert.False(pending.IsCompleted);
        Assert.Equal(0, _lookups);

        one.Release();
        Assert.NotNull(await pending);
        // The download waited its turn at the gate after the lookup.
        Assert.Equal(new[] { AnimatedArtworkAutoFetcher.RequestInterval }, _waits);
    }

    // ── Settings ──

    [Fact]
    public async Task ArtistVideos_IsOffByDefault_AndSavedAndLoaded()
    {
        Assert.False(new AppSettings().ArtistVideos);

        var lib = new FakeLibraryService();
        var vm = new SettingsViewModel(_persistence, lib, new FakeHistoryLog());
        await vm.LoadAsync();
        Assert.False(vm.ArtistVideos);
        vm.ArtistVideos = true;
        await vm.SaveAsync();

        var reloaded = new SettingsViewModel(_persistence, lib, new FakeHistoryLog());
        await reloaded.LoadAsync();
        Assert.True(reloaded.ArtistVideos);
    }

    // ── Artist page ──

    private async Task<(ArtistDetailViewModel Page, ArtistVideoAutoFetcher Fetcher)> OpenAsync(bool animated, bool artistVideos)
    {
        await _persistence.SaveSettingsAsync(new AppSettings { EnableAnimatedCovers = animated, ArtistVideos = artistVideos });
        var lib = new FakeLibraryService();
        var settings = new SettingsViewModel(_persistence, lib, new FakeHistoryLog());
        await settings.LoadAsync();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, _persistence, new FakeAnimatedCoverService());
        var page = new ArtistDetailViewModel("Chase Atlantic", lib, player, settings: settings);
        var fetcher = Fetcher();
        page.ArtistVideoFetcherProvider = () => fetcher;
        Dispatcher.UIThread.RunJobs();
        return (page, fetcher);
    }

    [AvaloniaTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task EitherSwitchOff_NothingIsAsked(bool animated, bool artistVideos)
    {
        var (page, _) = await OpenAsync(animated, artistVideos);
        page.StartArtistVideo();
        await page.ArtistVideoTask;
        Assert.Equal(0, _lookups);
        Assert.False(page.HasArtistVideo);
    }

    [AvaloniaFact]
    public async Task BothOn_ThePageGetsTheVideo_LeavingClearsIt_AndRemoveKeepsItGone()
    {
        var (page, fetcher) = await OpenAsync(true, true);
        Assert.False(page.HasArtistVideo);
        Assert.False(page.CanRemoveArtistVideo);

        page.StartArtistVideo();
        await page.ArtistVideoTask;
        var path = fetcher.Cache.ArtistVideoPathFor(879496517);
        Assert.Equal(path, page.ArtistVideoPath);
        Assert.True(page.HasArtistVideo);
        Assert.True(page.CanRemoveArtistVideo);

        // Navigated away: the hero lets go of it; back: from the cache, no new lookup.
        page.IsActive = false;
        Assert.Null(page.ArtistVideoPath);
        page.IsActive = true;
        await page.ArtistVideoTask;
        Assert.Equal(path, page.ArtistVideoPath);
        Assert.Equal(1, _lookups);

        await page.RemoveArtistVideoCommand.ExecuteAsync(null);
        Assert.Null(page.ArtistVideoPath);
        Assert.False(page.CanRemoveArtistVideo);
        Assert.False(File.Exists(path));
        Assert.True(fetcher.Cache.IsBlocked(page.Artist.Id));

        page.StartArtistVideo();
        await page.ArtistVideoTask;
        Assert.Null(page.ArtistVideoPath);
        Assert.Equal(1, _lookups);
    }

    // Owner 11-27: with the video already cached, the page showed the still portrait on the
    // right for a split second before the video's first frame.
    [AvaloniaFact]
    public async Task ACachedVideo_HoldsThePortraitBack_FromTheMomentThePageOpens_UntilTheFirstFrame()
    {
        var (page, fetcher) = await OpenAsync(true, true);

        // Not cached yet: the lookup and download can take long, the portrait shows meanwhile.
        page.StartArtistVideo();
        Assert.False(page.ArtistVideoPending);
        await page.ArtistVideoTask;
        Assert.True(page.HasArtistVideo);
        Assert.False(page.ArtistVideoPending);
        Assert.True(fetcher.HasCachedVideo(page.Artist.Id));

        // Back on the page with the file in the cache: held back at once, before the path is in.
        page.IsActive = false;
        Assert.False(page.ArtistVideoPending);
        page.IsActive = true;
        Assert.True(page.ArtistVideoPending);
        Assert.Null(page.ArtistVideoPath);
        await page.ArtistVideoTask;
        Assert.True(page.HasArtistVideo);
        Assert.True(page.ArtistVideoPending);

        // The first frame is up (the stills are faded out now).
        page.OnArtistVideoShown();
        Assert.False(page.ArtistVideoPending);

        // Leaving while it waits lets the portrait back too.
        page.IsActive = false;
        page.IsActive = true;
        Assert.True(page.ArtistVideoPending);
        page.IsActive = false;
        Assert.False(page.ArtistVideoPending);

        // Removed: nothing is waited for.
        page.IsActive = true;
        await page.ArtistVideoTask;
        await page.RemoveArtistVideoCommand.ExecuteAsync(null);
        page.StartArtistVideo();
        Assert.False(page.ArtistVideoPending);
    }

    [AvaloniaFact]
    public async Task ACachedVideo_ThatNeverShowsAFrame_GivesThePortraitBack()
    {
        var was = ArtistDetailViewModel.ArtistVideoPendingLimit;
        ArtistDetailViewModel.ArtistVideoPendingLimit = TimeSpan.FromMilliseconds(50);
        try
        {
            var (page, _) = await OpenAsync(true, true);
            page.StartArtistVideo();
            await page.ArtistVideoTask;
            page.IsActive = false;
            page.IsActive = true;
            Assert.True(page.ArtistVideoPending);
            await page.ArtistVideoTask;
            for (var i = 0; i < 100 && page.ArtistVideoPending; i++)
            {
                await Task.Delay(20);
                Dispatcher.UIThread.RunJobs();
            }
            Assert.False(page.ArtistVideoPending);
            Assert.True(page.HasArtistVideo);
        }
        finally { ArtistDetailViewModel.ArtistVideoPendingLimit = was; }
    }

    [AvaloniaFact]
    public async Task TurningTheSwitchOff_WhileThePageIsOpen_StopsTheVideo()
    {
        var (page, _) = await OpenAsync(true, true);
        page.StartArtistVideo();
        await page.ArtistVideoTask;
        Assert.True(page.HasArtistVideo);

        var settings = (SettingsViewModel)typeof(ArtistDetailViewModel)
            .GetField("_settings", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(page)!;
        settings.ArtistVideos = false;
        Dispatcher.UIThread.RunJobs();
        Assert.False(page.HasArtistVideo);
    }

    // ── Hero ──

    private static void EnsureAppStyles()
    {
        var app = Application.Current!;
        if (app.Resources.TryGetResource("HeartFillIcon", null, out _)) return;
        app.Resources["InterSemiBold"] = Avalonia.Media.FontFamily.Default;
        app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis/"))
        {
            Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml")
        });
        app.Styles.Add(new StyleInclude(new Uri("avares://Noctis/"))
        {
            Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml")
        });
    }

    // The value an Opacity transition is heading for (the animated value lags behind the clock).
    private static double Target(Visual v) => v.GetBaseValue(Visual.OpacityProperty).GetValueOrDefault(1.0);

    [AvaloniaFact]
    public void Hero_WithoutAVideo_IsTodaysStillHero_AndWithOne_CrossFadesOnTheFirstFrame()
    {
        EnsureAppStyles();
        var lib = new FakeLibraryService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, new TestPersistenceService(), new FakeAnimatedCoverService());
        var vm = new ArtistDetailViewModel("Chase Atlantic", lib, player);
        var view = new ArtistDetailView { DataContext = vm };
        var win = new Window { Width = 1280, Height = 900, Content = view };
        win.Show();
        Dispatcher.UIThread.RunJobs();

        var video = view.FindControl<VideoBackdrop>("HeroVideo")!;
        var stills = view.FindControl<Panel>("HeroStills")!;
        var hero = view.FindControl<Grid>("Hero")!;
        // Under the darkening gradients and the bottom fade: after the stills, before the text.
        var layers = hero.Children.ToList();
        Assert.True(layers.IndexOf(video) > layers.IndexOf(stills));
        Assert.True(layers.OfType<Rectangle>().All(r => layers.IndexOf(r) > layers.IndexOf(video)));
        Assert.Equal(Avalonia.Media.Stretch.UniformToFill, video.Stretch);
        Assert.False(video.IsHitTestVisible);

        // No video: nothing decodes, the still art is as before.
        Assert.False(video.IsVisible);
        Assert.False(video.IsActive);
        Assert.Null(video.Source);
        Assert.Equal(1, Target(stills));

        // A video (a path that does not exist, so nothing actually decodes here).
        var clip = Path.Combine(Path.GetTempPath(), "NoctisTests", "no-such-artist-video.mp4");
        vm.ArtistVideoPath = clip;
        Dispatcher.UIThread.RunJobs();
        Assert.True(video.IsVisible);
        Assert.True(video.IsActive);
        Assert.Equal(clip, video.Source);
        Assert.False(video.HasFrame);
        // The stills stay up until the first frame.
        Assert.Equal(1, Target(stills));
        Assert.Equal(0, Target(video));
        // A cached video about to play: the sharp portrait is held back, the glow stays.
        var portrait = stills.Children.OfType<CachedImage>().Last();
        vm.ImagePath = Path.Combine(Path.GetTempPath(), "NoctisTests", "no-such-portrait.jpg");
        Dispatcher.UIThread.RunJobs();
        Assert.True(portrait.IsVisible);
        vm.ArtistVideoPending = true;
        Dispatcher.UIThread.RunJobs();
        Assert.False(portrait.IsVisible);
        Assert.True(stills.Children.OfType<CachedImage>().First().IsVisible);

        view.ApplyHeroVideoFrame(true);
        Assert.False(vm.ArtistVideoPending);
        Assert.Equal(0, Target(stills));
        Assert.Equal(1, Target(video));

        // Window in the background: paused; back in front with the hero on screen: plays.
        view.OnHeroWindowActiveChanged(false);
        Assert.True(video.IsPaused);
        view.OnHeroWindowActiveChanged(true);
        Assert.False(video.IsPaused);

        // Gone (navigation, removal): the stills come back.
        vm.ArtistVideoPath = null;
        Dispatcher.UIThread.RunJobs();
        view.ApplyHeroVideoFrame(video.HasFrame);
        Assert.False(video.IsVisible);
        Assert.Equal(1, Target(stills));
        win.Close();
    }

    [AvaloniaFact]
    public void Hero_ScrolledOutOfView_PausesTheVideo()
    {
        EnsureAppStyles();
        var lib = new FakeLibraryService();
        // Enough songs and releases for the page to scroll past its 400 px hero.
        for (var a = 0; a < 6; a++)
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
        var win = new Window { Width = 1280, Height = 500, Content = view };
        win.Show();
        Dispatcher.UIThread.RunJobs();
        view.OnHeroWindowActiveChanged(true);

        var video = view.FindControl<VideoBackdrop>("HeroVideo")!;
        var sv = view.FindControl<ScrollViewer>("PageScrollViewer")!;
        Assert.False(video.IsPaused);
        sv.Offset = new Vector(0, 450);
        Dispatcher.UIThread.RunJobs();
        view.UpdateHeroVideoPause();
        Assert.True(sv.Offset.Y >= 400, $"offset {sv.Offset.Y}");
        Assert.True(video.IsPaused);
        sv.Offset = new Vector(0, 0);
        Dispatcher.UIThread.RunJobs();
        view.UpdateHeroVideoPause();
        Assert.False(video.IsPaused);
        win.Close();
    }
}
