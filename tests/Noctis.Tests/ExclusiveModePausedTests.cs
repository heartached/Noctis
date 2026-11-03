using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Localization;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Exclusive Mode falling back mid-session (the device could not be opened, or a DAC was
/// unplugged): the saved setting stays on, the toggle stays on with a "paused" notice, the
/// "on" that every audio-settings apply re-sends does not retry, and off then on does.
/// </summary>
public class ExclusiveModePausedTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private sealed class NoOpPlayHistoryService : IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    private SettingsViewModel CreateViewModel() => new(
        new PersistenceService(_root), new FakeLibraryService(), new NoOpPlayHistoryService());

    [Fact]
    public void Gate_HoldsOnWhilePaused_OffClearsIt_SoOffThenOnRetries()
    {
        var paused = false;
        Assert.True(VlcAudioPlayer.ExclusiveRequestAllowed(true, ref paused)); // normal on

        paused = true; // the session fell back
        Assert.False(VlcAudioPlayer.ExclusiveRequestAllowed(true, ref paused)); // a re-sent "on" is held
        Assert.True(paused);

        Assert.True(VlcAudioPlayer.ExclusiveRequestAllowed(false, ref paused)); // the user turns it off
        Assert.False(paused);
        Assert.True(VlcAudioPlayer.ExclusiveRequestAllowed(true, ref paused)); // and on again: retried
    }

    [AvaloniaFact]
    public async Task Fallback_KeepsTheSavedChoice_ShowsThePausedNotice_AndOffThenOnRetries()
    {
        var vm = CreateViewModel();
        await vm.LoadAsync();
        var audio = new FakeAudioPlayer();
        vm.SetAudioPlayer(audio);
        vm.ExclusiveAudioEnabled = true;
        await vm.SaveAsync();

        // The device could not be opened: the player falls back and reports it.
        audio.ExclusiveModePaused = true;
        audio.RaiseOutputModeChanged("No audio output could be opened in exclusive mode.");
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.ExclusiveAudioEnabled); // the toggle still shows on
        Assert.Equal(Loc.T("Settings.ExclusiveModePaused"), vm.ExclusiveAudioStatus);
        Assert.DoesNotContain("—", vm.ExclusiveAudioStatus);
        await vm.SaveAsync();
        var reloaded = CreateViewModel();
        await reloaded.LoadAsync();
        Assert.True(reloaded.ExclusiveAudioEnabled); // not turned off for good

        // Off and on again: the player is asked for off, then on (which retries).
        audio.ExclusiveRequests.Clear();
        vm.ExclusiveAudioEnabled = false;
        Assert.Equal("", vm.ExclusiveAudioStatus);
        vm.ExclusiveAudioEnabled = true;
        Assert.Equal(new[] { false, true }, audio.ExclusiveRequests);

        // The retry worked: the player's own status shows again.
        audio.ExclusiveModePaused = false;
        audio.RaiseOutputModeChanged("Exclusive output active: 44.1 kHz / 24-bit");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Exclusive output active: 44.1 kHz / 24-bit", vm.ExclusiveAudioStatus);
    }
}
