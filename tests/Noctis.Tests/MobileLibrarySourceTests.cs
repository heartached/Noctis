using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Noctis.Mobile.Services.Account;
using Noctis.Mobile.ViewModels;
using Noctis.Mobile.Views;
using Noctis.Models;
using Noctis.Services;
using Xunit;
using FakeAccount = Noctis.Tests.MobileAccountTests.FakeAccount;

namespace Noctis.Tests;

/// <summary>The Library's source choice (Discord #mobile-beta, 11-17: "make the songs on my phone
/// separate from the songs I sync from my PC"): All Music, On This Phone, From Computer, applied to
/// Songs, Albums, Artists and Recently Added, saved in AppSettings.PhoneLibrarySource.</summary>
public class MobileLibrarySourceTests
{
    private static Track Remote(string title, string artist = "Band")
    {
        var t = MobileFixtures.Song(title, artist);
        t.SourceType = SourceType.NoctisServer;
        t.FilePath = "noctis-remote://" + t.Id.ToString("N");
        return t;
    }

    private static ShellViewModel ShellWith(MobileFixtures.Rig rig, INoctisAccountService? account) =>
        new(rig.Shell.Library, rig.Shell.Player, rig.Shell.Lyrics)
        {
            Account = account,
            Marshal = a => a(),
            TintFactory = rig.Shell.TintFactory,
        };

    /// <summary>A library of one album, "Mixed", with 3 phone files (one paired with a computer
    /// song) and 9 computer songs (one downloaded), plus a phone-only album and a computer-only one.</summary>
    private sealed class Lib : IDisposable
    {
        public required MobileFixtures.Rig Rig { get; init; }
        public required FakeAccount Account { get; init; }
        public required ShellViewModel Shell { get; init; }
        public required Track Paired { get; init; }
        public required Track Downloaded { get; init; }
        public required Album Mixed { get; init; }
        public required Album PhoneOnly { get; init; }
        public required Album ComputerOnly { get; init; }
        public void Dispose() => Rig.Dispose();
    }

    private static Lib Make(bool signedIn = true, Func<PersistenceService, Task>? seed = null)
    {
        var local = Enumerable.Range(1, 3).Select(i => MobileFixtures.Song("Local " + i, "Band")).ToArray();
        var remote = Enumerable.Range(1, 9).Select(i => Remote("Remote " + i, "Band")).ToArray();
        var solo = MobileFixtures.Song("Solo", "Phone Artist");
        var desk = Remote("Desk", "Desk Artist");
        var mixed = MobileFixtures.MakeAlbum("Mixed", "Band", local.Concat(remote).ToArray());
        var phoneOnly = MobileFixtures.MakeAlbum("Phone Only", "Phone Artist", solo);
        var computerOnly = MobileFixtures.MakeAlbum("Computer Only", "Desk Artist", desk);
        var tracks = local.Concat(remote).Append(solo).Append(desk).ToArray();
        var rig = MobileFixtures.MakeRig(tracks, new[] { mixed, phoneOnly, computerOnly }, seed);
        rig.Library.ArtistList.AddRange(new[]
        {
            new Artist { Name = "Band", TrackCount = 12, AlbumCount = 1 },
            new Artist { Name = "Phone Artist", TrackCount = 1, AlbumCount = 1 },
            new Artist { Name = "Desk Artist", TrackCount = 1, AlbumCount = 1 },
        });
        var account = new FakeAccount { Account = signedIn ? FakeAccount.SignedInAs() : null };
        account.Links[local[0].Id] = Guid.NewGuid();     // the sync paired this file with a computer song
        account.DownloadedIds.Add(remote[0].Id);
        var shell = ShellWith(rig, account);
        return new Lib
        {
            Rig = rig, Account = account, Shell = shell, Paired = local[0], Downloaded = remote[0],
            Mixed = mixed, PhoneOnly = phoneOnly, ComputerOnly = computerOnly,
        };
    }

    [Fact]
    public void Buckets_PhoneFiles_ComputerSongs_DownloadsAndPairedFiles()
    {
        var account = new FakeAccount { Account = FakeAccount.SignedInAs() };
        var own = MobileFixtures.Song("Own");
        var paired = MobileFixtures.Song("Paired");
        account.Links[paired.Id] = Guid.NewGuid();
        var streamed = Remote("Streamed");
        var downloaded = Remote("Downloaded");
        account.DownloadedIds.Add(downloaded.Id);

        bool In(Track t, LibrarySource s) => LibrarySources.Includes(account, t, s);

        // The phone's own file: On This Phone only.
        Assert.True(In(own, LibrarySource.Phone));
        Assert.False(In(own, LibrarySource.Computer));
        // A computer song, streamed or downloaded: From Computer only (a download is still the computer's).
        Assert.False(In(streamed, LibrarySource.Phone));
        Assert.True(In(streamed, LibrarySource.Computer));
        Assert.False(In(downloaded, LibrarySource.Phone));
        Assert.True(In(downloaded, LibrarySource.Computer));
        // A phone file the sync paired with a computer song (one row): in both.
        Assert.True(In(paired, LibrarySource.Phone));
        Assert.True(In(paired, LibrarySource.Computer));
        // All takes everything; without an account everything is the phone's.
        Assert.All(new[] { own, paired, streamed, downloaded }, t => Assert.True(In(t, LibrarySource.All)));
        Assert.True(LibrarySources.Includes(null, own, LibrarySource.Phone));
        Assert.False(LibrarySources.Includes(null, own, LibrarySource.Computer));
    }

