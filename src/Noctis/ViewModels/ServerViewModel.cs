using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services;
using Noctis.Services.MediaServer;

namespace Noctis.ViewModels;

/// <summary>Grid tile for one server album; artwork arrives asynchronously.</summary>
public partial class ServerAlbumTileViewModel : ObservableObject
{
    public ServerAlbum Album { get; }

    [ObservableProperty] private string? _artworkPath;

    public string Name => Album.Name;
    public string Artist => Album.Artist;

    public ServerAlbumTileViewModel(ServerAlbum album) => Album = album;
}

/// <summary>
/// A row of up to 5 server album tiles: the grid's outer ListBox virtualizes
/// rows while each row lays its tiles out in a non-virtualizing UniformGrid
/// (same pattern as the local Albums page).
/// </summary>
public class ServerAlbumRow
{
    public List<ServerAlbumTileViewModel> Tiles { get; init; } = new();
}

/// <summary>
/// Sentinel last item of the album-row list: hosts the status line and the
/// load-more button inside the virtualized list so they keep scrolling with
/// the grid content.
/// </summary>
public class ServerGridFooter
{
    public static readonly ServerGridFooter Instance = new();
}

/// <summary>
/// The "Server" section: browse the connected media servers' albums (one merged
/// list when there are several, GitHub #268), open an
/// album's track list, and search server-side — all on demand, nothing enters
/// the local library. Tracks play through the regular queue (their FilePath is
/// the server stream URL).
/// </summary>
public partial class ServerViewModel : ViewModelBase, ISearchable
{
    private const int AlbumPageSize = 60;
    private const int AlbumGridColumns = 5;

    private readonly IMediaServerService _mediaServer;
    private readonly PlayerViewModel _player;

    private CancellationTokenSource _lifetimeCts = new();
    private bool _needsReload = true;
    private int _albumOffset;
    private int _searchGeneration;
    private int _openAlbumGeneration;

    // Several servers (GitHub #268): the merged list arrives a page at a time from each server
    // (IMediaServerService.GetAllAlbumsProgressiveAsync); the grid shows its first _shownCount.
    private bool _mergedMode;
    private IReadOnlyList<ServerAlbum> _mergedAlbums = Array.Empty<ServerAlbum>();
    private int _shownCount;
    private int _albumLoadGeneration;
    private PendingAlbums? _pendingAlbums;
    private int _pendingPosted;
    private int _appliedSequence;

    /// <summary>A merged list handed over from a server read (any thread), applied on the UI thread.</summary>
    private sealed record PendingAlbums(int Generation, int Sequence, IReadOnlyList<ServerAlbum> Albums);

    public ObservableCollection<ServerAlbumTileViewModel> Albums { get; } = new();

    /// <summary>
    /// <see cref="Albums"/> chunked into <see cref="ServerAlbumRow"/>s (plus the
    /// trailing <see cref="ServerGridFooter"/>) for the virtualized grid ListBox.
    /// </summary>
    public BulkObservableCollection<object> AlbumRows { get; } = new();

    public ObservableCollection<ServerAlbumTileViewModel> SearchAlbums { get; } = new();
    public ObservableCollection<Track> SearchTracks { get; } = new();
    public ObservableCollection<Track> AlbumTracks { get; } = new();

    [ObservableProperty] private bool _isConfigured;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isLoadingMore;
    [ObservableProperty] private bool _hasMoreAlbums;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _serverLabel = "";

    // Exactly one of the three content surfaces is visible at a time.
    [ObservableProperty] private bool _showAlbumGrid;
    [ObservableProperty] private bool _showAlbumDetail;
    [ObservableProperty] private bool _showSearchResults;

    [ObservableProperty] private ServerAlbumTileViewModel? _openAlbum;
    [ObservableProperty] private string _openAlbumMeta = "";
    [ObservableProperty] private bool _hasSearchAlbums;
    [ObservableProperty] private bool _hasSearchTracks;
    [ObservableProperty] private bool _searchCameUpEmpty;

    private string _searchQuery = "";

    public ServerViewModel(IMediaServerService mediaServer, PlayerViewModel player)
    {
        _mediaServer = mediaServer;
        _player = player;

        _mediaServer.ActiveConnectionChanged += (_, _) =>
            Dispatcher.UIThread.Post(ResetForConnectionChange);

        SyncConfiguredState();
        UpdateSurfaces();
    }

    /// <summary>Called by the shell every time the section is navigated to.</summary>
    public void OnNavigatedTo()
    {
        SyncConfiguredState();
        if (IsConfigured && (_needsReload || Albums.Count == 0))
            _ = ReloadAlbumsAsync();
        UpdateSurfaces();
    }

