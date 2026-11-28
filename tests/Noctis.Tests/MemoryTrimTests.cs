using System;
using System.IO;
using System.Linq;
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
    public void NoCodePath_AsksForABlockingCollection()
    {
        // 11-27 follow-up: Play must never land behind a blocking collection the app asked for.
        // The compacting trim (GCCollectionMode.Aggressive, blocking) paused 306 ms on a 575 MB
        // harness heap and a press landing in it waited 345 ms for audio.
        var src = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Noctis.Core", "Services", "MemoryTrim.cs"));
        var code = string.Join("\n", src.Split('\n').Where(l => !l.TrimStart().StartsWith("//")));
        var calls = System.Text.RegularExpressions.Regex.Matches(code, @"GC\.Collect\(([^;]*)\);");
        Assert.NotEmpty(calls);
        foreach (System.Text.RegularExpressions.Match call in calls)
        {
            Assert.Contains("blocking: false", call.Value);
            Assert.Contains("compacting: false", call.Value);
        }
        Assert.DoesNotContain("blocking: true", code);
        Assert.DoesNotContain("GCCollectionMode.Aggressive", code);
        Assert.DoesNotContain("LargeObjectHeapCompactionMode", code); // CompactOnce rides on the next blocking gen2
    }

    [Fact]
    public void TheBackgroundCollection_NeverTurnsBlocking_AndPutsTheLatencyModeBack()
    {
        // "blocking: false" alone is only a wish: in Interactive the runtime turned 4 of 48 such
        // requests into full blocking gen2s of 392-841 ms (harness). The trim requests it in
        // SustainedLowLatency, held until the background collection ends.
        var original = System.Runtime.GCSettings.LatencyMode;
        var garbage = new System.Collections.Generic.List<object[]>();
        // The trim only collects heaps of MinHeapToCollect and up (a smaller one is run blocking by
        // the runtime, see ShouldCollect): build one, as the app has after its startup burst.
        var live = new System.Collections.Generic.List<object[]>();
        while (GC.GetTotalMemory(false) < MemoryTrim.MinHeapToCollect + (16L << 20))
            live.Add(new object[] { new string('x', 40), new byte[120] });
        try
        {
            MemoryTrim.SetAudioPlaying(false);
            System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.Interactive;
            var gen2 = GC.CollectionCount(2);
            for (var round = 0; round < 3; round++)
            {
                // Small dead objects for the collection to free. Made before the baseline: the
                // test's own allocation may start a collection of the runtime's choosing, and
                // only the trim's request is under test (large arrays here once did, 11-28).
                for (var i = 0; i < 100_000; i++) garbage.Add(new object[4]);
                garbage.Clear();
                var fullBlocking = GC.GetGCMemoryInfo(GCKind.FullBlocking).Index;
                Assert.True(MemoryTrim.BackgroundCollect(TimeSpan.FromSeconds(10)), "the background gen2 never finished");
                Assert.Equal(fullBlocking, GC.GetGCMemoryInfo(GCKind.FullBlocking).Index);
                Assert.Equal(System.Runtime.GCLatencyMode.Interactive, System.Runtime.GCSettings.LatencyMode);
            }
            Assert.True(GC.CollectionCount(2) >= gen2 + 3);

            // While music plays the mode stays SustainedLowLatency after the trim's own hold.
            MemoryTrim.SetAudioPlaying(true);
            Assert.True(MemoryTrim.BackgroundCollect(TimeSpan.FromSeconds(10)));
            Assert.Equal(System.Runtime.GCLatencyMode.SustainedLowLatency, System.Runtime.GCSettings.LatencyMode);
            MemoryTrim.SetAudioPlaying(false);
            Assert.Equal(System.Runtime.GCLatencyMode.Interactive, System.Runtime.GCSettings.LatencyMode);
        }
        finally
        {
            MemoryTrim.SetAudioPlaying(false);
            System.Runtime.GCSettings.LatencyMode = original;
            GC.KeepAlive(live);
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

    [Theory]
    [InlineData(1L << 20, false)]
    [InlineData(25L << 20, false)] // runtime runs the request blocking at this size (harness)
    [InlineData(64L << 20, true)]
    [InlineData(500L << 20, true)]
    public void ASmallHeap_IsNotCollected(long allocated, bool expected) =>
        Assert.Equal(expected, MemoryTrim.ShouldCollect(allocated));

    [Fact]
    public async Task HostsThatDoNotReportWindows_NeverTrimTheWorkingSet_AndOweNothing()
    {
        var quiet = MemoryTrim.Quiet;
        var original = System.Runtime.GCSettings.LatencyMode;
        var longIdle = MemoryTrim.LongIdle;
        var modes = new System.Collections.Concurrent.ConcurrentQueue<bool>();
        MemoryTrim.ForgetAppState(); // server / phone
        MemoryTrim.Collector = trimWorkingSet => modes.Enqueue(trimWorkingSet);
        MemoryTrim.Quiet = TimeSpan.FromMilliseconds(50);
        MemoryTrim.LongIdle = TimeSpan.FromMilliseconds(50);
        try
        {
            MemoryTrim.SetAudioPlaying(true);
            MemoryTrim.RequestAfterIdle("scan");
            await WaitUntil(() => !modes.IsEmpty);
            Assert.Equal(new[] { false }, modes.ToArray());

            modes.Clear();
            MemoryTrim.SetAudioPlaying(false);
            MemoryTrim.RequestAfterIdle("scan");
            await WaitUntil(() => !modes.IsEmpty);
            await Task.Delay(200);
            Assert.Equal(new[] { false }, modes.ToArray()); // a background collection, nothing owed after it

            modes.Clear();
            MemoryTrim.SetAudioPlaying(true);
            MemoryTrim.SetAudioPlaying(false);
            await Task.Delay(200);
            Assert.Empty(modes);
        }
        finally
        {
            MemoryTrim.SetAudioPlaying(false);
            await Task.Delay(150);
            MemoryTrim.Collector = null;
            MemoryTrim.Quiet = quiet;
            MemoryTrim.LongIdle = longIdle;
            System.Runtime.GCSettings.LatencyMode = original;
        }
    }

    [Theory]
    // playing, app in background (null = host does not report), idle ms -> working-set trim
    [InlineData(true, null, 999_999, false)]
    [InlineData(false, null, 999_999, false)]   // server / phone: never
    [InlineData(true, true, 999_999, false)]    // never while music plays
    [InlineData(false, false, 999_999, false)]  // a play button on screen (window or mini player)
    [InlineData(false, true, 59_999, false)]    // paused a moment ago: a resume is likely
    [InlineData(false, true, 60_000, true)]     // in the background and idle for a minute
    public void TheWorkingSetTrim_OnlyWhenNothingCanBeResumedOnScreen(bool playing, bool? background, long idleMs, bool expected) =>
        Assert.Equal(expected, MemoryTrim.MayTrimWorkingSet(playing, background, idleMs, TimeSpan.FromSeconds(60)));

    [Fact]
    public async Task Desktop_PausedOnScreen_CollectsInTheBackground_AndTrimsTheWorkingSetOnceHiddenAndIdle()
    {
        // Follow-up 11-27: the working set is only given back with nothing of the app on screen
        // and playback idle a minute; before that the trim is the background collection alone.
        var quiet = MemoryTrim.Quiet;
        var longIdle = MemoryTrim.LongIdle;
        var now = MemoryTrim.Now;
        var original = System.Runtime.GCSettings.LatencyMode;
        long clock = 1_000_000;
        var modes = new System.Collections.Concurrent.ConcurrentQueue<bool>();
        MemoryTrim.Collector = trimWorkingSet => modes.Enqueue(trimWorkingSet);
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

            // A minute idle and still hidden: the working-set trim it was owed runs.
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
    public async Task Desktop_AnOwedWorkingSetTrim_IsAskedFor_WhenTheAppGoesToTheBackground()
    {
        var quiet = MemoryTrim.Quiet;
        var longIdle = MemoryTrim.LongIdle;
        var now = MemoryTrim.Now;
        var original = System.Runtime.GCSettings.LatencyMode;
        long clock = 5_000_000;
        var modes = new System.Collections.Concurrent.ConcurrentQueue<bool>();
        MemoryTrim.Collector = trimWorkingSet => modes.Enqueue(trimWorkingSet);
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
            Assert.Equal(new[] { false }, modes.ToArray()); // playing: background, working-set trim owed

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
        var vm = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Noctis", "ViewModels", "MainWindowViewModel.cs"))
            .Replace("\r\n", "\n"); // a Windows checkout may give CRLF
        Assert.Contains("e.PropertyName == nameof(PlayerViewModel.State))\n               Services.MemoryTrim.SetAudioPlaying(Player.IsPlaying);", vm);
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
