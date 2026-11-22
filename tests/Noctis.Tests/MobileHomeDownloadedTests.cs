using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Noctis.Mobile.Services.Account;
using Noctis.Mobile.ViewModels;
using Noctis.Models;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Home › Downloaded (beta testers 11-22): the albums with at least one song downloaded from the
/// computer to this phone, newest download first, as Home's other rails (tile, › header, a page
/// of them all); hidden while nothing is downloaded.
/// </summary>
public class MobileHomeDownloadedTests
{
    private static Track RemoteSong(string title)
    {
        var t = MobileFixtures.Song(title);
        t.FilePath = "noctis-remote://tr-" + t.Id.ToString("N");
        return t;
    }

    private static ShellViewModel ShellWith(MobileFixtures.Rig rig, INoctisAccountService account) =>
        new(rig.Shell.Library, rig.Shell.Player, rig.Shell.Lyrics)
        {
            Account = account,
            Marshal = a => a(),
            TintFactory = rig.Shell.TintFactory,
        };

    [Fact]
    public void Rail_ListsAlbumsWithADownloadedSong_NewestDownloadFirst_HiddenWithout()
    {
        var a1 = RemoteSong("A1");
        var a2 = RemoteSong("A2");
        var b1 = RemoteSong("B1");
        var c1 = RemoteSong("C1");
        var albumA = MobileFixtures.MakeAlbum("Alpha", "X", a1, a2);
        var albumB = MobileFixtures.MakeAlbum("Bravo", "Y", b1);
        var albumC = MobileFixtures.MakeAlbum("Charlie", "Z", c1);
        using var rig = MobileFixtures.MakeRig(new[] { a1, a2, b1, c1 }, new[] { albumA, albumB, albumC });
        var account = new MobileAccountTests.FakeAccount { Account = MobileAccountTests.FakeAccount.SignedInAs() };
        var shell = ShellWith(rig, account);
        Assert.False(shell.Library.HasDownloaded);
        Assert.Empty(shell.Library.DownloadedRail);

        account.DownloadedIds.Add(a2.Id);
        account.DownloadedTimes[a2.Id] = new DateTime(2026, 11, 1);
        account.DownloadedIds.Add(b1.Id);
        account.DownloadedTimes[b1.Id] = new DateTime(2026, 11, 20);
        account.RaiseStateChanged();   // the offline copies changed

        Assert.True(shell.Library.HasDownloaded);
        Assert.Equal(new[] { "Bravo", "Alpha" }, shell.Library.DownloadedRail.Select(r => r.Title));
        Assert.All(shell.Library.DownloadedRail, r => Assert.Equal(RailItemKind.Album, r.Kind));

        account.DownloadedIds.Clear();
        account.RaiseStateChanged();
        Assert.False(shell.Library.HasDownloaded);
        Assert.Empty(shell.Library.DownloadedRail);
    }

    [Fact]
    public async Task Rail_FollowsADownload_AndTheHeaderOpensEveryAlbum()
    {
        var songs = Enumerable.Range(1, 14).Select(i => RemoteSong("S" + i)).ToArray();
        var albums = songs.Select((s, i) => MobileFixtures.MakeAlbum("Album " + (char)('A' + i), "X", s)).ToArray();
        using var rig = MobileFixtures.MakeRig(songs, albums);
        var account = new MobileAccountTests.FakeAccount { Account = MobileAccountTests.FakeAccount.SignedInAs() };
        var shell = ShellWith(rig, account);

        await shell.DownloadTracksAsync(songs);
        account.RaiseStateChanged();

        Assert.Equal(12, shell.Library.DownloadedRail.Count);   // a rail holds twelve
        Assert.Equal("Album A", shell.Library.DownloadedRail[0].Title);   // no times known: by name

        shell.OpenDownloadedCommand.Execute(null);
        var page = Assert.IsType<RailGridPageViewModel>(shell.CurrentPage);
        Assert.Equal("Downloaded", page.Title);
        Assert.Equal(14, page.Items.Count);   // the page holds them all

        shell.OpenRailItemCommand.Execute(page.Items[0]);
        Assert.Equal("Album A", shell.CurrentPage!.Title);
    }

    [Fact]
    public void NoAccount_NoRail()
    {
        var a = RemoteSong("A");
        using var rig = MobileFixtures.MakeRig(new[] { a }, new[] { MobileFixtures.MakeAlbum("Alpha", "X", a) });
        Assert.False(rig.Shell.Library.HasDownloaded);
        Assert.Empty(rig.Shell.Library.DownloadedAlbums());
    }

    [AvaloniaFact]
    public void HomePage_ShowsTheSection_OnlyWithADownload()
    {
        var a = RemoteSong("A");
        using var rig = MobileFixtures.MakeRig(new[] { a }, new[] { MobileFixtures.MakeAlbum("Alpha", "X", a) });
        var account = new MobileAccountTests.FakeAccount { Account = MobileAccountTests.FakeAccount.SignedInAs() };
        var shell = ShellWith(rig, account);
        var window = MobileFixtures.Mount(shell, out var view);
        var section = MobileFixtures.Named<StackPanel>(view, "DownloadedSection");
        Assert.False(section.IsVisible);

        account.DownloadedIds.Add(a.Id);
        account.RaiseStateChanged();
        window.UpdateLayout();
        Assert.True(section.IsVisible);
        Assert.Single(MobileFixtures.Named<ItemsControl>(view, "DownloadedRail").Items);
        window.Close();
    }
}
