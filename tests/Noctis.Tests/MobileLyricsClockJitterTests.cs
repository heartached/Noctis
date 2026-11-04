using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Phone beta (Realme 7 Pro, Galaxy A55): "the lyrics desync and sync all the time". The phone's
/// position reaches the lyrics clock through a 250 ms poll on the UI thread and a dispatcher
/// post, and a mid-range phone stalls that thread for 40-350 ms at a time (covers, layout). The
/// clock used to stamp each reading with the frame that first saw it, so a post that waited
/// behind a long frame read as "the song is behind": the sweep slowed, then sped up at the next
/// poll, and a reading a few ms behind the last one threw it backwards. These simulate that
/// chain and pin the fix: readings stamped where they are taken, a monotonic estimate, no
/// overshoot after a long frame.
/// </summary>
public class MobileLyricsClockJitterTests
{
    private readonly ITestOutputHelper _output;
    public MobileLyricsClockJitterTests(ITestOutputHelper output) => _output = output;

    private const double FrameMs = 1000.0 / 90;   // Realme 7 Pro panel
    private const double PollMs = 250;            // Media3AudioPlayer.PositionPollMs
    private const double StartMs = 30_000;

    private readonly record struct Frame(double Now, double Raw, double RawAt);

    /// <summary>
    /// The UI thread draws at 90 Hz, but with <paramref name="stallChance"/> per frame it is
    /// busy for 40-350 ms. The poll runs on that thread (a Handler on the main looper, re-posted
    /// 250 ms after each run), reads ExoPlayer's position (10 ms steps; with
    /// <paramref name="noisyChance"/> a reading up to 15 ms behind the truth) and posts it to
    /// Now Playing, which applies it 0-20 ms later, or after a stall that starts meanwhile.
    /// </summary>
    private static List<Frame> Simulate(double seconds, double stallChance, double noisyChance, int seed)
    {
        var rng = new Random(seed);
        var end = seconds * 1000;
        var frameTimes = new List<double>();
        var stalls = new List<(double From, double To)>();
        for (var t = 0.0; t < end;)
        {
            frameTimes.Add(t);
            if (rng.NextDouble() < stallChance)
            {
                var from = t + 1;
                var to = from + 40 + rng.NextDouble() * 310;
                stalls.Add((from, to));
                t = to;
            }
            else t += FrameMs;
        }

        double FreeAt(double t)
        {
            foreach (var (from, to) in stalls)
                if (t >= from && t < to) return to;
            return t;
        }

        // Polls: (taken at, value, applied to Now Playing at).
        var polls = new List<(double At, double Value, double AppliedAt)>();
        for (var due = 0.0; due < end;)
        {
            var at = FreeAt(due);
            var value = Math.Floor((StartMs + at) / 10) * 10;
            if (rng.NextDouble() < noisyChance) value -= rng.NextDouble() * 15;
            polls.Add((at, value, FreeAt(at + rng.NextDouble() * 20)));
            due = at + PollMs;
        }

        var frames = new List<Frame>();
        foreach (var now in frameTimes)
        {
            var applied = polls.LastOrDefault(p => p.AppliedAt <= now);
            if (applied == default) continue;
            frames.Add(new Frame(now, applied.Value, applied.At));
        }
        return frames;
    }

    private sealed record Stats(int BackwardSteps, double MaxBackwardMs, double MaxErrorMs, double MeanErrorMs, double MaxRateDeviation);

    private static Stats Measure(List<Frame> frames, Func<Frame, double> sample)
    {
        double prev = double.NaN, prevNow = 0, maxBack = 0, maxErr = 0, sumErr = 0, maxRateDev = 0;
        int back = 0, n = 0;
        foreach (var f in frames)
        {
            var est = sample(f);
            if (f.Now >= 2000)
            {
                if (!double.IsNaN(prev))
                {
                    var step = est - prev;
                    if (step < -1e-9) { back++; maxBack = Math.Max(maxBack, -step); }
                    var dt = f.Now - prevNow;
                    if (dt > 0) maxRateDev = Math.Max(maxRateDev, Math.Abs(step / dt - 1));
                }
                var err = Math.Abs(est - (StartMs + f.Now));
                maxErr = Math.Max(maxErr, err);
                sumErr += err;
                n++;
            }
            prev = est;
            prevNow = f.Now;
        }
        return new Stats(back, maxBack, maxErr, sumErr / Math.Max(1, n), maxRateDev);
    }

    /// <summary>Verbatim port of the clock before this fix (stamp = first frame that saw the
    /// reading, error measured against the previous frame's estimate, any backward reading a seek).</summary>
    private sealed class LegacyClock
    {
        private double _raw = double.NaN, _rawAtMs, _estimate = double.NaN, _estimateAtMs;

