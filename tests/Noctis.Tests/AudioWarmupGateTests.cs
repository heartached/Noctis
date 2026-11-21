using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Noctis.Helpers;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Discord 11-16 ("Bug at Start", 1.6.7 -> 1.6.9): the first start after an update rebuilt
/// libvlc's plugins cache, libvlc-warm-done landed at 38.9 s and no window existed until
/// 40.9 s because the main window's view-model graph waited for the player on the UI thread.
/// These pin the new order: a starting window while the warm-up runs, the main window built
/// only once it has ended.
/// </summary>
public class AudioWarmupGateTests
{
    [Fact]
    public void FinishedWarmup_NoStartingWindow()
    {
        Assert.False(AudioWarmupGate.ShouldShowStartingWindow(Task.CompletedTask, false, TimeSpan.Zero));
    }

    [Fact]
    public void NoWarmup_NoStartingWindow()
    {
        Assert.False(AudioWarmupGate.ShouldShowStartingWindow(null, false, TimeSpan.Zero));
    }

    [Fact]
    public void RunningWarmup_ShowsStartingWindow()
    {
        var warm = new TaskCompletionSource();
        Assert.True(AudioWarmupGate.ShouldShowStartingWindow(warm.Task, false, TimeSpan.FromMilliseconds(10)));
    }

    [Fact]
    public void RunningWarmup_StartMinimized_NoStartingWindow()
    {
        var warm = new TaskCompletionSource();
        Assert.False(AudioWarmupGate.ShouldShowStartingWindow(warm.Task, true, TimeSpan.FromMilliseconds(10)));
    }

    [Fact]
    public void WarmupEndingInsideGrace_NoStartingWindow()
    {
        var warm = Task.Delay(20);
        Assert.False(AudioWarmupGate.ShouldShowStartingWindow(warm, false, TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void FaultedWarmup_NoStartingWindow()
    {
        // The main window resolve reports the broken libvlc, as before.
        Assert.False(AudioWarmupGate.ShouldShowStartingWindow(
            Task.FromException(new InvalidOperationException("libvlc")), false, TimeSpan.Zero));
    }

    [Fact]
    public async Task MainWindowBuild_WaitsForWarmup_ThenRunsOnUiPost()
    {
        var warm = new TaskCompletionSource();
        var posted = new List<Action>();
        var built = 0;
        var postedSignal = new TaskCompletionSource();

        AudioWarmupGate.RunWhenWarm(warm.Task,
            a => { lock (posted) posted.Add(a); postedSignal.TrySetResult(); },
            () => built++,
            _ => Assert.Fail("no failure expected"));

        await Task.Delay(50);
        lock (posted) Assert.Empty(posted); // nothing reaches the UI thread while the player builds
        Assert.Equal(0, built);

        warm.SetResult();
        await postedSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, built); // only through the UI post, not on the warm-up thread
        lock (posted) Assert.Single(posted).Invoke();
        Assert.Equal(1, built);
    }

    [Fact]
    public async Task FaultedWarmup_StillBuilds()
    {
        var built = new TaskCompletionSource();
        AudioWarmupGate.RunWhenWarm(Task.FromException(new InvalidOperationException("libvlc")),
            a => a(), () => built.TrySetResult(), _ => { });
        await built.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task BuildFailure_GoesToFail()
    {
        var failed = new TaskCompletionSource<Exception>();
        AudioWarmupGate.RunWhenWarm(Task.CompletedTask,
            a => a(), () => throw new InvalidOperationException("broken libvlc"), ex => failed.TrySetResult(ex));
        var ex = await failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("broken libvlc", ex.Message);
    }
}
