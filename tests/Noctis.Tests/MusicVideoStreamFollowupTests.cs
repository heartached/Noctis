using Avalonia.Headless.XUnit;
using Noctis.Controls;
using Noctis.Models;
using Noctis.Services.YouTube;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Follow-ups to the music videos streamed from YouTube (11-25):
/// <list type="bullet">
/// <item>A stream that breaks mid-song (network drop, 403/410, decoder error, early end) is
/// resolved again and resumed at the song's position, with a 2 s / 5 s / 15 s back-off, a few
/// tries per song, nothing run while offline, and a fresh budget for the next song.</item>
/// <item>Seeks land fast: the first version paused 2.5 s after every seek, which swallowed the
/// song's own seeks (a scrub left the video on the first position for up to 2.5 s). The gate
/// is driven here by a fake decoder whose seeks complete after a set time.</item>
/// </list>
/// All offline: yt-dlp is a fake source, the decoder a simulation.
/// </summary>
public class MusicVideoStreamFollowupTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "noctis-mvstream2-" + Guid.NewGuid().ToString("N")[..8]);

    public MusicVideoStreamFollowupTests(ITestOutputHelper output)
    {
        _out = output;
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    // ── Bug 1: a stream that breaks mid-song ──

    private sealed class FakeSource : IYouTubeVideoSource
    {
        public bool IsAvailable => true;
        public List<string> Resolves { get; } = new();
        public Exception? ResolveError { get; set; }
        private int _n;

        public Task<IReadOnlyList<YouTubeVideoInfo>> SearchAsync(string query, int count, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<YouTubeVideoInfo>>(Array.Empty<YouTubeVideoInfo>());

        public Task<YouTubeVideoInfo?> GetInfoAsync(string url, CancellationToken ct)
            => Task.FromResult<YouTubeVideoInfo?>(null);

        public Task<string> ResolveStreamUrlAsync(string videoId, int maxHeight, CancellationToken ct)
        {
            Resolves.Add(videoId);
            if (ResolveError is { } e) return Task.FromException<string>(e);
            var n = Interlocked.Increment(ref _n);
            return Task.FromResult($"https://rr1---sn-x.googlevideo.com/videoplayback?expire=1795679982&id={videoId}&n={n}");
        }
    }

    private static readonly DateTime Now = DateTimeOffset.FromUnixTimeSeconds(1_795_670_000).UtcDateTime;

    private (MusicVideoStreamService Service, FakeSource Source) MakeService()
    {
        var source = new FakeSource();
        var store = new MusicVideoChoiceStore(Path.Combine(_dir, "music_video_choices.json"));
        return (new MusicVideoStreamService(source, store) { Clock = () => Now }, source);
    }

    private static Track Song(string title) => new()
    {
        Id = Guid.NewGuid(),
        Title = title,
        Artist = "Rick Astley",
        Duration = TimeSpan.FromSeconds(213),
        FilePath = @"C:\Music\Rick Astley\" + title + ".flac",
    };

    /// <summary>A player streaming the user's chosen video for each song it is given.</summary>
    private static PlayerViewModel Player(MusicVideoStreamService service, List<TimeSpan> delays, Func<bool>? online = null)
    {
        var vm = new PlayerViewModel(new FakeAudioPlayer(), new FakeLibraryService(), new TestPersistenceService(), new FakeAnimatedCoverService())
        {
            MusicVideosEnabled = true,
            MusicVideoAutoFindEnabled = false,
            MusicVideoStreams = service,
        };
        vm.StreamClock = () => Now;
        vm.StreamRetryDelay = (d, _) => { delays.Add(d); return Task.CompletedTask; };
        vm.IsNetworkUp = online ?? (() => true);
        return vm;
    }

    private static async Task Play(PlayerViewModel vm, MusicVideoStreamService service, Track track, string videoId = "dQw4w9WgXcQ")
    {
        service.SetUserVideo(track, new YouTubeVideoInfo(videoId, "Official Video", "Rick Astley", 213));
        vm.CurrentTrack = track;
        await vm.MusicVideoStreamTask;
        Assert.True(vm.IsStreamingMusicVideo);
    }

    private static async Task Fail(PlayerViewModel vm, string? url)
    {
        vm.MusicVideoStreamFailedCommand.Execute(url);
        await vm.MusicVideoStreamTask;
    }

    [AvaloniaFact]
    public async Task Failure_ResolvesAgain_AndResumesAtTheSongsPosition()
    {
        var (service, source) = MakeService();
        var delays = new List<TimeSpan>();
        var vm = Player(service, delays);
        await Play(vm, service, Song("Never Gonna Give You Up"));
        var broken = vm.CurrentMusicVideoPath;
        vm.Position = TimeSpan.FromSeconds(42);

        var gate = new TaskCompletionSource();
        vm.StreamRetryDelay = (d, ct) => { delays.Add(d); return gate.Task.WaitAsync(ct); };
        vm.MusicVideoStreamFailedCommand.Execute(broken);
        // While it waits, the artwork shows instead of the frozen frame.
        Assert.False(vm.IsStreamingMusicVideo);
        Assert.Null(vm.CurrentMusicVideoPath);
        gate.SetResult();
        await vm.MusicVideoStreamTask;

        Assert.True(vm.IsStreamingMusicVideo);
        Assert.NotEqual(broken, vm.CurrentMusicVideoPath);
        Assert.Contains("id=dQw4w9WgXcQ", vm.CurrentMusicVideoPath);
        Assert.Equal(2, source.Resolves.Count);
        Assert.Equal(new[] { TimeSpan.FromSeconds(2) }, delays);
        Assert.True(vm.ShowsMusicVideoBackdrop);
        // The new layer starts at what the song plays now (VideoBackdrop's forced first sync).
        Assert.True(vm.MusicVideoSyncPosition >= TimeSpan.FromSeconds(41));
    }

    [AvaloniaFact]
    public async Task Failure_BacksOff_2s_5s_15s_ThenGivesUp_AndKeepsTheArtwork()
    {
        var (service, source) = MakeService();
        var delays = new List<TimeSpan>();
        var vm = Player(service, delays);
        await Play(vm, service, Song("Never Gonna Give You Up"));
        var broken = vm.CurrentMusicVideoPath;

        source.ResolveError = new YtDlpRunException(1, "ERROR: unable to download video data: HTTP Error 403: Forbidden", "HTTP Error 403: Forbidden");
        await Fail(vm, broken);

        Assert.Equal(new[] { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15) }, delays);
        Assert.Equal(1 + 3, source.Resolves.Count);
        Assert.False(vm.IsStreamingMusicVideo);
        Assert.Null(vm.CurrentMusicVideoPath);

        // Given up: a late report or the song playing on runs nothing more.
        await Fail(vm, broken);
        vm.Position = TimeSpan.FromSeconds(100);
        await vm.MusicVideoStreamTask;
        Assert.Equal(4, source.Resolves.Count);
        Assert.Equal(3, delays.Count);
    }

    [AvaloniaFact]
    public async Task TheBudgetIsPerSong_AndATrackChangeResetsIt()
    {
        var (service, source) = MakeService();
        var delays = new List<TimeSpan>();
        var vm = Player(service, delays);
        await Play(vm, service, Song("Never Gonna Give You Up"));

        for (var i = 0; i < 3; i++)
        {
            await Fail(vm, vm.CurrentMusicVideoPath); // breaks, comes back
            Assert.True(vm.IsStreamingMusicVideo);
        }
        Assert.Equal(new[] { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15) }, delays);
        var resolves = source.Resolves.Count;

        await Fail(vm, vm.CurrentMusicVideoPath); // a fourth break in the same song: no more tries
        Assert.False(vm.IsStreamingMusicVideo);
        Assert.Equal(resolves, source.Resolves.Count);

        delays.Clear();
        await Play(vm, service, Song("Together Forever"), "yPYZpwSpKmA");
        await Fail(vm, vm.CurrentMusicVideoPath);
        Assert.True(vm.IsStreamingMusicVideo);
        Assert.Contains("id=yPYZpwSpKmA", vm.CurrentMusicVideoPath);
        Assert.Equal(new[] { TimeSpan.FromSeconds(2) }, delays);
    }

    [AvaloniaFact]
    public async Task Offline_RunsNoYtDlp_AndTriesWhenTheNextAttemptIsDue()
    {
        var (service, source) = MakeService();
        var delays = new List<TimeSpan>();
        var online = true;
        var vm = Player(service, delays, () => online);
        await Play(vm, service, Song("Never Gonna Give You Up"));

        online = false;
        await Fail(vm, vm.CurrentMusicVideoPath);
        Assert.Single(source.Resolves); // only the first resolve, none while offline
        Assert.Equal(3, delays.Count);
        Assert.False(vm.IsStreamingMusicVideo);

        // Back online by the second attempt: that one resolves.
        delays.Clear();
        await Play(vm, service, Song("Together Forever"), "yPYZpwSpKmA");
        var resolves = source.Resolves.Count;
        online = false;
        vm.StreamRetryDelay = (d, _) => { delays.Add(d); online = delays.Count >= 2; return Task.CompletedTask; };
        await Fail(vm, vm.CurrentMusicVideoPath);
        Assert.Equal(resolves + 1, source.Resolves.Count);
        Assert.Equal(new[] { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5) }, delays);
        Assert.True(vm.IsStreamingMusicVideo);
    }

    [AvaloniaFact]
    public async Task ATrackChangeDuringTheWait_CancelsTheRetry()
    {
        var (service, source) = MakeService();
        var delays = new List<TimeSpan>();
        var vm = Player(service, delays);
        await Play(vm, service, Song("Never Gonna Give You Up"));
        var waiting = new TaskCompletionSource();
        vm.StreamRetryDelay = (d, ct) => waiting.Task.WaitAsync(ct);
        vm.MusicVideoStreamFailedCommand.Execute(vm.CurrentMusicVideoPath);
        var retry = vm.MusicVideoStreamTask;

        vm.CurrentTrack = Song("No choice, automatic off");
        await retry; // cancelled quietly
        await vm.MusicVideoStreamTask;

        Assert.Single(source.Resolves);
        Assert.False(vm.IsStreamingMusicVideo);
        Assert.Null(vm.CurrentMusicVideoPath);
    }

    [AvaloniaFact]
    public async Task AReportFromAStreamAlreadyReplaced_IsIgnored()
    {
        var (service, source) = MakeService();
        var delays = new List<TimeSpan>();
        var vm = Player(service, delays);
        await Play(vm, service, Song("Never Gonna Give You Up"));
        var current = vm.CurrentMusicVideoPath;

        await Fail(vm, "https://rr1---sn-x.googlevideo.com/videoplayback?expire=1&id=old");
        await Fail(vm, null);

        Assert.Empty(delays);
        Assert.Single(source.Resolves);
        Assert.Equal(current, vm.CurrentMusicVideoPath);
        Assert.True(vm.IsStreamingMusicVideo);
    }

    // ── The layer's failure signals ──

    [Fact]
    public void Gate_NoFramesWhileTheSongPlays_IsAStall_ButNotWhilePausedOrPastTheEnd()
    {
        long now = 0;
        var gate = new StreamSyncGate(() => now);
        now = StreamSyncGate.StallMs - 1;
        Assert.False(gate.IsStalled(30_000, 200_000));
        now = StreamSyncGate.StallMs + 1;
        Assert.True(gate.IsStalled(30_000, 200_000));
        Assert.False(gate.IsStalled(197_000, 200_000)); // the clip ended before the song: normal

        gate.NoteFrame();
        Assert.False(gate.IsStalled(30_000, 200_000));
        now += 60_000; // paused a minute (no checks while paused), then resumed
        gate.Resumed();
        now += 1_000;
        Assert.False(gate.IsStalled(31_000, 200_000));
    }

    [Theory]
    [InlineData(60_000, 200_000, true)]   // the stream ended at 1:00 of a 3:20 video: dropped
    [InlineData(196_000, 200_000, false)] // ended at the video's end
    [InlineData(10_000, 0, true)]         // length unknown: not the real end either
    public void Gate_AnEndLongBeforeTheVideosEnd_IsAFailure(long songMs, long lengthMs, bool premature)
        => Assert.Equal(premature, StreamSyncGate.IsPrematureEnd(songMs, lengthMs));

    // ── Bug 2: seeks land fast ──

    /// <summary>A stream decoder whose seeks show their first frame <c>latencyMs</c> after they are issued.</summary>
    private sealed class FakeDecoder(int latencyMs)
    {
        private long _pos, _posAt, _target, _doneAt;
        public bool Seeking { get; private set; }
        public int Seeks { get; private set; }

        public void Seek(long to, long now)
        {
            Seeking = true;
            _target = to;
            _doneAt = now + latencyMs;
            Seeks++;
        }

        /// <summary>The clock: the target once the seek is under way, as VLC's does.</summary>
        public long Time(long now) => Seeking ? _target : _pos + (now - _posAt);

        /// <summary>True when a frame shows at <paramref name="now"/>.</summary>
        public bool Step(long now)
        {
            if (Seeking)
            {
                if (now < _doneAt) return false;
                Seeking = false;
                _pos = _target;
                _posAt = now;
            }
            return true;
        }
    }

    private interface ISyncModel
    {
        void Tick(long now, long songMs, bool force, FakeDecoder dec);
        void Frame(long now, FakeDecoder dec);
    }

    /// <summary>The first version (f9aa48bc): 900 ms tolerance, and nothing but a forced sync
    /// for 2.5 s after any seek.</summary>
    private sealed class CooldownModel : ISyncModel
    {
        private long _lastSeek = long.MinValue / 2;

        public void Tick(long now, long songMs, bool force, FakeDecoder dec)
        {
            if (!force && now - _lastSeek < 2500) return;
            if (force || Math.Abs(dec.Time(now) - songMs) > 900)
            {
                dec.Seek(songMs, now);
                _lastSeek = now;
            }
        }

        public void Frame(long now, FakeDecoder dec) { }
    }

    /// <summary>The gate, driven the way VideoBackdrop's stream session drives it.</summary>
    private sealed class GateModel(Func<long> clock) : ISyncModel
    {
        public readonly StreamSyncGate Gate = new(clock);

        public void Tick(long now, long songMs, bool force, FakeDecoder dec)
        {
            Gate.Want(songMs, force, playing: true, rate: 1.0);
            Run(now, dec);
        }

        public void Frame(long now, FakeDecoder dec)
        {
            Gate.NoteFrame();
            if (Gate.IsSeeking && Gate.OnFrame(dec.Time(now))) Run(now, dec);
            if (Gate.OnCapElapsed()) Run(now, dec);
        }

        private void Run(long now, FakeDecoder dec)
        {
            while (Gate.TryTake(out var target, out var jumped))
            {
                var (action, to) = Gate.Plan(target, jumped, ended: false, dec.Time(now), lengthMs: 0, playing: true, rate: 1.0);
                if (action == VideoBackdrop.SyncAction.None) continue;
                Gate.SeekIssued(to);
                dec.Seek(to, now);
            }
        }
    }

    private sealed record SeekRun(long SingleSeekMs, long ScrubMs, int ScrubSeeks, int SteadySeeks);

    /// <summary>
    /// The song starts (forced sync), is sought at 1.0 s to 1:00, scrubbed at 5.0-5.4 s through
    /// five positions, then plays on to 15 s. Position ticks every 100 ms, frames every 20 ms.
    /// Measures the time from the song's (final) seek to the first frame in sync with it
    /// (within the stream's tolerance).
    /// </summary>
    private static SeekRun Simulate(Func<Func<long>, ISyncModel> make, int latencyMs)
    {
        long now = 0;
        var model = make(() => now);
        var dec = new FakeDecoder(latencyMs);
        long songAt = 0, songBase = 0;
        long Song(long t) => songBase + (t - songAt);
        void SeekSong(long t, long to) { songBase = to; songAt = t; }
        var scrub = new long[] { 90_000, 100_000, 110_000, 120_000, 130_000 };
        long? seekedAt = null, single = null, scrubbed = null;
        int seeksBeforeScrub = 0, seeksAfterScrub = 0, steadyFrom = 0;

        for (now = 0; now <= 15_000; now += 20)
        {
            if (dec.Step(now))
            {
                model.Frame(now, dec);
                if (seekedAt is { } at && !dec.Seeking && Math.Abs(dec.Time(now) - Song(now)) <= StreamSyncGate.ToleranceMs)
                {
                    if (at == 1_000) single ??= now - at;
                    else if (at == 5_400) scrubbed ??= now - at;
                    seekedAt = null;
                }
            }
            if (now % 100 != 0) continue;
            if (now == 1_000) { SeekSong(now, 60_000); seekedAt = now; }
            if (now >= 5_000 && now <= 5_400)
            {
                if (now == 5_000) seeksBeforeScrub = dec.Seeks;
                SeekSong(now, scrub[(now - 5_000) / 100]);
                seekedAt = now;
            }
            if (now == 9_000) { seeksAfterScrub = dec.Seeks; steadyFrom = dec.Seeks; }
            model.Tick(now, Song(now), now == 0, dec);
        }
        return new SeekRun(single ?? -1, scrubbed ?? -1, seeksAfterScrub - seeksBeforeScrub, dec.Seeks - steadyFrom);
    }

    [Theory]
    [InlineData(150)]
    [InlineData(300)]
    [InlineData(600)]
    public void Seek_LandsOnTheFirstFrameAtTheTarget_WellUnderASecond(int latencyMs)
    {
        var before = Simulate(_ => new CooldownModel(), latencyMs);
        var after = Simulate(clock => new GateModel(clock), latencyMs);
        _out.WriteLine($"decoder seek latency {latencyMs} ms | before: seek {before.SingleSeekMs} ms, scrub {before.ScrubMs} ms ({before.ScrubSeeks} seeks) | after: seek {after.SingleSeekMs} ms, scrub {after.ScrubMs} ms ({after.ScrubSeeks} seeks), steady seeks {after.SteadySeeks}");

        // The fixed 2.5 s hold: a seek soon after another waited it out.
        Assert.True(before.SingleSeekMs >= 1_500, $"before {before.SingleSeekMs}");
        Assert.True(before.ScrubMs >= 2_000, $"before scrub {before.ScrubMs}");

        // Now: the seek's own time plus at most a position tick, and the scrub's end lands as
        // fast once the seek in flight has (two decoder seeks at most).
        Assert.InRange(after.SingleSeekMs, 0, latencyMs + 120);
        Assert.InRange(after.ScrubMs, 0, 2 * latencyMs + 120);
        Assert.True(after.SingleSeekMs < 1_000 && after.ScrubMs < 1_500);
        // Coalesced: one seek per decoder seek time during the scrub, not one per position.
        Assert.InRange(after.ScrubSeeks, 1, Math.Min(4, 2 + 400 / latencyMs));
        // No stutter: once landed, steady playback needs no corrections.
        Assert.Equal(0, after.SteadySeeks);
    }

    [Fact]
    public void Seek_WhilePaused_TheCapReleasesIt_AndTheLatestTargetFollows()
    {
        long now = 0;
        var gate = new StreamSyncGate(() => now);
        gate.Want(10_000, force: true, playing: false, rate: 1);
        Assert.True(gate.TryTake(out var t, out var jumped));
        var (action, to) = gate.Plan(t, jumped, false, 0, 200_000, playing: false, rate: 1);
        Assert.Equal(VideoBackdrop.SyncAction.Seek, action);
        Assert.Equal(10_000, to); // paused: no lead
        gate.SeekIssued(to);

        now = 200;
        gate.Want(50_000, force: false, playing: false, rate: 1); // scrubbed again, no frame comes while paused
        Assert.False(gate.TryTake(out _, out _));                // waits for the seek in flight
        now = StreamSyncGate.SeekCapMs - 1;
        Assert.False(gate.OnCapElapsed());
        now = StreamSyncGate.SeekCapMs + 10;
        Assert.True(gate.OnCapElapsed());
        Assert.True(gate.TryTake(out t, out jumped));
        Assert.Equal(50_000, t);
        Assert.True(jumped);
        Assert.Equal(VideoBackdrop.SyncAction.Seek, gate.Plan(t, jumped, false, 10_000, 200_000, false, 1).Action);
    }

    [Fact]
    public void Drift_IsCorrectedOnlyAfterTheSettle_ButTheSongsSeekAlwaysGoesThrough()
    {
        long now = 0;
        var gate = new StreamSyncGate(() => now);
        gate.SeekIssued(30_000);
        now = 400;
        Assert.False(gate.OnFrame(30_000));
        Assert.False(gate.IsSeeking);
        Assert.Equal((StreamSyncGate.InitialLeadMs + 400) / 2, gate.LeadMs);

        // Within the settle a lagging clock is left alone (the old stutter)…
        now = 400 + StreamSyncGate.SettleMs - 1;
        Assert.Equal(VideoBackdrop.SyncAction.None, gate.Plan(32_000, false, false, 30_500, 0, true, 1).Action);
        // …the song's own seek is not.
        var (action, to) = gate.Plan(90_000, true, false, 30_500, 0, true, 1);
        Assert.Equal(VideoBackdrop.SyncAction.Seek, action);
        Assert.Equal(90_000 + gate.LeadMs, to);
        // After it, real drift is corrected; small drift is not.
        now = 400 + StreamSyncGate.SettleMs + 1;
        Assert.Equal(VideoBackdrop.SyncAction.Seek, gate.Plan(32_000, false, false, 30_500, 0, true, 1).Action);
        Assert.Equal(VideoBackdrop.SyncAction.None, gate.Plan(32_000, false, false, 31_500, 0, true, 1).Action);
    }

    [Fact]
    public void Gate_ASongThatPlaysOn_IsNotAJump_ButASeekIs()
    {
        long now = 0;
        var gate = new StreamSyncGate(() => now);
        gate.Want(10_000, force: false, playing: true, rate: 1);
        gate.TryTake(out _, out _);
        now = 250;
        gate.Want(10_250, force: false, playing: true, rate: 1);
        Assert.True(gate.TryTake(out _, out var jumped));
        Assert.False(jumped);
        now = 500;
        gate.Want(10_500 + StreamSyncGate.JumpMs + 1, force: false, playing: true, rate: 1);
        Assert.True(gate.TryTake(out _, out jumped));
        Assert.True(jumped);
        // At 2x the song moves twice as fast: still not a jump.
        now = 1_500;
        gate.Want(14_100, force: false, playing: true, rate: 2); // 12 001 + 1 s at 2x
        Assert.True(gate.TryTake(out _, out jumped));
        Assert.False(jumped);
    }
}