        public double Sample(double rawMs, double nowMs)
        {
            if (double.IsNaN(_estimate)) return Anchor(rawMs, nowMs);
            if (rawMs != _raw)
            {
                if (rawMs < _raw) return Anchor(rawMs, nowMs);
                _raw = rawMs;
                _rawAtMs = nowMs;
            }
            var dt = Math.Max(0, nowMs - _estimateAtMs);
            _estimateAtMs = nowMs;
            if (nowMs - _rawAtMs > LyricsPlaybackClock.StallHoldMs) return _estimate;
            var target = _raw + (nowMs - _rawAtMs);
            var error = target - _estimate;
            if (Math.Abs(error) > LyricsPlaybackClock.SnapThresholdMs) return _estimate = target;
            var rate = Math.Clamp(1 + error / LyricsPlaybackClock.CorrectionTauMs, 1 - LyricsPlaybackClock.MaxRateError, 1 + LyricsPlaybackClock.MaxRateError);
            _estimate += dt * rate;
            return _estimate;
        }

        private double Anchor(double rawMs, double nowMs)
        {
            _raw = rawMs; _rawAtMs = nowMs; _estimate = rawMs; _estimateAtMs = nowMs;
            return rawMs;
        }
    }

    private static LyricsPlaybackClock PhoneClock() => new() { BackwardToleranceMs = 250 };

    [Theory]
    [InlineData(0.03, 0.0, 1)]    // stalls only, ExoPlayer readings exact
    [InlineData(0.03, 0.15, 2)]   // stalls and readings a few ms behind the last
    [InlineData(0.08, 0.15, 3)]   // a struggling phone
    public void StampedClock_IsMonotonicAndTracksTheSong_UnderStallsAndJitteryPolls(double stallChance, double noisyChance, int seed)
    {
        var frames = Simulate(60, stallChance, noisyChance, seed);

        var legacy = new LegacyClock();
        var before = Measure(frames, f => legacy.Sample(f.Raw, f.Now));
        var clock = PhoneClock();
        var after = Measure(frames, f => clock.Sample(f.Raw, f.RawAt, f.Now));
        _output.WriteLine($"stall={stallChance} noisy={noisyChance}");
        _output.WriteLine($"  before: back steps {before.BackwardSteps} (max {before.MaxBackwardMs:F1} ms), |err| max {before.MaxErrorMs:F0} mean {before.MeanErrorMs:F1} ms, rate dev {before.MaxRateDeviation:P1}");
        _output.WriteLine($"  after:  back steps {after.BackwardSteps} (max {after.MaxBackwardMs:F1} ms), |err| max {after.MaxErrorMs:F0} mean {after.MeanErrorMs:F1} ms, rate dev {after.MaxRateDeviation:P1}");

        Assert.Equal(0, after.BackwardSteps);
        // 10 ms readings, 15 ms noise, and what a ±8% slew closes over a few frames.
        Assert.True(after.MaxErrorMs < 45, $"clock {after.MaxErrorMs:F0} ms off the song");
        Assert.True(after.MaxRateDeviation <= LyricsPlaybackClock.MaxRateError + 1e-9,
            $"sweep speed bent by {after.MaxRateDeviation:P1}");
        // The bound means something: the clock it replaced breaks it on the same chain.
        Assert.True(before.MaxErrorMs >= 45 || before.BackwardSteps > 0,
            "the legacy clock passed the bound: it is too loose to prove anything");
    }

    private sealed class NoPicker : Noctis.Mobile.Services.IFolderPicker
    {
        public Task<string?> PickFolderAsync() => Task.FromResult<string?>(null);
    }

    private static (Noctis.Mobile.ViewModels.LyricsPageViewModel Lyrics, Noctis.Mobile.ViewModels.NowPlayingViewModel Player, FakeAudioPlayer Engine) Playing()
    {
        var root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
        var library = new FakeLibraryService();
        var persistence = new PersistenceService(root);
        var engine = new FakeAudioPlayer();
        var files = new FakeTrackFiles();
        files.Sidecars[".lrc"] = "[00:01.00]one\n[00:20.00]two\n[00:40.00]three\n";
        var nowPlaying = new Noctis.Mobile.ViewModels.NowPlayingViewModel(engine, library, persistence, marshal: a => a());
        var lyrics = new Noctis.Mobile.ViewModels.LyricsPageViewModel(engine, nowPlaying, files, persistence, work => Task.FromResult(work()));
        nowPlaying.PlayTracks(new[] { new Track { Id = Guid.NewGuid(), Title = "Song", Artist = "A", FilePath = "content://x/1", Duration = TimeSpan.FromSeconds(90) } }, 0);
        Assert.True(lyrics.IsSynced);
        return (lyrics, nowPlaying, engine);
    }

