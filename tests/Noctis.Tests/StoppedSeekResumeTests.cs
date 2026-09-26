using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Audit A26: a seek on a Stopped track has no playing media to move. On a restored
/// session the player dropped it and Play jumped back to the saved position; after
/// stop-after-current it restarted the ended media behind a Stopped UI. The seek must
/// become the position the next Play starts from, without touching the audio player.
/// </summary>
public class StoppedSeekResumeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private static Track Trk(string title) => new()
    {
        Id = Guid.NewGuid(),
        Title = title,
        Artist = "A",
        FilePath = Path.Combine("C:", "Music", title + ".mp3"),
        Duration = TimeSpan.FromMinutes(3),
    };

    private async Task<(PlayerViewModel vm, FakeAudioPlayer audio, Track track)> RestoredAt(double positionSeconds)
    {
        var track = Trk("restored");
        var library = new FakeLibraryService();
        library.TrackList.Add(track);
        var persistence = new PersistenceService(Path.Combine(_root, "data"));
        await persistence.SaveQueueStateAsync(new QueueState { CurrentTrackId = track.Id, PositionSeconds = positionSeconds });

        var audio = new FakeAudioPlayer();
        var vm = new PlayerViewModel(audio, library, persistence, new FakeAnimatedCoverService());
        await vm.RestoreQueueStateAsync();
        Assert.Equal(PlaybackState.Stopped, vm.State);
        Assert.Equal(positionSeconds, vm.Position.TotalSeconds, 3);
        return (vm, audio, track);
    }

    [AvaloniaFact]
    public async Task Restored_SeekThenPlay_StartsAtTheSeekTarget_NotTheSavedPosition()
    {
        var (vm, audio, track) = await RestoredAt(83);

        vm.SeekToPositionCommand.Execute(0.5); // 1:30

        Assert.Empty(audio.Seeks);
        vm.PlayPauseCommand.Execute(null);
        Assert.Equal(new[] { track.FilePath }, audio.PlayedPaths);
        Assert.Equal(90_000, audio.PendingSeekMs);
        Assert.Equal(90, vm.Position.TotalSeconds, 3);
    }

    [AvaloniaFact]
    public async Task Restored_SliderDragToStartThenPlay_StartsFromTheBeginning()
    {
        var (vm, audio, _) = await RestoredAt(83);

        vm.BeginSeek();
        vm.PositionFraction = 0;
        vm.EndSeek();

        Assert.Empty(audio.Seeks);
        vm.PlayPauseCommand.Execute(null);
        Assert.Equal(-1, audio.PendingSeekMs);
        Assert.Equal(TimeSpan.Zero, vm.Position);
    }

    [AvaloniaFact]
    public async Task Restored_PreviousRestart_ThenPlay_StartsFromTheBeginning()
    {
        var (vm, audio, _) = await RestoredAt(83);

        vm.PreviousCommand.Execute(null); // past 3s: restart the current track

        Assert.Empty(audio.Seeks);
        vm.PlayPauseCommand.Execute(null);
        Assert.Equal(-1, audio.PendingSeekMs);
        Assert.Equal(TimeSpan.Zero, vm.Position);
    }

    [AvaloniaFact]
    public void StoppedAfterCurrent_Seek_DoesNotRestartTheAudio_AndPlayStartsThere()
    {
        var audio = new FakeAudioPlayer();
        var vm = new PlayerViewModel(audio, new FakeLibraryService(), new TestPersistenceService(), new FakeAnimatedCoverService());
        var a = Trk("a");
        vm.ReplaceQueueAndPlay(new[] { a, Trk("b") }, 0);
        vm.StopAfterCurrentTrack = true;
        audio.RaiseTrackEnded();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(PlaybackState.Stopped, vm.State);
        Assert.Equal(a.Id, vm.CurrentTrack?.Id);

        vm.SeekToPositionCommand.Execute(0.25); // 0:45

        Assert.Empty(audio.Seeks);
        Assert.Equal(PlaybackState.Stopped, vm.State);
        vm.PlayPauseCommand.Execute(null);
        Assert.Equal(new[] { a.FilePath, a.FilePath }, audio.PlayedPaths);
        Assert.Equal(45_000, audio.PendingSeekMs);
        Assert.Equal(PlaybackState.Playing, vm.State);
    }

    [AvaloniaFact]
    public void Playing_Seek_StillGoesToTheAudioPlayer()
    {
        var audio = new FakeAudioPlayer();
        var vm = new PlayerViewModel(audio, new FakeLibraryService(), new TestPersistenceService(), new FakeAnimatedCoverService());
        vm.ReplaceQueueAndPlay(new[] { Trk("a") }, 0);

        vm.SeekToPositionCommand.Execute(0.5);

        Assert.Equal(new[] { TimeSpan.FromSeconds(90) }, audio.Seeks);
    }
}
