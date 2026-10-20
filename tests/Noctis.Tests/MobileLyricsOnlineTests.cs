using Noctis.Mobile.Services;
using Noctis.Mobile.ViewModels;
using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The phone's automatic online lookup (the desktop's: a song with no lyrics of its own is
/// looked up online). Before it, such a song only ever showed "No lyrics" on the phone.
/// </summary>
public class MobileLyricsOnlineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private sealed class FakeOnline : IOnlineLyricsSource
    {
        public Func<Track, Task<OnlineLyrics?>> Answer { get; set; } = _ => Task.FromResult<OnlineLyrics?>(null);
        public List<string> Asked { get; } = new();

        public Task<OnlineLyrics?> FindAsync(Track track, CancellationToken ct)
        {
            Asked.Add(track.Title);
            return Answer(track);
        }
    }

    private sealed class FakeLrcLib : ILrcLibService
    {
        public LrcLibResult? Get { get; set; }
        public int Calls { get; private set; }

        public Task<LrcLibResult?> GetLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(Get);
        }

        public Task<List<LrcLibResult>> SearchLyricsAsync(string artist, string trackName, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(new List<LrcLibResult>());
        }
    }

    private const string SyncedLrc = "[00:01.00]first online line\n[00:04.00]second online line";

    private static Track NewTrack(string name) =>
        new() { Id = Guid.NewGuid(), Title = name, Artist = "Artist", FilePath = $"content://x/{name}", Duration = TimeSpan.FromSeconds(60) };

    private (LyricsPageViewModel Vm, NowPlayingViewModel Np, FakeAudioPlayer Player, FakeTrackFiles Files, PersistenceService Persistence)
        Make(IOnlineLyricsSource online)
    {
        var player = new FakeAudioPlayer();
        var persistence = new PersistenceService(_root);
        var np = new NowPlayingViewModel(player, new FakeLibraryService(), persistence, marshal: a => a());
        var files = new FakeTrackFiles();
        var vm = new LyricsPageViewModel(player, np, files, persistence, work => Task.FromResult(work())) { Online = online };
        return (vm, np, player, files, persistence);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(15);
        Assert.True(condition());
    }

    [Fact]
    public async Task NoLyricsOfItsOwn_LooksOnline_AndShowsSyncedLines()
    {
        var online = new FakeOnline { Answer = _ => Task.FromResult<OnlineLyrics?>(new OnlineLyrics(SyncedLrc, false)) };
        var (vm, np, player, _, _) = Make(online);

        np.PlayTracks(new[] { NewTrack("a") }, 0);
        await WaitForAsync(() => vm.HasLyrics);
        player.RaisePositionChanged(TimeSpan.FromSeconds(4.5));
        vm.OnFrame(1000);

        Assert.Equal(new[] { "a" }, online.Asked);
        Assert.True(vm.IsSynced);
        Assert.Equal(LyricsSource.Online, vm.Source);
        Assert.Equal("Lyrics from LRCLIB", vm.SourceCredit);
        Assert.Contains(vm.Lines, l => l.Text == "second online line" && l.IsActive);
        Assert.False(vm.ShowsEmptyState);
    }

    [Fact]
    public async Task OwnSidecar_IsNeverLookedUpOnline()
    {
        var online = new FakeOnline();
        var (vm, np, _, files, _) = Make(online);
        files.Sidecars[".lrc"] = "[00:01.00]local";

        np.PlayTracks(new[] { NewTrack("a") }, 0);
        await WaitForAsync(() => vm.HasLyrics);

        Assert.Empty(online.Asked);
        Assert.Equal(LyricsSource.SidecarLrc, vm.Source);
        Assert.Equal(string.Empty, vm.SourceCredit);
    }

    [Fact]
    public async Task SwitchedOff_IsNotAsked_AndTheEmptyStateSaysWhy()
    {
        var online = new FakeOnline();
        var (vm, np, _, _, persistence) = Make(online);
        await vm.InitializeAsync();
        vm.FindLyricsOnline = false;
        await vm.PendingSave;

        np.PlayTracks(new[] { NewTrack("a") }, 0);
        await WaitForAsync(() => !vm.IsLoading);

        Assert.Empty(online.Asked);
        Assert.Equal("No Lyrics", vm.StatusText);
        Assert.Equal("This song has no lyrics file or lyrics in its tags.", vm.StatusDetail);
        Assert.False((await persistence.LoadSettingsAsync()).LrcLibEnabled);   // the desktop's key
    }

    [Fact]
    public async Task NothingOnline_SaysSoInTheDetail()
    {
        var (vm, np, _, _, _) = Make(new FakeOnline());

        np.PlayTracks(new[] { NewTrack("a") }, 0);
        await WaitForAsync(() => !vm.IsLoading);

        Assert.True(vm.ShowsEmptyState);
        Assert.Equal("No Lyrics", vm.StatusText);
        Assert.Contains("none were found online", vm.StatusDetail);
    }

    [Fact]
    public async Task Instrumental_SaysInstrumental()
    {
        var online = new FakeOnline { Answer = _ => Task.FromResult<OnlineLyrics?>(new OnlineLyrics("", true)) };
        var (vm, np, _, _, _) = Make(online);

        np.PlayTracks(new[] { NewTrack("a") }, 0);
        await WaitForAsync(() => !vm.IsLoading);

        Assert.True(vm.IsInstrumental);
        Assert.Equal("Instrumental", vm.StatusText);
        Assert.True(vm.ShowsEmptyState);
    }

    [Fact]
    public async Task SearchingState_Shows_ThenAStaleAnswerForTheOldSongIsDropped()
    {
        var first = new TaskCompletionSource<OnlineLyrics?>();
        var online = new FakeOnline
        {
            Answer = t => t.Title == "a" ? first.Task : Task.FromResult<OnlineLyrics?>(new OnlineLyrics("[00:01.00]song b", false)),
        };
        var (vm, np, _, _, _) = Make(online);
        var a = NewTrack("a");
        var b = NewTrack("b");

        np.PlayTracks(new[] { a, b }, 0);
        await WaitForAsync(() => vm.IsSearchingOnline);
        Assert.Equal("Searching for Lyrics…", vm.StatusText);
        Assert.True(vm.ShowsEmptyState);

        np.NextCommand.Execute(null);
        await WaitForAsync(() => vm.HasLyrics);
        first.SetResult(new OnlineLyrics("[00:01.00]song a", false));
        await Task.Delay(50);

        Assert.Equal("song b", Assert.Single(vm.Lines).Text);
        Assert.False(vm.IsSearchingOnline);
    }

    [Fact]
    public async Task LrcLibSource_CachesAHit_AndAsksAgainAfterAMiss()
    {
        var lrcLib = new FakeLrcLib();
        var source = new LrcLibOnlineLyricsSource(lrcLib, Path.Combine(_root, "lyrics_cache"));
        var track = NewTrack("Song");

        Assert.Null(await source.FindAsync(track, CancellationToken.None));
        var afterMiss = lrcLib.Calls;
        Assert.True(afterMiss > 0);

        lrcLib.Get = new LrcLibResult { TrackName = "Song", ArtistName = "Artist", Duration = 60, SyncedLyrics = SyncedLrc, PlainLyrics = "plain" };
        var hit = await source.FindAsync(track, CancellationToken.None);
        Assert.Equal(SyncedLrc, hit!.Text);
        Assert.True(lrcLib.Calls > afterMiss);   // a miss is not remembered

        var calls = lrcLib.Calls;
        lrcLib.Get = null;
        var cached = await source.FindAsync(track, CancellationToken.None);
        Assert.Equal(SyncedLrc, cached!.Text);   // offline from the phone's cache
        Assert.Equal(calls, lrcLib.Calls);
        Assert.True(File.Exists(Path.Combine(_root, "lyrics_cache", track.Id.ToString("N") + ".lrc")));
    }

    [Fact]
    public async Task LrcLibSource_PlainOnly_AndInstrumental()
    {
        var lrcLib = new FakeLrcLib { Get = new LrcLibResult { TrackName = "Song", ArtistName = "Artist", Duration = 60, PlainLyrics = "one\ntwo" } };
        var source = new LrcLibOnlineLyricsSource(lrcLib, Path.Combine(_root, "lyrics_cache"));
        Assert.Equal("one\ntwo", (await source.FindAsync(NewTrack("Song"), CancellationToken.None))!.Text);

        lrcLib.Get = new LrcLibResult { TrackName = "Tune", ArtistName = "Artist", Duration = 60, Instrumental = true };
        var tune = NewTrack("Tune");
        Assert.True((await source.FindAsync(tune, CancellationToken.None))!.Instrumental);
        var calls = lrcLib.Calls;
        Assert.True((await source.FindAsync(tune, CancellationToken.None))!.Instrumental);
        Assert.Equal(calls, lrcLib.Calls);
    }

    [Fact]
    public async Task LrcLibSource_AFailingProvider_IsNullNotAThrow()
    {
        var source = new LrcLibOnlineLyricsSource(new ThrowingLrcLib(), Path.Combine(_root, "lyrics_cache"));
        Assert.Null(await source.FindAsync(NewTrack("Song"), CancellationToken.None));
    }

    private sealed class ThrowingLrcLib : ILrcLibService
    {
        public Task<LrcLibResult?> GetLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default) =>
            throw new LyricsProviderException("LRCLIB", new HttpRequestException("down"));

        public Task<List<LrcLibResult>> SearchLyricsAsync(string artist, string trackName, CancellationToken ct = default) =>
            throw new LyricsProviderException("LRCLIB", new HttpRequestException("down"));
    }
}