    // ── ISearchable (global search pill, throttled upstream) ──

    public void ApplyFilter(string query)
    {
        query = query?.Trim() ?? string.Empty;
        if (string.Equals(query, _searchQuery, StringComparison.Ordinal)) return;
        _searchQuery = query;

        var generation = ++_searchGeneration;
        if (query.Length == 0)
        {
            SearchAlbums.Clear();
            SearchTracks.Clear();
            SearchCameUpEmpty = false;
            UpdateSurfaces();
            return;
        }

        _ = RunSearchAsync(query, generation);
    }

    private async Task RunSearchAsync(string query, int generation)
    {
        if (!IsConfigured) return;
        var ct = _lifetimeCts.Token;
        ServerSearchResult result;
        try
        {
            result = await _mediaServer.SearchAsync(query, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (generation != _searchGeneration || ct.IsCancellationRequested) return;

        SearchAlbums.Clear();
        foreach (var album in result.Albums)
        {
            var tile = new ServerAlbumTileViewModel(album);
            SearchAlbums.Add(tile);
            _ = LoadTileArtworkAsync(tile, ct);
        }

        SearchTracks.Clear();
        foreach (var track in result.Tracks)
            SearchTracks.Add(track);

        HasSearchAlbums = SearchAlbums.Count > 0;
        HasSearchTracks = SearchTracks.Count > 0;
        SearchCameUpEmpty = !HasSearchAlbums && !HasSearchTracks;
        UpdateSurfaces();
    }

    // ── Albums grid ──

    private async Task ReloadAlbumsAsync()
    {
        _needsReload = false;
        _albumOffset = 0;
        _mergedMode = false;
        _albumLoadGeneration++;
        Albums.Clear();
        RebuildAlbumRows();
        HasMoreAlbums = false;
        StatusText = "";
        IsLoading = true;
        try
        {
            if (_mediaServer.Connections.Count > 1) await LoadMergedAlbumsAsync();
            else await LoadAlbumPageAsync();
            if (Albums.Count == 0)
            {
                StatusText = Noctis.Localization.Loc.T("Server.NoAlbums");
                // The clients fold network failures into empty results, so this
                // is the one spot that knows the server browse came back empty.
                DebugLog.Write("Server", "Album browse returned nothing — empty library or unreachable server.");
            }
        }
        finally
        {
            IsLoading = false;
            UpdateSurfaces();
        }
    }

    [RelayCommand]
    private async Task LoadMoreAlbums()
    {
        if (IsLoadingMore || !HasMoreAlbums) return;
        IsLoadingMore = true;
        try
        {
            if (_mergedMode)
            {
                // The merged list is in memory (or still arriving): show the next page of it.
                _shownCount += AlbumPageSize;
                ShowMergedAlbums(_lifetimeCts.Token);
            }
            else await LoadAlbumPageAsync();
        }
        finally
        {
            IsLoadingMore = false;
        }
    }

    private async Task LoadAlbumPageAsync()
    {
        var ct = _lifetimeCts.Token;
        IReadOnlyList<ServerAlbum> page;
        try
        {
            page = await _mediaServer.GetAlbumsAsync(_albumOffset, AlbumPageSize, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (ct.IsCancellationRequested) return;

        _albumOffset += page.Count;
        HasMoreAlbums = page.Count == AlbumPageSize;
        foreach (var album in page)
        {
            var tile = new ServerAlbumTileViewModel(album);
            Albums.Add(tile);
            _ = LoadTileArtworkAsync(tile, ct);
        }
        RebuildAlbumRows();
    }

    /// <summary>
    /// Several servers: reads them all in parallel and fills the grid as each one's pages
    /// arrive, instead of waiting for every server's whole list (the old first visit showed
    /// nothing until the slowest server had sent its last album). A slow or failing server
    /// holds back only its own albums.
    /// </summary>
    private async Task LoadMergedAlbumsAsync()
    {
        var ct = _lifetimeCts.Token;
        var generation = ++_albumLoadGeneration;
        var sequence = 0;
        _mergedMode = true;
        _mergedAlbums = Array.Empty<ServerAlbum>();
        _shownCount = AlbumPageSize;
        _appliedSequence = 0;
        IReadOnlyList<ServerAlbum> all;
        try
        {
            all = await _mediaServer.GetAllAlbumsProgressiveAsync(snapshot =>
            {
                // The service calls this in publish order; keep the latest and apply it once
                // per UI turn (pages from several servers can land back to back).
                Volatile.Write(ref _pendingAlbums, new PendingAlbums(generation, Interlocked.Increment(ref sequence), snapshot));
                if (Interlocked.Exchange(ref _pendingPosted, 1) == 0)
                    Dispatcher.UIThread.Post(ApplyPendingAlbums);
            }, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (ct.IsCancellationRequested || generation != _albumLoadGeneration) return;
        ApplyMergedSnapshot(new PendingAlbums(generation, int.MaxValue, all));
    }

    private void ApplyPendingAlbums()
    {
        Interlocked.Exchange(ref _pendingPosted, 0);
        if (Interlocked.Exchange(ref _pendingAlbums, null) is { } pending)
            ApplyMergedSnapshot(pending);
    }

    private void ApplyMergedSnapshot(PendingAlbums pending)
    {
        // A list from an earlier load, or older than one already shown (the complete list
        // can be applied before a posted partial one runs), is dropped.
        if (pending.Generation != _albumLoadGeneration || pending.Sequence <= _appliedSequence
            || _lifetimeCts.IsCancellationRequested) return;
        _appliedSequence = pending.Sequence;
        _mergedAlbums = pending.Albums;
        ShowMergedAlbums(_lifetimeCts.Token);
    }

    /// <summary>
    /// Shows the first <see cref="_shownCount"/> albums of the merged list. Tiles already on
    /// screen are kept (their covers stay); an album whose copy changed to a higher server's
    /// gets a new tile at the same place, starting from the cover already on disk.
    /// </summary>
    private void ShowMergedAlbums(CancellationToken ct)
    {
        var visible = _mergedAlbums.Take(_shownCount).ToList();
        var existing = new Dictionary<ServerAlbum, ServerAlbumTileViewModel>(ReferenceEqualityComparer.Instance);
        foreach (var tile in Albums) existing[tile.Album] = tile;

        var same = visible.Count == Albums.Count;
        for (var i = 0; same && i < visible.Count; i++)
            same = ReferenceEquals(visible[i], Albums[i].Album);
        HasMoreAlbums = _mergedAlbums.Count > _shownCount;
        _albumOffset = visible.Count;
        if (same) return;

        var tiles = new List<ServerAlbumTileViewModel>(visible.Count);
        foreach (var album in visible)
        {
            if (existing.TryGetValue(album, out var tile))
            {
                tiles.Add(tile);
                continue;
            }
            tile = new ServerAlbumTileViewModel(album) { ArtworkPath = _mediaServer.CachedArtworkPath(album) };
            tiles.Add(tile);
            if (tile.ArtworkPath == null) _ = LoadTileArtworkAsync(tile, ct);
        }
        Albums.Clear();
        foreach (var tile in tiles) Albums.Add(tile);
        RebuildAlbumRows();
        if (Albums.Count > 0) StatusText = "";
    }

    /// <summary>
    /// Re-chunks <see cref="Albums"/> into fixed-column rows (one Reset
    /// notification) so the grid's outer ListBox only realizes visible rows.
    /// The footer sentinel is always last; its content hides itself via
    /// StatusText/HasMoreAlbums bindings.
    /// </summary>
    private void RebuildAlbumRows()
    {
        var rows = new List<object>();
        for (int i = 0; i < Albums.Count; i += AlbumGridColumns)
        {
            var row = new ServerAlbumRow();
            for (int j = i; j < Albums.Count && j < i + AlbumGridColumns; j++)
                row.Tiles.Add(Albums[j]);
            rows.Add(row);
        }
        rows.Add(ServerGridFooter.Instance);
        AlbumRows.ReplaceAll(rows);
    }

    private async Task LoadTileArtworkAsync(ServerAlbumTileViewModel tile, CancellationToken ct)
    {
        try
        {
            var path = await _mediaServer.EnsureAlbumArtworkAsync(tile.Album, ct);
            if (path != null && !ct.IsCancellationRequested)
                tile.ArtworkPath = path;
        }
        catch (OperationCanceledException)
        {
            // connection changed mid-load
        }
    }

    // ── Album detail ──

    [RelayCommand]
    private async Task OpenServerAlbum(ServerAlbumTileViewModel tile)
    {
        var generation = ++_openAlbumGeneration;
        OpenAlbum = tile;
        OpenAlbumMeta = BuildAlbumMeta(tile.Album);
        AlbumTracks.Clear();
        ShowAlbumDetail = true;
        ShowAlbumGrid = false;
        ShowSearchResults = false;
        IsLoading = true;
        try
        {
            var tracks = await FetchAlbumTracksAsync(tile);
            if (generation != _openAlbumGeneration) return;
            foreach (var track in tracks)
                AlbumTracks.Add(track);
            if (AlbumTracks.Count == 0)
            {
                StatusText = Noctis.Localization.Loc.T("Server.AlbumLoadFailed");
                DebugLog.Write("Server", $"Album load returned no tracks: {tile.Name}");
            }
        }
        finally
        {
            if (generation == _openAlbumGeneration)
                IsLoading = false;
        }
    }

    [RelayCommand]
    private void CloseServerAlbum()
    {
        _openAlbumGeneration++;
        OpenAlbum = null;
        AlbumTracks.Clear();
        IsLoading = false;
        StatusText = "";
        UpdateSurfaces();
    }

    // ── Playback ──

    [RelayCommand]
    private async Task PlayServerAlbum(ServerAlbumTileViewModel tile)
    {
        var tracks = await FetchAlbumTracksAsync(tile);
        if (tracks.Count > 0)
            _player.WithSource(PlaySource.Section(PlaySourceKind.Server)).PlayAll(tracks.ToList());
    }

    [RelayCommand]
    private void PlayAlbumFromDetail()
    {
        if (AlbumTracks.Count > 0)
            _player.WithSource(PlaySource.Section(PlaySourceKind.Server)).PlayAll(AlbumTracks.ToList());
    }

    [RelayCommand]
    private void PlayAlbumTrack(Track track)
    {
        var index = AlbumTracks.IndexOf(track);
        if (index >= 0)
            _player.WithSource(PlaySource.Section(PlaySourceKind.Server)).ReplaceQueueAndPlay(AlbumTracks.ToList(), index);
    }

    [RelayCommand]
    private void PlaySearchTrack(Track track)
    {
        var index = SearchTracks.IndexOf(track);
        if (index >= 0)
            _player.WithSource(PlaySource.Section(PlaySourceKind.Server)).ReplaceQueueAndPlay(SearchTracks.ToList(), index);
    }

    [RelayCommand]
    private void PlayTrackNext(Track track) => _player.AddNext(track);

    [RelayCommand]
    private void AddTrackToQueue(Track track) => _player.AddToQueue(track);

    // ── Internals ──

    private async Task<IReadOnlyList<Track>> FetchAlbumTracksAsync(ServerAlbumTileViewModel tile)
    {
        try
        {
            return await _mediaServer.GetAlbumTracksAsync(tile.Album, _lifetimeCts.Token);
        }
        catch (OperationCanceledException)
        {
            return Array.Empty<Track>();
        }
    }

    private string BuildAlbumMeta(ServerAlbum album)
    {
        var parts = new List<string>(4);
        if (!string.IsNullOrWhiteSpace(album.Artist)) parts.Add(album.Artist);
        if (album.Year > 0) parts.Add(album.Year.ToString());
        if (album.SongCount > 0) parts.Add(Noctis.Localization.Loc.Plural("Count.Songs", album.SongCount));
        // With several servers, say which one the album plays from.
        if (_mediaServer.Connections.Count > 1 && !string.IsNullOrWhiteSpace(album.ServerName)) parts.Add(album.ServerName);
        return string.Join(" · ", parts);
    }

    private void SyncConfiguredState()
    {
        var connections = _mediaServer.Connections;
        IsConfigured = _mediaServer.IsConfigured;
        // Every server whose albums are in the grid (GitHub #268), in the app's language.
        ServerLabel = connections.Count == 0
            ? string.Empty
            : Noctis.Localization.Loc.T("Settings.ConnectedTo",
                string.Join(", ", connections.Select(MediaServerService.DisplayName)));
    }

    private void ResetForConnectionChange()
    {
        // Cancel every in-flight browse/artwork call for the old connection.
        _lifetimeCts.Cancel();
        _lifetimeCts.Dispose();
        _lifetimeCts = new CancellationTokenSource();

        _needsReload = true;
        _albumOffset = 0;
        _mergedMode = false;
        _mergedAlbums = Array.Empty<ServerAlbum>();
        _albumLoadGeneration++;
        _searchGeneration++;
        _openAlbumGeneration++;
        _searchQuery = "";
        Albums.Clear();
        RebuildAlbumRows();
        SearchAlbums.Clear();
        SearchTracks.Clear();
        AlbumTracks.Clear();
        OpenAlbum = null;
        HasMoreAlbums = false;
        IsLoading = false;
        IsLoadingMore = false;
        StatusText = "";
        SearchCameUpEmpty = false;
        SyncConfiguredState();
        UpdateSurfaces();
    }

    private void UpdateSurfaces()
    {
        var searching = _searchQuery.Length > 0;
        ShowSearchResults = IsConfigured && searching;
        ShowAlbumDetail = IsConfigured && !searching && OpenAlbum != null;
        ShowAlbumGrid = IsConfigured && !searching && OpenAlbum == null;
    }
}
