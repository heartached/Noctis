using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Noctis.Localization;
using Noctis.Mobile.Services;
using Noctis.Mobile.Services.Account;
using Noctis.Mobile.ViewModels;
using Noctis.Models;
using Xunit;
using FakeAccount = Noctis.Tests.MobileAccountTests.FakeAccount;

namespace Noctis.Tests;

/// <summary>Downloads from the computer: the album page's "Downloading 3 of 12" row, the Account
/// page opened mid-run, the system notification's calls (DownloadActivity) and the banner when a
/// download from a menu is refused.</summary>
public class MobileDownloadProgressTests
{
    private sealed class FakeNotifier : IDownloadNotifier
    {
        public List<string> Calls { get; } = new();
        public void ShowProgress(string title, string text, int done, int total) => Calls.Add($"progress|{title}|{text}|{done}/{total}");
        public void ShowFinished(string title, string text) => Calls.Add($"finished|{title}|{text}");
        public void Clear() => Calls.Add("clear");
    }

    private sealed class FakeKeeper : IDownloadKeeper
    {
        public List<string> Calls { get; } = new();
        public void Begin(long estimatedBytes) => Calls.Add($"begin|{estimatedBytes}");
        public void End() => Calls.Add("end");
        public event EventHandler? Paused;
        public event EventHandler? Resumed;
        public void Pause() => Paused?.Invoke(this, EventArgs.Empty);
        public void Resume() => Resumed?.Invoke(this, EventArgs.Empty);
    }

    private sealed class FakeWakeup : IDownloadWakeup
    {
        public List<string> Calls { get; } = new();
        public void Schedule(bool wifiOnly, TimeSpan delay) => Calls.Add($"schedule|{(wifiOnly ? "wifi" : "any")}|{delay.TotalMinutes}");
        public void Finish() => Calls.Add("finish");
        public void Cancel() => Calls.Add("cancel");
        public event EventHandler? Due;
        public event EventHandler? Stopped;
        /// <summary>The system runs the job (network up).</summary>
        public void Run() => Due?.Invoke(this, EventArgs.Empty);
        /// <summary>The system stops the job (time limit, network gone).</summary>
        public void Stop() => Stopped?.Invoke(this, EventArgs.Empty);
    }

    private static Track RemoteSong(string title)
    {
        var t = MobileFixtures.Song(title);
        t.FilePath = "noctis-remote://tr-" + t.Id.ToString("N");
        return t;
    }

    private static ShellViewModel ShellWith(MobileFixtures.Rig rig, INoctisAccountService account, IDownloadNotifier? notifier = null,
        IDownloadKeeper? keeper = null, IDownloadWakeup? wakeup = null) =>
        new(rig.Shell.Library, rig.Shell.Player, rig.Shell.Lyrics)
        {
            Account = account,
            Marshal = a => a(),
            TintFactory = rig.Shell.TintFactory,
            DownloadNotifier = notifier,
            DownloadKeeper = keeper,
            DownloadWakeup = wakeup,
        };

