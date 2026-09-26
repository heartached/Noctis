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
