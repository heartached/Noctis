using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The idle-RAM report: after startup the managed heap held ~236 MB of dead large
/// arrays (embedded cover files TagLib had materialised) that no later allocation
/// ever prompted the GC to collect. These pin the two answers: tag reads that do not
/// need pictures open files lazily, and the startup/scan producers request one
/// debounced trim when they finish.
/// </summary>
[Collection("MemoryTrim global state")]
public class MemoryTrimTests
{
    [Fact]
    public async Task OverlappingRequests_CollapseIntoOneTrim_AfterTheQuietPeriod()
    {
        var quiet = MemoryTrim.Quiet;
        var runs = 0;
        MemoryTrim.Collector = _ => Interlocked.Increment(ref runs);
        MemoryTrim.Quiet = TimeSpan.FromMilliseconds(150);
        try
        {
            MemoryTrim.RequestAfterIdle("a");
            await Task.Delay(60);
            MemoryTrim.RequestAfterIdle("b"); // restarts the quiet period
            await Task.Delay(60);
            MemoryTrim.RequestAfterIdle("c");
            await Task.Delay(80);
            Assert.Equal(0, Volatile.Read(ref runs)); // still inside the quiet period of "c"
            await Task.Delay(300);
            Assert.Equal(1, Volatile.Read(ref runs));
        }
        finally
        {
            MemoryTrim.Collector = null;
            MemoryTrim.Quiet = quiet;
        }
    }

    [Fact]
    public async Task WhileAudioPlays_TheTrimDoesNotBlock()
    {
        // R2: the trim after the startup scan ran a forced blocking compacting gen2 while
        // music played, and the render thread stalled 39 ms (gcPauseMs=34.1) inside it.
        var quiet = MemoryTrim.Quiet;
        var playing = true;
        var modes = new System.Collections.Concurrent.ConcurrentQueue<bool>();
        MemoryTrim.ForgetAppState(); // a host that never reports its windows: the old rule
        MemoryTrim.Collector = blocking => modes.Enqueue(blocking);
        MemoryTrim.IsAudioPlaying = () => Volatile.Read(ref playing);
        MemoryTrim.Quiet = TimeSpan.FromMilliseconds(50);
        try
        {
            MemoryTrim.RequestAfterIdle("playing");
            await WaitUntil(() => !modes.IsEmpty);
            Assert.NotEmpty(modes);
            Assert.DoesNotContain(true, modes); // background collection only

            modes.Clear();
            Volatile.Write(ref playing, false);
            MemoryTrim.RequestAfterIdle("idle");
            await WaitUntil(() => !modes.IsEmpty);
            Assert.NotEmpty(modes);
            Assert.DoesNotContain(false, modes); // nothing playing: the full compacting trim
        }
        finally
        {
            MemoryTrim.Collector = null;
            MemoryTrim.IsAudioPlaying = null;
            MemoryTrim.Quiet = quiet;
        }
    }

    [Fact]
    public void WhileAudioPlays_TheGcStaysInSustainedLowLatency_AndTheModeComesBackOnStop()
    {
        // 11-27 "weird audio cut off": one 760 ms blocking gen2 (gcs=1/1/1) froze the render
        // thread far past the 100 ms device buffer. SustainedLowLatency keeps the gen2s the
        // runtime starts on its own background ones while music plays.
        var original = System.Runtime.GCSettings.LatencyMode;
        try
        {
            MemoryTrim.SetAudioPlaying(false);
            System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.Interactive;
            MemoryTrim.SetAudioPlaying(true);
            Assert.Equal(System.Runtime.GCLatencyMode.SustainedLowLatency, System.Runtime.GCSettings.LatencyMode);
            MemoryTrim.SetAudioPlaying(true); // repeated state changes (track to track) keep it
            Assert.Equal(System.Runtime.GCLatencyMode.SustainedLowLatency, System.Runtime.GCSettings.LatencyMode);
            MemoryTrim.SetAudioPlaying(false);
            Assert.Equal(System.Runtime.GCLatencyMode.Interactive, System.Runtime.GCSettings.LatencyMode);
        }
        finally
        {
            MemoryTrim.SetAudioPlaying(false);
            System.Runtime.GCSettings.LatencyMode = original;
        }
    }

