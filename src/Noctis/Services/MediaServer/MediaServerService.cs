using System.Collections.Concurrent;
using System.Net.Http;
using Noctis.Models;

namespace Noctis.Services.MediaServer;

/// <summary>
/// App-facing facade over the media-server clients. Holds the single active
/// connection (v1 supports one server), routes calls to the matching protocol
/// client, and materializes server cover art into the regular artwork store
/// (<see cref="IPersistenceService.SaveArtwork"/>) keyed by the same AlbumId the
/// mapped tracks carry — so the grid tiles, now-playing art, SMTC and Discord all
/// use the standard local-file pipeline and no auth-bearing image URLs exist.
/// </summary>
public interface IMediaServerService
{
    /// <summary>The active server connection, or null when none is configured.</summary>
    SourceConnection? ActiveConnection { get; }

    bool IsConfigured { get; }

    /// <summary>Raised on the caller's thread whenever the active connection is set or cleared.</summary>
    event EventHandler? ActiveConnectionChanged;

    /// <summary>Validates credentials against a server. Does not change the active connection.</summary>
    Task<(MediaServerConnectResult result, SourceConnection connection)> ConnectAsync(
        SourceType type, string url, string username, string password, Guid? existingId, CancellationToken ct = default);

    /// <summary>Installs (or clears, with null) the active connection.</summary>
    void SetActiveConnection(SourceConnection? connection);

    Task<IReadOnlyList<ServerAlbum>> GetAlbumsAsync(int offset, int limit, CancellationToken ct = default);
    Task<IReadOnlyList<Track>> GetAlbumTracksAsync(ServerAlbum album, CancellationToken ct = default);
    Task<ServerSearchResult> SearchAsync(string query, CancellationToken ct = default);

    /// <summary>
    /// Ensures the album's cover art exists in the local artwork store; returns the
    /// local file path, or null when the server has no art for it.
    /// </summary>
    Task<string?> EnsureAlbumArtworkAsync(ServerAlbum album, CancellationToken ct = default);
}

public sealed class MediaServerService : IMediaServerService
{
    /// <summary>Decode size requested from the server; matches the player's art width.</summary>
    private const int ArtworkFetchSize = 768;

    /// <summary>
    /// Hard ceiling on one artwork download. HttpClient.Timeout stops applying once
    /// ResponseHeadersRead has delivered the headers (verified empirically on .NET 8:
    /// a stalled body read outlives the client timeout), so without this a stalled
    /// server would pin an <see cref="_artworkGate"/> slot and park the album's
    /// <see cref="_artworkInFlight"/> entry forever. Internal-settable for tests.
    /// </summary>
    internal static TimeSpan ArtworkDownloadTimeout = TimeSpan.FromSeconds(30);

    private readonly IPersistenceService _persistence;
    private readonly Dictionary<SourceType, IMediaServerClient> _clients;
    private readonly SemaphoreSlim _artworkGate = new(3);
    private readonly ConcurrentDictionary<Guid, Task<string?>> _artworkInFlight = new();

    private SourceConnection? _activeConnection;

    public MediaServerService(HttpClient http, IPersistenceService persistence)
    {
        _persistence = persistence;
        _clients = new Dictionary<SourceType, IMediaServerClient>
        {
            [SourceType.Navidrome] = new SubsonicClient(http),
            [SourceType.Jellyfin] = new JellyfinClient(http)
        };
    }

    public SourceConnection? ActiveConnection => _activeConnection;

    public bool IsConfigured => _activeConnection != null;

    public event EventHandler? ActiveConnectionChanged;

    public async Task<(MediaServerConnectResult result, SourceConnection connection)> ConnectAsync(
        SourceType type, string url, string username, string password, Guid? existingId, CancellationToken ct = default)
    {
        var connection = new SourceConnection
        {
            // Keep the previous guid so the Jellyfin DeviceId (and deterministic
            // track ids) stay stable across re-connects of the same setup.
            Id = existingId ?? Guid.NewGuid(),
            Name = type == SourceType.Jellyfin ? "Jellyfin" : "Subsonic",
            Type = type,
            BaseUriOrPath = url?.Trim() ?? string.Empty,
            Username = username?.Trim() ?? string.Empty,
            Enabled = true
        };

        if (!_clients.TryGetValue(type, out var client))
            return (MediaServerConnectResult.Fail(MediaServerError.ServerError, "Unsupported server type."), connection);

        var result = await client.ConnectAsync(connection, password, ct);
        return (result, connection);
    }

