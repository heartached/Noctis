using System;
using System.Threading;
using System.Threading.Tasks;
using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

public class LibraryWatcherServiceTests
{
    // Refresh is called from the UI thread (launch, music-folder changes, the Watch
    // Folders toggle). On Linux, enabling a recursive watcher walks the whole folder
    // tree on the calling thread, so the rebuild must not block the caller.
    [Fact]
    public async Task Refresh_ReturnsWhileTheRebuildIsStillRunning()
    {
        var ct = TestContext.Current.CancellationToken;
        var rebuildStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseRebuild = new ManualResetEventSlim();
        using var watcher = new LibraryWatcherService(new FakeLibraryService(), () =>
        {
            rebuildStarted.TrySetResult();
            releaseRebuild.Wait(TimeSpan.FromSeconds(30), ct);
            return new AppSettings { WatchFoldersEnabled = false };
        });

        var refresh = Task.Run(watcher.Refresh, ct);
        var returned = await Task.WhenAny(refresh, Task.Delay(TimeSpan.FromSeconds(5), ct)) == refresh;
        var started = await Task.WhenAny(rebuildStarted.Task, Task.Delay(TimeSpan.FromSeconds(5), ct)) == rebuildStarted.Task;
        releaseRebuild.Set();
        await refresh;

        Assert.True(started, "the watcher rebuild never ran");
        Assert.True(returned, "Refresh blocked the caller until the watcher rebuild finished");
    }
}
