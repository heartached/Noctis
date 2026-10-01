using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Noctis.Models;
using Noctis.Services;
using Noctis.Services.Server;
using Noctis.Services.Sync;
using Xunit;

namespace Noctis.Tests;

/// <summary>Minimal desktop library for the lyrics tests: the tracks, no albums/artists/playlists.</summary>
internal sealed class LyricsDesk(List<Track> tracks) : IServerLibrary
{
    public Task<LibrarySnapshot> SnapshotAsync() =>
        Task.FromResult(new LibrarySnapshot(tracks.ToList(), Array.Empty<Album>(), Array.Empty<Artist>(), Array.Empty<Playlist>()));
    public string? ArtworkPath(Guid albumId) => null;
    public Task SetStarredAsync(IReadOnlyList<Guid> trackIds, IReadOnlyList<Guid> albumIds, IReadOnlyList<Guid> artistIds, bool starred) => Task.CompletedTask;
    public Task ScrobbleAsync(Guid trackId) => Task.CompletedTask;
    public Task<Playlist> CreatePlaylistAsync(string name, IReadOnlyList<Guid> trackIds) => Task.FromResult(new Playlist { Name = name });
    public Task<bool> UpdatePlaylistAsync(Guid id, string? name, IReadOnlyList<Guid> add, IReadOnlyList<int> removeIndexes) => Task.FromResult(false);
    public Task<bool> DeletePlaylistAsync(Guid id) => Task.FromResult(false);
    public Task ApplyTrackStateAsync(Guid trackId, TrackSyncState state) => Task.CompletedTask;
    public Task ApplyPlaylistStateAsync(Guid playlistId, PlaylistSyncState state) => Task.CompletedTask;
}