    [Fact]
    public async Task ATrimSkippedWhilePlaying_RunsCompactingOnceStopped_AndRechecksBeforeRunning()
    {
        var quiet = MemoryTrim.Quiet;
        var original = System.Runtime.GCSettings.LatencyMode;
        var longIdle = MemoryTrim.LongIdle;
        var modes = new System.Collections.Concurrent.ConcurrentQueue<bool>();
        MemoryTrim.ForgetAppState(); // window rule: see the desktop tests below
        MemoryTrim.Collector = blocking => modes.Enqueue(blocking);
        MemoryTrim.Quiet = TimeSpan.FromMilliseconds(50);
        MemoryTrim.LongIdle = TimeSpan.FromMilliseconds(50);
        try
        {
            MemoryTrim.SetAudioPlaying(true);
            MemoryTrim.RequestAfterIdle("window in the background");
            await WaitUntil(() => !modes.IsEmpty);
            Assert.Equal(new[] { false }, modes.ToArray()); // background only while playing

            // Paused, then playing again before the deferred trim's quiet period ends: the
            // trim checks again when it fires and still does not block.
            modes.Clear();
            MemoryTrim.SetAudioPlaying(false);
            MemoryTrim.SetAudioPlaying(true);
            await WaitUntil(() => !modes.IsEmpty);
            Assert.Equal(new[] { false }, modes.ToArray());

            // Stopped for good: the compacting trim it was owed runs, once.
            modes.Clear();
            MemoryTrim.SetAudioPlaying(false);
            await WaitUntil(() => !modes.IsEmpty);
            await Task.Delay(150);
            Assert.Equal(new[] { true }, modes.ToArray());

            // Nothing was skipped since: another stop owes nothing.
            modes.Clear();
            MemoryTrim.SetAudioPlaying(true);
            MemoryTrim.SetAudioPlaying(false);
            await Task.Delay(200);
            Assert.Empty(modes);
        }
        finally
        {
            MemoryTrim.SetAudioPlaying(false);
            await Task.Delay(150); // let a deferred request, if any, drain into the fake collector
            MemoryTrim.Collector = null;
            MemoryTrim.Quiet = quiet;
            MemoryTrim.LongIdle = longIdle;
            System.Runtime.GCSettings.LatencyMode = original;
        }
    }

    [Theory]
    // playing, app in background (null = host does not report), idle ms -> may block
    [InlineData(true, null, 999_999, false)]
    [InlineData(false, null, 0, true)]          // server / phone: the old rule
    [InlineData(true, true, 999_999, false)]    // never while music plays
    [InlineData(false, false, 999_999, false)]  // a play button on screen (window or mini player)
    [InlineData(false, true, 59_999, false)]    // paused a moment ago: a resume is likely
    [InlineData(false, true, 60_000, true)]     // in the background and idle for a minute
    public void ABlockingTrim_OnlyWhenNothingCanBeResumedOnScreen(bool playing, bool? background, long idleMs, bool expected) =>
        Assert.Equal(expected, MemoryTrim.MayBlock(playing, background, idleMs, TimeSpan.FromSeconds(60)));

