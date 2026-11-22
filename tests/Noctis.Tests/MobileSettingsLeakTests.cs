using System;
using System.Collections.Generic;
using Noctis.Mobile.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>Settings opened and closed many times leaves nothing of itself following the app.</summary>
public class MobileSettingsLeakTests
{
    private static int Listeners(object source, string evt) =>
        (source.GetType().GetField(evt, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(source) as Delegate)?.GetInvocationList().Length ?? 0;

    /// <summary>Listening History's card followed every play through weak handlers that only let go
    /// once a collection had run: until then each Settings opened since launch re-read the whole
    /// play log on every play. Closed, the sheet lets go at once.</summary>
    [Fact]
    public void ClosingSettings_StopsItsHistoryCardFollowingPlays()
    {
        using var rig = MobileFixtures.MakeRig(new[] { MobileFixtures.Song("Tone") });
        var shell = rig.Shell;
        var played = Listeners(shell.Player, nameof(NowPlayingViewModel.PlayRecorded));
        var prefs = Listeners(shell, nameof(ShellViewModel.HistoryPrefsChanged));
        var kept = new List<SettingsPageViewModel>();   // not collected yet, as on a phone between GCs
        for (var i = 0; i < 5; i++)
        {
            shell.OpenSettingsCommand.Execute(null);
            kept.Add(Assert.IsType<SettingsPageViewModel>(shell.CurrentPage));
            Assert.True(shell.TryHandleBack());
        }
        Assert.Equal(played, Listeners(shell.Player, nameof(NowPlayingViewModel.PlayRecorded)));
        Assert.Equal(prefs, Listeners(shell, nameof(ShellViewModel.HistoryPrefsChanged)));
        GC.KeepAlive(kept);
    }
}