    /// <summary>The page's own path: a poll taken now but first drawn 200 ms later (its post
    /// waited behind a long frame) puts the sweep where the song is, not 200 ms behind it.</summary>
    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void LyricsPage_StampsThePollWhereItWasTaken()
    {
        var (lyrics, _, engine) = Playing();
        engine.RaisePositionChanged(TimeSpan.FromSeconds(10));
        var polledAt = Noctis.Mobile.ViewModels.LyricsPageViewModel.NowMs();
        lyrics.OnFrame(polledAt + 200);
        Assert.InRange(lyrics.LastAudibleMs, 10_190, 10_210);
    }

    /// <summary>A held scrub steers the position: the lyrics follow it verbatim (as the
    /// desktop's), not through the clock's slew, and re-anchor once it ends.</summary>
    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void LyricsPage_FollowsAScrubVerbatim()
    {
        var (lyrics, player, engine) = Playing();
        engine.RaisePositionChanged(TimeSpan.FromSeconds(10));
        var now = Noctis.Mobile.ViewModels.LyricsPageViewModel.NowMs();
        lyrics.OnFrame(now);
        player.BeginSeek();
        player.PreviewSeek(TimeSpan.FromSeconds(10.3));   // a nudge the clock would only slew toward
        lyrics.OnFrame(now + 16);
        Assert.Equal(10_300, lyrics.LastAudibleMs, 0.5);
        player.PreviewSeek(TimeSpan.FromSeconds(10.1));   // and back, without a seek's snap rules
        lyrics.OnFrame(now + 32);
        Assert.Equal(10_100, lyrics.LastAudibleMs, 0.5);
        player.EndSeek();
    }

    [Fact]
    public void ReadingDeliveredLate_StillSaysWhereTheSongIsNow()
    {
        var clock = PhoneClock();
        clock.Sample(10_000, 0, 0);
        for (var now = FrameMs; now < 250; now += FrameMs) clock.Sample(10_000, 0, now);
        // The 250 ms poll read 10 250 but its post waited behind a 200 ms frame.
        var est = clock.Sample(10_250, 250, 450);
        Assert.InRange(est, 10_445, 10_455);
    }

    [Fact]
    public void LongFrame_DoesNotOvershoot()
    {
        var clock = PhoneClock();
        var t = 0.0;
        for (; t < 1000; t += FrameMs) clock.Sample(5_000 + Math.Floor(t / 250) * 250, Math.Floor(t / 250) * 250, t);
        // A 380 ms frame: the clock lands on the song, not 8% past it.
        var after = clock.Sample(5_000 + Math.Floor((t + 380) / 250) * 250, Math.Floor((t + 380) / 250) * 250, t + 380);
        Assert.InRange(after - (5_000 + t + 380), -12, 3);
    }

    [Fact]
    public void ReadingSlightlyBehind_Holds_RealSeekBack_Snaps()
    {
        var clock = PhoneClock();
        clock.Sample(20_000, 0, 0);
        var a = clock.Sample(20_100, 100, 100);
        var b = clock.Sample(20_088, 116, 116);   // 12 ms behind the last reading
        Assert.True(b >= a, $"stepped back {a - b:F1} ms on a noisy reading");

        var c = clock.Sample(5_000, 133, 133);    // the player really went back
        Assert.Equal(5_000, c, 0.01);
    }

    [Fact]
    public void FarAheadAfterABufferingStall_HoldsInsteadOfJumpingBack()
    {
        var clock = PhoneClock();
        clock.Sample(1_000, 0, 0);
        double est = 0;
        for (var now = FrameMs; now < 950; now += FrameMs) est = clock.Sample(1_000, 0, now);   // source silent, clock runs on
        // Playback resumes from where it really is (it was buffering): far behind the estimate.
        var held = clock.Sample(1_050, 960, 960);
        Assert.True(held >= est, "the estimate jumped back");
        // It waits for the song, never stepping back, then runs with it.
        var prev = held;
        for (var now = 960 + FrameMs; now < 3_000; now += FrameMs)
        {
            var polledAt = 960 + Math.Floor((now - 960) / 250) * 250;
            var next = clock.Sample(1_050 + (polledAt - 960), polledAt, now);
            Assert.True(next >= prev, $"stepped back at {now:F0} ms");
            prev = next;
        }
        Assert.InRange(prev, 1_050 + (3_000 - 960) - 30, 1_050 + (3_000 - 960) + 30);
    }
}