/// <summary>
/// getNoctisLyrics over real Kestrel: the song's own sidecars (every casing, same stem only),
/// the stored synced/plain fields, the desktop lyrics cache as the last resort, the 2 MB cap,
/// error 70 for an unknown song and the usual auth.
/// </summary>
public class NoctisServerLyricsTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NoctisTests", "srv-lyrics-" + Guid.NewGuid().ToString("N"));
    private ServerUserStore _users = null!;
    private NoctisServer _server = null!;
    private HttpClient _http = null!;
    private string _apiKey = "";
    private Track _sidecars = null!, _stored = null!, _cachedSynced = null!, _cachedPlain = null!, _oversize = null!;

    private string Music => Path.Combine(_dir, "music");
    private string Cache => Path.Combine(_dir, "lyrics_cache");

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(Music);
        Directory.CreateDirectory(Cache);
        _users = new ServerUserStore(Path.Combine(_dir, "users.db"));
        _users.Create("alice", "correct horse", isAdmin: true);
        _apiKey = _users.RegenerateApiKey("alice");

        Track Song(string file)
        {
            var path = Path.Combine(Music, file);
            File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
            return new Track { Id = Guid.NewGuid(), Title = Path.GetFileNameWithoutExtension(file), Artist = "A", Album = "B", FilePath = path };
        }

        _sidecars = Song("Song One.mp3");
        File.WriteAllText(Path.Combine(Music, "Song One.ttml"), MobileLyricsViewModelTests.IssueTtml);
        File.WriteAllText(Path.Combine(Music, "Song One.ELRC"), "[00:01.00]<00:01.00>Word <00:01.50>by word");
        File.WriteAllText(Path.Combine(Music, "Song One.lrc"), "[00:01.00]Line one");
        // Neighbours that are not this song's sidecars.
        File.WriteAllText(Path.Combine(Music, "Song One (live).ttml"), "<tt>live</tt>");
        File.WriteAllText(Path.Combine(Music, "Song.ttml"), "<tt>other</tt>");

        _stored = Song("Stored.flac");
        _stored.SyncedLyrics = "[00:01.00]Stored line";
        _stored.Lyrics = "Stored plain words";
        File.WriteAllText(Path.Combine(Cache, _stored.Id.ToString("D") + ".lrc"), "[00:09.00]Cache must not win");

        _cachedSynced = Song("Cached synced.mp3");
        File.WriteAllText(Path.Combine(Cache, _cachedSynced.Id.ToString("D") + ".lrc"), "[00:02.00]Cached line");
        _cachedPlain = Song("Cached plain.mp3");
        File.WriteAllText(Path.Combine(Cache, _cachedPlain.Id.ToString("D") + ".lrc"), "Cached plain words");

        _oversize = Song("Huge.mp3");
        File.WriteAllBytes(Path.Combine(Music, "Huge.ttml"), Enumerable.Repeat((byte)'a', NoctisServer.MaxLyricsTextBytes + 1).ToArray());
        File.WriteAllText(Path.Combine(Music, "Huge.lrc"), "[00:03.00]Small one");

        var lib = new LyricsDesk(new List<Track> { _sidecars, _stored, _cachedSynced, _cachedPlain, _oversize });
        _server = new NoctisServer(lib, _users, "test") { LyricsCacheDirectory = Cache };
        await _server.StartAsync(0, certificate: null);
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_server.Port}/") };
    }

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        await _server.StopAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private async Task<JsonElement> Lyrics(string query, bool auth = true)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"rest/getNoctisLyrics.view?f=json&{query}");
        if (auth) request.Headers.Add(NoctisServer.KeyHeader, _apiKey);
        using var response = await _http.SendAsync(request, TestContext.Current.CancellationToken);
        var json = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return JsonDocument.Parse(json).RootElement.GetProperty("subsonic-response").Clone();
    }

    private static string Id(Track t) => "tr-" + t.Id.ToString("N");

    private static JsonElement Ok(JsonElement r)
    {
        Assert.Equal("ok", r.GetProperty("status").GetString());
        return r.GetProperty("noctisLyrics");
    }

    [Fact]
    public async Task Sidecars_OfTheSongsOwnStem_AreServed_InEveryCasing()
    {
        var l = Ok(await Lyrics("id=" + Id(_sidecars)));

        Assert.Equal(MobileLyricsViewModelTests.IssueTtml, l.GetProperty("ttml").GetString());
        Assert.Equal("[00:01.00]<00:01.00>Word <00:01.50>by word", l.GetProperty("elrc").GetString());
        Assert.Equal("[00:01.00]Line one", l.GetProperty("lrc").GetString());
        Assert.False(l.TryGetProperty("synced", out _));
        Assert.False(l.TryGetProperty("plain", out _));
        Assert.DoesNotContain("live", l.GetRawText());
        Assert.DoesNotContain("other", l.GetRawText());
    }

    [Fact]
    public async Task StoredFields_AreServed_AndOutrankTheCache()
    {
        var l = Ok(await Lyrics("id=" + Id(_stored)));

        Assert.Equal("[00:01.00]Stored line", l.GetProperty("synced").GetString());
        Assert.Equal("Stored plain words", l.GetProperty("plain").GetString());
        Assert.False(l.TryGetProperty("ttml", out _));
        Assert.DoesNotContain("Cache must not win", l.GetRawText());
    }

    [Fact]
    public async Task TheLyricsCache_FillsIn_WhenNothingIsStored()
    {
        var synced = Ok(await Lyrics("id=" + Id(_cachedSynced)));
        Assert.Equal("[00:02.00]Cached line", synced.GetProperty("synced").GetString());
        Assert.False(synced.TryGetProperty("plain", out _));

        var plain = Ok(await Lyrics("id=" + Id(_cachedPlain)));
        Assert.Equal("Cached plain words", plain.GetProperty("plain").GetString());
        Assert.False(plain.TryGetProperty("synced", out _));
    }

    [Fact]
    public async Task ATextOverTwoMegabytes_IsLeftOut()
    {
        var l = Ok(await Lyrics("id=" + Id(_oversize)));

        Assert.False(l.TryGetProperty("ttml", out _));
        Assert.Equal("[00:03.00]Small one", l.GetProperty("lrc").GetString());
    }

    [Fact]
    public async Task UnknownSong_Is70_MissingId_Is10()
    {
        var unknown = await Lyrics("id=tr-" + Guid.NewGuid().ToString("N"));
        Assert.Equal(70, unknown.GetProperty("error").GetProperty("code").GetInt32());

        // Not a track id at all (a path, another kind): nothing is looked up.
        var path = await Lyrics("id=" + Uri.EscapeDataString(Path.Combine(Music, "Song One.ttml")));
        Assert.Equal(70, path.GetProperty("error").GetProperty("code").GetInt32());
        var album = await Lyrics("id=al-" + _sidecars.Id.ToString("N"));
        Assert.Equal(70, album.GetProperty("error").GetProperty("code").GetInt32());

        var missing = await Lyrics("");
        Assert.Equal(10, missing.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task WithoutAValidKey_NothingIsServed()
    {
        var none = await Lyrics("id=" + Id(_sidecars), auth: false);
        Assert.Equal("failed", none.GetProperty("status").GetString());
        Assert.Equal(10, none.GetProperty("error").GetProperty("code").GetInt32());
        Assert.False(none.TryGetProperty("noctisLyrics", out _));

        var wrong = await Lyrics("id=" + Id(_sidecars) + "&apiKey=nk_garbage", auth: false);
        Assert.Equal(40, wrong.GetProperty("error").GetProperty("code").GetInt32());
        Assert.False(wrong.TryGetProperty("noctisLyrics", out _));
    }
}
