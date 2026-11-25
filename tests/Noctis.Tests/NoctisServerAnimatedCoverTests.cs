using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Noctis.Models;
using Noctis.Services;
using Noctis.Services.Server;
using Noctis.Services.Sync;
using Xunit;

namespace Noctis.Tests;

/// <summary>A desktop library whose animated covers are found as LibraryServerAdapter finds them
/// (<see cref="AnimatedCoverLocator"/> over the profile's animated_covers folder).</summary>
internal sealed class MotionDesk(List<Track> tracks, IPersistenceService persistence) : IServerLibrary
{
    public Task<LibrarySnapshot> SnapshotAsync() =>
        Task.FromResult(new LibrarySnapshot(tracks.ToList(), Array.Empty<Album>(), Array.Empty<Artist>(), Array.Empty<Playlist>()));
    public string? ArtworkPath(Guid albumId) => null;
    public string? AnimatedCoverPath(Guid albumId, IReadOnlyList<Track> albumTracks) => AnimatedCoverLocator.ResolveAlbum(persistence, albumId, albumTracks);
    public Task SetStarredAsync(IReadOnlyList<Guid> trackIds, IReadOnlyList<Guid> albumIds, IReadOnlyList<Guid> artistIds, bool starred) => Task.CompletedTask;
    public Task ScrobbleAsync(Guid trackId) => Task.CompletedTask;
    public Task<Playlist> CreatePlaylistAsync(string name, IReadOnlyList<Guid> trackIds) => Task.FromResult(new Playlist { Name = name });
    public Task<bool> UpdatePlaylistAsync(Guid id, string? name, IReadOnlyList<Guid> add, IReadOnlyList<int> removeIndexes) => Task.FromResult(false);
    public Task<bool> DeletePlaylistAsync(Guid id) => Task.FromResult(false);
    public Task ApplyTrackStateAsync(Guid trackId, TrackSyncState state) => Task.CompletedTask;
    public Task ApplyPlaylistStateAsync(Guid playlistId, PlaylistSyncState state) => Task.CompletedTask;
}

