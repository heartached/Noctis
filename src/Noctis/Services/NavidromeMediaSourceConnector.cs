using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Noctis.Models;

namespace Noctis.Services;

/// <summary>
/// Navidrome (Subsonic-compatible) connector baseline.
/// </summary>
public sealed class NavidromeMediaSourceConnector : IMediaSourceConnector
{
    private readonly HttpClient _http;

    public NavidromeMediaSourceConnector(HttpClient http) => _http = http;

    public SourceType SourceType => SourceType.Navidrome;
    public string Name => "Navidrome";

    public async Task<bool> ValidateConnectionAsync(SourceConnection connection, CancellationToken ct = default)
    {
        var pingUrl = BuildSubsonicUrl(connection, "ping.view");
        if (pingUrl == null) return false;

        try
        {
            var json = await _http.GetStringAsync(pingUrl, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement.GetProperty("subsonic-response");
            var status = root.TryGetProperty("status", out var s) ? s.GetString() : null;
            return string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<Track>> ScanAsync(SourceConnection connection, CancellationToken ct = default)
    {
        // Enumerate albums alphabetically and pull their tracks.
        var tracks = new List<Track>(1024);
        const int pageSize = 200;
        var offset = 0;

        while (true)
        {
            var listUrl = BuildSubsonicUrl(connection, "getAlbumList2.view",
                ("type", "alphabeticalByName"),
                ("size", pageSize.ToString()),
                ("offset", offset.ToString()));

            if (listUrl == null) break;

            var albums = await FetchAlbumsAsync(listUrl, ct);
            if (albums.Count == 0) break;

            foreach (var album in albums)
            {
                ct.ThrowIfCancellationRequested();
                var albumTracks = await FetchSongsForAlbumAsync(connection, album.Id, album.Name, album.Artist, ct);
                tracks.AddRange(albumTracks);
            }

            if (albums.Count < pageSize)
                break;

            offset += pageSize;
        }

        return tracks;
    }

    private async Task<List<(string Id, string Name, string Artist)>> FetchAlbumsAsync(string url, CancellationToken ct)
    {
        var result = new List<(string, string, string)>();
        try
        {
            var json = await _http.GetStringAsync(url, ct);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("subsonic-response", out var root)) return result;
            if (root.TryGetProperty("albumList2", out var list) &&
                list.TryGetProperty("album", out var albumsEl) &&
                albumsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var album in albumsEl.EnumerateArray())
                {
                    var id = album.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
                    var name = album.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "";
                    var artist = album.TryGetProperty("artist", out var artistEl) ? artistEl.GetString() ?? "" : "";
                    if (string.IsNullOrWhiteSpace(id)) continue;
                    result.Add((id, name, artist));
                }
            }
        }
        catch
        {
            // Network or parse failure: fall back to empty result
        }
        return result;
    }

    private async Task<List<Track>> FetchSongsForAlbumAsync(SourceConnection connection, string albumId, string albumName, string albumArtist, CancellationToken ct)
    {
        var songs = new List<Track>();
        var url = BuildSubsonicUrl(connection, "getAlbum.view", ("id", albumId));
        if (url == null) return songs;

        try
        {
            var json = await _http.GetStringAsync(url, ct);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("subsonic-response", out var root)) return songs;
            if (root.TryGetProperty("album", out var album) &&
                album.TryGetProperty("song", out var songArray) &&
                songArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var song in songArray.EnumerateArray())
                    songs.Add(MapSong(connection, song, albumName, albumArtist));
            }
        }
        catch
        {
            // Ignore and return partial
        }

