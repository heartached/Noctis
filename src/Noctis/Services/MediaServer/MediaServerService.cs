using System.Collections.Concurrent;
using System.Net.Http;
using Noctis.Localization;
using Noctis.Models;

namespace Noctis.Services.MediaServer;

/// <summary>
/// App-facing facade over the media-server clients. Holds the user's connected servers
/// (GitHub #268: several Subsonic-family and Jellyfin servers side by side), routes each
/// call to the server an album or track came from, merges their albums into one list,
/// and materializes server cover art into the regular artwork store
/// (<see cref="IPersistenceService.SaveArtwork"/>) keyed by the same AlbumId the
/// mapped tracks carry — so the grid tiles, now-playing art, SMTC and Discord all
/// use the standard local-file pipeline and no auth-bearing image URLs exist.
/// </summary>
public interface IMediaServerService
{
    /// <summary>The first enabled server (the highest priority), or null when none is configured.</summary>
    SourceConnection? ActiveConnection { get; }

    /// <summary>Every enabled server, in the user's order (first = preferred for an album on several).</summary>
    IReadOnlyList<SourceConnection> Connections => ActiveConnection is { } c ? new[] { c } : Array.Empty<SourceConnection>();

    bool IsConfigured { get; }

    /// <summary>Raised on the caller's thread whenever the set of servers changes.</summary>
    event EventHandler? ActiveConnectionChanged;

    /// <summary>Validates credentials against a server. Does not change the active servers.</summary>
    Task<(MediaServerConnectResult result, SourceConnection connection)> ConnectAsync(
        SourceType type, string url, string username, string password, Guid? existingId, CancellationToken ct = default);

    /// <summary>Installs (or clears, with null) a single server.</summary>
    void SetActiveConnection(SourceConnection? connection);

    /// <summary>Installs the user's servers, in priority order. Disabled ones are left out.</summary>
    void SetConnections(IEnumerable<SourceConnection> connections)
        => SetActiveConnection(connections.FirstOrDefault(c => c.Enabled));

    /// <summary>
    /// A page of the unified album list (alphabetical). With one server this pages the
    /// server directly; with several it pages <see cref="GetAllAlbumsAsync"/>.
    /// </summary>
    Task<IReadOnlyList<ServerAlbum>> GetAlbumsAsync(int offset, int limit, CancellationToken ct = default);

    /// <summary>
    /// Every album of every enabled server, merged: the same album (album artist + title)
    /// on several servers appears once, from the first server in the user's order, with
    /// the others as <see cref="ServerAlbum.Alternates"/>. Cached for a few minutes.
    /// </summary>
    Task<IReadOnlyList<ServerAlbum>> GetAllAlbumsAsync(CancellationToken ct = default)
        => GetAlbumsAsync(0, 500, ct);

    /// <summary>An album of the last unified list by its <see cref="ServerAlbum.Key"/>, or null.</summary>
    ServerAlbum? FindAlbum(string key) => null;

    /// <summary>The album's cover when it is already in the local artwork store, else null (no network).</summary>
    string? CachedArtworkPath(ServerAlbum album) => null;

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

    /// <summary>Albums asked for per request while reading a whole server (the Subsonic maximum).</summary>
    internal const int FullListPageSize = 500;

    /// <summary>Ceiling on one server's album list, so a broken server that never ends its paging cannot loop forever.</summary>
    internal const int MaxAlbumsPerServer = 100_000;

    /// <summary>How long the unified album list is reused before the servers are read again.</summary>
    internal static TimeSpan AlbumListLifetime = TimeSpan.FromMinutes(10);

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
    private readonly object _albumsGate = new();

    private IReadOnlyList<SourceConnection> _connections = Array.Empty<SourceConnection>();
    private int _generation;
    private Task<IReadOnlyList<ServerAlbum>>? _allAlbums;
    private int _allAlbumsGeneration = -1;
    private DateTime _allAlbumsAtUtc;
    private Dictionary<string, ServerAlbum> _albumsByKey = new(StringComparer.Ordinal);

    public MediaServerService(HttpClient http, IPersistenceService persistence)
        : this(persistence, new Dictionary<SourceType, IMediaServerClient>
        {
            [SourceType.Navidrome] = new SubsonicClient(http),
            [SourceType.Jellyfin] = new JellyfinClient(http)
        })
    {
    }

    /// <summary>Tests: supply the protocol clients.</summary>
    internal MediaServerService(IPersistenceService persistence, Dictionary<SourceType, IMediaServerClient> clients)
    {
        _persistence = persistence;
        _clients = clients;
    }

    public SourceConnection? ActiveConnection => _connections.Count > 0 ? _connections[0] : null;

