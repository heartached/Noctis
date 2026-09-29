using Avalonia.Headless.XUnit;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Audit A27: a mute saved with the queue came back only in the UI. The restore set the
/// view-model flag, but the audio player stayed unmuted, so the app showed Muted, played
/// audibly, and the first mute press only flipped the UI back.
/// </summary>
public class MuteRestoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    [AvaloniaFact]
    public async Task Restore_SavedMute_ReachesTheAudioPlayer_AndOneToggleUnmutesIt()
    {
        var track = new Track
        {
            Id = Guid.NewGuid(),
            Title = "muted",
            Artist = "A",
            FilePath = Path.Combine("C:", "Music", "muted.mp3"),
            Duration = TimeSpan.FromMinutes(3),
        };
        var library = new FakeLibraryService();
        library.TrackList.Add(track);
        var persistence = new PersistenceService(Path.Combine(_root, "data"));
        await persistence.SaveQueueStateAsync(new QueueState { CurrentTrackId = track.Id, IsMuted = true });

        var audio = new FakeAudioPlayer();
        var vm = new PlayerViewModel(audio, library, persistence, new FakeAnimatedCoverService());
        await vm.RestoreQueueStateAsync();

        Assert.True(vm.IsMuted);
        Assert.True(audio.IsMuted);

        vm.ToggleMuteCommand.Execute(null);
        Assert.False(vm.IsMuted);
        Assert.False(audio.IsMuted);
    }

    [AvaloniaFact]
    public void ToggleMute_And_AdjustWhileMuted_KeepTheAudioPlayerInStep()
    {
        var audio = new FakeAudioPlayer();
        var vm = new PlayerViewModel(audio, new FakeLibraryService(), new TestPersistenceService(), new FakeAnimatedCoverService());

        vm.ToggleMuteCommand.Execute(null);
        Assert.True(audio.IsMuted);

        vm.UnmuteForAdjust();
        Assert.False(vm.IsMuted);
        Assert.False(audio.IsMuted);
    }
}
