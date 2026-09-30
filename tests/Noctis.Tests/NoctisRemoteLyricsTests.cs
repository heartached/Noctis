using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Noctis.Mobile.Services.Account;
using Noctis.Mobile.ViewModels;
using Noctis.Models;
using Noctis.Services;
using Noctis.Services.Server;
using Noctis.Services.Sync;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The phone end to end against the real NoctisServer: a desktop song whose desktop has a TTML
/// sidecar with translation and romanization loads through LyricsLoader on the phone, stored
/// lyrics reach the loader through the stand-in track, and the saved copy keeps working after
/// the desktop stops (including after a sync made it due for a refresh, and for downloads that
/// were never played).
/// </summary>
[Collection("MetadataServiceStatics")]
public class NoctisRemoteLyricsTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NoctisTests", "remote-lyrics-" + Guid.NewGuid().ToString("N"));
    private ServerUserStore _users = null!;
    private TestPersistenceService _deskPersistence = null!;
    private LibrarySyncService _sync = null!;
    private readonly AppSettings _deskSettings = new() { SyncEnabled = true, SyncDeviceId = "desktop-test", SyncDeviceName = "Test PC" };
    private NoctisServer _server = null!;
    private string _url = "";
    private PersistenceService _phonePersistence = null!;
    private LibraryService _phoneLibrary = null!;
    private readonly NoctisStateRecorder _recorder = new();
    private Track _ttmlSong = null!, _storedSong = null!;

    private string AccountDir => Path.Combine(_dir, "phone-nobackup", "account");
    private string OfflineDir => Path.Combine(_dir, "phone-nobackup", "offline");
    private string SavedLyricsFile(Track t) => Path.Combine(AccountDir, "lyrics", t.Id.ToString("N") + ".json");

    public async ValueTask InitializeAsync()
    {
        var music = Path.Combine(_dir, "desk-music");
        Directory.CreateDirectory(music);
        _users = new ServerUserStore(Path.Combine(_dir, "users.db"));
        _users.Create("alice", "correct horse", isAdmin: true);

        var album = Guid.NewGuid();
        var ttmlPath = Path.Combine(music, "01 Tatoeba.mp3");
        File.WriteAllBytes(ttmlPath, Enumerable.Range(0, 3000).Select(i => (byte)(i % 251)).ToArray());
        File.WriteAllText(Path.Combine(music, "01 Tatoeba.ttml"), MobileLyricsViewModelTests.IssueTtml, new UTF8Encoding(false));
        _ttmlSong = new Track { Id = Guid.NewGuid(), Title = "Tatoeba", Artist = "A", AlbumArtist = "A", Album = "B", AlbumId = album, FilePath = ttmlPath, Duration = TimeSpan.FromSeconds(60), FileSize = 3000 };

        var storedPath = Path.Combine(music, "02 Stored.mp3");
        File.WriteAllBytes(storedPath, new byte[] { 1, 2, 3 });
        _storedSong = new Track { Id = Guid.NewGuid(), Title = "Stored", Artist = "A", AlbumArtist = "A", Album = "B", AlbumId = album, FilePath = storedPath, Duration = TimeSpan.FromSeconds(60), FileSize = 3 };
        _storedSong.SyncedLyrics = "[00:01.00]Stored first\n[00:03.00]Stored second";

        _deskPersistence = new TestPersistenceService();
        _sync = new LibrarySyncService(() => _deskSettings, _deskPersistence);
        _server = new NoctisServer(new LyricsDesk(new List<Track> { _ttmlSong, _storedSong }), _users, "test", _sync) { PrivateClientsOnly = true };
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

    private NoctisAccountService Restart() =>
        new(_phoneLibrary, _phonePersistence, NoctisHandlers.Sockets, AccountDir, OfflineDir, "Test Phone", _recorder, marshal: a => a());

    private async Task<NoctisAccountService> SignedInAsync()
    {
        var svc = Restart();
        var fingerprint = await svc.ProbeFingerprintAsync(_url, TestContext.Current.CancellationToken);
        await svc.SignInAsync(_url, "alice", "correct horse", fingerprint, TestContext.Current.CancellationToken);
        return svc;
    }

    /// <summary>The phone's library copy of a desktop song, as the catalog import makes it.</summary>
    private static Track PhoneCopy(Track desk) => new()
    {
        Id = desk.Id, Title = desk.Title, Artist = desk.Artist, Album = desk.Album,
        FilePath = NoctisRemoteIds.ToPath(desk.Id), SourceType = SourceType.NoctisServer, Duration = desk.Duration,
    };

    /// <summary>What the lyrics page does: stand-in, then the loader, on a pool thread.</summary>
    private static Task<LoadedLyrics> LoadOnPhone(NoctisAccountService svc, Track phoneTrack)
    {
        var files = new RemoteLyricsFileAccess(new FakeTrackFiles(), svc);
        return Task.Run(() => LyricsLoader.Load(files.ForLoad(phoneTrack), files, joinSplitWords: false));
    }

    private static void AssertIssueTtmlWithLayers(LoadedLyrics loaded)
    {
        Assert.Equal(LyricsSource.SidecarTtml, loaded.Source);
        Assert.True(loaded.IsSynced);
        var line = loaded.Lines.First(l => l.HasTranslation);
        Assert.Equal("For example, if I were not myself", line.Translation);
        Assert.True(line.HasTransliteration);
        Assert.Contains(loaded.Lines, l => l.Text.Contains('次'));
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

    [Fact]
    public async Task DesktopTtmlSidecar_WithTranslation_LoadsOnThePhone_AndAgainFromTheSavedCopy_WithTheDesktopOff()
    {
        var svc = await SignedInAsync();
        var phoneTrack = PhoneCopy(_ttmlSong);

        AssertIssueTtmlWithLayers(await LoadOnPhone(svc, phoneTrack));
        Assert.True(File.Exists(SavedLyricsFile(_ttmlSong)));
        Assert.Equal(string.Empty, phoneTrack.SyncedLyrics); // the library's track is never written

        // A sync makes the saved copy due for a refresh; then the desktop goes away.
        await svc.SyncNowAsync(TestContext.Current.CancellationToken);
        await _server.StopAsync();

        // Same process: the refresh fails, the saved copy is used.
        AssertIssueTtmlWithLayers(await LoadOnPhone(svc, phoneTrack));
        // A fresh start of the app, desktop still off: from disk.
        AssertIssueTtmlWithLayers(await LoadOnPhone(Restart(), phoneTrack));
    }

    [Fact]
    public async Task StoredSyncedLyrics_ReachTheLoader_ThroughTheStandIn()
    {
        var svc = await SignedInAsync();
        var phoneTrack = PhoneCopy(_storedSong);

        var loaded = await LoadOnPhone(svc, phoneTrack);

        Assert.Equal(LyricsSource.EmbeddedSynced, loaded.Source);
        Assert.Contains(loaded.Lines, l => l.Text == "Stored second");
        Assert.Equal(string.Empty, phoneTrack.SyncedLyrics);
    }

    [Fact]
    public async Task TheSavedCopy_IsAskedForAgain_OnlyAfterASync()
    {
        var svc = await SignedInAsync();
        var phoneTrack = PhoneCopy(_storedSong);
        Assert.Contains((await LoadOnPhone(svc, phoneTrack)).Lines, l => l.Text == "Stored second");

        _storedSong.SyncedLyrics = "[00:01.00]Edited on the desktop";
        // Still current (nothing synced since): the desktop is not asked again.
        Assert.Contains((await LoadOnPhone(svc, phoneTrack)).Lines, l => l.Text == "Stored second");
        Assert.Contains((await LoadOnPhone(Restart(), phoneTrack)).Lines, l => l.Text == "Stored second");

        await svc.SyncNowAsync(TestContext.Current.CancellationToken);
        Assert.Contains((await LoadOnPhone(svc, phoneTrack)).Lines, l => l.Text == "Edited on the desktop");
    }

    [Fact]
    public async Task ADownloadedSong_GetsItsLyricsSaved_WithoutBeingPlayed_AndShowsThemOffline()
    {
        var svc = await SignedInAsync();
        await svc.SyncNowAsync(TestContext.Current.CancellationToken);
        var phoneTrack = _phoneLibrary.GetTrackById(_ttmlSong.Id)!;
        Assert.Equal(SourceType.NoctisServer, phoneTrack.SourceType);

        // The download batch saves the lyrics of what it downloaded.
        await svc.DownloadAsync(new[] { phoneTrack }, TestContext.Current.CancellationToken);
        await Until(() => File.Exists(SavedLyricsFile(_ttmlSong)));

        // Gone again (say, saved by an older version): the next sync puts them back.
        File.Delete(SavedLyricsFile(_ttmlSong));
        await svc.SyncNowAsync(TestContext.Current.CancellationToken);
        Assert.True(File.Exists(SavedLyricsFile(_ttmlSong)));
        Assert.False(File.Exists(SavedLyricsFile(_storedSong))); // not downloaded: fetched when played

        await _server.StopAsync();
        var offline = Restart();
        Assert.NotNull(offline.DownloadedPath(_ttmlSong.Id));
        AssertIssueTtmlWithLayers(await LoadOnPhone(offline, phoneTrack));
    }

    [Fact]
    public async Task SignOut_RemovesTheSavedLyrics()
    {
        var svc = await SignedInAsync();
        await LoadOnPhone(svc, PhoneCopy(_ttmlSong));
        Assert.True(File.Exists(SavedLyricsFile(_ttmlSong)));

        await svc.SignOutAsync(removeDownloads: false, TestContext.Current.CancellationToken);

        Assert.False(Directory.Exists(Path.Combine(AccountDir, "lyrics")));
    }
}

/// <summary>The phone's lyrics page with the remote hook: a desktop song's TTML shows its layers.</summary>
public class MobileRemoteLyricsPageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private sealed class FakeRemoteLyrics : IRemoteLyricsSource
    {
        public RemoteLyrics? Lyrics { get; set; }
        public int Lookups;
        public RemoteLyrics? GetLyrics(Guid trackId, TimeSpan timeout) { Interlocked.Increment(ref Lookups); return Lyrics; }
        public string? DownloadedPath(Guid trackId) => null;
    }

    [Fact]
    public async Task DesktopSong_ShowsTheDesktopsTtml_WithTranslations_AndLocalSongsStillUseTheirOwnFiles()
    {
        var player = new FakeAudioPlayer();
        var persistence = new PersistenceService(_root);
        var np = new NowPlayingViewModel(player, new FakeLibraryService(), persistence, marshal: a => a());
        var local = new FakeTrackFiles();
        local.Sidecars[".lrc"] = "[00:01.00]Local line";
        var remote = new FakeRemoteLyrics { Lyrics = new RemoteLyrics(MobileLyricsViewModelTests.IssueTtml, null, null, null, null) };
        var files = new RemoteLyricsFileAccess(local, remote);
        var vm = new LyricsPageViewModel(player, np, files, persistence, work => Task.Run(work)) { PrepareTrack = files.ForLoad };

        var id = Guid.NewGuid();
        var desktopSong = new Track { Id = id, Title = "Desk", FilePath = NoctisRemoteIds.ToPath(id), SourceType = SourceType.NoctisServer, Duration = TimeSpan.FromSeconds(60) };
        np.PlayTracks(new[] { desktopSong }, 0);
        await WaitFor(() => vm.HasLyrics);
        Assert.True(vm.IsSynced);
        Assert.True(vm.HasTranslations);
        Assert.True(vm.HasRomanizations);
        Assert.Equal(0, local.SidecarReads);

        var lookups = remote.Lookups;
        var phoneSong = new Track { Id = Guid.NewGuid(), Title = "Local", FilePath = "content://x/local", Duration = TimeSpan.FromSeconds(60) };
        np.PlayTracks(new[] { phoneSong }, 0);
        await WaitFor(() => vm.HasLyrics && vm.Lines.Any(l => l.Text == "Local line"));
        Assert.Equal(lookups, remote.Lookups);
        vm.Dispose();
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(15);
        Assert.True(condition());
    }
}
