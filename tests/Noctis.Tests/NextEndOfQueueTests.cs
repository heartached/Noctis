using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Models;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Discord (Andre, 10-03): "on Shuffle, sometimes it doesn't let me press Next" — 33 presses
/// logged "Next | queueLen=0". Next has nowhere to go at the end of the queue (unless Repeat
/// All wraps), but its buttons stayed lit; NextCommand.CanExecute now says so, and the bar and
/// mini player buttons dim off it.
/// </summary>
public class NextEndOfQueueTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"noctis-next-end-{Guid.NewGuid():N}");

    public NextEndOfQueueTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private Track Trk(string name)
    {
        var path = Path.Combine(_dir, $"{name}.mp3");
        File.WriteAllBytes(path, new byte[] { 0 });
        return new Track { Id = Guid.NewGuid(), Title = name, Artist = "A", FilePath = path, Duration = TimeSpan.FromMinutes(3) };
    }

    private static PlayerViewModel NewPlayer() => new(new FakeAudioPlayer(), new FakeLibraryService(),
        new TestPersistenceService(), new FakeAnimatedCoverService());

    [AvaloniaFact]
    public void LastSongInTheQueue_NextCannotExecute_UntilSomethingIsQueued()
    {
        var player = NewPlayer();
        var raised = 0;
        player.NextCommand.CanExecuteChanged += (_, _) => raised++;

        player.ReplaceQueueAndPlay(new[] { Trk("a") }, 0);
        Dispatcher.UIThread.RunJobs();
        Assert.False(player.NextCommand.CanExecute(null));

        raised = 0;
        player.AddToQueue(Trk("b"));
        Assert.True(raised > 0);
        Assert.True(player.NextCommand.CanExecute(null));

        player.NextCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.False(player.NextCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void RepeatAll_KeepsNextAvailable_OnTheLastSong()
    {
        var player = NewPlayer();
        player.ReplaceQueueAndPlay(new[] { Trk("a") }, 0);
        Dispatcher.UIThread.RunJobs();
        Assert.False(player.NextCommand.CanExecute(null));

        while (player.RepeatMode != RepeatMode.All)
            player.CycleRepeatCommand.Execute(null);

        Assert.True(player.NextCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void ShuffleOn_WithAnEmptyQueue_NextCannotExecute()
    {
        var player = NewPlayer();
        player.ReplaceQueueAndPlay(new[] { Trk("a"), Trk("b") }, 0);
        Dispatcher.UIThread.RunJobs();
        player.ToggleShuffleCommand.Execute(null);
        Assert.True(player.NextCommand.CanExecute(null));

        player.NextCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(player.IsShuffleEnabled);
        Assert.Empty(player.UpNext);
        Assert.False(player.NextCommand.CanExecute(null));
    }
}