        return songs;
    }

    private static Track MapSong(SourceConnection connection, JsonElement song, string albumName, string albumArtist)
    {
        string songId = song.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? Guid.NewGuid().ToString("N") : Guid.NewGuid().ToString("N");
        string title = song.TryGetProperty("title", out var tEl) ? tEl.GetString() ?? "Unknown Title" : "Unknown Title";
        string artist = ResolveTrackArtist(song, albumArtist);
        // Merge featured artists from title (e.g. "200 MPH (feat. Diplo)") into the artist field
        artist = MetadataService.EnrichArtistFromTitle(artist, title);
        string genre = song.TryGetProperty("genre", out var gEl) ? gEl.GetString() ?? string.Empty : string.Empty;
        int trackNo = song.TryGetProperty("track", out var trEl) && trEl.TryGetInt32(out var trVal) ? trVal : 0;
        int discNo = song.TryGetProperty("discNumber", out var dEl) && dEl.TryGetInt32(out var dVal) ? dVal : 1;
        int year = song.TryGetProperty("year", out var yEl) && yEl.TryGetInt32(out var yVal) ? yVal : 0;
        long size = song.TryGetProperty("size", out var sEl) && sEl.TryGetInt64(out var sVal) ? sVal : 0;
        int durationSeconds = song.TryGetProperty("duration", out var durEl) && durEl.TryGetInt32(out var durVal) ? durVal : 0;

        var track = new Track
        {
            Id = ComputeDeterministicId(connection.Id, songId),
            FilePath = $"navidrome://{connection.Id:N}/{songId}",
            Title = title,
            Artist = artist,
            AlbumArtist = string.IsNullOrWhiteSpace(albumArtist) ? artist : albumArtist,
            Album = string.IsNullOrWhiteSpace(albumName) ? "Unknown Album" : albumName,
            Genre = genre ?? string.Empty,
            TrackNumber = trackNo,
            DiscNumber = discNo,
            Year = year,
            Duration = TimeSpan.FromSeconds(Math.Max(0, durationSeconds)),
            FileSize = size,
            LastModified = DateTime.UtcNow,
            DateAdded = DateTime.UtcNow,
            SourceType = SourceType.Navidrome,
            SourceTrackId = songId,
            SourceConnectionId = connection.Id.ToString("N")
        };

        track.AlbumId = Track.ComputeAlbumId(track.AlbumArtist, track.Album);
        return track;
    }

    /// <summary>
    /// Reads per-track artist credits from the Subsonic API response.
    /// Prefers the "artists" array (multi-artist) over the single "artist" field.
    /// </summary>
    private static string ResolveTrackArtist(JsonElement song, string albumArtist)
    {
        // Newer Subsonic/Navidrome responses include an "artists" array with individual credits
        if (song.TryGetProperty("artists", out var artistsEl) &&
            artistsEl.ValueKind == JsonValueKind.Array &&
            artistsEl.GetArrayLength() > 0)
        {
            var names = new List<string>();
            foreach (var entry in artistsEl.EnumerateArray())
            {
                var name = entry.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (!string.IsNullOrWhiteSpace(name))
                    names.Add(name!);
            }
            if (names.Count > 0)
                return string.Join(", ", names);
        }

        // Fall back to the singular "artist" field, then album artist
        return song.TryGetProperty("artist", out var aEl)
            ? aEl.GetString() ?? albumArtist
            : albumArtist;
    }

    private static Guid ComputeDeterministicId(Guid connectionId, string songId)
    {
        var raw = $"{connectionId:N}:{songId}";
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(raw));
        return new Guid(hash);
    }


    public async Task<Stream?> OpenTrackStreamAsync(SourceConnection connection, Track track, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(track.SourceTrackId))
            return null;

        var url = BuildSubsonicUrl(connection, "stream.view", ("id", track.SourceTrackId));
        if (url == null) return null;

        try
        {
            var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadAsStreamAsync(ct);
        }
        catch
        {
            return null;
        }
    }

    public async Task<bool> DownloadTrackAsync(SourceConnection connection, Track track, string destinationPath, CancellationToken ct = default)
    {
        await using var stream = await OpenTrackStreamAsync(connection, track, ct);
        if (stream == null) return false;

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        await using var output = File.Create(destinationPath);
        await stream.CopyToAsync(output, ct);
        return true;
    }

    public static string? BuildSubsonicUrl(SourceConnection connection, string endpoint, params (string key, string value)[] extra)
    {
        if (string.IsNullOrWhiteSpace(connection.BaseUriOrPath)) return null;
        var baseUri = connection.BaseUriOrPath.TrimEnd('/');
        var query = new List<string>
        {
            $"u={Uri.EscapeDataString(connection.Username)}",
            $"p={Uri.EscapeDataString(connection.TokenOrPassword)}",
            "v=1.16.1",
            "c=Noctis",
            "f=json"
        };

        foreach (var (key, value) in extra)
            query.Add($"{Uri.EscapeDataString(key)}={Uri.EscapeDataString(value)}");

        return $"{baseUri}/rest/{endpoint}?{string.Join("&", query)}";
    }
}
