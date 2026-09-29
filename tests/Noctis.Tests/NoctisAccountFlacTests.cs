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
/// A phone without an ALAC decoder (<see cref="NoctisAccountService.PreferFlacForAlac"/>) against
/// the REAL NoctisServer with a FLAC cache over a stand-in transcoder: MP4-family desktop songs
/// stream with <c>format=flac</c> and download as FLAC when the desktop sends FLAC; every other
/// song, and a phone that can decode ALAC, keeps today's URLs and files.
/// </summary>
[Collection("MetadataServiceStatics")]
public class NoctisAccountFlacTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NoctisTests", "acc-flac-" + Guid.NewGuid().ToString("N"));
    private ServerUserStore _users = null!;
    private TestPersistenceService _deskPersistence = null!;
    private NoctisServer _server = null!;
    private string _url = "";
    private readonly byte[] _original = Enumerable.Range(0, 4000).Select(i => (byte)(i % 251)).ToArray();
    private readonly byte[] _flac = new byte[] { (byte)'f', (byte)'L', (byte)'a', (byte)'C', 9, 8, 7, 6, 5 };
    private int _transcodes;

    private PersistenceService _phonePersistence = null!;
    private LibraryService _phoneLibrary = null!;
    private readonly NoctisStateRecorder _recorder = new();

    private Track _alac = null!, _aac = null!, _flacTrack = null!;

    private string AccountDir => Path.Combine(_dir, "phone-nobackup", "account");
    private string OfflineDir => Path.Combine(_dir, "phone-files", "offline");

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        _users = new ServerUserStore(Path.Combine(_dir, "users.db"));
        _users.Create("alice", "correct horse", isAdmin: true);

        string Song(string name) { var p = Path.Combine(_dir, name); File.WriteAllBytes(p, _original); return p; }
        var album = Guid.NewGuid();
        // Codecs and bitrates as the desktop library stores them (ALAC runs far above AAC's rates).
        _alac = new Track { Id = Guid.NewGuid(), Title = "Veni", Artist = "glaive", AlbumArtist = "glaive", Album = "Y'all", AlbumId = album, FilePath = Song("06. Veni.m4a"), Codec = "MPEG-4 Audio (alac)", Bitrate = 900, SampleRate = 44100, Duration = TimeSpan.FromSeconds(120), FileSize = _original.Length };
        _aac = new Track { Id = Guid.NewGuid(), Title = "Aac", Artist = "glaive", AlbumArtist = "glaive", Album = "Y'all", AlbumId = album, FilePath = Song("07. Aac.m4a"), Codec = "MPEG-4 Audio (mp4a)", Bitrate = 256, SampleRate = 44100, Duration = TimeSpan.FromSeconds(120), FileSize = _original.Length };
        _flacTrack = new Track { Id = Guid.NewGuid(), Title = "Flac", Artist = "glaive", AlbumArtist = "glaive", Album = "Y'all", AlbumId = album, FilePath = Song("08. Flac.flac"), Codec = "Flac Audio", Bitrate = 900, SampleRate = 44100, Duration = TimeSpan.FromSeconds(120), FileSize = _original.Length };

        _deskPersistence = new TestPersistenceService();
        var settings = new AppSettings { SyncEnabled = true, SyncDeviceId = "desktop-test", SyncDeviceName = "Test PC" };
        var sync = new LibrarySyncService(() => settings, _deskPersistence);
        var cache = new FlacTranscodeCache(Path.Combine(_dir, "server", "transcode"), async (_, target, _) =>
        {
            Interlocked.Increment(ref _transcodes);
            await File.WriteAllBytesAsync(target, _flac);
            return true;
        });
        _server = new NoctisServer(new DesktopLibrary(_alac, _aac, _flacTrack), _users, "test", sync, flacTranscoder: cache.GetAsync) { PrivateClientsOnly = true };
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

    private NoctisAccountService Phone(bool preferFlac) =>
        new(_phoneLibrary, _phonePersistence, NoctisHandlers.Sockets, AccountDir, OfflineDir, "Test Phone", _recorder, marshal: a => a())
        {
            PreferFlacForAlac = preferFlac,
        };

    private async Task<NoctisAccountService> SignedInAsync(bool preferFlac)
    {
        var svc = Phone(preferFlac);
        var fingerprint = await svc.ProbeFingerprintAsync(_url, TestContext.Current.CancellationToken);
        await svc.SignInAsync(_url, "alice", "correct horse", fingerprint, TestContext.Current.CancellationToken);
        await svc.SyncNowAsync(TestContext.Current.CancellationToken);
        return svc;
    }

    private static string Remote(Track t) => "noctis-remote://tr-" + t.Id.ToString("N");

    private string Stream(Track t, bool flac) =>
        $"{_url}/rest/stream?id=tr-{t.Id:N}&c=NoctisAndroid&v=1.16.1" + (flac ? "&format=flac" : "");

    [Fact]
    public async Task NoAlacDecoder_StreamsMp4SongsWithFormatFlac_AndTheDesktopSendsFlac()
    {
        var svc = await SignedInAsync(preferFlac: true);
        var ct = TestContext.Current.CancellationToken;

        Assert.Equal(Stream(_alac, flac: true), svc.ResolvePlaybackUri(Remote(_alac)));
        Assert.Equal(Stream(_aac, flac: true), svc.ResolvePlaybackUri(Remote(_aac)));   // the desktop decides by the real codec
        Assert.Equal(Stream(_flacTrack, flac: false), svc.ResolvePlaybackUri(Remote(_flacTrack)));

        // What the player then gets from those URLs (the key in the header, as Media3 sends it).
        using var http = new HttpClient();
        async Task<(string? Type, byte[] Body)> Fetch(string url)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add(NoctisServer.KeyHeader, svc.Account!.DeviceKey);
            using var res = await http.SendAsync(req, ct);
            return (res.Content.Headers.ContentType?.MediaType, await res.Content.ReadAsByteArrayAsync(ct));
        }
        var flac = await Fetch(svc.ResolvePlaybackUri(Remote(_alac))!);
        Assert.Equal("audio/flac", flac.Type);
        Assert.Equal(_flac, flac.Body);
        var aac = await Fetch(svc.ResolvePlaybackUri(Remote(_aac))!);
        Assert.Equal("audio/mp4", aac.Type);
        Assert.Equal(_original, aac.Body);
        Assert.Equal(1, _transcodes);

        // A restart, before this run's first catalog: the catalog's codec label still picks FLAC for ALAC only.
        var restarted = Phone(preferFlac: true);
        Assert.Equal(Stream(_alac, flac: true), restarted.ResolvePlaybackUri(Remote(_alac)));
        Assert.Equal(Stream(_aac, flac: false), restarted.ResolvePlaybackUri(Remote(_aac)));
    }

    [Fact]
    public async Task NoAlacDecoder_DownloadsAlacAsFlac_AndEverythingElseAsBefore()
    {
        var svc = await SignedInAsync(preferFlac: true);
        await svc.DownloadAllAsync(TestContext.Current.CancellationToken);

        var alacFile = Path.Combine(OfflineDir, _alac.Id.ToString("N") + ".flac");
        Assert.Equal(_flac, File.ReadAllBytes(alacFile));
        Assert.False(File.Exists(Path.Combine(OfflineDir, _alac.Id.ToString("N") + ".m4a")));
        Assert.Equal(Path.GetFullPath(alacFile), svc.ResolvePlaybackUri(Remote(_alac)));
        // AAC in an .m4a: the desktop sent the original, so it keeps its own extension.
        Assert.Equal(_original, File.ReadAllBytes(Path.Combine(OfflineDir, _aac.Id.ToString("N") + ".m4a")));
        Assert.Equal(_original, File.ReadAllBytes(Path.Combine(OfflineDir, _flacTrack.Id.ToString("N") + ".flac")));
        Assert.Equal(3, svc.DownloadedCount);
        Assert.Empty(Directory.GetFiles(OfflineDir, "*.part"));

        // A restart finds the .flac download again.
        Assert.True(Phone(preferFlac: true).IsDownloaded(_phoneLibrary.GetTrackById(_alac.Id)!));
    }

    [Fact]
    public async Task PhoneWithAnAlacDecoder_KeepsTheOriginals()
    {
        var svc = await SignedInAsync(preferFlac: false);
        Assert.Equal(Stream(_alac, flac: false), svc.ResolvePlaybackUri(Remote(_alac)));

        await svc.DownloadAsync(new[] { _phoneLibrary.GetTrackById(_alac.Id)! }, TestContext.Current.CancellationToken);
        Assert.Equal(_original, File.ReadAllBytes(Path.Combine(OfflineDir, _alac.Id.ToString("N") + ".m4a")));
        Assert.Equal(0, _transcodes);
    }

    private sealed class DesktopLibrary(params Track[] tracks) : IServerLibrary
    {
        private readonly List<Track> _tracks = tracks.ToList();
        public Task<LibrarySnapshot> SnapshotAsync() => Task.FromResult(new LibrarySnapshot(_tracks.ToList(), new List<Album>(), new List<Artist>(), new List<Playlist>()));
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
