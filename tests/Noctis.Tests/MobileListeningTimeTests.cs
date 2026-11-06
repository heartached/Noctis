using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Noctis.Mobile.ViewModels;
using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Listening History's time is what was heard, not the length of every play (tester, beta 1: "3 h
/// 48 min" for a day of skipping), and the play-count threshold the desktop has (Settings →
/// Playback → Count a play after) works on the phone.
/// </summary>
public class MobileListeningTimeTests
{
    private static void Listen(MobileFixtures.Rig rig, int seconds)
    {
        for (var s = 1; s <= seconds; s++) rig.Player.RaisePositionChanged(TimeSpan.FromSeconds(s));
    }

    [AvaloniaFact]
    public void SkippingThroughSongs_CountsTheSecondsHeard_NotTheSongLengths()
    {
        var songs = Enumerable.Range(1, 10).Select(i => MobileFixtures.Song("S" + i)).ToArray();   // 90 s each
        using var rig = MobileFixtures.MakeRig(songs);

        for (var i = 0; i < songs.Length; i++)
        {
            rig.Shell.Player.PlayTracks(songs, i);
            Listen(rig, 5);
        }
        rig.Shell.Player.PlayTracks(new[] { songs[0] }, 0);   // the last skip lands too

        var vm = new ListeningHistoryViewModel(rig.Shell);
        Assert.Equal("10 plays", vm.WeekPlays);
        // 10 x 5 s heard. Counting lengths gave 15 min.
        Assert.Equal("50 s", vm.WeekTotal);
    }

    [AvaloniaFact]
    public void AFullListen_CountsTheSongOnce_AndASeekIsNotListening()
    {
        var song = MobileFixtures.Song("Full");   // 90 s
        using var rig = MobileFixtures.MakeRig(new[] { song });

        rig.Shell.Player.PlayTracks(new[] { song }, 0);
        Listen(rig, 20);
        rig.Player.RaisePositionChanged(TimeSpan.FromSeconds(80));   // a seek forward
        for (var s = 81; s <= 90; s++) rig.Player.RaisePositionChanged(TimeSpan.FromSeconds(s));
        rig.Shell.Player.PlayTracks(new[] { song }, 0);              // flushes the first

        Assert.Equal(30_000L, rig.History.EventList[0].ListenedMs);
    }

    [AvaloniaFact]
    public async Task CountAPlayAfter_HoldsThePlayBackUntilThatMuchWasHeard()
    {
        var song = MobileFixtures.Song("Slow");   // 90 s
        using var rig = MobileFixtures.MakeRig(new[] { song });
        await rig.Shell.LoadHistoryPrefsAsync();
        Assert.Equal(0, rig.Shell.PlayCountThresholdPercent);   // the computer's default: Immediately
        rig.Shell.SetPlayCountThresholdPercent(50);

        rig.Shell.Player.PlayTracks(new[] { song }, 0);
        Listen(rig, 44);
        Assert.Empty(rig.History.EventList);                    // 44 of 90 s: not yet
        rig.Player.RaisePositionChanged(TimeSpan.FromSeconds(45));   // 45 s = half of 90
        Assert.Single(rig.History.EventList);

        rig.Shell.Player.PlayTracks(new[] { song }, 0);         // skipped before 50%: no play
        Listen(rig, 10);
        rig.Shell.Player.PlayTracks(new[] { song }, 0);
        Assert.Single(rig.History.EventList);

        await rig.Shell.PendingHistorySave;
        Assert.Equal(50, (await rig.Persistence.LoadSettingsAsync()).PlayCountThresholdPercent);
    }

    [AvaloniaFact]
    public void TheRow_ShowsTheChoice_AndThePickerSetsIt()
    {
        using var rig = MobileFixtures.MakeRig();
        var vm = new ListeningHistoryViewModel(rig.Shell);
        Assert.Equal("Immediately", vm.CountText);

        vm.ChooseCountCommand.Execute(null);
        var prompt = Assert.IsType<PromptViewModel>(rig.Shell.Prompt);
        Assert.Equal(new[] { "Immediately", "25%", "50%", "75%", "90%" }, prompt.Actions.Select(a => a.Label));
        prompt.Actions[3].RunCommand.Execute(null);

        Assert.Equal(75, rig.Shell.PlayCountThresholdPercent);
        Assert.Equal("75%", vm.CountText);
    }

    [Fact]
    public void Heard_PrefersTheLoggedTime_AndFallsBackToHalfTheSongForOldEvents()
    {
        var song = TimeSpan.FromMinutes(4);
        PlayHistoryEvent E(long? ms = null, bool skipped = false) => new() { ListenedMs = ms, Skipped = skipped };

        Assert.Equal(TimeSpan.FromSeconds(7), PlayListening.Heard(E(7000), song));
        Assert.Equal(song, PlayListening.Heard(E(99_000_000), song));            // never more than the song
        Assert.Equal(TimeSpan.FromMinutes(2), PlayListening.Heard(E(), song));   // logged before the time existed
        Assert.Equal(TimeSpan.FromMinutes(1.5), PlayListening.Heard(E(), null)); // unknown length: half of 3 min
        Assert.Equal(TimeSpan.Zero, PlayListening.Heard(E(7000, skipped: true), song));
    }
}