    [Fact]
    public async Task Desktop_PausedOnScreen_TrimsInTheBackground_AndCompactsOnceHiddenAndIdle()
    {
        // Follow-up 11-27: a full trim ~6 s after pausing made a resume wait behind it (harness:
        // up to 68.7 ms from resume to the next render, against 15.3 ms with a background trim).
        var quiet = MemoryTrim.Quiet;
        var longIdle = MemoryTrim.LongIdle;
        var now = MemoryTrim.Now;
        var original = System.Runtime.GCSettings.LatencyMode;
        long clock = 1_000_000;
        var modes = new System.Collections.Concurrent.ConcurrentQueue<bool>();
        MemoryTrim.Collector = blocking => modes.Enqueue(blocking);
        MemoryTrim.Now = () => Volatile.Read(ref clock);
        MemoryTrim.Quiet = TimeSpan.FromMilliseconds(50);
        try
        {
            MemoryTrim.ForgetAppState();
            MemoryTrim.LongIdle = TimeSpan.FromSeconds(60); // compared with the fake clock
            MemoryTrim.SetAppInBackground(false);           // the desktop reports its windows
            MemoryTrim.SetAudioPlaying(true);
            MemoryTrim.SetAudioPlaying(false);              // paused, window on screen
            Interlocked.Add(ref clock, 120_000);            // even long after the pause

            MemoryTrim.RequestAfterIdle("window state");
            await WaitUntil(() => !modes.IsEmpty);
            Assert.Equal(new[] { false }, modes.ToArray()); // on screen: background only

            // Paused again, then hidden a moment later: still a resume away (media keys, tray).
            modes.Clear();
            MemoryTrim.SetAudioPlaying(true);
            MemoryTrim.SetAudioPlaying(false);
            MemoryTrim.SetAppInBackground(true);
            Interlocked.Add(ref clock, 59_000);
            MemoryTrim.RequestAfterIdle("window in the background");
            await WaitUntil(() => !modes.IsEmpty);
            Assert.Equal(new[] { false }, modes.ToArray());

            // A minute idle and still hidden: the compaction it was owed runs.
            modes.Clear();
            Interlocked.Add(ref clock, 1_000);
            MemoryTrim.RequestAfterIdle("window in the background");
            await WaitUntil(() => !modes.IsEmpty);
            Assert.Equal(new[] { true }, modes.ToArray());
        }
        finally
        {
            MemoryTrim.SetAudioPlaying(false);
            MemoryTrim.ForgetAppState();
            MemoryTrim.Collector = null;
            MemoryTrim.Now = now;
            MemoryTrim.Quiet = quiet;
            MemoryTrim.LongIdle = longIdle;
            System.Runtime.GCSettings.LatencyMode = original;
        }
    }

    [Fact]
    public async Task Desktop_AnOwedCompaction_IsAskedFor_WhenTheAppGoesToTheBackground()
    {
        var quiet = MemoryTrim.Quiet;
        var longIdle = MemoryTrim.LongIdle;
        var now = MemoryTrim.Now;
        var original = System.Runtime.GCSettings.LatencyMode;
        long clock = 5_000_000;
        var modes = new System.Collections.Concurrent.ConcurrentQueue<bool>();
        MemoryTrim.Collector = blocking => modes.Enqueue(blocking);
        MemoryTrim.Now = () => Volatile.Read(ref clock);
        MemoryTrim.Quiet = TimeSpan.FromMilliseconds(50);
        // Small, so the deferred requests fire within the test; the idle rule then compares the
        // fake clock (which only moves when the test says so) against it.
        MemoryTrim.LongIdle = TimeSpan.FromMilliseconds(50);
        try
        {
            MemoryTrim.ForgetAppState();
            MemoryTrim.SetAppInBackground(false);
            MemoryTrim.SetAudioPlaying(true);
            MemoryTrim.RequestAfterIdle("startup");
            await WaitUntil(() => !modes.IsEmpty);
            Assert.Equal(new[] { false }, modes.ToArray()); // playing: background, compaction owed

            modes.Clear();
            MemoryTrim.SetAudioPlaying(false); // asks again after LongIdle: the window is on screen
            await WaitUntil(() => !modes.IsEmpty);
            Assert.Equal(new[] { false }, modes.ToArray());

            modes.Clear();
            Interlocked.Add(ref clock, 1_000);
            MemoryTrim.SetAppInBackground(true); // owed: this asks for it
            await WaitUntil(() => !modes.IsEmpty);
            Assert.Equal(new[] { true }, modes.ToArray());

            modes.Clear();
            MemoryTrim.SetAppInBackground(false);
            MemoryTrim.SetAppInBackground(true); // nothing owed any more
            await Task.Delay(200);
            Assert.Empty(modes);
        }
        finally
        {
            MemoryTrim.ForgetAppState();
            MemoryTrim.Collector = null;
            MemoryTrim.Now = now;
            MemoryTrim.Quiet = quiet;
            MemoryTrim.LongIdle = longIdle;
            System.Runtime.GCSettings.LatencyMode = original;
        }
    }

