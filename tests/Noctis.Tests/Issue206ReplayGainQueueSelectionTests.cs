using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #206 "Using ReplayGain shortcut ignores file selection". The queue selection is
/// keyed by row of Player.QueueRows; the shortcut read those indices against Up Next. With
/// Keep Played Songs on the rows start with the played songs and the playing one, so the
/// selected songs further down mapped past the end of Up Next and the dialog opened on the
/// playing track. (The focus half of the bug is QueueKeyFocusTests.)
/// </summary>
public class Issue206ReplayGainQueueSelectionTests
{
    private static Track Trk(string name) => new()
    {
        Id = Guid.NewGuid(),
        Title = name,
        Artist = "A",
        FilePath = TestPaths.Primary("t", $"{name}.mp3"),
        Duration = TimeSpan.FromMinutes(3)
    };

    private static (PlayerViewModel vm, FakeAudioPlayer player) Played(bool keepPlayed)
    {
        var player = new FakeAudioPlayer();
        var vm = new PlayerViewModel(player, new FakeLibraryService(),
            new TestPersistenceService(), new FakeAnimatedCoverService()) { KeepPlayedInQueue = keepPlayed };
        vm.ReplaceQueueAndPlay(new[] { "a", "b", "c", "d", "e", "f" }.Select(Trk).ToArray(), 0);
        for (var i = 0; i < 2; i++)
        {
            player.RaiseTrackEnded();
            Dispatcher.UIThread.RunJobs();
        }
        return (vm, player); // a, b played | c playing | d, e, f up next
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void SelectedQueueRows_AreTheTracksTheScanGets(bool keepPlayed)
    {
        var (vm, _) = Played(keepPlayed);
        Assert.Equal("c", vm.CurrentTrack!.Title);
        var selection = new QueueRowSelection<Track>(vm.QueueRows);

        // Select the last two rows (e and f), wherever Up Next starts in the list.
        var last = vm.QueueRows.Count - 1;
        selection.SelectOnly(last - 1);
        selection.SelectRangeTo(last);

        var tracks = MainWindow.QueueSelectionTracks(vm, selection);
        Assert.Equal(new[] { "e", "f" }, tracks.Select(t => t.Title));
        Assert.Same(tracks, TrackSelection.ForReplayGain(tracks, null, vm.CurrentTrack));
    }

    [AvaloniaFact]
    public void PlayedAndPlayingRows_CanBeScannedToo()
    {
        var (vm, _) = Played(keepPlayed: true);
        var selection = new QueueRowSelection<Track>(vm.QueueRows);
        selection.SelectOnly(0);
        selection.SelectRangeTo(2);
        Assert.Equal(new[] { "a", "b", "c" }, MainWindow.QueueSelectionTracks(vm, selection).Select(t => t.Title));
    }
}
