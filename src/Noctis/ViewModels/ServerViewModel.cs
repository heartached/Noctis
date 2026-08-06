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
/// The "Server" section: browse the connected media server's albums, open an
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
        Albums.Clear();
        RebuildAlbumRows();
        HasMoreAlbums = false;
        StatusText = "";
        IsLoading = true;
        try
        {
            await LoadAlbumPageAsync();
            if (Albums.Count == 0)
            {
                StatusText = "No albums yet. The music library on this server is empty — or it couldn't be reached.";
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
            await LoadAlbumPageAsync();
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
                StatusText = "Couldn't load this album from the server.";
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
            _player.ReplaceQueueAndPlay(tracks.ToList(), 0);
    }

    [RelayCommand]
    private void PlayAlbumFromDetail()
    {
        if (AlbumTracks.Count > 0)
            _player.ReplaceQueueAndPlay(AlbumTracks.ToList(), 0);
    }

    [RelayCommand]
    private void PlayAlbumTrack(Track track)
    {
        var index = AlbumTracks.IndexOf(track);
        if (index >= 0)
            _player.ReplaceQueueAndPlay(AlbumTracks.ToList(), index);
    }

    [RelayCommand]
    private void PlaySearchTrack(Track track)
    {
        var index = SearchTracks.IndexOf(track);
        if (index >= 0)
            _player.ReplaceQueueAndPlay(SearchTracks.ToList(), index);
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

    private static string BuildAlbumMeta(ServerAlbum album)
    {
        var parts = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(album.Artist)) parts.Add(album.Artist);
        if (album.Year > 0) parts.Add(album.Year.ToString());
        if (album.SongCount > 0) parts.Add(album.SongCount == 1 ? "1 song" : $"{album.SongCount} songs");
        return string.Join(" · ", parts);
    }

    private void SyncConfiguredState()
    {
        var connection = _mediaServer.ActiveConnection;
        IsConfigured = connection != null;
        ServerLabel = connection == null
            ? string.Empty
            : $"Connected to {connection.Name} at {connection.BaseUriOrPath}";
    }

    private void ResetForConnectionChange()
    {
        // Cancel every in-flight browse/artwork call for the old connection.
        _lifetimeCts.Cancel();
        _lifetimeCts.Dispose();
        _lifetimeCts = new CancellationTokenSource();

        _needsReload = true;
        _albumOffset = 0;
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