    /// <summary>Downloads stopped with the app in the background and nothing playing (Android froze
    /// or ended the process): the keeper (a user-initiated job / dataSync service) is begun once when
    /// the first start runs, with what the songs weigh, and ended once when the last one ends.</summary>
    [Fact]
    public async Task Keeper_BegunOnceForTheFirstStart_EndedWithTheLast()
    {
        var a = RemoteSong("A");
        a.FileSize = 3000;
        var b = RemoteSong("B");
        b.FileSize = 4000;
        var c = RemoteSong("C");
        c.FileSize = 9000;
        using var rig = MobileFixtures.MakeRig(new[] { a, b, c });
        var account = new FakeAccount { Account = FakeAccount.SignedInAs() };
        account.DownloadedIds.Add(c.Id);   // already on the phone: not fetched, not counted
        var keeper = new FakeKeeper();
        var shell = ShellWith(rig, account, keeper: keeper);
        var gate1 = new TaskCompletionSource();
        var gate2 = new TaskCompletionSource();

        var first = shell.Downloads!.RunAsync("One", new[] { a, c }, () => gate1.Task);
        var second = shell.Downloads.RunAsync("Two", new[] { b }, () => gate2.Task);
        Assert.Equal(new[] { "begin|3000" }, keeper.Calls);
        gate1.SetResult();
        await first;
        Assert.Equal(new[] { "begin|3000" }, keeper.Calls);
        gate2.SetException(new InvalidOperationException("failed"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => second);
        Assert.Equal(new[] { "begin|3000", "end" }, keeper.Calls);
    }

    /// <summary>The system stopped the job mid-run (network gone, too hot): the downloads pause
    /// instead of failing song after song, the card stays (no "Downloaded" while paused), the keeper
    /// is not ended; when the system runs the job again the songs left go on, without a new begin
    /// (the job is already running), and the keeper ends with them.</summary>
    [Fact]
    public async Task Keeper_StoppedBySystem_PausesTheDownloads_ThenResumesTheRest()
    {
        var a = RemoteSong("A");
        var b = RemoteSong("B");
        var c = RemoteSong("C");
        using var rig = MobileFixtures.MakeRig(new[] { a, b, c });
        var account = new FakeAccount { Account = FakeAccount.SignedInAs() };
        var notifier = new FakeNotifier();
        var keeper = new FakeKeeper();
        var shell = ShellWith(rig, account, notifier, keeper);
        var gate = new TaskCompletionSource();
        account.OnPause = () => gate.TrySetResult();

        var run = shell.Downloads!.RunAsync("Blue Album", new[] { a, b, c }, () => gate.Task);
        account.RaiseDownloadProgress(new NoctisDownloadProgress(3, 0, 0, 0));
        account.DownloadedIds.Add(a.Id);
        account.RaiseDownloadProgress(new NoctisDownloadProgress(2, 1, 0, 0));
        keeper.Pause();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        account.RaiseDownloadProgress(new NoctisDownloadProgress(0, 0, 0, 0));   // the paused batch winding down

        Assert.Equal(1, account.PauseCalls);
        Assert.Equal(new[] { "begin|0" }, keeper.Calls);
        Assert.DoesNotContain(notifier.Calls, c => c.StartsWith("finished") || c == "clear");

        keeper.Resume();
        await Until(() => keeper.Calls.Count == 2);

        Assert.Equal(new[] { b.Id, c.Id }, account.DownloadCalls.Single().Select(t => t.Id));
        Assert.Equal(new[] { "begin|0", "end" }, keeper.Calls);
    }

    /// <summary>Signed out, Remove All Downloads or the computer dropping this phone while the
    /// system has the downloads paused (the job stopped, waiting to be run again): the keeper is
    /// ended and the card goes at once, not after its 10-minute stale timeout; a later run of the
    /// job resumes nothing.</summary>
    [Theory]
    [InlineData("sign-out")]
    [InlineData("remove-all")]
    [InlineData("computer")]
    public async Task Keeper_PausedBySystem_CalledOff_EndsTheKeeperAndClearsTheCard(string how)
    {
        var a = RemoteSong("A");
        var b = RemoteSong("B");
        using var rig = MobileFixtures.MakeRig(new[] { a, b });
        var account = new FakeAccount { Account = FakeAccount.SignedInAs() };
        var notifier = new FakeNotifier();
        var keeper = new FakeKeeper();
        var shell = ShellWith(rig, account, notifier, keeper);
        var gate = new TaskCompletionSource();
        account.OnPause = () => gate.TrySetResult();

        var run = shell.Downloads!.RunAsync("Blue Album", new[] { a, b }, () => gate.Task);
        account.RaiseDownloadProgress(new NoctisDownloadProgress(2, 0, 0, 0));
        keeper.Pause();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        account.RaiseDownloadProgress(new NoctisDownloadProgress(0, 0, 0, 0));   // the paused batch winding down
        Assert.Equal(new[] { "begin|0" }, keeper.Calls);

        switch (how)
        {
            case "sign-out": await account.SignOutAsync(removeDownloads: false); break;
            case "remove-all": await account.RemoveAllDownloadsAsync(); break;
            default:
                account.Account = null;   // the computer refused the key (signed out locally)
                account.RaiseStateChanged();
                break;
        }

        Assert.Equal(new[] { "begin|0", "end" }, keeper.Calls);
        Assert.Equal("clear", notifier.Calls.Last());
        keeper.Resume();
        await Task.Delay(50);
        Assert.Empty(account.DownloadCalls);
        Assert.Equal(new[] { "begin|0", "end" }, keeper.Calls);
    }

    /// <summary>The same while running: the card goes and the keeper ends once, and a tap after it
    /// begins a keeper again.</summary>
    [Fact]
    public async Task Keeper_Running_SignOut_EndsItOnce_AndANewTapBeginsAgain()
    {
        var a = RemoteSong("A");
        var b = RemoteSong("B");
        using var rig = MobileFixtures.MakeRig(new[] { a, b });
        var account = new FakeAccount { Account = FakeAccount.SignedInAs() };
        var notifier = new FakeNotifier();
        var keeper = new FakeKeeper();
        var shell = ShellWith(rig, account, notifier, keeper);
        var gate = new TaskCompletionSource();

        var run = shell.Downloads!.RunAsync("Blue Album", new[] { a }, () => gate.Task);
        account.RaiseDownloadProgress(new NoctisDownloadProgress(1, 0, 0, 0));
        await account.SignOutAsync(removeDownloads: false);
        Assert.Equal(new[] { "begin|0", "end" }, keeper.Calls);
        Assert.Equal("clear", notifier.Calls.Last());
        gate.SetResult();   // the stopped batch's task ends after the call-off
        await run;
        Assert.Equal(new[] { "begin|0", "end" }, keeper.Calls);

        account.Account = FakeAccount.SignedInAs();
        await shell.Downloads.RunAsync("Red Album", new[] { b }, () => Task.CompletedTask);
        Assert.Equal(new[] { "begin|0", "end", "begin|0", "end" }, keeper.Calls);
    }

    /// <summary>The app ended mid-download: the next start (after its sync with the computer) resumes
    /// the songs still asked for, under the keeper (begun at start, with the app on screen) and with
    /// the usual card, instead of waiting for another tap.</summary>
    [Fact]
    public async Task Startup_ResumesUnfinishedDownloads_UnderTheKeeper_WithTheUsualCard()
    {
        var a = RemoteSong("A");
        a.FileSize = 3000;
        var b = RemoteSong("B");
        b.FileSize = 4000;
        using var rig = MobileFixtures.MakeRig(new[] { a, b });
        var account = new FakeAccount { Account = FakeAccount.SignedInAs() };
        account.Unfinished.AddRange(new[] { a, b });
        var notifier = new FakeNotifier();
        var keeper = new FakeKeeper();
        var shell = ShellWith(rig, account, notifier, keeper);
        account.DownloadProgress += (_, _) => { };

        await shell.InitializeAsync();
        await shell.StartupSync;
        await Until(() => keeper.Calls.Count == 2);

        Assert.Equal(1, account.Syncs);
        Assert.Equal(new[] { a.Id, b.Id }, account.DownloadCalls.Single().Select(t => t.Id));
        Assert.Equal(new[] { "begin|7000", "end" }, keeper.Calls);
    }

    /// <summary>The computer did not answer the startup sync: every song would fail, so they wait
    /// for the next start (the list stays); signed out, nothing is resumed.</summary>
    [Fact]
    public async Task Startup_SyncFailedOrSignedOut_ResumesNothing()
    {
        var a = RemoteSong("A");
        using var rig = MobileFixtures.MakeRig(new[] { a });
        var account = new FakeAccount
        {
            Account = FakeAccount.SignedInAs(),
            SyncError = new NoctisServerException(NoctisErrorKind.Unreachable, "off"),
        };
        account.Unfinished.Add(a);
        var keeper = new FakeKeeper();
        var shell = ShellWith(rig, account, keeper: keeper);

        await shell.InitializeAsync();
        await shell.StartupSync;
        await Task.Delay(50);
        Assert.Empty(account.DownloadCalls);

        var signedOut = new FakeAccount();
        signedOut.Unfinished.Add(a);
        await ShellWith(rig, signedOut, keeper: keeper).Downloads!.ResumeUnfinishedAsync();
        Assert.Empty(signedOut.DownloadCalls);
        Assert.Empty(keeper.Calls);
    }

    /// <summary>Wi-Fi only on mobile data, or the app not in front (Android 14+ starts a user-initiated
    /// job only while the app is visible): the resumed songs wait, nothing fails, no keeper is begun;
    /// the app coming to the front on Wi-Fi resumes them.</summary>
    [Fact]
    public async Task ResumeUnfinished_WaitsForWifiAndTheAppInFront()
    {
        var a = RemoteSong("A");
        using var rig = MobileFixtures.MakeRig(new[] { a });
        var account = new FakeAccount { Account = FakeAccount.SignedInAs(), WaitsForWifi = true };
        account.Unfinished.Add(a);
        var notifier = new FakeNotifier();
        var keeper = new FakeKeeper();
        var shell = ShellWith(rig, account, notifier, keeper);

        await shell.Downloads!.ResumeUnfinishedAsync();
        Assert.Empty(account.DownloadCalls);
        Assert.Empty(keeper.Calls);
        Assert.Empty(notifier.Calls);

        // On Wi-Fi now, but the app went to the background: still waiting.
        account.WaitsForWifi = false;
        shell.IsSuspended = true;
        await Task.Delay(50);
        Assert.Empty(account.DownloadCalls);

        shell.IsSuspended = false;   // back in front
        await Until(() => keeper.Calls.Count == 2);
        Assert.Equal(new[] { a.Id }, account.DownloadCalls.Single().Select(t => t.Id));

        // Once resumed, another return to the front resumes nothing again.
        shell.IsSuspended = true;
        shell.IsSuspended = false;
        await Task.Delay(50);
        Assert.Single(account.DownloadCalls);
    }

    /// <summary>Signed out while the resumed songs wait for Wi-Fi: they never go.</summary>
    [Fact]
    public async Task ResumeUnfinished_Waiting_ThenSignedOut_NeverResumes()
    {
        var a = RemoteSong("A");
        using var rig = MobileFixtures.MakeRig(new[] { a });
        var account = new FakeAccount { Account = FakeAccount.SignedInAs(), WaitsForWifi = true };
        account.Unfinished.Add(a);
        var shell = ShellWith(rig, account, new FakeNotifier(), new FakeKeeper());

        await shell.Downloads!.ResumeUnfinishedAsync();
        await account.SignOutAsync(removeDownloads: false);
        account.WaitsForWifi = false;
        shell.IsSuspended = true;
        shell.IsSuspended = false;
        await Task.Delay(50);

        Assert.Empty(account.DownloadCalls);
    }

    /// <summary>Wi-Fi only on mobile data with the app in the background: the songs left wait for a
    /// run on Wi-Fi (scheduled, nothing fails). When the system runs it, the app syncs and resumes
    /// the songs still asked for, read again (one removed meanwhile is left out), in the background
    /// under the run, which ends with them; nothing waits, so nothing stays scheduled.</summary>
    [Fact]
    public async Task Wakeup_WaitingForWifiInTheBackground_RunOnWifiResumesTheSongsLeft()
    {
        var a = RemoteSong("A");
        var b = RemoteSong("B");
        using var rig = MobileFixtures.MakeRig(new[] { a, b });
        var account = new FakeAccount { Account = FakeAccount.SignedInAs(), WaitsForWifi = true, WifiOnly = true };
        account.Unfinished.AddRange(new[] { a, b });
        var keeper = new FakeKeeper();
        var wakeup = new FakeWakeup();
        var shell = ShellWith(rig, account, new FakeNotifier(), keeper, wakeup);
        shell.IsSuspended = true;   // a process the job started: no activity on screen

        await shell.InitializeAsync();
        await shell.StartupSync;
        await Until(() => wakeup.Calls.Count == 1);
        Assert.Equal(new[] { "schedule|wifi|0" }, wakeup.Calls);
        Assert.Empty(account.DownloadCalls);

        account.WaitsForWifi = false;   // on Wi-Fi
        account.Unfinished.Remove(b);   // removed meanwhile
        wakeup.Run();
        await Until(() => wakeup.Calls.Count == 3);

        Assert.Equal(2, account.Syncs);
        Assert.Equal(new[] { a.Id }, account.DownloadCalls.Single().Select(t => t.Id));
        Assert.Equal(new[] { "schedule|wifi|0", "finish", "cancel" }, wakeup.Calls);
    }

    /// <summary>A run that still finds mobile data (the setting turned on meanwhile) resumes nothing
    /// and asks for one on Wi-Fi.</summary>
    [Fact]
    public async Task Wakeup_RunOnMobileDataWithWifiOnly_FinishesAndWaitsForWifi()
    {
        var a = RemoteSong("A");
        using var rig = MobileFixtures.MakeRig(new[] { a });
        var account = new FakeAccount { Account = FakeAccount.SignedInAs() };
        account.Unfinished.Add(a);
        var wakeup = new FakeWakeup();
        var shell = ShellWith(rig, account, new FakeNotifier(), new FakeKeeper(), wakeup);
        shell.IsSuspended = true;
        await shell.InitializeAsync();
        await shell.StartupSync;
        await Until(() => wakeup.Calls.Count == 1);
        Assert.Equal("schedule|any|0", wakeup.Calls[0]);

        account.WaitsForWifi = true;
        account.WifiOnly = true;
        wakeup.Run();
        await Until(() => wakeup.Calls.Count == 3);

        Assert.Empty(account.DownloadCalls);
        Assert.Equal(new[] { "schedule|any|0", "finish", "schedule|wifi|0" }, wakeup.Calls);
    }

    /// <summary>Downloads started from a tap schedule a watch run (an ended process has them resumed
    /// then); a watch run that finds them still going under the keeper ends and watches again; all
    /// landed, nothing stays scheduled.</summary>
    [Fact]
    public async Task Wakeup_WatchesRunningDownloads_CancelledWhenAllLanded()
    {
        var a = RemoteSong("A");
        using var rig = MobileFixtures.MakeRig(new[] { a });
        var account = new FakeAccount { Account = FakeAccount.SignedInAs() };
        var wakeup = new FakeWakeup();
        var shell = ShellWith(rig, account, new FakeNotifier(), new FakeKeeper(), wakeup);
        var gate = new TaskCompletionSource();

        var run = shell.Downloads!.RunAsync("One", new[] { a }, () => gate.Task);
        await Until(() => wakeup.Calls.Count == 1);
        Assert.Equal(new[] { "schedule|any|15" }, wakeup.Calls);

        wakeup.Run();
        await Until(() => wakeup.Calls.Count == 3);
        Assert.Equal(new[] { "schedule|any|15", "finish", "schedule|any|15" }, wakeup.Calls);

        gate.SetResult();
        await run;
        Assert.Equal("cancel", wakeup.Calls.Last());
        Assert.Equal(4, wakeup.Calls.Count);
    }

    /// <summary>The system stops a run mid-way (its time limit, Wi-Fi gone): the downloads it held
    /// pause (finished files kept), the songs left are scheduled again, and the next run goes on
    /// with them.</summary>
    [Fact]
    public async Task Wakeup_StoppedMidRun_PausesAndSchedulesTheRest()
    {
        var a = RemoteSong("A");
        var b = RemoteSong("B");
        var c = RemoteSong("C");
        using var rig = MobileFixtures.MakeRig(new[] { a, b, c });
        var account = new FakeAccount { Account = FakeAccount.SignedInAs(), DownloadGate = new TaskCompletionSource() };
        account.Unfinished.AddRange(new[] { a, b, c });
        account.OnPause = () => account.DownloadGate?.TrySetResult();
        var wakeup = new FakeWakeup();
        var shell = ShellWith(rig, account, new FakeNotifier(), new FakeKeeper(), wakeup);
        shell.IsSuspended = true;
        await shell.InitializeAsync();
        await shell.StartupSync;
        await Until(() => wakeup.Calls.Count == 1);

        wakeup.Run();
        await Until(() => account.DownloadCalls.Count == 1);
        account.DownloadedIds.Add(a.Id);   // landed before the stop
        wakeup.Stop();
        await Until(() => wakeup.Calls.Count == 2);

        Assert.Equal(1, account.PauseCalls);
        Assert.Equal(new[] { "schedule|any|0", "schedule|any|0" }, wakeup.Calls);

        account.DownloadGate = null;
        account.Unfinished.Remove(a);
        wakeup.Run();
        await Until(() => wakeup.Calls.Count == 4);

        Assert.Equal(new[] { b.Id, c.Id }, account.DownloadCalls[1].Select(t => t.Id));
        Assert.Equal(new[] { "finish", "cancel" }, wakeup.Calls.Skip(2));
    }

    /// <summary>Signed out while songs wait: the run is cancelled and nothing is scheduled again; a
    /// run the system had started already resumes nothing; a start signed out cancels a left one.</summary>
    [Fact]
    public async Task Wakeup_SignedOut_NothingScheduled()
    {
        var a = RemoteSong("A");
        using var rig = MobileFixtures.MakeRig(new[] { a });
        var account = new FakeAccount { Account = FakeAccount.SignedInAs(), WaitsForWifi = true, WifiOnly = true };
        account.Unfinished.Add(a);
        var wakeup = new FakeWakeup();
        var shell = ShellWith(rig, account, new FakeNotifier(), new FakeKeeper(), wakeup);
        await shell.InitializeAsync();
        await shell.StartupSync;
        await Until(() => wakeup.Calls.Count == 1);

        await account.SignOutAsync(removeDownloads: false);
        account.WaitsForWifi = false;
        wakeup.Run();
        await Task.Delay(50);

        Assert.Empty(account.DownloadCalls);
        Assert.DoesNotContain(wakeup.Calls.Skip(1), c => c.StartsWith("schedule"));
        Assert.Equal("cancel", wakeup.Calls.Last());

        var otherWakeup = new FakeWakeup();
        await ShellWith(rig, new FakeAccount(), wakeup: otherWakeup).InitializeAsync();
        Assert.Equal(new[] { "cancel" }, otherWakeup.Calls);
    }

    /// <summary>Away from home: the tap's download cannot reach the computer. Nothing fails: the
    /// banner says it waits for the computer, and the wakeup is scheduled for Wi-Fi (the phone is on
    /// mobile data). A run on Wi-Fi whose sync still finds no computer checks again in 15 minutes;
    /// once it answers the song downloads and nothing stays scheduled.</summary>
    [Fact]
    public async Task Wakeup_ComputerUnreachable_SongsWait_RetriedOnWifiThenEvery15Minutes()
    {
        var a = RemoteSong("A");
        using var rig = MobileFixtures.MakeRig(new[] { a });
        var account = new FakeAccount { Account = FakeAccount.SignedInAs(), IsOnWifiNow = false };
        var wakeup = new FakeWakeup();
        var shell = ShellWith(rig, account, new FakeNotifier(), new FakeKeeper(), wakeup);
        await shell.InitializeAsync();
        await shell.StartupSync;

        account.DownloadError = new NoctisServerException(NoctisErrorKind.Unreachable, "away");
        await shell.DownloadTracksAsync(new[] { a });
        await Until(() => wakeup.Calls.Count == 2);

        Assert.Equal(Loc.T("Mobile.Download.WaitingForComputer"), shell.Player.Notice);
        Assert.Equal(new[] { "schedule|any|15", "schedule|wifi|0" }, wakeup.Calls);
        shell.IsSuspended = true;   // Home key

        // Home Wi-Fi, but the computer is asleep.
        account.IsOnWifiNow = true;
        account.SyncError = new NoctisServerException(NoctisErrorKind.Unreachable, "asleep");
        account.Unfinished.Add(a);
        wakeup.Run();
        await Until(() => wakeup.Calls.Count == 4);
        Assert.Equal(new[] { "finish", "schedule|any|15" }, wakeup.Calls.Skip(2));

        account.SyncError = null;
        account.DownloadError = null;
        wakeup.Run();
        await Until(() => wakeup.Calls.Count == 6);

        Assert.Equal(2, account.DownloadCalls.Count);
        Assert.True(account.IsDownloaded(a));
        Assert.Equal(new[] { "finish", "cancel" }, wakeup.Calls.Skip(4));
    }

    /// <summary>The batch's last report says songs wait (the computer out of reach, or Wi-Fi gone with
    /// Wi-Fi only on): the card says so instead of "Downloaded" or a failure.</summary>
    [Theory]
    [InlineData(false, "Mobile.Download.WaitingForComputer")]
    [InlineData(true, "Mobile.Download.WaitingForWifi")]
    public void Notification_SongsWaiting_SaysWhatTheyWaitFor(bool forWifi, string key)
    {
        using var rig = MobileFixtures.MakeRig(Array.Empty<Track>());
        var account = new FakeAccount { Account = FakeAccount.SignedInAs() };
        var notifier = new FakeNotifier();
        var shell = ShellWith(rig, account, notifier);
        _ = shell.Downloads;

        account.RaiseDownloadProgress(new NoctisDownloadProgress(3, 0, 0, 0));
        account.RaiseDownloadProgress(new NoctisDownloadProgress(0, 1, 0, 0, Waiting: 2, WaitsForWifi: forWifi));

        Assert.Equal($"finished|{Loc.T(key)}|{Loc.Plural("Mobile.Download.Done", 1)}", notifier.Calls.Last());
    }

    private static async Task Until(Func<bool> condition)
    {
        var start = Environment.TickCount64;
        while (!condition())
        {
            if (Environment.TickCount64 - start > 5000) throw new TimeoutException("condition never became true");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task Notification_NamesTheAlbum_CountsUp_ThenSaysHowManyLanded()
    {
        var a = RemoteSong("A");
        var b = RemoteSong("B");
        var c = RemoteSong("C");
        using var rig = MobileFixtures.MakeRig(new[] { a, b, c });
        var account = new FakeAccount { Account = FakeAccount.SignedInAs() };
        var notifier = new FakeNotifier();
        var shell = ShellWith(rig, account, notifier);
        var gate = new TaskCompletionSource();

        var run = shell.Downloads!.RunAsync("Blue Album", new[] { a, b, c }, () => gate.Task);
        account.RaiseDownloadProgress(new NoctisDownloadProgress(3, 0, 0, 0));
        account.RaiseDownloadProgress(new NoctisDownloadProgress(2, 1, 0, 0));
        account.RaiseDownloadProgress(new NoctisDownloadProgress(0, 2, 1, 0));
        gate.SetResult();
        await run;

        Assert.Equal(new[]
        {
            "progress|Blue Album|" + Loc.T("Mobile.Account.Downloading", 0, 3, "") + "|0/3",
            "progress|Blue Album|" + Loc.T("Mobile.Account.Downloading", 1, 3, "") + "|1/3",
            "finished|" + Loc.Plural("Mobile.Download.Done", 2) + "|" + Loc.T("Mobile.Account.Failed", 1),
        }, notifier.Calls);
        Assert.False(shell.Downloads.IsActive);
    }

    /// <summary>Before, a failed song left the total ("Downloading 1 of 3" became "1 of 2"): the
    /// total stays the number asked for and the failure is counted apart, on the notification and
    /// the Account page alike; the bar counts the failed song as handled.</summary>
    [Fact]
    public async Task FailedSong_KeepsTheTotal_AndIsCountedApart()
    {
        var a = RemoteSong("A");
        var b = RemoteSong("B");
        var c = RemoteSong("C");
        using var rig = MobileFixtures.MakeRig(new[] { a, b, c });
        var account = new FakeAccount { Account = FakeAccount.SignedInAs() };
        var notifier = new FakeNotifier();
        var shell = ShellWith(rig, account, notifier);
        var gate = new TaskCompletionSource();

        var run = shell.Downloads!.RunAsync("Blue Album", new[] { a, b, c }, () => gate.Task);
        account.RaiseDownloadProgress(new NoctisDownloadProgress(3, 0, 0, 0));
        account.RaiseDownloadProgress(new NoctisDownloadProgress(2, 0, 1, 0));
        account.RaiseDownloadProgress(new NoctisDownloadProgress(1, 1, 1, 0));
        shell.OpenAccountCommand.Execute(null);
        var page = Assert.IsType<AccountPageViewModel>(shell.CurrentPage);

        var failed = " · " + Loc.T("Mobile.Account.Failed", 1);
        Assert.Equal("progress|Blue Album|" + Loc.T("Mobile.Account.Downloading", 0, 3, failed) + "|1/3", notifier.Calls[1]);
        Assert.Equal("progress|Blue Album|" + Loc.T("Mobile.Account.Downloading", 1, 3, failed) + "|2/3", notifier.Calls[2]);
        Assert.Equal(Loc.T("Mobile.Account.Downloading", 1, 3, failed), page.DownloadText);
        Assert.Equal(2 / 3.0, page.DownloadFraction, 3);
        gate.SetResult();
        await run;
    }

    [Fact]
    public async Task Notification_TwoStartsAtOnce_SaysFromYourComputer()
    {
        var a = RemoteSong("A");
        var b = RemoteSong("B");
        using var rig = MobileFixtures.MakeRig(new[] { a, b });
        var account = new FakeAccount { Account = FakeAccount.SignedInAs() };
        var notifier = new FakeNotifier();
        var shell = ShellWith(rig, account, notifier);
        var gate = new TaskCompletionSource();

        var first = shell.Downloads!.RunAsync("Album One", new[] { a }, () => gate.Task);
        var second = shell.Downloads.RunAsync("Album Two", new[] { b }, () => gate.Task);
        account.RaiseDownloadProgress(new NoctisDownloadProgress(2, 0, 0, 0));
        gate.SetResult();
        await Task.WhenAll(first, second);

        Assert.StartsWith("progress|" + Loc.T("Mobile.Download.FromComputer") + "|", notifier.Calls.Single());
    }

    /// <summary>Remove All Downloads or sign-out mid-run: nothing landed, nothing failed, so the card
    /// just goes rather than saying "Downloads finished".</summary>
    [Fact]
    public void Notification_NothingLandedNorFailed_IsCleared()
    {
        var a = RemoteSong("A");
        using var rig = MobileFixtures.MakeRig(new[] { a });
        var account = new FakeAccount { Account = FakeAccount.SignedInAs() };
        var notifier = new FakeNotifier();
        var shell = ShellWith(rig, account, notifier);
        _ = shell.Downloads;

        account.RaiseDownloadProgress(new NoctisDownloadProgress(1, 0, 0, 0));
        account.RaiseDownloadProgress(new NoctisDownloadProgress(0, 0, 0, 0));
        // An idle report with no download shown before is not a finish either.
        account.RaiseDownloadProgress(new NoctisDownloadProgress(0, 0, 0, 0));

        Assert.Equal("clear", notifier.Calls.Last());
        Assert.Equal(2, notifier.Calls.Count);
    }

    [Fact]
    public async Task AlbumPage_ShowsItsOwnSongsCount_WhileTheyDownload()
    {
        var a = RemoteSong("A");
        var b = RemoteSong("B");
        var local = MobileFixtures.Song("Local");
        var other = RemoteSong("Elsewhere");
        var album = MobileFixtures.MakeAlbum("Mixed", "Artist", a, b, local);
        using var rig = MobileFixtures.MakeRig(new[] { a, b, local, other }, new[] { album });
        var account = new FakeAccount { Account = FakeAccount.SignedInAs() };
        var shell = ShellWith(rig, account);
        var page = new AlbumPageViewModel(shell, album);
        Assert.False(page.IsDownloading);

        // Another album's download does not show here.
        var gate = new TaskCompletionSource();
        var elsewhere = shell.Downloads!.RunAsync("Other", new[] { other }, () => gate.Task);
        Assert.False(page.IsDownloading);
        gate.SetResult();
        await elsewhere;

        var albumGate = new TaskCompletionSource();
        var run = shell.Downloads.RunAsync("Mixed", album.Tracks, () => albumGate.Task);
        Assert.True(page.IsDownloading);
        Assert.Equal(Loc.T("Mobile.Account.Downloading", 0, 2, ""), page.DownloadText);
        Assert.Equal(0, page.DownloadFraction);

        account.DownloadedIds.Add(a.Id);
        account.RaiseDownloadProgress(new NoctisDownloadProgress(1, 1, 0, 0));
        Assert.Equal(Loc.T("Mobile.Account.Downloading", 1, 2, ""), page.DownloadText);
        Assert.Equal(0.5, page.DownloadFraction);

        account.DownloadedIds.Add(b.Id);
        account.RaiseDownloadProgress(new NoctisDownloadProgress(0, 2, 0, 0));
        Assert.False(page.IsDownloading);
        albumGate.SetResult();
        await run;
        Assert.False(page.IsDownloading);
        page.OnClosed();
    }

    /// <summary>The album's menu › Download goes through the same start: the page follows it.</summary>
    [Fact]
    public async Task AlbumMenuDownload_IsFollowedByTheAlbumPage_AndShowsTheBanner()
    {
        var a = RemoteSong("A");
        var album = MobileFixtures.MakeAlbum("Solo", "Artist", a);
        using var rig = MobileFixtures.MakeRig(new[] { a }, new[] { album });
        var account = new FakeAccount { Account = FakeAccount.SignedInAs() };
        var shell = ShellWith(rig, account);
        var page = new AlbumPageViewModel(shell, album);
        var states = new List<bool>();
        page.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(AlbumPageViewModel.IsDownloading)) states.Add(page.IsDownloading); };

        await shell.DownloadTracksAsync(new[] { a }, "Solo");

        Assert.Equal(new[] { true, false }, states);
        Assert.Equal(Loc.T("Mobile.Account.StartingDownloads"), shell.Player.Notice);
    }

    /// <summary>Before, a menu's Download on mobile data with Wi-Fi only on was only logged: the tap
    /// seemed to do nothing.</summary>
    [Fact]
    public async Task MenuDownload_Refused_SaysWhyInTheBanner()
    {
        var a = RemoteSong("A");
        using var rig = MobileFixtures.MakeRig(new[] { a });
        var account = new FakeAccount
        {
            Account = FakeAccount.SignedInAs(),
            DownloadError = new NoctisServerException(NoctisErrorKind.WifiRequired, "Wi-Fi only"),
        };
        var shell = ShellWith(rig, account);

        await shell.DownloadTracksAsync(new[] { a }, "A");

        Assert.Equal(Loc.T("Mobile.Account.Error.WifiRequired"), shell.Player.Notice);
    }

    /// <summary>Opened while an album downloads: the counts show at once, not when the next song
    /// lands, and Download Everything is not on offer meanwhile.</summary>
    [Fact]
    public async Task AccountPage_OpenedMidRun_ShowsTheRunningCounts()
    {
        var a = RemoteSong("A");
        var b = RemoteSong("B");
        using var rig = MobileFixtures.MakeRig(new[] { a, b });
        var account = new FakeAccount { Account = FakeAccount.SignedInAs() };
        var shell = ShellWith(rig, account);
        var gate = new TaskCompletionSource();
        var run = shell.Downloads!.RunAsync("Album", new[] { a, b }, () => gate.Task);
        account.RaiseDownloadProgress(new NoctisDownloadProgress(1, 1, 0, 0));

        shell.OpenAccountCommand.Execute(null);
        var page = Assert.IsType<AccountPageViewModel>(shell.CurrentPage);

        Assert.True(page.IsDownloading);
        Assert.True(page.IsDownloadCounted);
        Assert.False(page.CanDownload);
        Assert.Equal(Loc.T("Mobile.Account.Downloading", 1, 2, ""), page.DownloadText);
        gate.SetResult();
        await run;
    }

    /// <summary>Download Everything is one start too: an album page follows its songs in it.</summary>
    [Fact]
    public async Task DownloadEverything_IsFollowedByTheAlbumPage()
    {
        var a = RemoteSong("A");
        var album = MobileFixtures.MakeAlbum("Solo", "Artist", a);
        using var rig = MobileFixtures.MakeRig(new[] { a }, new[] { album });
        var account = new FakeAccount { Account = FakeAccount.SignedInAs() };
        var shell = ShellWith(rig, account);
        var albumPage = new AlbumPageViewModel(shell, album);
        var shown = false;
        albumPage.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(AlbumPageViewModel.IsDownloading) && albumPage.IsDownloading) shown = true; };

        shell.OpenAccountCommand.Execute(null);
        var page = Assert.IsType<AccountPageViewModel>(shell.CurrentPage);
        await page.DownloadAllCommand.ExecuteAsync(null);

        Assert.Equal(1, account.DownloadAllCalls);
        Assert.True(shown);
        Assert.False(albumPage.IsDownloading);
    }
}
