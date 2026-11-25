using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Noctis.Mobile.Services.Account;
using Noctis.Models;
using Noctis.Services;
using Noctis.Services.Server;
using Noctis.Services.Sync;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Sync with another computer (<see cref="ComputerLinkService"/>), end to end: two real desktop
/// stacks (PersistenceService, LibraryService scanning real files, LibrarySyncService, the
/// LibraryServerAdapter that applies synced state) in two temp folders, each with its music in a
/// different folder so every song has a different id on each computer. Computer A runs the real
/// NoctisServer on loopback; computer B signs in to it the real way (probe, noctisSignIn, device key).
/// </summary>
[Collection("MetadataServiceStatics")]
public class ComputerLinkServiceTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NoctisTests", "pc-link-" + Guid.NewGuid().ToString("N"));
    private Pc _a = null!, _b = null!;
    private ServerUserStore _users = null!;
    private NoctisServer _server = null!;
    private string _url = "";

    public async ValueTask InitializeAsync()
    {
        _a = new Pc(Path.Combine(_root, "A"), "A", syncOn: true);
        _b = new Pc(Path.Combine(_root, "B"), "B", syncOn: false);
        _users = new ServerUserStore(Path.Combine(_root, "A", "users.db"));
        _users.Create("alice", "correct horse", isAdmin: true);
        _server = new NoctisServer(_a.Adapter, _users, "test", _a.Ledger) { PrivateClientsOnly = true };
        await _server.StartAsync(0, certificate: null, bindAddress: System.Net.IPAddress.Loopback);
        _url = $"http://127.0.0.1:{_server.Port}";
    }

    public async ValueTask DisposeAsync()
    {
        await _server.StopAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch { }
    }

    /// <summary>One computer: its own data folder and music folder (so its own song ids).</summary>
    private sealed class Pc
    {
        public readonly string Music;
        public readonly PersistenceService Persistence;
        public readonly LibraryService Library;
        public readonly LibrarySyncService Ledger;
        public readonly LibraryServerAdapter Adapter;
        public readonly AppSettings Settings;
        public readonly TestComputer Protector;

        public Pc(string root, string name, bool syncOn)
        {
            Music = Path.Combine(root, "music-on-" + name);
            Directory.CreateDirectory(Music);
            Protector = new TestComputer(name);
            Persistence = new PersistenceService(Path.Combine(root, "data")) { Protector = Protector };
            Settings = new AppSettings { SyncEnabled = syncOn, SyncDeviceId = "desktop-" + name.ToLowerInvariant() + "-test", SyncDeviceName = "PC " + name, MusicFolders = { Music } };
            Ledger = new LibrarySyncService(() => Settings, Persistence);
            Library = new LibraryService(new MetadataService(), Persistence, new SqliteLibraryIndexService(Persistence),
                new NoctisAccountLibraryTests.NoOpAudit(), syncRecorder: Ledger);
            Adapter = new LibraryServerAdapter(Library, Persistence, new NoOpPlayHistory());
        }

        public async Task AddSongsAsync(params string[] titles)
        {
            foreach (var title in titles)
                NoctisAccountLibraryTests.WriteMp3(Path.Combine(Music, title + ".mp3"), title);
            await Persistence.SaveSettingsAsync(Settings);
            await Library.ScanAsync(new[] { Music });
        }

        public Track Song(string title) => Library.Tracks.Single(t => t.Title == title);

        /// <summary>A change made in this computer's window (the library save records it).</summary>
        public async Task EditAsync(string title, Action<Track> change)
        {
            var t = Song(title);
            change(t);
            await Library.SaveTrackUserStateAsync(new[] { t });
            Ledger.RecordTrackStates(new[] { t }); // what the save's background record does, now
        }

        public async Task<Playlist?> PlaylistAsync(Guid id) => (await Persistence.LoadPlaylistsAsync()).SingleOrDefault(p => p.Id == id);

        public async Task SavePlaylistAsync(Playlist playlist)
        {
            var all = await Persistence.LoadPlaylistsAsync();
            all.RemoveAll(p => p.Id == playlist.Id);
            all.Add(playlist);
            await Persistence.SavePlaylistsAsync(all);
        }

        public async Task DeletePlaylistAsync(Guid id)
        {
            var all = await Persistence.LoadPlaylistsAsync();
            all.RemoveAll(p => p.Id == id);
            await Persistence.SavePlaylistsAsync(all);
        }
    }

    /// <summary>A computer's secret protection: only the computer that protected a key reads it (as DPAPI).</summary>
    private sealed class TestComputer(string name) : SecretProtector
    {
        public override string ComputerId => "pc-" + name;
        public override string Protect(string value) => string.IsNullOrEmpty(value) ? value
            : "enc:test:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(name + "|" + value));
        public override string Unprotect(string value)
        {
            if (string.IsNullOrEmpty(value) || !value.StartsWith("enc:test:", StringComparison.Ordinal)) return string.Empty;
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(value["enc:test:".Length..])).Split('|', 2);
            return parts.Length == 2 && parts[0] == name ? parts[1] : string.Empty;
        }
    }

    private sealed class NoOpPlayHistory : IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    private ComputerLinkService Link(Pc pc) =>
        new(pc.Library, pc.Persistence, pc.Ledger, pc.Adapter, NoctisHandlers.Sockets, pc.Protector);

    private async Task<ComputerLinkService> LinkedAsync()
    {
        var link = Link(_b);
        var (url, fingerprint) = await link.ProbeAsync(_url, TestContext.Current.CancellationToken);
        await link.SignInAsync(url, "alice", "correct horse", fingerprint, TestContext.Current.CancellationToken);
        return link;
    }

    private static Task Tick() => Task.Delay(30); // the next change gets a later stamp

    // ── The basics ───────────────────────────────────────────────────────

    [Fact]
    public async Task DifferentMusicFolders_FavouritesRatingsAndPlaylists_TravelBothWays()
    {
        var ct = TestContext.Current.CancellationToken;
        await _a.AddSongsAsync("One", "Two");
        await _b.AddSongsAsync("One", "Two");
        Assert.NotEqual(_a.Song("One").Id, _b.Song("One").Id); // different paths: different ids
        await _a.EditAsync("One", t => t.IsFavorite = true);
        await _a.EditAsync("Two", t => t.Rating = 4);
        var mix = new Playlist { Name = "Mix", TrackIds = { _a.Song("One").Id, _a.Song("Two").Id }, ModifiedAt = DateTime.UtcNow };
        await _a.SavePlaylistAsync(mix);
        var link = await LinkedAsync();

        var first = await link.SyncNowAsync(ct);

        Assert.Equal(2, first.Paired);
        Assert.True(_b.Song("One").IsFavorite);
        Assert.Equal(4, _b.Song("Two").Rating);
        var onB = await _b.PlaylistAsync(mix.Id);
        Assert.NotNull(onB);
        Assert.Equal("Mix", onB.Name);
        Assert.Equal(new[] { _b.Song("One").Id, _b.Song("Two").Id }, onB.TrackIds);

        // And back: B's own edits reach A, in A's ids.
        await Tick();
        await _b.EditAsync("One", t => t.IsFavorite = false);
        await _b.EditAsync("Two", t => t.Rating = 2);
        var made = new Playlist { Name = "Made on B", TrackIds = { _b.Song("Two").Id }, ModifiedAt = DateTime.UtcNow };
        await _b.SavePlaylistAsync(made);
        await link.SyncNowAsync(ct);

        Assert.False(_a.Song("One").IsFavorite);
        Assert.Equal(2, _a.Song("Two").Rating);
        var onA = await _a.PlaylistAsync(made.Id);
        Assert.NotNull(onA);
        Assert.Equal(new[] { _a.Song("Two").Id }, onA.TrackIds);
    }

    [Fact]
    public async Task ASecondSync_WithNothingNew_ChangesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await _a.AddSongsAsync("One", "Two");
        await _b.AddSongsAsync("One", "Two");
        await _a.EditAsync("One", t => t.IsFavorite = true);
        await _b.EditAsync("Two", t => t.Rating = 3);
        await _a.SavePlaylistAsync(new Playlist { Name = "Mix", TrackIds = { _a.Song("One").Id }, ModifiedAt = DateTime.UtcNow });
        var link = await LinkedAsync();
        await link.SyncNowAsync(ct);
        await Task.Delay(300); // the library saves' own background records settle
        await link.SyncNowAsync(ct);
        await Task.Delay(300);

        var third = await link.SyncNowAsync(ct);

        Assert.Equal(0, third.Received);
        Assert.Equal(0, third.Sent);
        Assert.True(_b.Song("One").IsFavorite);
        Assert.Equal(3, _a.Song("Two").Rating);
    }

    // ── Conflicts ────────────────────────────────────────────────────────

    [Fact]
    public async Task FirstPairing_Merges_NothingIsUnfavouritedOrLost()
    {
        var ct = TestContext.Current.CancellationToken;
        await _a.AddSongsAsync("One");
        await _b.AddSongsAsync("One");
        await _a.EditAsync("One", t => { t.IsFavorite = true; t.PlayCount = 3; });
        await Tick();
        // B's copy was edited later, without a favourite: by "newest wins" alone A would lose it.
        await _b.EditAsync("One", t => { t.Rating = 5; t.PlayCount = 7; });
        var link = await LinkedAsync();

        await link.SyncNowAsync(ct);

        foreach (var t in new[] { _a.Song("One"), _b.Song("One") })
        {
            Assert.True(t.IsFavorite);
            Assert.Equal(5, t.Rating);
            Assert.Equal(7, t.PlayCount);
        }
    }

    [Fact]
    public async Task AfterPairing_TheNewestChangeWins_OnEitherSide()
    {
        var ct = TestContext.Current.CancellationToken;
        await _a.AddSongsAsync("One");
        await _b.AddSongsAsync("One");
        var link = await LinkedAsync();
        await link.SyncNowAsync(ct);

        // Both computers change the same song between syncs (one was off): the later change wins.
        await Tick();
        await _b.EditAsync("One", t => t.Rating = 2);
        await Tick();
        await _a.EditAsync("One", t => t.Rating = 5);
        await link.SyncNowAsync(ct);
        Assert.Equal(5, _a.Song("One").Rating);
        Assert.Equal(5, _b.Song("One").Rating);

        await Tick();
        await _a.EditAsync("One", t => t.Rating = 1);
        await Tick();
        await _b.EditAsync("One", t => t.Rating = 4);
        await link.SyncNowAsync(ct);
        Assert.Equal(4, _a.Song("One").Rating);
        Assert.Equal(4, _b.Song("One").Rating);
    }

    [Fact]
    public async Task PlayCounts_NeverGoDown()
    {
        var ct = TestContext.Current.CancellationToken;
        await _a.AddSongsAsync("One");
        await _b.AddSongsAsync("One");
        await _a.EditAsync("One", t => t.PlayCount = 10);
        var link = await LinkedAsync();
        await link.SyncNowAsync(ct);
        Assert.Equal(10, _b.Song("One").PlayCount);

        await Tick();
        await _b.EditAsync("One", t => t.PlayCount = 12);
        await link.SyncNowAsync(ct);

        Assert.Equal(12, _a.Song("One").PlayCount);
        Assert.Equal(12, _b.Song("One").PlayCount);
    }

    // ── Songs only one computer has ──────────────────────────────────────

    [Fact]
    public async Task ASongOnlyOneComputerHas_KeepsItsPlaceInPlaylists_OnBothComputers()
    {
        var ct = TestContext.Current.CancellationToken;
        await _a.AddSongsAsync("One", "Only On A");
        await _b.AddSongsAsync("One", "Only On B");
        var aOnly = _a.Song("Only On A").Id;
        var mix = new Playlist { Name = "Mix", TrackIds = { _a.Song("One").Id, aOnly }, ModifiedAt = DateTime.UtcNow };
        await _a.SavePlaylistAsync(mix);
        var link = await LinkedAsync();
        await link.SyncNowAsync(ct);

        var onB = (await _b.PlaylistAsync(mix.Id))!;
        Assert.Equal(new[] { _b.Song("One").Id, aOnly }, onB.TrackIds); // A's song stays, under A's id

        await Tick();
        onB.TrackIds.Add(_b.Song("Only On B").Id);
        onB.ModifiedAt = DateTime.UtcNow;
        await _b.SavePlaylistAsync(onB);
        await link.SyncNowAsync(ct);

        var onA = (await _a.PlaylistAsync(mix.Id))!;
        Assert.Equal(new[] { _a.Song("One").Id, aOnly, _b.Song("Only On B").Id }, onA.TrackIds);
    }

    [Fact]
    public async Task ASongBAddsLater_PairsOnTheNextSync()
    {
        var ct = TestContext.Current.CancellationToken;
        await _a.AddSongsAsync("One", "Later");
        await _b.AddSongsAsync("One");
        await _a.EditAsync("Later", t => t.IsFavorite = true);
        var link = await LinkedAsync();
        Assert.Equal(1, (await link.SyncNowAsync(ct)).Paired);

        await _b.AddSongsAsync("Later");
        Assert.Equal(2, (await link.SyncNowAsync(ct)).Paired);

        Assert.True(_b.Song("Later").IsFavorite);
    }

    // ── The same playlists on both computers before linking ──────────────

    [Fact]
    public async Task APlaylistBothComputersHave_UnderOneName_BecomesOnePlaylist_NotTwo()
    {
        var ct = TestContext.Current.CancellationToken;
        await _a.AddSongsAsync("One", "Two", "Three");
        await _b.AddSongsAsync("One", "Two", "Three");
        // Made on each computer by hand (or imported from the same file): two different ids.
        var onA = new Playlist { Name = "Mix", TrackIds = { _a.Song("One").Id, _a.Song("Two").Id }, ModifiedAt = DateTime.UtcNow };
        await _a.SavePlaylistAsync(onA);
        await Tick();
        var onB = new Playlist { Name = "mix ", TrackIds = { _b.Song("One").Id, _b.Song("Three").Id }, ModifiedAt = DateTime.UtcNow };
        await _b.SavePlaylistAsync(onB);
        var link = await LinkedAsync();

        await link.SyncNowAsync(ct);

        // One playlist each, holding every song either had (nothing is lost by linking).
        var allA = await _a.Persistence.LoadPlaylistsAsync();
        var allB = await _b.Persistence.LoadPlaylistsAsync();
        Assert.Single(allA);
        Assert.Single(allB);
        Assert.Equal(onA.Id, allA[0].Id);
        Assert.Equal(onB.Id, allB[0].Id);
        Assert.Equal(new[] { "One", "Three", "Two" }, allA[0].TrackIds.Select(id => _a.Library.GetTrackById(id)!.Title).OrderBy(t => t));
        Assert.Equal(new[] { "One", "Three", "Two" }, allB[0].TrackIds.Select(id => _b.Library.GetTrackById(id)!.Title).OrderBy(t => t));

        // From then on it is one playlist: an edit on A changes B's existing one, and back.
        await Tick();
        var edit = (await _a.PlaylistAsync(onA.Id))!;
        edit.TrackIds.Remove(_a.Song("Two").Id);
        edit.ModifiedAt = DateTime.UtcNow;
        await _a.SavePlaylistAsync(edit);
        await link.SyncNowAsync(ct);
        allB = await _b.Persistence.LoadPlaylistsAsync();
        Assert.Single(allB);
        Assert.DoesNotContain(_b.Song("Two").Id, allB[0].TrackIds);

        await Tick();
        var back = (await _b.PlaylistAsync(onB.Id))!;
        back.Name = "Mix 2";
        back.ModifiedAt = DateTime.UtcNow;
        await _b.SavePlaylistAsync(back);
        await link.SyncNowAsync(ct);
        allA = await _a.Persistence.LoadPlaylistsAsync();
        Assert.Single(allA);
        Assert.Equal("Mix 2", allA[0].Name);

        // A delete follows the pair too.
        await Tick();
        await _a.DeletePlaylistAsync(onA.Id);
        await link.SyncNowAsync(ct);
        Assert.Empty(await _b.Persistence.LoadPlaylistsAsync());
    }

    [Fact]
    public async Task TheSamePlaylistId_OnBothComputers_AtTheFirstSync_TheNewerEditWins()
    {
        var ct = TestContext.Current.CancellationToken;
        await _a.AddSongsAsync("One", "Two");
        await _b.AddSongsAsync("One", "Two");
        // A copied data folder: one id on both computers; B's copy was edited later.
        var id = Guid.NewGuid();
        await _a.SavePlaylistAsync(new Playlist { Id = id, Name = "Mix", TrackIds = { _a.Song("One").Id }, ModifiedAt = DateTime.UtcNow.AddDays(-2) });
        await _b.SavePlaylistAsync(new Playlist { Id = id, Name = "Mix", TrackIds = { _b.Song("One").Id, _b.Song("Two").Id }, ModifiedAt = DateTime.UtcNow.AddDays(-1) });
        var link = await LinkedAsync();

        await link.SyncNowAsync(ct);

        Assert.Equal(new[] { _b.Song("One").Id, _b.Song("Two").Id }, (await _b.PlaylistAsync(id))!.TrackIds);
        Assert.Equal(new[] { _a.Song("One").Id, _a.Song("Two").Id }, (await _a.PlaylistAsync(id))!.TrackIds);
    }

    // ── Deletes ──────────────────────────────────────────────────────────

    [Fact]
    public async Task APlaylistDeletedOnOneComputer_IsDeletedOnTheOther()
    {
        var ct = TestContext.Current.CancellationToken;
        await _a.AddSongsAsync("One");
        await _b.AddSongsAsync("One");
        var fromA = new Playlist { Name = "From A", TrackIds = { _a.Song("One").Id }, ModifiedAt = DateTime.UtcNow };
        var fromB = new Playlist { Name = "From B", TrackIds = { _b.Song("One").Id }, ModifiedAt = DateTime.UtcNow };
        await _a.SavePlaylistAsync(fromA);
        await _b.SavePlaylistAsync(fromB);
        var link = await LinkedAsync();
        await link.SyncNowAsync(ct);
        Assert.NotNull(await _a.PlaylistAsync(fromB.Id));
        Assert.NotNull(await _b.PlaylistAsync(fromA.Id));

        await Tick();
        await _a.DeletePlaylistAsync(fromA.Id);
        await _b.DeletePlaylistAsync(fromB.Id);
        await link.SyncNowAsync(ct);

        Assert.Null(await _b.PlaylistAsync(fromA.Id));
        Assert.Null(await _a.PlaylistAsync(fromB.Id));
    }

    [Fact]
    public async Task AnEmptyPlaylistList_OnThisComputer_DeletesNothingOnTheOther()
    {
        var ct = TestContext.Current.CancellationToken;
        await _a.AddSongsAsync("One");
        await _b.AddSongsAsync("One");
        var mix = new Playlist { Name = "Mix", TrackIds = { _a.Song("One").Id }, ModifiedAt = DateTime.UtcNow };
        await _a.SavePlaylistAsync(mix);
        var link = await LinkedAsync();
        await link.SyncNowAsync(ct);
        Assert.NotNull(await _b.PlaylistAsync(mix.Id));

        // playlists.json emptied on B (a damaged or replaced file reads as no playlists).
        await Tick();
        await _b.Persistence.SavePlaylistsAsync(new List<Playlist>());
        await link.SyncNowAsync(ct);

        Assert.NotNull(await _a.PlaylistAsync(mix.Id));
    }

    // ── Sign-in, offline, sign-out ───────────────────────────────────────

    [Fact]
    public async Task ChangesMadeWhileTheOtherComputerIsOff_ArriveAtTheNextSync()
    {
        var ct = TestContext.Current.CancellationToken;
        await _a.AddSongsAsync("One", "Two");
        await _b.AddSongsAsync("One", "Two");
        var link = await LinkedAsync();
        await link.SyncNowAsync(ct);

        await _server.StopAsync();
        await Tick();
        await _b.EditAsync("One", t => t.IsFavorite = true);
        await Assert.ThrowsAsync<NoctisServerException>(() => link.SyncNowAsync(ct));
        await _a.EditAsync("Two", t => t.Rating = 3);

        // A back on the same address.
        _server = new NoctisServer(_a.Adapter, _users, "test", _a.Ledger) { PrivateClientsOnly = true };
        await _server.StartAsync(new Uri(_url).Port, certificate: null, bindAddress: System.Net.IPAddress.Loopback);
        await link.SyncNowAsync(ct);

        Assert.True(_a.Song("One").IsFavorite);
        Assert.Equal(3, _b.Song("Two").Rating);
    }

    [Fact]
    public async Task SyncOffOnTheOtherComputer_SaysSo()
    {
        await _a.AddSongsAsync("One");
        await _b.AddSongsAsync("One");
        _a.Settings.SyncEnabled = false;
        var link = await LinkedAsync();

        var ex = await Assert.ThrowsAsync<NoctisServerException>(() => link.SyncNowAsync(TestContext.Current.CancellationToken));

        Assert.Equal(NoctisErrorKind.SyncDisabled, ex.Kind);
    }

    [Fact]
    public async Task TheLink_SurvivesARestart_IsThisComputersOnly_AndSignOutKeepsTheData()
    {
        var ct = TestContext.Current.CancellationToken;
        await _a.AddSongsAsync("One");
        await _b.AddSongsAsync("One");
        await _a.EditAsync("One", t => t.IsFavorite = true);
        var link = await LinkedAsync();
        await link.SyncNowAsync(ct);

        Assert.True(Link(_b).IsLinked);                              // a restart reads it back
        var keyFile = Directory.GetFiles(Path.Combine(_b.Persistence.DataDirectory, "sync"), "computer-link-*.json").Single();
        Assert.DoesNotContain("nk_", File.ReadAllText(keyFile));    // the key is never stored in clear
        var other = new ComputerLinkService(_b.Library, _b.Persistence, _b.Ledger, _b.Adapter, NoctisHandlers.Sockets, new TestComputer("C"));
        Assert.False(other.IsLinked);                                // another computer on the same data folder

        await link.SignOutAsync(ct);

        Assert.False(link.IsLinked);
        Assert.False(Link(_b).IsLinked);
        Assert.True(_b.Song("One").IsFavorite);
        Assert.Empty(_users.Devices());                              // its key is gone on A
    }

    [Fact]
    public async Task WhileSignedOut_NothingIsRecorded_AndPhoneSyncOffStaysOff()
    {
        await _b.AddSongsAsync("One");
        _ = Link(_b); // created (as the app does at start), not signed in

        await _b.EditAsync("One", t => t.IsFavorite = true);

        Assert.Equal(0, _b.Ledger.CurrentSeq);
        Assert.False(_b.Ledger.IsEnabled);
    }

    [Fact]
    public async Task ALibraryNotLoadedYet_SyncsNothing_AndKeepsThePairs()
    {
        var ct = TestContext.Current.CancellationToken;
        await _a.AddSongsAsync("One");
        await _b.AddSongsAsync("One");
        var link = await LinkedAsync();
        await link.SyncNowAsync(ct);
        Assert.Single(link.Account!.Links);

        // The app just started: the same data folder, its library not loaded yet.
        var starting = new LibraryService(new MetadataService(), _b.Persistence, new SqliteLibraryIndexService(_b.Persistence),
            new NoctisAccountLibraryTests.NoOpAudit(), syncRecorder: _b.Ledger);
        var early = new ComputerLinkService(starting, _b.Persistence, _b.Ledger, _b.Adapter, NoctisHandlers.Sockets, _b.Protector);

        Assert.Equal(new ComputerLinkResult(0, 0, 0, NoLocalSongs: true), await early.SyncNowAsync(ct));
        Assert.True(early.LastSyncFoundNoSongs);
        Assert.Single(Link(_b).Account!.Links);
    }

    // ── The Settings card ────────────────────────────────────────────────

    /// <summary>Signed in, before the first sync: "Connected to", not "Synced with … · Not synced yet" (11-19).</summary>
    [Avalonia.Headless.XUnit.AvaloniaFact]
    public async Task SettingsCard_BeforeTheFirstSync_SaysConnected_ThenSynced()
    {
        await _a.AddSongsAsync("One");
        await _b.AddSongsAsync("One");
        var link = await LinkedAsync();
        var vm = new Noctis.ViewModels.SettingsViewModel(_b.Persistence, new FakeLibraryService(), new NoOpPlayHistory()) { ComputerLink = link };

        vm.RefreshComputerLinkState();
        Assert.True(vm.IsComputerLinked);
        Assert.StartsWith("Connected to ", vm.ComputerLinkStatus);
        Assert.EndsWith(" · Not synced yet", vm.ComputerLinkStatus);
        Assert.DoesNotContain("Synced with", vm.ComputerLinkStatus);

        await vm.SyncComputerLinkNowCommand.ExecuteAsync(null);

        Assert.Equal(string.Empty, vm.ComputerLinkError);
        Assert.StartsWith("Synced with ", vm.ComputerLinkStatus);
        Assert.Contains(" · Last synced", vm.ComputerLinkStatus);
    }

    /// <summary>Sync now on a computer with no songs yet (11-19): the sync is skipped on purpose (it
    /// would drop every pair), and the card says why instead of a silent "Not synced yet".</summary>
    [Avalonia.Headless.XUnit.AvaloniaFact]
    public async Task SettingsCard_SyncNow_WithNoSongsHere_SaysWhyNothingSynced()
    {
        await _a.AddSongsAsync("One");
        var link = await LinkedAsync(); // B has scanned nothing
        var vm = new Noctis.ViewModels.SettingsViewModel(_b.Persistence, new FakeLibraryService(), new NoOpPlayHistory()) { ComputerLink = link };

        await vm.SyncComputerLinkNowCommand.ExecuteAsync(null);

        Assert.True(link.LastSyncFoundNoSongs);
        Assert.Null(link.Account!.LastSyncUtc);
        Assert.Equal(string.Empty, vm.ComputerLinkError);
        Assert.Equal("Connected to " + NameOf(link) + " · No songs in this library yet. Add a music folder, then sync.", vm.ComputerLinkStatus);

        // Songs scanned: the next Sync now works and the note goes.
        await _b.AddSongsAsync("One");
        await vm.SyncComputerLinkNowCommand.ExecuteAsync(null);

        Assert.False(link.LastSyncFoundNoSongs);
        Assert.StartsWith("Synced with ", vm.ComputerLinkStatus);
        Assert.Contains(" · Last synced", vm.ComputerLinkStatus);
    }

    private static string NameOf(ComputerLinkService link) =>
        string.IsNullOrWhiteSpace(link.Account!.ServerName) ? link.Account.ServerUrl : link.Account.ServerName;

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public async Task SettingsCard_ConnectShowsTheCode_SignInSyncs_SignOutReturnsToTheForm()
    {
        await _a.AddSongsAsync("One");
        await _b.AddSongsAsync("One");
        await _a.EditAsync("One", t => t.IsFavorite = true);
        var vm = new Noctis.ViewModels.SettingsViewModel(_b.Persistence, new FakeLibraryService(), new NoOpPlayHistory()) { ComputerLink = Link(_b) };
        Assert.True(vm.IsComputerLinkFormVisible);
        // The steps name the other computer's switches as its Settings shows them.
        Assert.Equal("On the other computer, turn on “Noctis on your phone” and “Sync with your phone”, then open Connect a phone › Enter manually. Type its address and your account here.",
            vm.ComputerLinkHelp);

        vm.ComputerLinkAddress = _url;
        vm.ComputerLinkUser = "alice";
        vm.ComputerLinkPassword = "wrong";
        await vm.ConnectComputerLinkCommand.ExecuteAsync(null);
        Assert.True(vm.IsComputerLinkConfirming);
        Assert.False(vm.IsComputerLinkFormVisible);
        await vm.ConfirmComputerLinkCommand.ExecuteAsync(null);
        Assert.False(vm.IsComputerLinked);
        Assert.Equal("The email or password isn't right.", vm.ComputerLinkError);

        vm.ComputerLinkPassword = "correct horse";
        await vm.ConfirmComputerLinkCommand.ExecuteAsync(null);

        Assert.True(vm.IsComputerLinked, vm.ComputerLinkError);
        Assert.Equal(string.Empty, vm.ComputerLinkError);
        Assert.Equal(string.Empty, vm.ComputerLinkPassword);              // not kept after sign-in
        Assert.StartsWith("Synced with ", vm.ComputerLinkStatus);
        Assert.Contains("Last synced", vm.ComputerLinkStatus);
        Assert.True(_b.Song("One").IsFavorite);                           // signing in syncs

        await vm.SignOutComputerLinkCommand.ExecuteAsync(null);
        Assert.False(vm.IsComputerLinked);
        Assert.True(vm.IsComputerLinkFormVisible);
    }

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public async Task SettingsCard_AFailedSync_LeavesTheCard_OnceASyncWorksAgain()
    {
        await _a.AddSongsAsync("One");
        await _b.AddSongsAsync("One");
        var link = await LinkedAsync();
        var vm = new Noctis.ViewModels.SettingsViewModel(_b.Persistence, new FakeLibraryService(), new NoOpPlayHistory()) { ComputerLink = link };

        _a.Settings.SyncEnabled = false;
        await vm.SyncComputerLinkNowCommand.ExecuteAsync(null);
        Assert.NotEqual(string.Empty, vm.ComputerLinkError);
        Assert.NotNull(link.LastError);

        // The next sync (the automatic one: straight on the service) works again.
        _a.Settings.SyncEnabled = true;
        await link.SyncNowAsync(TestContext.Current.CancellationToken);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Null(link.LastError);
        Assert.Equal(string.Empty, vm.ComputerLinkError);
        Assert.True(vm.IsComputerLinked);
    }

    [Fact]
    public void Merge_RulesForAFirstPairing()
    {
        var at1 = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var at2 = at1.AddDays(3);
        var theirs = new TrackSyncState(Favorite: false, Rating: 0, Disliked: true, PlayCount: 4, LastPlayed: at2, FavoritedAt: null);
        var mine = new TrackSyncState(Favorite: true, Rating: 3, Disliked: false, PlayCount: 9, LastPlayed: at1, FavoritedAt: at1);

        var merged = ComputerLinkService.Merge(theirs, mine);

        Assert.Equal(new TrackSyncState(true, 3, false, 9, at2, at1), merged);
        Assert.Equal(4, ComputerLinkService.Merge(theirs with { Rating = 4 }, mine).Rating); // both rated: the other computer's
        Assert.Same(mine, ComputerLinkService.Merge(null, mine));
    }
}