    [Theory]
    [InlineData(null, LibrarySource.All)]
    [InlineData("", LibrarySource.All)]
    [InlineData("Phone", LibrarySource.Phone)]
    [InlineData("computer", LibrarySource.Computer)]
    [InlineData("2", LibrarySource.All)]
    [InlineData("Cloud", LibrarySource.All)]
    public void Parse_ReadsTheSavedValue_UnknownIsAll(string? saved, LibrarySource expected) =>
        Assert.Equal(expected, LibrarySources.Parse(saved));

    [Fact]
    public void Default_IsAll_AndTheChoiceIsHiddenWithoutAComputer()
    {
        using var lib = Make(signedIn: false);
        var library = lib.Shell.Library;
        Assert.Equal(LibrarySource.All, library.Source);
        Assert.Equal(14, library.SourceSongs().Count());

        // No account service at all (a host without one): no choice.
        using var bare = MobileFixtures.MakeRig(new[] { MobileFixtures.Song("Only") });
        Assert.False(bare.Shell.Library.CanChooseSource);

        // Signed out, but the computer's songs are still listed (it revoked this phone): a choice.
        Assert.True(library.CanChooseSource);

        // Signed out with none of its songs: hidden, and a saved choice is not in force.
        lib.Rig.Library.TrackList.RemoveAll(t => lib.Account.IsRemote(t));
        lib.Rig.Library.RaiseLibraryUpdated();
        Assert.False(library.CanChooseSource);
        library.Source = LibrarySource.Computer;
        Assert.Equal(LibrarySource.All, library.ActiveSource);
        Assert.False(library.IsSourceFiltered);
        Assert.Equal(4, library.SourceSongs().Count());

        // Signing in brings it back (StateChanged), with the choice in force.
        lib.Account.Account = FakeAccount.SignedInAs();
        lib.Account.RaiseStateChanged();
        Assert.True(library.CanChooseSource);
        Assert.Equal(LibrarySource.Computer, library.ActiveSource);
    }

    [Fact]
    public async Task OnThisPhone_SongsAlbumsArtists_CountOnlyThePhonesSongs()
    {
        using var lib = Make();
        var shell = lib.Shell;
        await shell.Library.SetSourceAsync(LibrarySource.Phone);
        Assert.True(shell.Library.IsSourceFiltered);

        shell.OpenSongsCommand.Execute(null);
        var songs = Assert.IsType<SongListPageViewModel>(shell.CurrentPage);
        Assert.Equal(4, songs.TotalCount);                                     // 3 of Mixed + Solo
        Assert.DoesNotContain(songs.Songs, t => lib.Account.IsRemote(t));
        Assert.Contains(lib.Paired, songs.Songs);

        shell.OpenAlbumsCommand.Execute(null);
        var albums = Assert.IsType<AlbumGridPageViewModel>(shell.CurrentPage);
        Assert.Equal(new[] { "Mixed", "Phone Only" }, albums.Albums.Select(a => a.Name));

        shell.OpenArtistsCommand.Execute(null);
        var artists = Assert.IsType<ArtistListPageViewModel>(shell.CurrentPage);
        Assert.Equal(new[] { "Band", "Phone Artist" }, artists.Artists.Select(a => a.Name));
        var band = artists.Artists.Single(a => a.Name == "Band").Artist;
        Assert.Equal(3, band.TrackCount);                                      // not 12
        Assert.Equal(1, band.AlbumCount);

        // Switching to From Computer re-reads the open pages at once.
        await shell.Library.SetSourceAsync(LibrarySource.Computer);
        Assert.Equal(new[] { "Band", "Desk Artist" }, artists.Artists.Select(a => a.Name));
        Assert.Equal(10, artists.Artists.Single(a => a.Name == "Band").Artist.TrackCount);   // 9 + the paired file
        Assert.Equal(new[] { "Computer Only", "Mixed" }, albums.Albums.Select(a => a.Name));
        Assert.Equal(11, songs.TotalCount);                                    // 9 + paired + Desk
        Assert.Contains(lib.Downloaded, songs.Songs);
        Assert.Contains(lib.Paired, songs.Songs);

        await shell.Library.SetSourceAsync(LibrarySource.All);
        Assert.Equal(14, songs.TotalCount);
        Assert.Equal(3, albums.Albums.Count);
        Assert.Equal(12, artists.Artists.Single(a => a.Name == "Band").Artist.TrackCount);
    }

