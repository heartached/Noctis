using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using Noctis.Controls;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;
using static Noctis.Services.ITunesArtworkService;

namespace Noctis.Tests;

/// <summary>
/// Owner 11-26 follow-ups to "slow and choppy": an artist video cached at 1080p before the
/// picker took the hero-size stream is replaced by the smaller one in the background, once; and
/// at most one video backdrop plays at a time, the artist hero yielding to any other.
/// </summary>
public class ArtistVideoShrinkAndArbiterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NoctisTests", "avshrink-" + Guid.NewGuid().ToString("N"));
    private readonly PersistenceService _persistence;
    private DateTime _clock = new(2026, 11, 26, 12, 0, 0, DateTimeKind.Utc);
    private int _lookups, _downloads;
    private ArtistVideoLookup _answer = Found(1920, 1080);
    private int _bytes = 300;
    private const long AppleId = 879496517;
    private static readonly Guid Chase = LibraryService.ComputeArtistId("Chase Atlantic");

    public ArtistVideoShrinkAndArbiterTests()
    {
        _persistence = new PersistenceService(_dir);
        VideoBackdrop.SkipDecoderForTests = true;
        DebugLogger.IsEnabled = true;
    }

    public void Dispose()
    {
        VideoBackdrop.SkipDecoderForTests = false;
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static ArtistVideoLookup Found(int w, int h) => new(ArtistVideoOutcome.Found, AppleId,
        new AnimatedArtworkVariant($"{h}p", $"https://mvod.itunes.apple.com/{w}x{h}.m3u8", w, h, "avc1.64001f", 2_000_000, true));

    private ArtistVideoAutoFetcher Fetcher()
        => new(
            (_, _, _, _) => { _lookups++; return Task.FromResult(_answer); },
            async (_, part, ct) => { _downloads++; await File.WriteAllBytesAsync(part, new byte[_bytes], ct); return true; },
            new AnimatedArtworkCache(_persistence, Path.Combine(_dir, ArtistVideoAutoFetcher.BlockListFile), () => _clock),
            Path.Combine(_dir, ArtistVideoAutoFetcher.MissLedgerFile),
            Path.Combine(_dir, ArtistVideoAutoFetcher.IndexFile),
            new PoliteGate(TimeSpan.Zero, () => _clock, (_, _) => Task.CompletedTask),
            new SemaphoreSlim(1, 1),
            () => _clock,
            Path.Combine(_dir, ArtistVideoAutoFetcher.ShrinkLedgerFile));

    /// <summary>A 1080p video in the cache, as saved before the picker change (300 bytes).</summary>
    private async Task<(ArtistVideoAutoFetcher Fetcher, string Path)> OldBigVideoAsync()
    {
        var fetcher = Fetcher();
        var path = (await fetcher.FetchAsync(Chase, "Chase Atlantic", long.MaxValue, CancellationToken.None))!;
        Assert.Equal(300, new FileInfo(path).Length);
        _answer = Found(1024, 576); // what the picker takes now
        _bytes = 100;
        _lookups = _downloads = 0;
        return (fetcher, path);
    }

    [Fact]
    public async Task AnOld1080pVideo_IsReplacedByTheSmallerStream_Once_AndLogged()
    {
        var (fetcher, path) = await OldBigVideoAsync();
        DebugLogger.Clear();

        Assert.True(await fetcher.ShrinkAsync(Chase, "Chase Atlantic", 1920, 1080, long.MaxValue, CancellationToken.None));
        Assert.Equal(100, new FileInfo(path).Length);
        Assert.False(File.Exists(ArtistVideoAutoFetcher.NextPathFor(path)));
        Assert.Empty(Directory.GetFiles(fetcher.Cache.Folder, "*.part"));
        var log = Assert.Single(DebugLogger.GetEntries(), e => e.Action == "ArtistVideo.Shrink");
        Assert.Equal($"artist_{AppleId}: 1920x1080 -> 1024x576", log.Metadata);
        Assert.Equal(_clock, File.GetLastWriteTimeUtc(path)); // counts as just used

        // Never again: not this session, not the next (remembered on disk).
        Assert.False(await fetcher.ShrinkAsync(Chase, "Chase Atlantic", 1920, 1080, long.MaxValue, CancellationToken.None));
        Assert.False(await Fetcher().ShrinkAsync(Chase, "Chase Atlantic", 1920, 1080, long.MaxValue, CancellationToken.None));
        Assert.Equal((1, 1), (_lookups, _downloads));
    }

    [Fact]
    public async Task WhileTheOldOneIsPlaying_ItKeepsPlaying_AndTheNewOneTakesOverAtTheNextStart()
    {
        var (fetcher, path) = await OldBigVideoAsync();
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) // LibVLC holding it
        {
            Assert.True(await fetcher.ShrinkAsync(Chase, "Chase Atlantic", 1920, 1080, long.MaxValue, CancellationToken.None));
            Assert.Equal(300, new FileInfo(path).Length);
            Assert.True(File.Exists(ArtistVideoAutoFetcher.NextPathFor(path)));
        }
        // The next visit's start: the smaller one is swapped in before the hero opens it.
        Assert.Equal(path, fetcher.CachedPath(Chase));
        Assert.Equal(100, new FileInfo(path).Length);
        Assert.False(File.Exists(ArtistVideoAutoFetcher.NextPathFor(path)));
    }

    [Fact]
    public async Task Offline_KeepsTheOldFile_AndAsksAgainNextTime()
    {
        var (fetcher, path) = await OldBigVideoAsync();
        _answer = new ArtistVideoLookup(ArtistVideoOutcome.Failed, AppleId);
        Assert.False(await fetcher.ShrinkAsync(Chase, "Chase Atlantic", 1920, 1080, long.MaxValue, CancellationToken.None));
        Assert.Equal(300, new FileInfo(path).Length);
        _answer = Found(1024, 576);
        Assert.True(await fetcher.ShrinkAsync(Chase, "Chase Atlantic", 1920, 1080, long.MaxValue, CancellationToken.None));
        Assert.Equal((2, 1), (_lookups, _downloads));
    }

    [Fact]
    public async Task NothingSmallerOnApple_IsNotFetchedEveryTime()
    {
        var (fetcher, path) = await OldBigVideoAsync();
        _answer = Found(1920, 1080); // the smallest stream that fills the hero is still 1080p
        Assert.False(await fetcher.ShrinkAsync(Chase, "Chase Atlantic", 1920, 1080, long.MaxValue, CancellationToken.None));
        Assert.False(await fetcher.ShrinkAsync(Chase, "Chase Atlantic", 1920, 1080, long.MaxValue, CancellationToken.None));
        Assert.Equal((1, 0), (_lookups, _downloads));
        Assert.Equal(300, new FileInfo(path).Length);
    }

    [Fact]
    public async Task HeroSizedOrRemovedVideos_AreLeftAlone()
    {
        var (fetcher, _) = await OldBigVideoAsync();
        Assert.False(await fetcher.ShrinkAsync(Chase, "Chase Atlantic", 1024, 576, long.MaxValue, CancellationToken.None));
        fetcher.Cache.Block(Chase);
        Assert.False(await fetcher.ShrinkAsync(Chase, "Chase Atlantic", 1920, 1080, long.MaxValue, CancellationToken.None));
        Assert.Equal(0, _lookups);
    }

    [AvaloniaFact]
    public async Task TheArtistPage_ShrinksWhenItsHeroReportsA1080pClip_OnlyWithTheSettingsOn()
    {
        var (fetcher, path) = await OldBigVideoAsync();
        await _persistence.SaveSettingsAsync(new AppSettings { EnableAnimatedCovers = true, ArtistVideos = true });
        var lib = new FakeLibraryService();
        var settings = new SettingsViewModel(_persistence, lib, new FakeHistoryLog());
        await settings.LoadAsync();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, _persistence, new FakeAnimatedCoverService());
        var page = new ArtistDetailViewModel("Chase Atlantic", lib, player, settings: settings) { ArtistVideoFetcherProvider = () => fetcher };

        page.OnArtistVideoClipSize(1024, 576);
        await page.ArtistVideoShrinkTask;
        Assert.Equal(0, _lookups);

        settings.ArtistVideos = false;
        page.OnArtistVideoClipSize(1920, 1080);
        await page.ArtistVideoShrinkTask;
        Assert.Equal(0, _lookups);

        settings.ArtistVideos = true;
        page.OnArtistVideoClipSize(1920, 1080);
        await page.ArtistVideoShrinkTask;
        Assert.Equal(100, new FileInfo(path).Length);
    }

    // ── One playing video backdrop ──

    private string Clip(string name)
    {
        Directory.CreateDirectory(_dir);
        var file = Path.Combine(_dir, name);
        File.WriteAllBytes(file, new byte[10]);
        return file;
    }

    [AvaloniaFact]
    public void AnotherVideo_PausesTheHero_AndItsEndResumesIt()
    {
        var hero = new VideoBackdrop { Priority = VideoBackdrop.HeroPriority };
        var panel = new VideoBackdrop(); // the lyrics videos: default priority
        var win = new Window { Content = new StackPanel { Children = { hero, panel } } };
        win.Show();
        var playing = VideoBackdrop.PlayingCount;

        hero.Source = Clip("hero.mp4");
        Assert.Equal(playing + 1, VideoBackdrop.PlayingCount);
        Assert.False(hero.IsHeld);

        panel.Source = Clip("panel.mp4");
        Assert.True(hero.IsHeld);
        Assert.True(hero.IsYielding);
        Assert.False(panel.IsHeld);
        Assert.Equal(playing + 1, VideoBackdrop.PlayingCount); // the panel's only

        panel.Source = null; // the panel closes
        Assert.False(hero.IsHeld);
        Assert.Equal(playing + 1, VideoBackdrop.PlayingCount);

        // Gone from the window altogether works the same way.
        panel.Source = Clip("panel.mp4");
        Assert.True(hero.IsHeld);
        ((StackPanel)win.Content!).Children.Remove(panel);
        Assert.False(hero.IsHeld);

        // The hero never holds a lyrics video back.
        var lyrics = new VideoBackdrop();
        ((StackPanel)win.Content!).Children.Add(lyrics);
        lyrics.Source = Clip("lyrics.mp4");
        Assert.False(lyrics.IsHeld);
        Assert.True(hero.IsHeld);
        win.Close();
        Assert.Equal(playing, VideoBackdrop.PlayingCount);
    }

    [AvaloniaFact]
    public void TheArtistHero_IsTheLowestPriority()
    {
        var app = Avalonia.Application.Current!;
        if (!app.Resources.TryGetResource("HeartFillIcon", null, out _))
        {
            app.Resources["InterSemiBold"] = Avalonia.Media.FontFamily.Default;
            app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml") });
            app.Styles.Add(new StyleInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml") });
        }
        var lib = new FakeLibraryService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, new TestPersistenceService(), new FakeAnimatedCoverService());
        var view = new ArtistDetailView { DataContext = new ArtistDetailViewModel("Chase Atlantic", lib, player) };
        Assert.Equal(VideoBackdrop.HeroPriority, view.FindControl<VideoBackdrop>("HeroVideo")!.Priority);
        Assert.True(VideoBackdrop.HeroPriority < new VideoBackdrop().Priority);
    }
}
