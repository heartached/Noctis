using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

public class DeferredTagWriterTests
{
    private static string P(string name) => TestPaths.Primary("Music", name);

    [Fact]
    public async Task SamePathAndKey_Coalesce_OnlyLatestWriteRuns()
    {
        using var writer = new DeferredTagWriter(TimeSpan.FromHours(1));
        var runs = new List<int>();
        writer.Enqueue(P("a.mp3"), "rating", () => runs.Add(1));
        writer.Enqueue(P("a.mp3"), "rating", () => runs.Add(2));
        writer.Enqueue(P("a.mp3"), "rating", () => runs.Add(3));
        Assert.Equal(1, writer.PendingCount);

        await writer.FlushAsync();

        Assert.Equal(new[] { 3 }, runs);
        Assert.Equal(0, writer.PendingCount);
    }

    [Fact]
    public async Task DifferentKeys_OnTheSameFile_BothRun()
    {
        using var writer = new DeferredTagWriter(TimeSpan.FromHours(1));
        var runs = new List<string>();
        writer.Enqueue(P("a.mp3"), "rating", () => runs.Add("rating"));
        writer.Enqueue(P("a.mp3"), "lyrics", () => runs.Add("lyrics"));

        await writer.FlushAsync();

        Assert.Equal(2, runs.Count);
        Assert.Contains("rating", runs);
        Assert.Contains("lyrics", runs);
    }

    [Fact]
    public async Task QuietFlush_SkipsTheFileInUse_AndKeepsItPending()
    {
        using var writer = new DeferredTagWriter(TimeSpan.FromHours(1));
        var runs = new List<string>();
        writer.InUsePath = () => P("playing.mp3");
        writer.Enqueue(P("playing.mp3"), "rating", () => runs.Add("playing"));
        writer.Enqueue(P("idle.mp3"), "rating", () => runs.Add("idle"));

        await writer.FlushDueAsync();

        Assert.Equal(new[] { "idle" }, runs);
        Assert.Equal(1, writer.PendingCount);
    }

    [Fact]
    public async Task ShutdownFlush_WritesTheFileInUseToo()
    {
        using var writer = new DeferredTagWriter(TimeSpan.FromHours(1));
        var runs = new List<string>();
        writer.InUsePath = () => P("playing.mp3");
        writer.Enqueue(P("playing.mp3"), "rating", () => runs.Add("playing"));

        await writer.FlushAsync();

        Assert.Equal(new[] { "playing" }, runs);
        Assert.Equal(0, writer.PendingCount);
    }

    [Fact]
    public async Task QuietPeriod_FlushesOnItsOwn()
    {
        using var writer = new DeferredTagWriter(TimeSpan.FromMilliseconds(30));
        var done = new TaskCompletionSource();
        writer.Enqueue(P("a.mp3"), "rating", () => done.TrySetResult());

        var finished = await Task.WhenAny(done.Task, Task.Delay(5000));

        Assert.Same(done.Task, finished);
    }

    [Fact]
    public async Task FailingWrite_DoesNotBlockOthers()
    {
        using var writer = new DeferredTagWriter(TimeSpan.FromHours(1));
        var ok = false;
        writer.Enqueue(P("bad.mp3"), "rating", () => throw new IOException("locked"));
        writer.Enqueue(P("good.mp3"), "rating", () => ok = true);

        await writer.FlushAsync();

        Assert.True(ok);
        Assert.Equal(0, writer.PendingCount);
    }
}
