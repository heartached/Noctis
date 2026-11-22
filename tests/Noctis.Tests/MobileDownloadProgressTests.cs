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

    private static Track RemoteSong(string title)
    {
        var t = MobileFixtures.Song(title);
        t.FilePath = "noctis-remote://tr-" + t.Id.ToString("N");
        return t;
    }

    private static ShellViewModel ShellWith(MobileFixtures.Rig rig, INoctisAccountService account, IDownloadNotifier? notifier = null) =>
        new(rig.Shell.Library, rig.Shell.Player, rig.Shell.Lyrics)
        {
            Account = account,
            Marshal = a => a(),
            TintFactory = rig.Shell.TintFactory,
            DownloadNotifier = notifier,
        };

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
