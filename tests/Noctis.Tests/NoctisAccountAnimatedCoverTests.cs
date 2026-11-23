using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Noctis.Mobile.Services;
using Noctis.Mobile.Services.Account;
using Noctis.Mobile.ViewModels;
using Noctis.Models;
using Noctis.Services;
using Noctis.Services.Server;
using Noctis.Services.Sync;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The phone fetching the computer's animated covers from the REAL NoctisServer
/// (getNoctisAnimatedCover): the loop saved whole, an unchanged one answered 304 with nothing sent,
/// none for an album without one or when signed out, and AnimatedCoverSource over the account.
/// </summary>
[Collection("MetadataServiceStatics")]
public class NoctisAccountAnimatedCoverTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NoctisTests", "acc-motion-" + Guid.NewGuid().ToString("N"));
    private ServerUserStore _users = null!;
    private TestPersistenceService _deskPersistence = null!;
    private NoctisServer _server = null!;
    private string _url = "";
    private readonly byte[] _loop = Enumerable.Range(0, 300_000).Select(i => (byte)(i % 251)).ToArray();
    private readonly Guid _motionAlbum = Guid.NewGuid(), _stillAlbum = Guid.NewGuid();

    private PersistenceService _phonePersistence = null!;
    private LibraryService _phoneLibrary = null!;
    private readonly NoctisStateRecorder _recorder = new();

    private string AccountDir => Path.Combine(_dir, "phone-nobackup", "account");
    private string OfflineDir => Path.Combine(_dir, "phone-files", "offline");
    private string Loops => Path.Combine(_dir, "phone-cache", "animated_covers");

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        _users = new ServerUserStore(Path.Combine(_dir, "users.db"));
        _users.Create("alice", "correct horse", isAdmin: true);

        Track Song(Guid album, string folder)
        {
            Directory.CreateDirectory(Path.Combine(_dir, folder));
            var path = Path.Combine(_dir, folder, "song.flac");
            File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
            return new Track { Id = Guid.NewGuid(), Title = folder, Artist = "A", AlbumArtist = "A", Album = folder, AlbumId = album, FilePath = path, Duration = TimeSpan.FromSeconds(60), FileSize = 3 };
        }
        var motion = Song(_motionAlbum, "motion");
        File.WriteAllBytes(Path.Combine(_dir, "motion", "cover.mp4"), _loop);
        var still = Song(_stillAlbum, "still");

        _deskPersistence = new TestPersistenceService();
        var settings = new AppSettings { SyncEnabled = true, SyncDeviceId = "desktop-test", SyncDeviceName = "Test PC" };
        var sync = new LibrarySyncService(() => settings, _deskPersistence);
        _server = new NoctisServer(new MotionDesk(new List<Track> { motion, still }, _deskPersistence), _users, "test", sync) { PrivateClientsOnly = true };
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

    private NoctisAccountService Phone() =>
        new(_phoneLibrary, _phonePersistence, NoctisHandlers.Sockets, AccountDir, OfflineDir, "Test Phone", _recorder, marshal: a => a());

    private async Task<NoctisAccountService> SignedInAsync()
    {
        var svc = Phone();
        var ct = TestContext.Current.CancellationToken;
        await svc.SignInAsync(_url, "alice", "correct horse", await svc.ProbeFingerprintAsync(_url, ct), ct);
        return svc;
    }

    [Fact]
    public async Task TheComputersLoop_IsSavedWhole_ThenAnsweredNotModified_AndNoneWithoutOne()
    {
        var ct = TestContext.Current.CancellationToken;
        var svc = await SignedInAsync();
        var destination = Path.Combine(Loops, "a.mp4");

        var saved = await svc.FetchAnimatedCoverAsync(_motionAlbum, destination, null, ct);
        Assert.Equal(AnimatedCoverFetch.Saved, saved.Result);
        Assert.Equal(_loop, File.ReadAllBytes(destination));
        Assert.False(File.Exists(destination + ".part"));
        Assert.NotNull(saved.ETag);

        File.Delete(destination);
        var again = await svc.FetchAnimatedCoverAsync(_motionAlbum, destination, saved.ETag, ct);
        Assert.Equal(AnimatedCoverFetch.NotModified, again.Result);
        Assert.False(File.Exists(destination));                           // nothing sent

        Assert.Equal(AnimatedCoverFetch.None, (await svc.FetchAnimatedCoverAsync(_stillAlbum, destination, null, ct)).Result);
        Assert.False(File.Exists(destination));

        await svc.SignOutAsync(removeDownloads: false, ct);
        Assert.Equal(AnimatedCoverFetch.None, (await svc.FetchAnimatedCoverAsync(_motionAlbum, destination, null, ct)).Result);
    }

    [Fact]
    public async Task TheSource_OverTheAccount_PlaysTheComputersLoop_ForItsSongsOnly()
    {
        var ct = TestContext.Current.CancellationToken;
        var svc = await SignedInAsync();
        var source = AnimatedCoverSource.ForAccount(new AnimatedCoverCache(Loops), () => true, svc, findSidecar: null);
        var desk = new Track
        {
            Id = Guid.NewGuid(), AlbumId = _motionAlbum, SourceType = SourceType.NoctisServer,
        };
        desk.FilePath = NoctisRemoteIds.ToPath(desk.Id);
        var path = await source.ResolveAsync(desk, AnimatedCoversSetting.WifiOnly, ct);
        Assert.Equal(_loop, File.ReadAllBytes(path!));

        var phonesOwn = new Track { Id = Guid.NewGuid(), AlbumId = _motionAlbum, FilePath = "content://x/y.flac" };
        Assert.Null(await source.ResolveAsync(phonesOwn, AnimatedCoversSetting.Always, ct));
    }
}
