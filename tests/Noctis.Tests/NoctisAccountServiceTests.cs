using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
/// The phone's account service end to end against the REAL NoctisServer on loopback Kestrel
/// (plain http, which the client allows for loopback only; TLS pinning has its own HTTPS test).
/// Every test signs in the real way (probe, noctisSignIn, device key in the X-Noctis-Key
/// header) against a ledger paged two items at a time: catalog → NoctisServer tracks with the desktop's ids that survive a rescan, covers,
/// downloads and the playback switch, favorites both ways without echo, playlists both ways,
/// scrobbles, sync-off, a revoked key and sign-out. The phone side is a real LibraryService on
/// a real PersistenceService.
/// </summary>
[Collection("MetadataServiceStatics")]
public class NoctisAccountServiceTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NoctisTests", "acc-e2e-" + Guid.NewGuid().ToString("N"));
    private ServerUserStore _users = null!;
    private string _apiKey = "";
    private DesktopPersistence _deskPersistence = null!;
    private DesktopLibrary _desk = null!;
    private LibrarySyncService _sync = null!;
    private readonly AppSettings _deskSettings = new() { SyncEnabled = true, SyncDeviceId = "desktop-test", SyncDeviceName = "Test PC" };
    private NoctisServer _server = null!;
    private string _url = "";
    private byte[] _audio = Array.Empty<byte>();
    private byte[] _art = Array.Empty<byte>();
    private readonly List<int> _coverSizes = new();

    private PersistenceService _phonePersistence = null!;
    private LibraryService _phoneLibrary = null!;
    private readonly NoctisStateRecorder _recorder = new();

    private static readonly Guid AlbumA = Guid.NewGuid(), AlbumB = Guid.NewGuid();
    private Track _t1 = null!, _t2 = null!, _t3 = null!;
    private Guid _deskMixId;

    private string AccountDir => Path.Combine(_dir, "phone-nobackup", "account");
    private string OfflineDir => Path.Combine(_dir, "phone-files", "offline");
    private string PhoneMusic => Path.Combine(_dir, "phone-music");

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        _users = new ServerUserStore(Path.Combine(_dir, "users.db"));
        _users.Create("alice", "correct horse", isAdmin: true);
        _apiKey = _users.RegenerateApiKey("alice");

        _audio = Enumerable.Range(0, 5000).Select(i => (byte)(i % 251)).ToArray();
        var audioPath = Path.Combine(_dir, "song.flac");
        File.WriteAllBytes(audioPath, _audio);
        _art = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4 };
        var artPath = Path.Combine(_dir, "cover.jpg");
        File.WriteAllBytes(artPath, _art);

        _t1 = new Track { Id = Guid.NewGuid(), Title = "Alpha", Artist = "The Xylophones", AlbumArtist = "The Xylophones", Album = "First", AlbumId = AlbumA, FilePath = audioPath, Duration = TimeSpan.FromSeconds(200), TrackNumber = 1, Year = 2001, Genre = "Rock", FileSize = _audio.Length, Bitrate = 900, SampleRate = 44100 };
        _t2 = new Track { Id = Guid.NewGuid(), Title = "Beta", Artist = "The Xylophones feat. Z", AlbumArtist = "The Xylophones", Album = "First", AlbumId = AlbumA, FilePath = audioPath, Duration = TimeSpan.FromSeconds(100), TrackNumber = 2, Year = 2001, Genre = "Rock", FileSize = _audio.Length, Rating = 3 };
        _t3 = new Track { Id = Guid.NewGuid(), Title = "Gamma", Artist = "Yolanda", AlbumArtist = "Yolanda", Album = "Second", AlbumId = AlbumB, FilePath = audioPath, Duration = TimeSpan.FromSeconds(50), TrackNumber = 1, Year = 2010, Genre = "Jazz", FileSize = _audio.Length, IsFavorite = true, PlayCount = 5 };

        _deskPersistence = new DesktopPersistence();
        _deskMixId = Guid.NewGuid();
        _deskPersistence.Playlists.Add(new Playlist { Id = _deskMixId, Name = "Desk mix", TrackIds = { _t1.Id, _t3.Id }, ModifiedAt = DateTime.UtcNow.AddHours(-1) });
        _desk = new DesktopLibrary(new[] { _t1, _t2, _t3 },
            new[]
            {
                new Album { Id = AlbumA, Name = "First", Artist = "The Xylophones", Year = 2001, TrackCount = 2 },
                new Album { Id = AlbumB, Name = "Second", Artist = "Yolanda", Year = 2010, TrackCount = 1 },
            },
            new[] { new Artist { Id = Guid.NewGuid(), Name = "The Xylophones" }, new Artist { Id = Guid.NewGuid(), Name = "Yolanda" } },
            _deskPersistence.Playlists, artPath);
        // Two items per pull page, so every sync walks the "more" loop.
        _sync = new LibrarySyncService(() => _deskSettings, _deskPersistence) { ChangesPageSize = 2 };
        _desk.Sync = _sync;
        _server = new NoctisServer(_desk, _users, "test", _sync, coverResizer: (_, path, size, _) =>
        {
            lock (_coverSizes) _coverSizes.Add(size);
            return Task.FromResult<string?>(path);
        }) { PrivateClientsOnly = true };
        await _server.StartAsync(0, certificate: null);
        _url = $"http://127.0.0.1:{_server.Port}";

        _phonePersistence = new PersistenceService(Path.Combine(_dir, "phone-files"));
        _phoneLibrary = new LibraryService(new MetadataService(), _phonePersistence, new SqliteLibraryIndexService(_phonePersistence),
            new NoctisAccountLibraryTests.NoOpAudit(), syncRecorder: _recorder);
    }

    public async ValueTask DisposeAsync()
    {
        await _server.StopAsync();
        _deskPersistence.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { }
    }

    /// <summary>The phone app starting over the same files (first launch or a restart).</summary>
    private NoctisAccountService Restart() =>
        new(_phoneLibrary, _phonePersistence, NoctisHandlers.Sockets, AccountDir, OfflineDir, "Test Phone", _recorder, marshal: a => a());

    /// <summary>A phone signed in the real way: probe, confirm the fingerprint, noctisSignIn.</summary>
    private async Task<NoctisAccountService> SignedInAsync()
    {
        var svc = Restart();
        var fingerprint = await svc.ProbeFingerprintAsync(_url, TestContext.Current.CancellationToken);
        await svc.SignInAsync(_url, "alice", "correct horse", fingerprint, TestContext.Current.CancellationToken);
        return svc;
    }

    private static async Task Until(Func<bool> condition, int timeoutMs = 10_000)
    {
        var start = Environment.TickCount64;
        while (!condition())
        {
            if (Environment.TickCount64 - start > timeoutMs) throw new TimeoutException("condition never became true");
            await Task.Delay(20);
        }
    }

    private Track Phone(Track desk) => _phoneLibrary.GetTrackById(desk.Id)!;

    private static string RemotePath(Track t) => "noctis-remote://tr-" + t.Id.ToString("N");

    // ── Catalog, covers, rescans ─────────────────────────────────────────

    [Fact]
    public async Task CatalogSync_AddsTheDesktopSongs_WithTheDesktopIds_AndCovers_AndTheySurviveARescan()
    {
        var svc = await SignedInAsync();
        var stages = new List<NoctisSyncStage>();
        svc.SyncProgress += (_, p) => { lock (stages) stages.Add(p.Stage); };

        var result = await svc.SyncNowAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, result.Songs);
        var remote = _phoneLibrary.Tracks.Where(t => t.SourceType == SourceType.NoctisServer).ToList();
        Assert.Equal(new[] { _t1.Id, _t2.Id, _t3.Id }.OrderBy(g => g), remote.Select(t => t.Id).OrderBy(g => g));
        var p1 = Phone(_t1);
        Assert.Equal(RemotePath(_t1), p1.FilePath);
        Assert.Equal("Alpha", p1.Title);
        Assert.Equal(AlbumA, p1.AlbumId);
        Assert.Equal("The Xylophones", Phone(_t2).AlbumArtist); // from the album list, not the song's own artist
        Assert.Equal("FLAC", p1.Codec);
        Assert.True(p1.IsLossless);
        Assert.Equal(TimeSpan.FromSeconds(200), p1.Duration);
        Assert.Equal(3, Phone(_t2).Rating);
        Assert.True(Phone(_t3).IsFavorite);
        Assert.Equal(5, Phone(_t3).PlayCount);
        Assert.Contains(NoctisSyncStage.Catalog, stages);
        Assert.Contains(NoctisSyncStage.Done, stages);
        Assert.NotNull(svc.Account!.LastSyncUtc);

        // Covers land where the library looks for them; an album without art is skipped.
        Assert.Equal(_art, File.ReadAllBytes(_phonePersistence.GetArtworkPath(AlbumA)));
        Assert.False(File.Exists(_phonePersistence.GetArtworkPath(AlbumB)));
        lock (_coverSizes) Assert.Equal(new[] { 512 }, _coverSizes);
        await Until(() => _phoneLibrary.GetAlbumById(AlbumA)?.ArtworkPath is not null);

        // A phone rescan of its own folder keeps them.
        Directory.CreateDirectory(PhoneMusic);
        NoctisAccountLibraryTests.WriteMp3(Path.Combine(PhoneMusic, "local.mp3"), "Phone song");
        await _phonePersistence.SaveSettingsAsync(new AppSettings { MusicFolders = { PhoneMusic } });
        await _phoneLibrary.ScanAsync(new[] { PhoneMusic }, TestContext.Current.CancellationToken);
        Assert.Equal(4, _phoneLibrary.Tracks.Count);
        Assert.Same(p1, Phone(_t1));

        // A second sync with nothing new changes nothing.
        var again = await svc.SyncNowAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, again.StateChangesPulled);
        Assert.Equal(0, again.StateChangesPushed);
        Assert.Same(p1, Phone(_t1));
    }

    [Fact]
    public async Task SyncNow_IsSingleFlight()
    {
        var svc = await SignedInAsync();
        var a = svc.SyncNowAsync(TestContext.Current.CancellationToken);
        var b = svc.SyncNowAsync(TestContext.Current.CancellationToken);
        Assert.Same(a, b);
        Assert.True(svc.IsSyncing || a.IsCompleted);
        await a;
        Assert.False(svc.IsSyncing);
    }

    [Fact]
    public async Task CatalogSync_WhileTheDesktopScans_KeepsTheSongsTheScanHasNotReachedYet()
    {
        var ct = TestContext.Current.CancellationToken;
        var svc = await SignedInAsync();
        await svc.SyncNowAsync(ct);
        var p2 = Phone(_t2);
        p2.PlayCount = 7;

        // Mid-scan the desktop lists only what the scan has reached so far.
        _desk.IsScanning = true;
        _desk.Show(_t1);
        var during = await svc.SyncNowAsync(ct);

        Assert.Equal(3, during.Songs);
        Assert.Equal(3, _phoneLibrary.Tracks.Count(t => t.SourceType == SourceType.NoctisServer));
        Assert.Same(p2, Phone(_t2));
        Assert.Equal(7, Phone(_t2).PlayCount);

        // The scan ends without _t3: now it is really gone.
        _desk.IsScanning = false;
        _desk.Show(_t1, _t2);
        await svc.SyncNowAsync(ct);
        Assert.Null(_phoneLibrary.GetTrackById(_t3.Id));
        Assert.Same(p2, Phone(_t2));
    }

    [Fact]
    public async Task CatalogSync_NeverDropsMostSongs_WhileTheDesktopListKeepsChanging()
    {
        var ct = TestContext.Current.CancellationToken;
        var many = Enumerable.Range(0, 80).Select(i => new Track
        {
            Id = Guid.NewGuid(), Title = $"Song {i}", Artist = "Yolanda", AlbumArtist = "Yolanda", Album = "Second", AlbumId = AlbumB,
            FilePath = _t1.FilePath, Duration = TimeSpan.FromSeconds(60), TrackNumber = i + 2, FileSize = _audio.Length,
        }).ToList();
        _desk.Show(new[] { _t1, _t2, _t3 }.Concat(many).ToArray());
        var svc = await SignedInAsync();
        Assert.Equal(83, (await svc.SyncNowAsync(ct)).Songs);

        // A desktop that does not report its scans (an older version): the list starts over
        // and grows between requests, so no two reads agree.
        var reached = 0;
        _desk.Show(_t1, _t2, _t3);
        _desk.BeforeSnapshot = () =>
        {
            if (reached < many.Count) _desk.Show(new[] { _t1, _t2, _t3 }.Concat(many.Take(++reached)).ToArray());
        };
        await svc.SyncNowAsync(ct);
        Assert.Equal(83, _phoneLibrary.Tracks.Count(t => t.SourceType == SourceType.NoctisServer));

        // Settled (two reads agree, not scanning): the 80 are really gone.
        _desk.BeforeSnapshot = null;
        _desk.Show(_t1, _t2, _t3);
        await svc.SyncNowAsync(ct);
        Assert.Equal(3, _phoneLibrary.Tracks.Count(t => t.SourceType == SourceType.NoctisServer));
    }

    // ── Downloads and playback ───────────────────────────────────────────

    [Fact]
    public async Task Download_WritesTheFile_AndPlaybackSwitchesToIt_ThenBackWhenRemoved()
    {
        var svc = await SignedInAsync();
        await svc.SyncNowAsync(TestContext.Current.CancellationToken);
        var p1 = Phone(_t1);
        Assert.True(svc.IsRemote(p1));
        Assert.False(svc.IsDownloaded(p1));

        var stream = svc.ResolvePlaybackUri(p1.FilePath);
        Assert.Equal($"{_url}/rest/stream?id=tr-{_t1.Id:N}&c=NoctisAndroid&v=1.16.1", stream);
        Assert.DoesNotContain(_apiKey, stream);

        var progress = new List<NoctisDownloadProgress>();
        svc.DownloadProgress += (_, p) => { lock (progress) progress.Add(p); };
        await svc.DownloadAsync(new[] { p1 }, TestContext.Current.CancellationToken);

        var file = Path.Combine(OfflineDir, _t1.Id.ToString("N") + ".flac");
        Assert.Equal(_audio, File.ReadAllBytes(file));
        Assert.Empty(Directory.GetFiles(OfflineDir, "*.part"));
        Assert.True(svc.IsDownloaded(p1));
        Assert.Equal(1, svc.DownloadedCount);
        Assert.Equal(_audio.Length, svc.DownloadedBytes);
        Assert.Equal(Path.GetFullPath(file), svc.ResolvePlaybackUri(p1.FilePath));
        lock (progress) Assert.Contains(progress, p => p.Completed >= 1 && p.Pending == 0);

        // A restart finds the file again.
        var restarted = Restart();
        Assert.True(restarted.IsDownloaded(p1));
        Assert.Equal(Path.GetFullPath(file), restarted.ResolvePlaybackUri(p1.FilePath));

        await svc.RemoveDownloadsAsync(new[] { p1 });
        Assert.False(File.Exists(file));
        Assert.Equal(stream, svc.ResolvePlaybackUri(p1.FilePath));
        Assert.Equal(0, svc.DownloadedCount);
    }

    [Fact]
    public async Task Downloads_StopWithStorageFull_WhenThePhoneHasNoRoomLeft()
    {
        await SignedInAsync();
        // The same phone, restarted with only the reserve (minus a byte) free.
        var svc = new NoctisAccountService(_phoneLibrary, _phonePersistence, NoctisHandlers.Sockets, AccountDir, OfflineDir,
            "Test Phone", _recorder, marshal: a => a())
        {
            FreeSpace = _ => NoctisAccountService.StorageReserveBytes - 1,
        };
        await svc.SyncNowAsync(TestContext.Current.CancellationToken);

        var ex = await Assert.ThrowsAsync<NoctisServerException>(() => svc.DownloadAllAsync(TestContext.Current.CancellationToken));
        Assert.Equal(NoctisErrorKind.StorageFull, ex.Kind);
        Assert.Equal(0, svc.DownloadedCount);
        Assert.Empty(Directory.Exists(OfflineDir) ? Directory.GetFiles(OfflineDir) : Array.Empty<string>());
        Assert.True(svc.IsSignedIn); // a full phone is not a reason to forget the account
    }

    [Fact]
    public async Task DownloadAll_FetchesEveryDesktopSong_AndRemoveAllEmptiesTheFolder()
    {
        var svc = await SignedInAsync();
        await svc.SyncNowAsync(TestContext.Current.CancellationToken);
        await svc.DownloadAllAsync(TestContext.Current.CancellationToken);
        Assert.Equal(3, svc.DownloadedCount);
        Assert.Equal(3L * _audio.Length, svc.DownloadedBytes);

        await svc.RemoveAllDownloadsAsync();
        Assert.Equal(0, svc.DownloadedCount);
        Assert.Empty(Directory.GetFiles(OfflineDir));
    }

    [Fact]
    public async Task ResolvePlaybackUri_AcceptsOnlyTheStrictDesktopSongPath()
    {
        var svc = await SignedInAsync();
        var hex = _t1.Id.ToString("N");
        var stream = svc.ResolvePlaybackUri("noctis-remote://tr-" + hex);
        Assert.Equal($"{_url}/rest/stream?id=tr-{hex}&c=NoctisAndroid&v=1.16.1", stream);
        Assert.DoesNotContain(svc.Account!.DeviceKey, stream);
        Assert.Null(svc.ResolvePlaybackUri("noctis-remote://tr-../../x"));
        Assert.Null(svc.ResolvePlaybackUri("noctis-remote://tr-" + hex.ToUpperInvariant()));
        Assert.Null(svc.ResolvePlaybackUri("noctis-remote://tr-" + hex + "/"));
        Assert.Null(svc.ResolvePlaybackUri("noctis-remote://tr-" + hex + "?apiKey=x"));
        Assert.Null(svc.ResolvePlaybackUri("noctis-remote://tr-" + _t1.Id.ToString("D")));
        Assert.Null(svc.ResolvePlaybackUri("noctis-remote://al-" + hex));
        Assert.Null(svc.ResolvePlaybackUri(Path.Combine(_dir, "song.flac")));
        Assert.Null(svc.ResolvePlaybackUri(""));
        Assert.Null(svc.ResolvePlaybackUri(null!));
    }

    // ── Favorites and ratings, both ways ─────────────────────────────────

    [Fact]
    public async Task PhoneFavorite_IsPushed_TheDesktopAppliesIt_AndNothingEchoesBack()
    {
        var svc = await SignedInAsync();
        await svc.SyncNowAsync(TestContext.Current.CancellationToken);
        var p1 = Phone(_t1);

        p1.IsFavorite = true;
        p1.Rating = 4;
        await _phoneLibrary.SaveTrackUserStateAsync(new[] { p1 });
        await Until(() => svc.PendingTrackCount == 1);

        var result = await svc.SyncNowAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, result.StateChangesPushed);
        var applied = Assert.Single(_desk.AppliedTracks, a => a.Id == _t1.Id);
        Assert.True(applied.State.Favorite);
        Assert.Equal(4, applied.State.Rating);
        Assert.Equal(0, applied.State.PlayCount);   // plays travel as scrobbles only
        Assert.Null(applied.State.LastPlayed);
        Assert.True(_t1.IsFavorite);
        Assert.Equal(4, _t1.Rating);
        Assert.Equal(0, svc.PendingTrackCount);

        var again = await svc.SyncNowAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, again.StateChangesPushed);
        Assert.Single(_desk.AppliedTracks, a => a.Id == _t1.Id);
        Assert.True(Phone(_t1).IsFavorite);
    }

    [Fact]
    public async Task DesktopChange_IsPulled_AndApplyingItIsNotRecordedAsAPhoneEdit()
    {
        var svc = await SignedInAsync();
        await svc.SyncNowAsync(TestContext.Current.CancellationToken);

        _t2.IsDisliked = true;
        _t2.Rating = 5;
        _t2.PlayCount = 9;
        _t2.LastPlayed = DateTime.UtcNow.AddMinutes(-5);
        _sync.RecordTrackStates(new[] { _t2 });

        var result = await svc.SyncNowAsync(TestContext.Current.CancellationToken);

        var p2 = Phone(_t2);
        Assert.True(result.StateChangesPulled >= 1);
        Assert.True(p2.IsDisliked);
        Assert.Equal(5, p2.Rating);
        Assert.Equal(9, p2.PlayCount);
        Assert.NotNull(p2.LastPlayed);

        // The library save of the applied state reaches the recorder; it must not queue a push.
        await Task.Delay(300);
        Assert.Equal(0, svc.PendingTrackCount);
        var again = await svc.SyncNowAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, again.StateChangesPushed);
        Assert.DoesNotContain(_desk.AppliedTracks, a => a.Id == _t2.Id);
    }

    [Fact]
    public async Task ALedgerLongerThanOnePage_IsFollowedToTheEnd()
    {
        // Five items (three songs, two playlists) at two per page: three pulls in one sync.
        _t1.Rating = 2; _t2.Rating = 4; _t3.IsDisliked = true;
        _sync.RecordTrackStates(new[] { _t1, _t2, _t3 });
        _deskPersistence.Playlists.Add(new Playlist { Name = "Second list", TrackIds = { _t2.Id }, ModifiedAt = DateTime.UtcNow });
        var svc = await SignedInAsync();

        var result = await svc.SyncNowAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Playlists);
        Assert.Equal(2, Phone(_t1).Rating);
        Assert.Equal(4, Phone(_t2).Rating);
        Assert.True(Phone(_t3).IsDisliked);
        var again = await svc.SyncNowAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, again.StateChangesPulled);
    }

    [Fact]
    public async Task NewerPhoneEdit_WinsOverAnOlderDesktopItem()
    {
        var svc = await SignedInAsync();
        await svc.SyncNowAsync(TestContext.Current.CancellationToken);

        _t1.Rating = 1;
        _sync.RecordTrackStates(new[] { _t1 });          // desktop edit first…
        await Task.Delay(20);
        var p1 = Phone(_t1);
        p1.Rating = 5;                                    // …the phone's is later
        await _phoneLibrary.SaveTrackUserStateAsync(new[] { p1 });
        await Until(() => svc.PendingTrackCount == 1);

        await svc.SyncNowAsync(TestContext.Current.CancellationToken);

        Assert.Equal(5, Phone(_t1).Rating);
        Assert.Equal(5, _t1.Rating);
    }

    // ── Playlists ────────────────────────────────────────────────────────

    [Fact]
    public async Task Playlists_RoundTrip_DesktopToPhone_PhoneEditsBack_DesktopRenameAndDelete()
    {
        await _phonePersistence.SavePlaylistsAsync(new List<Playlist> { new() { Name = "Phone only" } });
        var svc = await SignedInAsync();

        var first = await svc.SyncNowAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, first.Playlists);
        var phoneLists = await _phonePersistence.LoadPlaylistsAsync();
        var mix = Assert.Single(phoneLists, p => p.Id == _deskMixId);
        Assert.Equal("Desk mix", mix.Name);
        Assert.Equal(new[] { _t1.Id, _t3.Id }, mix.TrackIds);
        Assert.Contains(phoneLists, p => p.Name == "Phone only");

        // Phone adds a song (what LibraryViewModel.AddToPlaylistAsync does).
        mix.TrackIds.Add(_t2.Id);
        mix.ModifiedAt = DateTime.UtcNow;
        await _phonePersistence.SavePlaylistsAsync(phoneLists);
        var second = await svc.SyncNowAsync(TestContext.Current.CancellationToken);
        Assert.True(second.StateChangesPushed >= 1);
        var applied = _desk.AppliedPlaylists.Last(a => a.Id == _deskMixId);
        Assert.Equal(new[] { _t1.Id, _t3.Id, _t2.Id }, applied.State.TrackIds);
        Assert.Equal(new[] { _t1.Id, _t3.Id, _t2.Id }, _deskPersistence.Playlists.Single(p => p.Id == _deskMixId).TrackIds);
        Assert.DoesNotContain(_desk.AppliedPlaylists, a => a.State.Name == "Phone only");

        // Desktop renames it.
        await Task.Delay(20);
        var deskMix = _deskPersistence.Playlists.Single(p => p.Id == _deskMixId);
        deskMix.Name = "Renamed";
        deskMix.ModifiedAt = DateTime.UtcNow;
        await svc.SyncNowAsync(TestContext.Current.CancellationToken);
        Assert.Equal("Renamed", (await _phonePersistence.LoadPlaylistsAsync()).Single(p => p.Id == _deskMixId).Name);

        // A stale phone save that dropped it (in-memory list from before the sync) is repaired.
        await _phonePersistence.SavePlaylistsAsync((await _phonePersistence.LoadPlaylistsAsync()).Where(p => p.Id != _deskMixId).ToList());
        await svc.SyncNowAsync(TestContext.Current.CancellationToken);
        Assert.Contains(await _phonePersistence.LoadPlaylistsAsync(), p => p.Id == _deskMixId && p.Name == "Renamed");
        Assert.Contains(_deskPersistence.Playlists, p => p.Id == _deskMixId); // and never deleted on the desktop

        // Desktop deletes it: the tombstone removes it from the phone.
        _deskPersistence.Playlists.RemoveAll(p => p.Id == _deskMixId);
        await svc.SyncNowAsync(TestContext.Current.CancellationToken);
        var after = await _phonePersistence.LoadPlaylistsAsync();
        Assert.DoesNotContain(after, p => p.Id == _deskMixId);
        Assert.Contains(after, p => p.Name == "Phone only");
    }

    // ── Plays ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Plays_AreQueuedAcrossRestarts_AndSentAsScrobbles()
    {
        var svc = await SignedInAsync();
        await svc.SyncNowAsync(TestContext.Current.CancellationToken);

        var playedAt = DateTime.UtcNow.AddMinutes(-3);
        svc.RecordPlay(Phone(_t1), playedAt);
        svc.RecordPlay(new Track { FilePath = Path.Combine(_dir, "local.mp3") }, DateTime.UtcNow); // not a desktop song
        Assert.Equal(1, svc.PendingPlayCount);
        var pendingFile = Path.Combine(AccountDir, "pending.json");
        await Until(() => File.Exists(pendingFile) && File.ReadAllText(pendingFile).Contains(_t1.Id.ToString("N")));

        var restarted = Restart();
        Assert.Equal(1, restarted.PendingPlayCount);
        var result = await restarted.SyncNowAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, result.PlaysSent);
        Assert.Equal(new[] { _t1.Id }, _desk.Scrobbled);
        // Sent with its own time (ms), not the sync's.
        var sentAt = Assert.Single(_desk.ScrobbleTimes);
        Assert.InRange((sentAt - playedAt).Duration(), TimeSpan.Zero, TimeSpan.FromMilliseconds(1));
        Assert.Equal(0, restarted.PendingPlayCount);
        var again = await restarted.SyncNowAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, again.PlaysSent);
        Assert.Single(_desk.Scrobbled);

        // The desktop's count comes back through the ledger.
        Assert.Equal(1, Phone(_t1).PlayCount);
    }

    [Fact]
    public async Task Plays_ReachTheDisk_EvenWhileSomethingIsReadingThePendingFile()
    {
        var svc = await SignedInAsync();
        await svc.SyncNowAsync(TestContext.Current.CancellationToken);
        var pendingFile = Path.Combine(AccountDir, "pending.json");
        Assert.True(File.Exists(pendingFile));

        // A reader (virus scanner, indexer) holding the file open the way File.ReadAllText does:
        // Windows refuses to replace it until the handle goes, so the save must wait it out.
        using (new FileStream(pendingFile, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            svc.RecordPlay(Phone(_t1), DateTime.UtcNow);
            await Task.Delay(300, TestContext.Current.CancellationToken);
        }

        await Until(() => File.ReadAllText(pendingFile).Contains(_t1.Id.ToString("N")));
        Assert.Equal(1, Restart().PendingPlayCount);
    }

    // ── Failures and sign-out ────────────────────────────────────────────

    [Fact]
    public async Task SyncOff_StillImportsTheCatalog_ThenReportsSyncDisabled()
    {
        _deskSettings.SyncEnabled = false;
        var svc = await SignedInAsync();

        var ex = await Assert.ThrowsAsync<NoctisServerException>(() => svc.SyncNowAsync(TestContext.Current.CancellationToken));

        Assert.Equal(NoctisErrorKind.SyncDisabled, ex.Kind);
        Assert.Equal(3, _phoneLibrary.Tracks.Count(t => t.SourceType == SourceType.NoctisServer));
        Assert.True(svc.IsSignedIn);
        Assert.False(svc.IsSyncing);
    }

    [Fact]
    public async Task RejectedKey_SignsOutLocally_KeepingTheLibraryAndDownloads()
    {
        var svc = await SignedInAsync();
        await svc.SyncNowAsync(TestContext.Current.CancellationToken);
        await svc.DownloadAsync(new[] { Phone(_t1) }, TestContext.Current.CancellationToken);

        // The desktop removes this phone (Settings → Account & Devices → Remove).
        Assert.True(_users.RevokeDevice("alice", svc.DeviceId));
        var changed = 0;
        svc.StateChanged += (_, _) => Interlocked.Increment(ref changed);
        var ex = await Assert.ThrowsAsync<NoctisServerException>(() => svc.SyncNowAsync(TestContext.Current.CancellationToken));

        Assert.Equal(NoctisErrorKind.SignedOut, ex.Kind);
        Assert.False(svc.IsSignedIn);
        Assert.False(File.Exists(Path.Combine(AccountDir, "account.json")));
        Assert.True(changed > 0);
        Assert.Equal(3, _phoneLibrary.Tracks.Count(t => t.SourceType == SourceType.NoctisServer));
        // Downloads stay and still resolve; streaming needs an account.
        Assert.NotNull(svc.ResolvePlaybackUri(RemotePath(_t1)));
        Assert.Null(svc.ResolvePlaybackUri(RemotePath(_t2)));

        // Signing in again works and picks up where it left off.
        var fingerprint = await svc.ProbeFingerprintAsync(_url, TestContext.Current.CancellationToken);
        await svc.SignInAsync(_url, "alice", "correct horse", fingerprint, TestContext.Current.CancellationToken);
        Assert.Equal(3, (await svc.SyncNowAsync(TestContext.Current.CancellationToken)).Songs);
    }

    [Fact]
    public async Task SignOut_RemovesDesktopSongs_TheirPlaylists_Downloads_AndTheAccount()
    {
        Directory.CreateDirectory(PhoneMusic);
        NoctisAccountLibraryTests.WriteMp3(Path.Combine(PhoneMusic, "local.mp3"), "Phone song");
        await _phonePersistence.SaveSettingsAsync(new AppSettings { MusicFolders = { PhoneMusic } });
        await _phoneLibrary.ScanAsync(new[] { PhoneMusic }, TestContext.Current.CancellationToken);
        await _phonePersistence.SavePlaylistsAsync(new List<Playlist> { new() { Name = "Phone only" } });
        var svc = await SignedInAsync();
        var deviceId = svc.DeviceId;
        var key = svc.Account!.DeviceKey;
        await svc.SyncNowAsync(TestContext.Current.CancellationToken);
        await svc.DownloadAsync(new[] { Phone(_t1) }, TestContext.Current.CancellationToken);
        svc.RecordPlay(Phone(_t2), DateTime.UtcNow);
        Assert.Equal(4, _phoneLibrary.Tracks.Count);

        await svc.SignOutAsync(removeDownloads: true, TestContext.Current.CancellationToken);

        Assert.Null(_users.ByApiKey(key));                // revoked on the desktop too
        Assert.False(svc.IsSignedIn);
        Assert.Null(svc.Account);
        Assert.False(File.Exists(Path.Combine(AccountDir, "account.json")));
        Assert.False(File.Exists(Path.Combine(AccountDir, "sync.json")));
        Assert.Equal(SourceType.Local, Assert.Single(_phoneLibrary.Tracks).SourceType);
        Assert.Equal("Phone only", Assert.Single(await _phonePersistence.LoadPlaylistsAsync()).Name);
        Assert.Empty(Directory.GetFiles(OfflineDir));
        Assert.Equal(0, svc.DownloadedCount);
        Assert.Equal(0, svc.PendingPlayCount);
        Assert.Null(svc.ResolvePlaybackUri(RemotePath(_t1)));
        await Assert.ThrowsAsync<NoctisServerException>(() => svc.SyncNowAsync(TestContext.Current.CancellationToken));
        // The install keeps its device id for the next sign-in.
        Assert.Equal(deviceId, Restart().DeviceId);
    }

    [Fact]
    public async Task SignOut_KeepingDownloads_LeavesTheFiles()
    {
        var svc = await SignedInAsync();
        await svc.SyncNowAsync(TestContext.Current.CancellationToken);
        await svc.DownloadAsync(new[] { Phone(_t1) }, TestContext.Current.CancellationToken);

        await svc.SignOutAsync(removeDownloads: false, TestContext.Current.CancellationToken);

        Assert.Single(Directory.GetFiles(OfflineDir));
        Assert.Equal(1, svc.DownloadedCount);
    }

    // ── Sign-in and TLS pinning ──────────────────────────────────────────

    [Fact]
    public async Task SignIn_WrongPassword_IsBadCredentials()
    {
        var svc = Restart();
        Assert.Equal(string.Empty, await svc.ProbeFingerprintAsync(_url, TestContext.Current.CancellationToken)); // plain-http loopback has no certificate
        var ex = await Assert.ThrowsAsync<NoctisServerException>(() =>
            svc.SignInAsync(_url, "alice", "wrong password", "", TestContext.Current.CancellationToken));
        Assert.Equal(NoctisErrorKind.BadCredentials, ex.Kind);
        Assert.False(svc.IsSignedIn);
        Assert.False(File.Exists(Path.Combine(AccountDir, "account.json")));
    }

    [Fact]
    public async Task SignIn_IssuesADeviceKey_ThatSyncs_AndStoresNoPassword()
    {
        var svc = Restart();
        var fingerprint = await svc.ProbeFingerprintAsync(_url, TestContext.Current.CancellationToken);

        await svc.SignInAsync(_url, "alice", "correct horse", fingerprint, TestContext.Current.CancellationToken);

        Assert.True(svc.IsSignedIn);
        Assert.False(string.IsNullOrEmpty(svc.Account!.DeviceKey));
        Assert.NotEqual(_apiKey, svc.Account.DeviceKey);
        Assert.Equal(svc.DeviceId, svc.Account.DeviceId);
        Assert.Equal("alice", svc.Account.UserName);
        Assert.Equal("Test PC", svc.Account.ServerName);
        var device = Assert.Single(_users.Devices());
        Assert.Equal(svc.DeviceId, device.DeviceId);
        Assert.Equal("Test Phone", device.DeviceName);
        Assert.True(File.Exists(Path.Combine(AccountDir, "account.json")));
        Assert.DoesNotContain("correct horse", File.ReadAllText(Path.Combine(AccountDir, "account.json")));
        var result = await svc.SyncNowAsync(TestContext.Current.CancellationToken);
        Assert.Equal(3, result.Songs);
    }

    [Fact]
    public async Task Https_ProbeReturnsTheFingerprint_PinnedCallsWork_AndAnyOtherCertificateIsRefusedBeforeCredentialsLeave()
    {
        // Schannel refuses ephemeral private keys for a server credential (see NoctisServerTests).
        if (!OperatingSystem.IsWindows()) return;
        using var cert = ServerCertificate.LoadOrCreate(Path.Combine(_dir, "tls"));
        using var other = ServerCertificate.Create();
        await using var https = new NoctisServer(_desk, _users, "test", _sync);
        var authenticated = 0;
        https.ClientAuthenticated += (_, _) => Interlocked.Increment(ref authenticated);
        await https.StartAsync(0, cert, TestContext.Current.CancellationToken);
        var url = $"https://127.0.0.1:{https.Port}";
        var svc = Restart();

        var fingerprint = await svc.ProbeFingerprintAsync(url + "/", TestContext.Current.CancellationToken);
        Assert.Equal(ServerCertificate.Fingerprint(cert), fingerprint);
        Assert.Equal(0, authenticated); // the probe carries no credentials

        using (var pinned = new NoctisServerClient(NoctisHandlers.Sockets, url, fingerprint, _apiKey))
            Assert.Equal(3, await pinned.GetSongCountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, authenticated);

        var wrong = ServerCertificate.Fingerprint(other);
        using (var mismatched = new NoctisServerClient(NoctisHandlers.Sockets, url, wrong, _apiKey))
        {
            var ex = await Assert.ThrowsAsync<NoctisServerException>(() => mismatched.GetSongCountAsync(TestContext.Current.CancellationToken));
            Assert.Equal(NoctisErrorKind.CertificateChanged, ex.Kind);
        }
        var signIn = await Assert.ThrowsAsync<NoctisServerException>(() =>
            svc.SignInAsync(url, "alice", "correct horse", wrong, TestContext.Current.CancellationToken));
        Assert.Equal(NoctisErrorKind.CertificateChanged, signIn.Kind);
        Assert.Equal(1, authenticated); // neither the key nor the password reached the server
        Assert.False(svc.IsSignedIn);

        // The right pin but a wrong password gets as far as the password check.
        var bad = await Assert.ThrowsAsync<NoctisServerException>(() =>
            svc.SignInAsync(url, "alice", "nope nope", fingerprint, TestContext.Current.CancellationToken));
        Assert.Equal(NoctisErrorKind.BadCredentials, bad.Kind);

        // The right pin and password: signed in, the pin stored, and a full sync over TLS.
        await svc.SignInAsync(url, "alice", "correct horse", fingerprint, TestContext.Current.CancellationToken);
        Assert.Equal(fingerprint, svc.Account!.Fingerprint);
        Assert.Equal(3, (await svc.SyncNowAsync(TestContext.Current.CancellationToken)).Songs);
    }

    // ── Desktop fakes ────────────────────────────────────────────────────

    /// <summary>The desktop's playlists.json: the ledger reads it on every pull, the applier writes it.</summary>
    private sealed class DesktopPersistence : TestPersistenceService
    {
        public List<Playlist> Playlists { get; } = new();
        public override Task<List<Playlist>> LoadPlaylistsAsync() => Task.FromResult(Playlists.ToList());
    }

    /// <summary>In-memory desktop library that behaves like LibraryServerAdapter + the desktop's
    /// recorder: applied and scrobbled state is re-recorded into the ledger (echo-guarded there).</summary>
    private sealed class DesktopLibrary : IServerLibrary
    {
        private readonly List<Track> _tracks;
        private readonly List<Album> _albums;
        private readonly List<Artist> _artists;
        private readonly List<Playlist> _playlists;
        private readonly string _art;
        public LibrarySyncService? Sync { get; set; }
        public List<Guid> Scrobbled { get; } = new();
        public List<DateTime> ScrobbleTimes { get; } = new();
        public List<(Guid Id, TrackSyncState State)> AppliedTracks { get; } = new();
        public List<(Guid Id, PlaylistSyncState State)> AppliedPlaylists { get; } = new();

        public DesktopLibrary(IEnumerable<Track> tracks, IEnumerable<Album> albums, IEnumerable<Artist> artists, List<Playlist> playlists, string art)
        {
            _tracks = tracks.ToList(); _albums = albums.ToList(); _artists = artists.ToList(); _playlists = playlists; _art = art;
            foreach (var a in _albums) a.Tracks = _tracks.Where(t => t.AlbumId == a.Id).ToList();
        }

        public bool IsScanning { get; set; }

        /// <summary>Runs before every request reads the library (a scan moving along).</summary>
        public Action? BeforeSnapshot { get; set; }

        /// <summary>What the desktop's library lists from now on.</summary>
        public void Show(params Track[] tracks)
        {
            lock (_tracks) { _tracks.Clear(); _tracks.AddRange(tracks); }
        }

        public Task<LibrarySnapshot> SnapshotAsync()
        {
            BeforeSnapshot?.Invoke();
            lock (_tracks)
                return Task.FromResult(new LibrarySnapshot(_tracks.ToList(), _albums.ToList(), _artists.ToList(), _playlists.ToList()));
        }

        public string? ArtworkPath(Guid albumId) => albumId == AlbumA ? _art : null;

        public Task SetStarredAsync(IReadOnlyList<Guid> trackIds, IReadOnlyList<Guid> albumIds, IReadOnlyList<Guid> artistIds, bool starred)
        {
            foreach (var t in _tracks.Where(t => trackIds.Contains(t.Id))) t.IsFavorite = starred;
            return Task.CompletedTask;
        }

        public Task ScrobbleAsync(Guid trackId, DateTime playedUtc)
        {
            lock (Scrobbled) ScrobbleTimes.Add(playedUtc);
            return ScrobbleAsync(trackId);
        }

        public Task ScrobbleAsync(Guid trackId)
        {
            lock (Scrobbled) Scrobbled.Add(trackId);
            var t = _tracks.FirstOrDefault(x => x.Id == trackId);
            if (t is not null)
            {
                t.PlayCount++;
                t.LastPlayed = DateTime.UtcNow;
                Sync?.RecordTrackStates(new[] { t });
            }
            return Task.CompletedTask;
        }

        public Task<Playlist> CreatePlaylistAsync(string name, IReadOnlyList<Guid> trackIds)
        {
            var p = new Playlist { Name = name, TrackIds = trackIds.ToList() };
            _playlists.Add(p);
            return Task.FromResult(p);
        }

        public Task<bool> UpdatePlaylistAsync(Guid id, string? name, IReadOnlyList<Guid> add, IReadOnlyList<int> removeIndexes) =>
            Task.FromResult(false);

        public Task<bool> DeletePlaylistAsync(Guid id) => Task.FromResult(_playlists.RemoveAll(p => p.Id == id) > 0);

        public Task ApplyTrackStateAsync(Guid trackId, TrackSyncState state)
        {
            AppliedTracks.Add((trackId, state));
            var t = _tracks.FirstOrDefault(x => x.Id == trackId);
            if (t is null) return Task.CompletedTask;
            t.IsFavorite = state.Favorite;
            if (state.FavoritedAt.HasValue) t.FavoritedAt = state.FavoritedAt;
            if (state.PlayCount > t.PlayCount) t.PlayCount = state.PlayCount;
            if (state.LastPlayed.HasValue && (t.LastPlayed is null || state.LastPlayed > t.LastPlayed)) t.LastPlayed = state.LastPlayed;
            t.Rating = Math.Clamp(state.Rating, 0, 5);
            t.IsDisliked = state.Disliked;
            Sync?.RecordTrackStates(new[] { t });
            return Task.CompletedTask;
        }

        public Task ApplyPlaylistStateAsync(Guid playlistId, PlaylistSyncState state)
        {
            AppliedPlaylists.Add((playlistId, state));
            var existing = _playlists.FirstOrDefault(p => p.Id == playlistId);
            if (state.Deleted)
            {
                if (existing is not null) _playlists.Remove(existing);
                return Task.CompletedTask;
            }
            if (existing is null)
            {
                existing = new Playlist { Id = playlistId, CreatedAt = state.ModifiedAt };
                _playlists.Add(existing);
            }
            if (!string.IsNullOrWhiteSpace(state.Name)) existing.Name = state.Name;
            existing.Description = state.Description ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(state.Color)) existing.Color = state.Color;
            existing.TrackIds = state.TrackIds?.ToList() ?? new List<Guid>();
            existing.ModifiedAt = state.ModifiedAt;
            return Task.CompletedTask;
        }
    }
}