    public void SetActiveConnection(SourceConnection? connection)
    {
        _activeConnection = connection;
        ActiveConnectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task<IReadOnlyList<ServerAlbum>> GetAlbumsAsync(int offset, int limit, CancellationToken ct = default)
    {
        var (connection, client) = ResolveActive();
        if (connection == null || client == null) return Array.Empty<ServerAlbum>();
        return await client.GetAlbumsAsync(connection, offset, limit, ct);
    }

    public async Task<IReadOnlyList<Track>> GetAlbumTracksAsync(ServerAlbum album, CancellationToken ct = default)
    {
        var (connection, client) = ResolveActive();
        if (connection == null || client == null) return Array.Empty<Track>();

        var tracks = await client.GetAlbumTracksAsync(connection, album, ct);

        // Materialize art once per distinct AlbumId the mapped tracks actually carry
        // (usually one; can differ from the listing when server tags disagree).
        if (album.CoverArtId != null && tracks.Count > 0)
        {
            foreach (var albumId in tracks.Select(t => t.AlbumId).Distinct())
            {
                var path = await EnsureArtworkForAlbumIdAsync(albumId, album.CoverArtId, ct);
                if (path == null) continue;
                foreach (var track in tracks)
                    if (track.AlbumId == albumId)
                        track.AlbumArtworkPath = path;
            }
        }

        return tracks;
    }

    public async Task<ServerSearchResult> SearchAsync(string query, CancellationToken ct = default)
    {
        var (connection, client) = ResolveActive();
        if (connection == null || client == null || string.IsNullOrWhiteSpace(query))
            return ServerSearchResult.Empty;

        var result = await client.SearchAsync(connection, query, ct);

        // Best-effort art for song hits, in the background so search stays snappy;
        // by play time the file is usually in place (graceful no-art otherwise).
        foreach (var track in result.Tracks)
        {
            if (!result.TrackArtIds.TryGetValue(track.Id, out var artId)) continue;
            var albumId = track.AlbumId;
            _ = EnsureArtworkForAlbumIdAsync(albumId, artId, CancellationToken.None)
                .ContinueWith(t =>
                {
                    if (t.Status == TaskStatus.RanToCompletion && t.Result != null)
                        track.AlbumArtworkPath = t.Result;
                }, TaskScheduler.Default);
        }

        return result;
    }

    public Task<string?> EnsureAlbumArtworkAsync(ServerAlbum album, CancellationToken ct = default)
    {
        if (album.CoverArtId == null) return Task.FromResult<string?>(null);
        var albumId = Track.ComputeAlbumId(
            string.IsNullOrWhiteSpace(album.Artist) ? "Unknown Artist" : album.Artist,
            album.Name);
        return EnsureArtworkForAlbumIdAsync(albumId, album.CoverArtId, ct);
    }

    private async Task<string?> EnsureArtworkForAlbumIdAsync(Guid albumId, string artId, CancellationToken ct)
    {
        if (albumId == Track.UnknownAlbumBucketId) return null; // SaveArtwork refuses the bucket

        var artPath = _persistence.GetArtworkPath(albumId);
        if (File.Exists(artPath)) return artPath;

        var task = _artworkInFlight.GetOrAdd(albumId, _ => DownloadArtworkAsync(albumId, artId, artPath));
        try
        {
            return await task.WaitAsync(ct);
        }
        finally
        {
            if (task.IsCompleted) _artworkInFlight.TryRemove(albumId, out _);
        }
    }

    private async Task<string?> DownloadArtworkAsync(Guid albumId, string artId, string artPath)
    {
        var (connection, client) = ResolveActive();
        if (connection == null || client == null) return null;

        await _artworkGate.WaitAsync();
        try
        {
            if (File.Exists(artPath)) return artPath;
            // The download is shared by every caller via _artworkInFlight, so it
            // deliberately ignores caller tokens — the timeout is its only bound.
            using var timeout = new CancellationTokenSource(ArtworkDownloadTimeout);
            var bytes = await client.GetArtworkAsync(connection, artId, ArtworkFetchSize, timeout.Token);
            if (bytes is not { Length: > 0 }) return null;
            _persistence.SaveArtwork(albumId, bytes);
            ArtworkCache.Invalidate(artPath);
            return File.Exists(artPath) ? artPath : null;
        }
        catch (Exception ex)
        {
            // One line per failure kind — a dead server fails for every cover.
            DebugLog.WriteOnce("Server", $"artwork:{ex.GetBaseException().GetType().Name}",
                $"Server artwork fetch failed: {ex.Message}");
            return null;
        }
        finally
        {
            _artworkGate.Release();
        }
    }

    private (SourceConnection? connection, IMediaServerClient? client) ResolveActive()
    {
        var connection = _activeConnection;
        if (connection == null) return (null, null);
        return _clients.TryGetValue(connection.Type, out var client) ? (connection, client) : (connection, null);
    }
}