    public IReadOnlyList<SourceConnection> Connections => _connections;

    public bool IsConfigured => _connections.Count > 0;

    public event EventHandler? ActiveConnectionChanged;

    /// <summary>
    /// What the app calls a server: the user's own name for it, else the preset and host
    /// ("Navidrome (music.example.com)") so two servers of one kind stay apart.
    /// </summary>
    public static string DisplayName(SourceConnection connection)
    {
        if (!string.IsNullOrWhiteSpace(connection.Label)) return connection.Label.Trim();
        var name = string.IsNullOrWhiteSpace(connection.Name)
            ? connection.Type == SourceType.Jellyfin ? "Jellyfin" : "Subsonic"
            : connection.Name.Trim();
        return Uri.TryCreate(connection.BaseUriOrPath, UriKind.Absolute, out var uri) && uri.Host.Length > 0
            ? $"{name} ({uri.Host})"
            : name;
    }

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
            return (MediaServerConnectResult.Fail(MediaServerError.ServerError, Loc.T("Settings.Server.UnsupportedType")), connection);

        var result = await client.ConnectAsync(connection, password, ct);
        return (result, connection);
    }

    public void SetActiveConnection(SourceConnection? connection)
        => SetConnections(connection == null ? Array.Empty<SourceConnection>() : new[] { connection });

    public void SetConnections(IEnumerable<SourceConnection> connections)
    {
        var list = connections.Where(c => c != null && c.Enabled && _clients.ContainsKey(c.Type))
            .GroupBy(c => c.Id).Select(g => g.First()).ToArray();
        lock (_albumsGate)
        {
            _connections = list;
            _generation++;
            _allAlbums = null;
            _albumsByKey = new Dictionary<string, ServerAlbum>(StringComparer.Ordinal);
        }
        ActiveConnectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task<IReadOnlyList<ServerAlbum>> GetAlbumsAsync(int offset, int limit, CancellationToken ct = default)
    {
        var connections = _connections;
        if (connections.Count == 0) return Array.Empty<ServerAlbum>();

        if (connections.Count == 1)
        {
            // One server: page it directly, as before (no whole-library read on first open).
            var connection = connections[0];
            if (!_clients.TryGetValue(connection.Type, out var client)) return Array.Empty<ServerAlbum>();
            var page = await client.GetAlbumsAsync(connection, offset, limit, ct);
            Stamp(page, connection);
            lock (_albumsGate)
                foreach (var album in page) _albumsByKey[album.Key] = album;
            return page;
        }

        var all = await GetAllAlbumsAsync(ct);
        if (offset >= all.Count || limit <= 0) return Array.Empty<ServerAlbum>();
        return all.Skip(Math.Max(0, offset)).Take(limit).ToArray();
    }

    public Task<IReadOnlyList<ServerAlbum>> GetAllAlbumsAsync(CancellationToken ct = default)
    {
        Task<IReadOnlyList<ServerAlbum>> task;
        lock (_albumsGate)
        {
            if (_allAlbums == null || _allAlbumsGeneration != _generation || _allAlbums.IsFaulted || _allAlbums.IsCanceled
                || (_allAlbums.IsCompleted && DateTime.UtcNow - _allAlbumsAtUtc > AlbumListLifetime))
            {
                var generation = _generation;
                _allAlbumsGeneration = generation;
                _allAlbumsAtUtc = DateTime.UtcNow;
                _allAlbums = ReadAllAlbumsAsync(_connections, generation);
            }
            task = _allAlbums;
        }
        return task.WaitAsync(ct);
    }

    public ServerAlbum? FindAlbum(string key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        lock (_albumsGate)
            return _albumsByKey.TryGetValue(key, out var album) ? album : null;
    }

    /// <summary>
    /// Reads every server's whole album list (in parallel) and merges them. Shared by every
    /// caller, so it ignores caller tokens; a server that fails folds into an empty list
    /// (the clients do that), leaving the others' albums.
    /// </summary>
    private async Task<IReadOnlyList<ServerAlbum>> ReadAllAlbumsAsync(IReadOnlyList<SourceConnection> connections, int generation)
    {
        var lists = await Task.WhenAll(connections.Select(ReadServerAlbumsAsync));
        var merged = MergeAlbums(lists);
        lock (_albumsGate)
        {
            if (generation == _generation)
            {
                var byKey = new Dictionary<string, ServerAlbum>(StringComparer.Ordinal);
                foreach (var album in merged)
                {
                    byKey[album.Key] = album;
                    foreach (var alternate in album.Alternates) byKey.TryAdd(alternate.Key, alternate);
                }
                _albumsByKey = byKey;
            }
        }
        return merged;
    }

    private async Task<IReadOnlyList<ServerAlbum>> ReadServerAlbumsAsync(SourceConnection connection)
    {
        if (!_clients.TryGetValue(connection.Type, out var client)) return Array.Empty<ServerAlbum>();
        var result = new List<ServerAlbum>();
        try
        {
            while (result.Count < MaxAlbumsPerServer)
            {
                var page = await client.GetAlbumsAsync(connection, result.Count, FullListPageSize);
                result.AddRange(page);
                if (page.Count < FullListPageSize) break;
            }
        }
        catch (Exception ex)
        {
            DebugLog.WriteOnce("Server", $"albums:{connection.Id:N}:{ex.GetBaseException().GetType().Name}",
                $"Server album list failed ({DisplayName(connection)}): {ex.Message}");
        }
        Stamp(result, connection);
        return result;
    }

    private static void Stamp(IEnumerable<ServerAlbum> albums, SourceConnection connection)
    {
        var name = DisplayName(connection);
        foreach (var album in albums)
        {
            album.ConnectionId = connection.Id;
            album.ServerName = name;
        }
    }

    /// <summary>The identity two servers' copies of one album share: album artist + title, as the library groups albums.</summary>
    internal static Guid AlbumIdentity(ServerAlbum album) =>
        Track.ComputeAlbumId(string.IsNullOrWhiteSpace(album.Artist) ? "Unknown Artist" : album.Artist, album.Name);

    /// <summary>
    /// Merges per-server album lists given in priority order: an album on several servers
    /// is kept once, from the first server listing it, with the later servers' copies as
    /// its alternates. Two albums of one name on the same server stay two albums, and
    /// albums without a real identity ("Unknown Album" by "Unknown Artist") are never
    /// merged. Sorted by title, then artist.
    /// </summary>
    internal static IReadOnlyList<ServerAlbum> MergeAlbums(IEnumerable<IReadOnlyList<ServerAlbum>> perServer)
    {
        var result = new List<ServerAlbum>();
        var primary = new Dictionary<Guid, ServerAlbum>();
        var alternates = new Dictionary<ServerAlbum, List<ServerAlbum>>(ReferenceEqualityComparer.Instance);
        foreach (var list in perServer)
        {
            foreach (var album in list)
            {
                var identity = AlbumIdentity(album);
                if (identity != Track.UnknownAlbumBucketId && primary.TryGetValue(identity, out var first)
                    && first.ConnectionId != album.ConnectionId)
                {
                    if (!alternates.TryGetValue(first, out var others)) alternates[first] = others = new List<ServerAlbum>();
                    if (others.All(o => o.ConnectionId != album.ConnectionId)) others.Add(album);
                    continue;
                }
                if (identity != Track.UnknownAlbumBucketId) primary.TryAdd(identity, album);
                result.Add(album);
            }
        }
        foreach (var (album, others) in alternates) album.Alternates = others;
        result.Sort((a, b) =>
        {
            var byName = string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
            return byName != 0 ? byName : string.Compare(a.Artist, b.Artist, StringComparison.CurrentCultureIgnoreCase);
        });
        return result;
    }

    public async Task<IReadOnlyList<Track>> GetAlbumTracksAsync(ServerAlbum album, CancellationToken ct = default)
    {
        // The album's own server first, then the servers that also have it.
        foreach (var candidate in new[] { album }.Concat(album.Alternates))
        {
            var (connection, client) = Resolve(candidate.ConnectionId);
            if (connection == null || client == null) continue;

            var tracks = await client.GetAlbumTracksAsync(connection, candidate, ct);
            if (tracks.Count == 0) continue;

            // Materialize art once per distinct AlbumId the mapped tracks actually carry
            // (usually one; can differ from the listing when server tags disagree).
            if (candidate.CoverArtId != null)
            {
                foreach (var albumId in tracks.Select(t => t.AlbumId).Distinct())
                {
                    var path = await EnsureArtworkForAlbumIdAsync(albumId, candidate.CoverArtId, connection, client, ct);
                    if (path == null) continue;
                    foreach (var track in tracks)
                        if (track.AlbumId == albumId)
                            track.AlbumArtworkPath = path;
                }
            }

            return tracks;
        }

        return Array.Empty<Track>();
    }

    public async Task<ServerSearchResult> SearchAsync(string query, CancellationToken ct = default)
    {
        var connections = _connections;
        if (connections.Count == 0 || string.IsNullOrWhiteSpace(query))
            return ServerSearchResult.Empty;

        var results = await Task.WhenAll(connections.Select(async connection =>
        {
            if (!_clients.TryGetValue(connection.Type, out var client))
                return (connection, client: (IMediaServerClient?)null, result: ServerSearchResult.Empty);
            var result = await client.SearchAsync(connection, query, ct);
            Stamp(result.Albums, connection);
            return (connection, client: (IMediaServerClient?)client, result);
        }));

        var tracks = new List<Track>();
        var artIds = new Dictionary<Guid, string>();
        var seenSongs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (connection, client, result) in results)
        {
            foreach (var track in result.Tracks)
            {
                // The same song on two servers is listed once, from the first server.
                if (connections.Count > 1 && !seenSongs.Add(SongIdentity(track))) continue;
                tracks.Add(track);
                if (client == null || !result.TrackArtIds.TryGetValue(track.Id, out var artId)) continue;
                artIds[track.Id] = artId;

                // Best-effort art for song hits, in the background so search stays snappy;
                // by play time the file is usually in place (graceful no-art otherwise).
                _ = EnsureArtworkForAlbumIdAsync(track.AlbumId, artId, connection, client, CancellationToken.None)
                    .ContinueWith(t =>
                    {
                        if (t.Status == TaskStatus.RanToCompletion && t.Result != null)
                            track.AlbumArtworkPath = t.Result;
                    }, TaskScheduler.Default);
            }
        }

        var albums = results.Length == 1
            ? results[0].result.Albums
            : MergeAlbumsKeepingOrder(results.Select(r => r.result.Albums).ToList());
        lock (_albumsGate)
            foreach (var album in albums) _albumsByKey.TryAdd(album.Key, album);

        return new ServerSearchResult { Albums = albums, Tracks = tracks, TrackArtIds = artIds };
    }

    /// <summary>Search hits keep the servers' relevance order; only the duplicates fold into alternates.</summary>
    private static IReadOnlyList<ServerAlbum> MergeAlbumsKeepingOrder(IReadOnlyList<IReadOnlyList<ServerAlbum>> perServer)
    {
        var kept = new HashSet<ServerAlbum>(MergeAlbums(perServer), ReferenceEqualityComparer.Instance);
        return perServer.SelectMany(l => l).Where(kept.Contains).ToArray();
    }

    private static string SongIdentity(Track t) =>
        $"{t.AlbumId:N}|{t.DiscNumber}|{t.TrackNumber}|{t.Title.Trim().ToLowerInvariant()}";

    public string? CachedArtworkPath(ServerAlbum album)
    {
        if (album.CoverArtId == null) return null;
        var albumId = AlbumIdentity(album);
        if (albumId == Track.UnknownAlbumBucketId) return null;
        var path = _persistence.GetArtworkPath(albumId);
        return File.Exists(path) ? path : null;
    }

    public Task<string?> EnsureAlbumArtworkAsync(ServerAlbum album, CancellationToken ct = default)
    {
        if (album.CoverArtId == null) return Task.FromResult<string?>(null);
        var (connection, client) = Resolve(album.ConnectionId);
        if (connection == null || client == null) return Task.FromResult<string?>(null);
        return EnsureArtworkForAlbumIdAsync(AlbumIdentity(album), album.CoverArtId, connection, client, ct);
    }

    private async Task<string?> EnsureArtworkForAlbumIdAsync(Guid albumId, string artId,
        SourceConnection connection, IMediaServerClient client, CancellationToken ct)
    {
        if (albumId == Track.UnknownAlbumBucketId) return null; // SaveArtwork refuses the bucket

        var artPath = _persistence.GetArtworkPath(albumId);
        if (File.Exists(artPath)) return artPath;

        var task = _artworkInFlight.GetOrAdd(albumId, _ => DownloadArtworkAsync(albumId, artId, artPath, connection, client));
        try
        {
            return await task.WaitAsync(ct);
        }
        finally
        {
            if (task.IsCompleted) _artworkInFlight.TryRemove(albumId, out _);
        }
    }

    private async Task<string?> DownloadArtworkAsync(Guid albumId, string artId, string artPath,
        SourceConnection connection, IMediaServerClient client)
    {
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

    /// <summary>
    /// The enabled server with this id and its protocol client. An album from a bare
    /// client call (no id stamped) goes to the only server when there is just one.
    /// </summary>
    private (SourceConnection? connection, IMediaServerClient? client) Resolve(Guid connectionId)
    {
        var connections = _connections;
        var connection = connectionId == Guid.Empty
            ? connections.Count == 1 ? connections[0] : null
            : connections.FirstOrDefault(c => c.Id == connectionId);
        if (connection == null) return (null, null);
        return _clients.TryGetValue(connection.Type, out var client) ? (connection, client) : (connection, null);
    }
}