/// <summary>
/// getNoctisAnimatedCover over real Kestrel (Discord beta 11-23, "no animated cover support" on
/// the phone): the album's cover.mp4 beside its music, else its saved, fetched or song-scoped loop
/// in the profile; Range, ETag/304, 70 for none, and the usual auth.
/// </summary>
public class NoctisServerAnimatedCoverTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NoctisTests", "srv-motion-" + Guid.NewGuid().ToString("N"));
    private readonly TestPersistenceService _persistence = new();
    private ServerUserStore _users = null!;
    private NoctisServer _server = null!;
    private HttpClient _http = null!;
    private string _apiKey = "";
    private readonly Guid _sidecarAlbum = Guid.NewGuid(), _savedAlbum = Guid.NewGuid(), _fetchedAlbum = Guid.NewGuid(),
        _songAlbum = Guid.NewGuid(), _bareAlbum = Guid.NewGuid();
    private Track _sidecarSong = null!, _songScoped = null!;
    private readonly byte[] _loop = Enumerable.Range(0, 4000).Select(i => (byte)(i % 251)).ToArray();

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        _users = new ServerUserStore(Path.Combine(_dir, "users.db"));
        _users.Create("alice", "correct horse", isAdmin: true);
        _apiKey = _users.RegenerateApiKey("alice");

        Track Song(Guid album, string folderName)
        {
            var folder = Path.Combine(_dir, folderName);
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "song.flac");
            File.WriteAllBytes(path, new byte[] { 1 });
            return new Track { Id = Guid.NewGuid(), Title = folderName, Artist = "A", Album = folderName, AlbumId = album, FilePath = path };
        }

        _sidecarSong = Song(_sidecarAlbum, "sidecar");
        File.WriteAllBytes(Path.Combine(_dir, "sidecar", "cover.mp4"), _loop);
        // The song's own same-name clip is its music video, never the cover.
        var saved = Song(_savedAlbum, "saved");
        File.WriteAllBytes(Path.Combine(_dir, "saved", "song.mp4"), new byte[] { 9 });
        _persistence.EnsureAnimatedCoverDir();
        File.WriteAllBytes(_persistence.GetAnimatedCoverPath(_savedAlbum, null, ".webm"), new byte[] { 7, 7, 7 });
        var fetched = Song(_fetchedAlbum, "fetched");
        Directory.CreateDirectory(Path.GetDirectoryName(Persistence.GetCachedAnimatedCoverPath(_fetchedAlbum))!);
        File.WriteAllBytes(Persistence.GetCachedAnimatedCoverPath(_fetchedAlbum), new byte[] { 5, 5 });
        _songScoped = Song(_songAlbum, "songscoped");
        File.WriteAllBytes(_persistence.GetAnimatedCoverPath(_songAlbum, _songScoped.Id, ".mp4"), new byte[] { 3 });
        var bare = Song(_bareAlbum, "bare");

        _server = new NoctisServer(new MotionDesk(new List<Track> { _sidecarSong, saved, fetched, _songScoped, bare }, _persistence), _users, "test");
        await _server.StartAsync(0, certificate: null, bindAddress: IPAddress.Loopback);
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_server.Port}/") };
    }

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        await _server.StopAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        _persistence.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private IPersistenceService Persistence => _persistence;

    private string Url(string id, bool auth = true) =>
        $"rest/getNoctisAnimatedCover.view?f=json{(auth ? "&apiKey=" + _apiKey : "")}&id={id}";

    private static async Task<int> ErrorCode(HttpResponseMessage response)
    {
        var r = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("subsonic-response");
        Assert.Equal("failed", r.GetProperty("status").GetString());
        return r.GetProperty("error").GetProperty("code").GetInt32();
    }

    [Fact]
    public async Task AlbumWithCoverMp4BesideItsMusic_ServesTheLoop_AsVideo_WithRanges()
    {
        var whole = await _http.GetAsync(Url($"al-{_sidecarAlbum:N}"));
        Assert.Equal(HttpStatusCode.OK, whole.StatusCode);
        Assert.Equal("video/mp4", whole.Content.Headers.ContentType!.MediaType);
        Assert.Equal(_loop, await whole.Content.ReadAsByteArrayAsync());
        Assert.NotNull(whole.Headers.ETag);

        using var req = new HttpRequestMessage(HttpMethod.Get, Url($"al-{_sidecarAlbum:N}"));
        req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(1000, 1999);
        var part = await _http.SendAsync(req);
        Assert.Equal(HttpStatusCode.PartialContent, part.StatusCode);
        Assert.Equal(_loop.Skip(1000).Take(1000).ToArray(), await part.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task SameETag_Answers304_AChangedLoop_SendsTheNewOne()
    {
        var first = await _http.GetAsync(Url($"al-{_sidecarAlbum:N}"));
        var tag = first.Headers.ETag!;

        using (var again = new HttpRequestMessage(HttpMethod.Get, Url($"al-{_sidecarAlbum:N}")))
        {
            again.Headers.IfNoneMatch.Add(tag);
            Assert.Equal(HttpStatusCode.NotModified, (await _http.SendAsync(again)).StatusCode);
        }

        File.WriteAllBytes(Path.Combine(_dir, "sidecar", "cover.mp4"), new byte[] { 4, 2 });
        using var changed = new HttpRequestMessage(HttpMethod.Get, Url($"al-{_sidecarAlbum:N}"));
        changed.Headers.IfNoneMatch.Add(tag);
        var r = await _http.SendAsync(changed);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(new byte[] { 4, 2 }, await r.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task SavedFetchedAndSongScopedLoops_AreServed_TheMusicVideoIsNot()
    {
        var saved = await _http.GetAsync(Url($"al-{_savedAlbum:N}"));
        Assert.Equal("video/webm", saved.Content.Headers.ContentType!.MediaType);
        Assert.Equal(new byte[] { 7, 7, 7 }, await saved.Content.ReadAsByteArrayAsync());

        Assert.Equal(new byte[] { 5, 5 }, await (await _http.GetAsync(Url($"al-{_fetchedAlbum:N}"))).Content.ReadAsByteArrayAsync());
        Assert.Equal(new byte[] { 3 }, await (await _http.GetAsync(Url($"tr-{_songScoped.Id:N}"))).Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task ASongId_ServesItsAlbumsLoop()
    {
        var r = await _http.GetAsync(Url($"tr-{_sidecarSong.Id:N}"));
        Assert.Equal(_loop, await r.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task NoLoop_UnknownAlbum_AndBadIds_Are70()
    {
        Assert.Equal(70, await ErrorCode(await _http.GetAsync(Url($"al-{_bareAlbum:N}"))));
        Assert.Equal(70, await ErrorCode(await _http.GetAsync(Url($"al-{Guid.NewGuid():N}"))));
        Assert.Equal(70, await ErrorCode(await _http.GetAsync(Url("../../etc/passwd"))));
        Assert.Equal(10, await ErrorCode(await _http.GetAsync($"rest/getNoctisAnimatedCover.view?f=json&apiKey={_apiKey}")));
    }

    [Fact]
    public async Task WithoutCredentials_NothingIsServed()
    {
        var r = await _http.GetAsync(Url($"al-{_sidecarAlbum:N}", auth: false));
        Assert.NotEqual("video/mp4", r.Content.Headers.ContentType?.MediaType);
        Assert.Equal(10, await ErrorCode(r));
    }
}