    [Fact]
    public void TheMainWindow_ReportsTheAppInTheBackground_OnlyWithoutAMiniPlayer()
    {
        var src = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Noctis", "Views", "MainWindow.axaml.cs"));
        Assert.Contains("MemoryTrim.SetAppInBackground(IsInBackground && _miniPlayer == null);", src);
        Assert.Contains("MemoryTrim.SetAppInBackground(false);", src);
    }

    [Fact]
    public void ThePlayerStateDrivesTheGcMode()
    {
        var vm = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Noctis", "ViewModels", "MainWindowViewModel.cs"));
        Assert.Contains("e.PropertyName == nameof(PlayerViewModel.State))\n                Services.MemoryTrim.SetAudioPlaying(Player.IsPlaying);", vm);
    }

    [Fact]
    public void StartupAndScan_RequestATrim_WhenTheyFinish()
    {
        var vm = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Noctis", "ViewModels", "MainWindowViewModel.cs"));
        Assert.Contains("MemoryTrim.RequestAfterIdle(\"startup\")", vm);
        Assert.Contains("MemoryTrim.RequestAfterIdle(\"startup scan\")", vm);
        // The artwork backfill is a fire-and-forget task started by the library load,
        // outside both of the above; it produces most of the dead cover arrays.
        var lib = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Noctis.Core", "Services", "LibraryService.cs"));
        Assert.Contains("MemoryTrim.RequestAfterIdle(\"library background init\")", lib);
    }

    [Theory]
    [InlineData("Services/VlcAudioPlayer.cs", "ReadReplayGainTags(")]
    [InlineData("../Noctis.Core/Services/MetadataService.cs", "ReadFileInfo(")]
    [InlineData("../Noctis.Core/Services/MetadataService.cs", "ReadTrackMetadata(string filePath, out byte[]? embeddedArt)")]
    public void TagReadsThatDoNotNeedPictures_OpenFilesWithPictureLazy(string relPath, string method)
    {
        var src = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Noctis", relPath.Replace('/', Path.DirectorySeparatorChar)));
        var start = src.IndexOf(method, StringComparison.Ordinal);
        Assert.True(start >= 0, $"{method} not found in {relPath}");
        var open = src.IndexOf("TagLib.File.Create(", start, StringComparison.Ordinal);
        Assert.True(open >= 0, $"no TagLib.File.Create after {method}");
        var line = src.Substring(open, src.IndexOf('\n', open) - open);
        Assert.Contains("ReadStyle.PictureLazy", line);
    }

    private static async Task WaitUntil(Func<bool> done)
    {
        for (var i = 0; i < 200 && !done(); i++)
            await Task.Delay(10);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Noctis.sln"))
                || Directory.Exists(Path.Combine(dir.FullName, "src", "Noctis", "Assets", "Icons")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate repo root from " + AppContext.BaseDirectory);
    }
}

// MemoryTrim is static: windows and view-models built by other test classes report to it
// (SetAppInBackground, SetAudioPlaying), so these run with nothing else in parallel.
[CollectionDefinition("MemoryTrim global state", DisableParallelization = true)]
public sealed class MemoryTrimGlobalStateCollection { }