    [Fact]
    public async Task RecentlyAdded_RailAndList_FollowTheSource_FavoritesAndDownloadedDoNot()
    {
        using var lib = Make();
        var library = lib.Shell.Library;
        foreach (var t in lib.Mixed.Tracks.Concat(lib.ComputerOnly.Tracks)) t.IsFavorite = true;

        await library.SetSourceAsync(LibrarySource.Phone);
        Assert.Equal(new[] { lib.Mixed.Id, lib.PhoneOnly.Id }.OrderBy(i => i),
            library.RecentlyAddedRail.Select(r => ((Album)r.Payload!).Id).OrderBy(i => i));
        Assert.Equal(4, library.RecentlyAdded().Count());

        // Favorites and the Downloaded row keep everything (a filter there would hide content).
        Assert.Equal(13, library.Favourites().Count());

        await library.SetSourceAsync(LibrarySource.Computer);
        Assert.DoesNotContain(library.RecentlyAddedRail, r => ((Album)r.Payload!).Id == lib.PhoneOnly.Id);
        Assert.Equal(11, library.RecentlyAdded().Count());
    }

    [Fact]
    public async Task TheChoice_IsSaved_AndReadBackAtStart()
    {
        using var lib = Make();
        await lib.Shell.Library.SetSourceAsync(LibrarySource.Computer);
        Assert.Equal("Computer", (await lib.Rig.Persistence.LoadSettingsAsync()).PhoneLibrarySource);

        var again = new LibraryViewModel(lib.Rig.Library, lib.Rig.Persistence, new MobileFixtures.NoPicker(), marshal: a => a());
        await again.InitializeAsync();
        Assert.Equal(LibrarySource.Computer, again.Source);

        await lib.Shell.Library.SetSourceAsync(LibrarySource.All);
        Assert.Equal("", (await lib.Rig.Persistence.LoadSettingsAsync()).PhoneLibrarySource);
    }

    [Fact]
    public async Task Menu_TicksTheSourceInForce_AndAPickSetsIt()
    {
        using var lib = Make();
        var menu = OptionsMenuViewModel.ForLibrarySource(lib.Shell, lib.Shell.Library);
        Assert.Equal(new[] { "All Music", "On This Phone", "From Computer" }, menu.Items.Select(i => i.Label));
        Assert.Equal(new[] { true, false, false }, menu.Items.Select(i => i.IsChecked));

        menu.Items[1].PickCommand.Execute(null);
        await Task.Yield();
        Assert.Equal(LibrarySource.Phone, lib.Shell.Library.Source);
        menu = OptionsMenuViewModel.ForLibrarySource(lib.Shell, lib.Shell.Library);
        Assert.Equal(new[] { false, true, false }, menu.Items.Select(i => i.IsChecked));
    }

    [Fact]
    public async Task Search_StaysGlobal()
    {
        using var lib = Make();
        await lib.Shell.Library.SetSourceAsync(LibrarySource.Phone);
        lib.Shell.Search.Query = "Remote 1";
        Assert.Contains(lib.Shell.Search.Songs, t => t.Title == "Remote 1");
    }

    [AvaloniaFact]
    public async Task LibraryHeader_SourceButton_ShowsWithAComputer_AndLightsWhenFiltered()
    {
        using var bare = MobileFixtures.MakeRig(new[] { MobileFixtures.Song("Only") });
        bare.Shell.SelectTabCommand.Execute(MobileTab.Library);
        var window = MobileFixtures.Mount(bare.Shell, out var view);
        Assert.False(MobileFixtures.Named<Button>(MobileFixtures.Find<LibraryPage>(view), "LibrarySourceButton").IsVisible);
        window.Close();

        using var lib = Make();
        lib.Shell.SelectTabCommand.Execute(MobileTab.Library);
        window = MobileFixtures.Mount(lib.Shell, out view);
        var button = MobileFixtures.Named<Button>(MobileFixtures.Find<LibraryPage>(view), "LibrarySourceButton");
        Assert.True(button.IsEffectivelyVisible);
        Assert.False(button.Classes.Contains("on"));

        button.Command!.Execute(null);
        Assert.NotNull(lib.Shell.OptionsMenu);
        await lib.Shell.Library.SetSourceAsync(LibrarySource.Phone);
        window.UpdateLayout();
        Assert.True(button.Classes.Contains("on"));
        window.Close();
    }
}
