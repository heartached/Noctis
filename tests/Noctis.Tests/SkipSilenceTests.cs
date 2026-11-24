using System.Reflection;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Models;
using Noctis.Services;
using Noctis.Services.AudioAnalysis;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Skip Silence (Discord, Spark 11-23: "many of my songs have unnecessary blank starts and
/// ends ... detect the silence of the beginning and end", with an adjustable level).
/// </summary>
public class SkipSilenceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "noctis-skipsilence-" + Guid.NewGuid().ToString("N"));

    public SkipSilenceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp folder */ }
    }

    // ── Edge detection ──────────────────────────────────────────────────

    private static float[] Peaks(int silentBefore, int loud, int silentAfter, float loudPeak = 0.5f, float floor = 0f)
    {
        var p = new float[silentBefore + loud + silentAfter];
        Array.Fill(p, floor);
        for (var i = silentBefore; i < silentBefore + loud; i++) p[i] = loudPeak;
        return p;
    }

    [Fact]
    public void Leading_IsTheSilenceBeforeTheFirstSound_MinusThePad()
    {
        var peaks = Peaks(200, 100, 0); // 2 s of silence, windows are 10 ms
        var t = SilenceEdges.ThresholdToLinear(-50);

        Assert.Equal(TimeSpan.FromSeconds(2) - SilenceEdges.LeadPad, SilenceEdges.Leading(peaks, t));
    }

    [Fact]
    public void Trailing_IsTheSilenceAfterTheLastSound_MinusThePad()
    {
        var peaks = Peaks(0, 100, 300); // 3 s of silence at the end
        var t = SilenceEdges.ThresholdToLinear(-50);

        // The 300 silent windows after the last loud one.
        Assert.Equal(TimeSpan.FromSeconds(3) - SilenceEdges.TrailPad, SilenceEdges.Trailing(peaks, t));
    }

    [Fact]
    public void ShortSilence_IsLeftAlone()
    {
        var t = SilenceEdges.ThresholdToLinear(-50);
        Assert.Equal(TimeSpan.Zero, SilenceEdges.Leading(Peaks(30, 100, 0), t));   // 0.3 s
        Assert.Equal(TimeSpan.Zero, SilenceEdges.Trailing(Peaks(0, 100, 80), t)); // 0.8 s
    }

    [Fact]
    public void NoSoundAtAll_CutsNothing()
    {
        // A stretch that is silent from end to end says nothing about where the music is.
        var peaks = new float[3000];
        var t = SilenceEdges.ThresholdToLinear(-50);

        Assert.Equal(TimeSpan.Zero, SilenceEdges.Leading(peaks, t));
        Assert.Equal(TimeSpan.Zero, SilenceEdges.Trailing(peaks, t));
    }

    [Fact]
    public void TheLevel_DecidesWhatCountsAsSilence()
    {
        // Hiss at -55 dB before the music: silence at -50 dB, sound at -60 dB.
        var hiss = SilenceEdges.ThresholdToLinear(-55);
        var peaks = Peaks(200, 100, 0, floor: hiss);

        Assert.True(SilenceEdges.Leading(peaks, SilenceEdges.ThresholdToLinear(-50)) > TimeSpan.FromSeconds(1.5));
        Assert.Equal(TimeSpan.Zero, SilenceEdges.Leading(peaks, SilenceEdges.ThresholdToLinear(-60)));
    }

    [Fact]
    public void PeakAccumulator_TakesTheLoudestChannel_PerWindow()
    {
        var acc = new SilenceScanService.PeakAccumulator(4); // 2 stereo frames per window
        acc.Add(new[] { 0f, 0.1f, -0.3f, 0f, 0f, 0f });     // window 1 complete, half of window 2
        acc.Add(new[] { 0f, 0.2f });

        Assert.Equal(new[] { 0.3f, 0.2f }, acc.Finish());
    }

    // ── Real ffmpeg (skipped without one) ───────────────────────────────

    private static string? Ffmpeg() => new AudioConverterService(() => string.Empty, new MetadataService()).GetFfmpegPath();

    /// <summary>A FLAC of <paramref name="before"/> s of digital silence, a tone, and
    /// <paramref name="after"/> s of silence.</summary>
    private string Song(string ffmpeg, double before, double tone, double after, double toneVolume = 0.5)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".flac");
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var psi = new System.Diagnostics.ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[]
        {
            "-nostdin", "-hide_banner", "-loglevel", "error", "-y",
            "-f", "lavfi", "-i", string.Format(inv, "anullsrc=r=44100:cl=stereo:d={0}", before),
            "-f", "lavfi", "-i", string.Format(inv, "sine=f=440:r=44100:d={0}", tone),
            "-f", "lavfi", "-i", string.Format(inv, "anullsrc=r=44100:cl=stereo:d={0}", after),
            // Every part at 32-bit: left to negotiate, concat picked a format that rounded a
            // quiet tone to digital silence.
            "-filter_complex", string.Format(inv,
                "[0]aformat=sample_fmts=s32:channel_layouts=stereo[a];[1]volume={0},aformat=sample_fmts=s32:channel_layouts=stereo[s];" +
                "[2]aformat=sample_fmts=s32:channel_layouts=stereo[c];[a][s][c]concat=n=3:v=0:a=1", toneVolume),
            path,
        }) psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi)!;
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
        return path;
    }

    private static Track LocalTrack(string path, TimeSpan duration) => new()
    {
        Id = Guid.NewGuid(),
        Title = Path.GetFileNameWithoutExtension(path),
        FilePath = path,
        Duration = duration,
    };

    [Fact]
    public async Task Scan_FindsTheSilenceAtBothEnds_OfARealFile()
    {
        var ffmpeg = Ffmpeg();
        if (ffmpeg == null) return; // no ffmpeg on this machine
        var path = Song(ffmpeg, before: 2, tone: 5, after: 4);
        var scanner = new SilenceScanService(() => ffmpeg);

        var b = await scanner.ScanAsync(LocalTrack(path, TimeSpan.FromSeconds(11)), -50);

        Assert.InRange(b.Leading.TotalSeconds, 2 - 0.06 - 0.02, 2 - 0.06 + 0.02);
        Assert.InRange(b.Trailing.TotalSeconds, 4 - 0.25 - 0.03, 4 - 0.25 + 0.03);
    }

    [Fact]
    public async Task Scan_OfASongWithNoSilence_CutsNothing()
    {
        var ffmpeg = Ffmpeg();
        if (ffmpeg == null) return;
        var path = Song(ffmpeg, before: 0.01, tone: 8, after: 0.01);

        var b = await new SilenceScanService(() => ffmpeg).ScanAsync(LocalTrack(path, TimeSpan.FromSeconds(8)), -50);

        Assert.Equal(SilenceBounds.None, b);
    }

    [Fact]
    public async Task Scan_UsesTheLevel_AQuietSongBelowItIsLeftWhole()
    {
        // ffmpeg's sine is at 1/8 full scale (-18 dBFS); x0.01 puts it near -60 dBFS: all
        // "silence" at -50 dB (nothing is known, nothing is cut), music at -70 dB.
        var ffmpeg = Ffmpeg();
        if (ffmpeg == null) return;
        var path = Song(ffmpeg, before: 2, tone: 5, after: 2, toneVolume: 0.01);
        var scanner = new SilenceScanService(() => ffmpeg);
        var track = LocalTrack(path, TimeSpan.FromSeconds(9));

        Assert.Equal(SilenceBounds.None, await scanner.ScanAsync(track, -50));
        Assert.True((await scanner.ScanAsync(track, -70)).Leading > TimeSpan.FromSeconds(1.5));
    }

    [Fact]
    public async Task Scan_WithoutFfmpeg_OrAMissingFile_CutsNothing()
    {
        var noFfmpeg = new SilenceScanService(() => null);
        var missing = new SilenceScanService(() => "ffmpeg");
        var track = LocalTrack(Path.Combine(_dir, "gone.flac"), TimeSpan.FromMinutes(3));

        Assert.Equal(SilenceBounds.None, await noFfmpeg.ScanAsync(track, -50));
        Assert.Equal(SilenceBounds.None, await missing.ScanAsync(track, -50));
    }

    // ── Player ──────────────────────────────────────────────────────────

    private Track FileTrack(string name)
    {
        var path = Path.Combine(_dir, name + ".flac");
        File.WriteAllBytes(path, new byte[] { 0 });
        return new Track
        {
            Id = Guid.NewGuid(),
            Title = name,
            Artist = "A",
            Album = name,
            AlbumId = Guid.NewGuid(),
            FilePath = path,
            Duration = TimeSpan.FromMinutes(3),
        };
    }

    private static void Know(PlayerViewModel vm, Track t, double leadingS, double trailingS)
    {
        var memo = (Dictionary<Guid, SilenceBounds>)typeof(PlayerViewModel)
            .GetField("_silenceByTrack", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(vm)!;
        memo[t.Id] = new SilenceBounds(TimeSpan.FromSeconds(leadingS), TimeSpan.FromSeconds(trailingS));
    }

    private static (FakeAudioPlayer Player, PlayerViewModel Vm) NewPlayer(bool skipSilence = true)
    {
        var player = new FakeAudioPlayer();
        var vm = new PlayerViewModel(player, new FakeLibraryService(), new TestPersistenceService(), new FakeAnimatedCoverService())
        {
            // ffmpeg "found" but never run: these tests hand the player the results.
            SilenceScanner = new SilenceScanService(() => null),
            SkipSilenceEnabled = skipSilence,
        };
        return (player, vm);
    }

    private static void Play(PlayerViewModel vm, params Track[] queue)
    {
        vm.ReplaceQueueAndPlay(queue, 0);
        Dispatcher.UIThread.RunJobs();
        typeof(PlayerViewModel)
            .GetField("_autoMixCommitGuardUntilUtc", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(vm, DateTime.MinValue);
    }

    [AvaloniaFact]
    public void AKnownSong_StartsAtItsFirstSound()
    {
        var (player, vm) = NewPlayer();
        var a = FileTrack("a");
        Know(vm, a, 2.5, 0);

        Play(vm, a);

        Assert.Equal(2500, player.PendingSeekMs);
    }

    [AvaloniaFact]
    public void AStartTimeSetByHand_Wins()
    {
        var (player, vm) = NewPlayer();
        var a = FileTrack("a");
        a.StartTimeMs = 7000;
        Know(vm, a, 2.5, 0);

        Play(vm, a);

        Assert.Equal(7000, player.PendingSeekMs);
    }

    [AvaloniaFact]
    public void SkipSilenceOff_PlaysFromTheStart()
    {
        var (player, vm) = NewPlayer(skipSilence: false);
        var a = FileTrack("a");
        Know(vm, a, 2.5, 4);

        Play(vm, a);

        Assert.Equal(-1, player.PendingSeekMs);
        Assert.Equal(TimeSpan.Zero, vm.EffectiveStopTime(a, a.Duration));
    }

    [AvaloniaFact]
    public void TheSong_EndsAfterItsLastSound()
    {
        var (_, vm) = NewPlayer();
        var a = FileTrack("a");
        Know(vm, a, 0, 4);
        Play(vm, a);

        Assert.Equal(a.Duration - TimeSpan.FromSeconds(4), vm.EffectiveStopTime(a, a.Duration));
    }

    [AvaloniaFact]
    public void AStopTimeSetByHand_Wins()
    {
        var (_, vm) = NewPlayer();
        var a = FileTrack("a");
        a.StopTimeMs = 100_000;
        Know(vm, a, 0, 4);
        Play(vm, a);

        Assert.Equal(TimeSpan.FromSeconds(100), vm.EffectiveStopTime(a, a.Duration));
    }

    [AvaloniaFact]
    public void TheNextSong_IsStagedAtItsFirstSound()
    {
        var (player, vm) = NewPlayer();
        var a = FileTrack("a");
        var b = FileTrack("b");
        Know(vm, b, 1.8, 0);
        Play(vm, a, b);
        var duration = a.Duration;

        Assert.False(vm.TryAdvanceForGapless(duration - TimeSpan.FromSeconds(6), duration));

        var i = player.PreparedPaths.IndexOf(b.FilePath);
        Assert.True(i >= 0);
        Assert.Equal(1800, player.PreparedStartMs[i]);
    }

    [AvaloniaFact]
    public void ASongWithTrailingSilence_IsNotHandedOffAtTheFileEnd()
    {
        // It ends early on its own (the stop check), like a song with a stop time set by hand.
        var (player, vm) = NewPlayer();
        var a = FileTrack("a");
        var b = FileTrack("b");
        Know(vm, a, 0, 4);
        Play(vm, a, b);
        var duration = a.Duration;

        Assert.False(vm.TryAdvanceForGapless(duration - TimeSpan.FromSeconds(6), duration));
        Assert.DoesNotContain(b.FilePath, player.PreparedPaths);
    }

    [AvaloniaFact]
    public void SongTransitions_SeeTheMeasuredSilence()
    {
        var (_, vm) = NewPlayer();
        var a = FileTrack("a");
        var b = FileTrack("b");
        Know(vm, a, 0, 4);
        Know(vm, b, 1.5, 0);
        Play(vm, a, b);

        var options = (AutoMixPlannerOptions)typeof(PlayerViewModel)
            .GetMethod("CreateAutoMixOptions", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(vm, new object[] { false })!;

        Assert.True(options.RemoveSilenceBetweenSongs);
        Assert.Equal(TimeSpan.FromSeconds(4), AutoMixTransitionPlanner.EstimateSilenceProfile(a, options.DetectedSilence!(a)).EndSilence);
        Assert.Equal(TimeSpan.FromSeconds(1.5), AutoMixTransitionPlanner.EstimateSilenceProfile(b, options.DetectedSilence!(b)).StartSilence);
    }

    [Fact]
    public void Planner_HandSetTimesStillWin_OverMeasuredSilence()
    {
        var t = new Track { StartTimeMs = 9000, StopTimeMs = 170_000, Duration = TimeSpan.FromMinutes(3) };
        var measured = new AudioSilenceProfile(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(6), false);

        var p = AutoMixTransitionPlanner.EstimateSilenceProfile(t, measured);

        Assert.Equal(TimeSpan.FromSeconds(9), p.StartSilence);
        Assert.Equal(TimeSpan.FromSeconds(5), p.EndSilence); // capped region after the stop time, as before
    }

    [Fact]
    public void Settings_LevelIsClamped()
    {
        var s = new AppSettings { SkipSilenceThresholdDb = -200 };
        s.ClampToValidRanges();
        Assert.Equal(AppSettings.SkipSilenceThresholdMin, s.SkipSilenceThresholdDb);
        Assert.False(new AppSettings().SkipSilenceEnabled);
        Assert.Equal(-50, new AppSettings().SkipSilenceThresholdDb);
    }
}
