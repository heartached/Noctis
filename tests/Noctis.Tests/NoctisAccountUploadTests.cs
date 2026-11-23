using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Noctis.Mobile.Services.Account;
using Noctis.Models;
using Noctis.Services;
using Noctis.Services.Server;
using Noctis.Services.Sync;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The phone's "Send phone songs to computer" end to end against the real NoctisServer on loopback:
/// the phone's own scanned songs go up (desktop songs never), songs the desktop already has are
/// skipped after one noctisHasTracks ask, progress and result counts, what was sent is remembered
/// per account (a restart and a second press send nothing again), a song that cannot be read fails
/// alone and is retried next time, and a cancelled send resumes. Plus the desktop's profile name
/// reaching the phone's account at sign-in and on every sync.
/// </summary>
[Collection("MetadataServiceStatics")]
public class NoctisAccountUploadTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NoctisTests", "acc-upload-" + Guid.NewGuid().ToString("N"));
    private readonly AppSettings _deskSettings = new() { SyncEnabled = true, SyncDeviceId = "desktop-test", SyncDeviceName = "Test PC" };
    private ServerUserStore _users = null!;
    private TestPersistenceService _deskPersistence = null!;
    private LibrarySyncService _sync = null!;
    private DeskLibrary _desk = null!;
    private NoctisServer _server = null!;
    private string _url = "";

    private PersistenceService _phonePersistence = null!;
    private LibraryService _phoneLibrary = null!;
    private readonly NoctisStateRecorder _recorder = new();

    private string AccountDir => Path.Combine(_dir, "phone-nobackup", "account");
    private string OfflineDir => Path.Combine(_dir, "phone-files", "offline");
    private string PhoneMusic => Path.Combine(_dir, "phone-music");
    private string Imports => Path.Combine(_dir, "desk-music", "Noctis Imports");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        _users = new ServerUserStore(Path.Combine(_dir, "users.db"));
        _users.Create("alice", "correct horse", isAdmin: true);
        _deskPersistence = new TestPersistenceService();
        _sync = new LibrarySyncService(() => _deskSettings, _deskPersistence);
        var deskAudio = Path.Combine(_dir, "desk.flac");
        File.WriteAllBytes(deskAudio, Enumerable.Range(0, 4000).Select(i => (byte)(i % 251)).ToArray());
        _desk = new DeskLibrary(Imports);
        _desk.Tracks.Add(new Track { Id = Guid.NewGuid(), Title = "Desk song", Artist = "Desk", AlbumArtist = "Desk", Album = "Desk album", AlbumId = Guid.NewGuid(), FilePath = deskAudio, Duration = TimeSpan.FromSeconds(200), FileSize = 4000 });
        _server = new NoctisServer(_desk, _users, "test", _sync) { PrivateClientsOnly = true };
        await _server.StartAsync(0, certificate: null, bindAddress: IPAddress.Loopback);
        _url = $"http://127.0.0.1:{_server.Port}";

        _phonePersistence = new PersistenceService(Path.Combine(_dir, "phone-files"));
        _phoneLibrary = new LibraryService(new MetadataService(), _phonePersistence, new SqliteLibraryIndexService(_phonePersistence),
            new NoctisAccountLibraryTests.NoOpAudit(), syncRecorder: _recorder);
        Directory.CreateDirectory(PhoneMusic);
        foreach (var name in new[] { "Phone one", "Phone two", "Phone three" })
            NoctisAccountLibraryTests.WriteMp3(Path.Combine(PhoneMusic, name + ".mp3"), name);
        await _phonePersistence.SaveSettingsAsync(new AppSettings { MusicFolders = { PhoneMusic } });
        await _phoneLibrary.ScanAsync(new[] { PhoneMusic }, Ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _server.StopAsync();
        _deskPersistence.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { }
    }

    /// <summary>Every call the phone made, by method name ("noctisHasTracks").</summary>
    private readonly List<string> _calls = new();

    /// <summary>Requests this picks fail the way a dropped connection does.</summary>
    private Func<string, bool> _fail = _ => false;

    private NoctisAccountService Restart() =>
        new(_phoneLibrary, _phonePersistence, accept => new Spy(NoctisHandlers.Sockets(accept), this), AccountDir, OfflineDir, "Galaxy S23",
            _recorder, marshal: a => a());

    private int Calls(string method) { lock (_calls) return _calls.Count(c => c == method); }

    private sealed class Spy(HttpMessageHandler inner, NoctisAccountUploadTests owner) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var method = Path.GetFileNameWithoutExtension(request.RequestUri!.AbsolutePath);
            lock (owner._calls) owner._calls.Add(method);
            return owner._fail(method) ? throw new HttpRequestException("connection reset") : base.SendAsync(request, ct);
        }
    }

    private async Task<NoctisAccountService> SignedInAsync()
    {
        var svc = Restart();
        var fingerprint = await svc.ProbeFingerprintAsync(_url, Ct);
        await svc.SignInAsync(_url, "alice", "correct horse", fingerprint, Ct);
        return svc;
    }

    private Track PhoneSong(string title) => _phoneLibrary.Tracks.Single(t => t.Title == title);

    /// <summary>What reached the desktop's device folder, by file name.</summary>
    private string[] Received() => Directory.Exists(Path.Combine(Imports, "Galaxy S23"))
        ? Directory.GetFiles(Path.Combine(Imports, "Galaxy S23")).Select(Path.GetFileName).OrderBy(n => n).ToArray()!
        : Array.Empty<string>();

    /// <summary>The desktop already has "Phone two" (another file: same title, artist and length).</summary>
    private void DesktopAlreadyHasPhoneTwo()
    {
        var two = PhoneSong("Phone two");
        var copy = Path.Combine(_dir, "desk-two.flac");
        File.WriteAllBytes(copy, new byte[] { 1, 2, 3 });
        _desk.Tracks.Add(new Track { Id = Guid.NewGuid(), Title = "Phone two", Artist = two.Artist, Album = "Elsewhere", AlbumId = Guid.NewGuid(), FilePath = copy, Duration = two.Duration });
    }

    [Fact]
    public async Task Send_UploadsOnlyThePhonesOwnSongs_SkipsWhatTheDesktopHas_AndCounts()
    {
        var svc = await SignedInAsync();
        await svc.SyncNowAsync(Ct);                                                // a desktop song joins the phone's library
        Assert.Contains(_phoneLibrary.Tracks, t => t.SourceType == SourceType.NoctisServer);
        DesktopAlreadyHasPhoneTwo();
        var progress = new List<NoctisUploadProgress>();
        svc.UploadProgress += (_, p) => { lock (progress) progress.Add(p); };

        var result = await svc.SendLocalSongsAsync(Ct);

        Assert.Equal(new NoctisUploadResult(Sent: 2, AlreadyThere: 1, Failed: 0, Previously: 0), result);
        Assert.Equal(new[] { "Phone one.mp3", "Phone three.mp3" }, Received());
        Assert.Equal(2, _desk.Imported.Count);
        Assert.Equal(1, Calls("noctisHasTracks"));                                 // one batched ask for all three
        Assert.Equal(2, Calls("noctisUploadTrack"));
        lock (progress)
            Assert.Equal(new[] { new NoctisUploadProgress(0, 0), new(0, 2), new(1, 2), new(2, 2) }, progress);
        Assert.False(svc.IsUploading);
        // The desktop got the phone's bytes, untouched.
        Assert.Equal(File.ReadAllBytes(PhoneSong("Phone one").FilePath), File.ReadAllBytes(Path.Combine(Imports, "Galaxy S23", "Phone one.mp3")));
    }

    [Fact]
    public async Task Send_Again_OrAfterARestart_SendsNothingTwice_AndANewSongAlone()
    {
        var svc = await SignedInAsync();
        Assert.Equal(3, (await svc.SendLocalSongsAsync(Ct)).Sent);

        Assert.Equal(new NoctisUploadResult(0, 0, 0, Previously: 3), await svc.SendLocalSongsAsync(Ct));
        Assert.Equal(new NoctisUploadResult(0, 0, 0, Previously: 3), await Restart().SendLocalSongsAsync(Ct));
        Assert.Equal(1, Calls("noctisHasTracks"));                                 // remembered: not even asked again
        Assert.Equal(3, Calls("noctisUploadTrack"));

        NoctisAccountLibraryTests.WriteMp3(Path.Combine(PhoneMusic, "Phone four.mp3"), "Phone four");
        await _phoneLibrary.ScanAsync(new[] { PhoneMusic }, Ct);
        Assert.Equal(new NoctisUploadResult(1, 0, 0, Previously: 3), await Restart().SendLocalSongsAsync(Ct));
        Assert.Equal(new[] { "Phone four.mp3", "Phone one.mp3", "Phone three.mp3", "Phone two.mp3" }, Received());
        Assert.Equal(4, _desk.Imported.Count);
    }

    [Fact]
    public async Task Send_ASongThatCannotBeRead_FailsAlone_AndIsTriedAgainNextTime()
    {
        var svc = await SignedInAsync();
        File.Delete(PhoneSong("Phone three").FilePath);                            // gone since the scan

        Assert.Equal(new NoctisUploadResult(2, 0, Failed: 1, 0), await svc.SendLocalSongsAsync(Ct));
        Assert.Equal(new[] { "Phone one.mp3", "Phone two.mp3" }, Received());

        NoctisAccountLibraryTests.WriteMp3(PhoneSong("Phone three").FilePath, "Phone three");
        Assert.Equal(new NoctisUploadResult(1, 0, 0, Previously: 2), await svc.SendLocalSongsAsync(Ct));
    }

    [Fact]
    public async Task Send_Cancelled_KeepsWhatWasSent_AndTheNextPressSendsTheRest()
    {
        var svc = await SignedInAsync();
        using var cts = new CancellationTokenSource();
        svc.UploadProgress += (_, p) => { if (p.Done == 1) cts.Cancel(); };     // the user taps Stop after the first song

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => svc.SendLocalSongsAsync(cts.Token));
        Assert.Single(Received());
        Assert.False(svc.IsUploading);

        Assert.Equal(new NoctisUploadResult(2, 0, 0, Previously: 1), await svc.SendLocalSongsAsync(Ct));
        Assert.Equal(3, Received().Length);
    }

    /// <summary>The sent-songs record was rewritten after every song: N songs, N writes of a record
    /// that grows with each one (N² bytes). It is written every few songs now, and at the end; what a
    /// batch remembered is on disk before the next song goes, so an app killed mid-send re-sends at
    /// most one batch (the desktop then finds those by their bytes: "already there").</summary>
    [Fact]
    public async Task Send_RemembersWhatWasSent_InBatches_NotOneWriteAfterEverySong()
    {
        await SignedInAsync();
        var recordWrites = 0;
        var onDiskAfterTwo = -1;
        var batched = new NoctisAccountService(_phoneLibrary, _phonePersistence, accept => new Spy(NoctisHandlers.Sockets(accept), this),
            AccountDir, OfflineDir, "Galaxy S23", _recorder, marshal: a => a()) { UploadSaveEvery = 2 };
        batched.UploadRecordSaved += () => recordWrites++;
        batched.UploadProgress += (_, p) =>
        {
            if (p.Done == 2) onDiskAfterTwo = SentOnDisk();
        };

        Assert.Equal(3, (await batched.SendLocalSongsAsync(Ct)).Sent);

        Assert.Equal(2, onDiskAfterTwo);         // the first batch was written before the third song went
        Assert.Equal(2, recordWrites);           // after two songs, then at the end: not after each of three
        Assert.Equal(3, SentOnDisk());
    }

    /// <summary>How many songs uploads.json remembers as sent.</summary>
    private int SentOnDisk() =>
        System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(AccountDir, "uploads.json")))!["sent"]!.AsArray().Count;

    [Fact]
    public async Task Send_ASecondCallWhileOneRuns_GetsThatOne_AndItsTokenStopsIt()
    {
        var svc = await SignedInAsync();
        Task<NoctisUploadResult>? second = null;
        using var stop = new CancellationTokenSource();
        svc.UploadProgress += (_, p) =>
        {
            if (p.Done != 1 || second is not null) return;
            second = svc.SendLocalSongsAsync(stop.Token);                         // the Account page reopened mid-send
            stop.Cancel();                                                         // and its Stop tapped
        };

        var first = svc.SendLocalSongsAsync(Ct);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);

        Assert.Same(first, second);
        Assert.Single(Received());
    }

    [Fact]
    public async Task Send_TheSameFileTheDesktopAlreadyHolds_IsAlreadyThere_ByItsBytes()
    {
        var svc = await SignedInAsync();
        // A copy of "Phone one" on the computer under other tags: the title check misses, the hash does not.
        var copy = Path.Combine(_dir, "renamed on the pc.mp3");
        File.Copy(PhoneSong("Phone one").FilePath, copy);
        _desk.Tracks.Add(new Track { Id = Guid.NewGuid(), Title = "Something else", Artist = "Other", Album = "A", AlbumId = Guid.NewGuid(), FilePath = copy, FileSize = new FileInfo(copy).Length });

        Assert.Equal(new NoctisUploadResult(2, AlreadyThere: 1, 0, 0), await svc.SendLocalSongsAsync(Ct));
        Assert.Equal(new[] { "Phone three.mp3", "Phone two.mp3" }, Received());
    }

    [Fact]
    public async Task Send_SyncOffOnTheDesktop_StopsWithSyncDisabled_AndSendsNothing()
    {
        var svc = await SignedInAsync();
        _deskSettings.SyncEnabled = false;
        var ex = await Assert.ThrowsAsync<NoctisServerException>(() => svc.SendLocalSongsAsync(Ct));
        Assert.Equal(NoctisErrorKind.SyncDisabled, ex.Kind);
        Assert.Empty(Received());
    }

    [Fact]
    public async Task Send_ByAnotherAccount_DoesNotReuseTheFirstAccountsRecord()
    {
        var svc = await SignedInAsync();
        await svc.SendLocalSongsAsync(Ct);
        _users.Create("bob", "another pass");
        var fingerprint = await svc.ProbeFingerprintAsync(_url, Ct);
        await svc.SignInAsync(_url, "bob", "another pass", fingerprint, Ct);

        // Asked again from scratch: the desktop already has all three (the hash finds the files).
        Assert.Equal(new NoctisUploadResult(0, AlreadyThere: 3, 0, Previously: 0), await svc.SendLocalSongsAsync(Ct));
    }

    // ── The desktop's profile name ──

    /// <summary>
    /// Bug: the computer's name ("on Test PC" under the account) was taken at sign-in only, so
    /// renaming the computer in Settings → Sync never reached a phone already signed in.
    /// </summary>
    [Fact]
    public async Task ComputerName_ArrivesAtSignIn_AndFollowsARenameOnSync()
    {
        var svc = await SignedInAsync();
        Assert.Equal("Test PC", svc.Account!.ServerName);

        _deskSettings.SyncDeviceName = "Studio PC";
        await svc.SyncNowAsync(Ct);

        Assert.Equal("Studio PC", svc.Account!.ServerName);
        Assert.Equal("Studio PC", Restart().Account!.ServerName);                  // persisted
    }

    [Fact]
    public async Task DisplayName_ArrivesAtSignIn_FollowsARenameOnSync_AndOldAccountsWithoutItStillLoad()
    {
        _desk.ProfileName = "notheartached";
        var svc = await SignedInAsync();
        Assert.Equal("notheartached", svc.Account!.DisplayName);
        Assert.Equal("alice", svc.Account.UserName);
        Assert.Contains("\"displayName\":\"notheartached\"", File.ReadAllText(Path.Combine(AccountDir, "account.json")));

        _desk.ProfileName = "Renamed";
        await svc.SyncNowAsync(Ct);
        Assert.Equal("Renamed", svc.Account!.DisplayName);
        Assert.Equal("Renamed", Restart().Account!.DisplayName);                  // persisted

        _desk.ProfileName = null;                                                  // the desktop removed it
        await svc.SyncNowAsync(Ct);
        Assert.Null(svc.Account!.DisplayName);
        Assert.DoesNotContain("displayName", File.ReadAllText(Path.Combine(AccountDir, "account.json")));

        // An account.json written before the field existed loads as before.
        var path = Path.Combine(AccountDir, "account.json");
        Assert.DoesNotContain("displayName", File.ReadAllText(path));
        var reloaded = Restart().Account;
        Assert.NotNull(reloaded);
        Assert.Null(reloaded!.DisplayName);
        Assert.Equal("alice", reloaded.UserName);
    }

    [Fact]
    public async Task DisplayName_ASyncThatCannotAskForIt_KeepsTheOneShown()
    {
        _desk.ProfileName = "notheartached";
        var svc = await SignedInAsync();
        _desk.ProfileName = "Renamed";
        _fail = method => method == "getNoctisSyncStatus";                         // the one ask drops

        await svc.SyncNowAsync(Ct);                                                // the sync itself still succeeds

        Assert.Equal("notheartached", svc.Account!.DisplayName);
        Assert.NotNull(svc.Account.LastSyncUtc);
        _fail = _ => false;
        await svc.SyncNowAsync(Ct);
        Assert.Equal("Renamed", svc.Account!.DisplayName);
        Assert.Null(NoctisServerClient.CleanDisplayName(" ‮​ "));
        Assert.Equal("a b", NoctisServerClient.CleanDisplayName("  a​ b\u0007 "));
        Assert.Equal(64, NoctisServerClient.CleanDisplayName(new string('n', 100))!.Length);
    }

    /// <summary>The desktop: one song of its own, the upload hooks writing under a temp "Noctis Imports".</summary>
    private sealed class DeskLibrary(string imports) : IServerLibrary
    {
        public List<Track> Tracks { get; } = new();
        public string? ProfileName { get; set; }
        public List<string> Imported { get; } = new();

        public Task<string?> UploadRootAsync(CancellationToken ct)
        {
            Directory.CreateDirectory(imports);
            return Task.FromResult<string?>(imports);
        }

        public Task ImportUploadedAsync(string path, CancellationToken ct)
        {
            lock (Imported) Imported.Add(path);
            return Task.CompletedTask;
        }

        public Task<LibrarySnapshot> SnapshotAsync()
        {
            lock (Tracks) return Task.FromResult(new LibrarySnapshot(Tracks.ToList(), new List<Album>(), new List<Artist>(), new List<Playlist>()));
        }

        public string? ArtworkPath(Guid albumId) => null;
        public Task SetStarredAsync(IReadOnlyList<Guid> trackIds, IReadOnlyList<Guid> albumIds, IReadOnlyList<Guid> artistIds, bool starred) => Task.CompletedTask;
        public Task ScrobbleAsync(Guid trackId) => Task.CompletedTask;
        public Task<Playlist> CreatePlaylistAsync(string name, IReadOnlyList<Guid> trackIds) => Task.FromResult(new Playlist { Name = name });
        public Task<bool> UpdatePlaylistAsync(Guid id, string? name, IReadOnlyList<Guid> add, IReadOnlyList<int> removeIndexes) => Task.FromResult(false);
        public Task<bool> DeletePlaylistAsync(Guid id) => Task.FromResult(false);
        public Task ApplyTrackStateAsync(Guid trackId, TrackSyncState state) => Task.CompletedTask;
        public Task ApplyPlaylistStateAsync(Guid playlistId, PlaylistSyncState state) => Task.CompletedTask;
    }
}
