using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services;

namespace Noctis.ViewModels;

/// <summary>
/// ViewModel for the Favorites tab — shows favorited tracks and fully-favorited albums.
/// </summary>
public partial class FavoritesViewModel : ViewModelBase, ISearchable
{
    private readonly PlayerViewModel _player;
    private readonly ILibraryService _library;
    private readonly IPersistenceService _persistence;
    private readonly SidebarViewModel _sidebar;

    private const int ColumnsPerRow = 5;
    private bool _isDirty = true;
    private string _currentFilter = string.Empty;
    private List<FavoriteItem> _allFavoriteItems = new();

    /// <summary>Saved scroll offset for restoring position after navigation.</summary>
    public double SavedScrollOffset { get; set; }

    [ObservableProperty] private double _tileArtworkSize = 180;
    [ObservableProperty] private string _searchText = string.Empty;

    /// <summary>FavoriteItems currently Ctrl-selected in the view. Set by code-behind.</summary>
    public List<FavoriteItem> CtrlSelectedItems { get; set; } = new();

    /// <summary>Favorite items (albums where all tracks are favorited, plus individual tracks).</summary>
    public BulkObservableCollection<FavoriteItem> FavoriteItems { get; } = new();

    /// <summary>Favorite items grouped into rows for the virtualized grid.</summary>
    public BulkObservableCollection<FavoriteItemRow> FavoriteItemRows { get; } = new();

    /// <summary>Exposes the sidebar's playlists for the Add to Playlist submenu.</summary>
    public ObservableCollection<Playlist> Playlists => _sidebar.Playlists;

    /// <summary>Fires when the user wants to view an album from a track.</summary>
    public event EventHandler<Track>? ViewAlbumRequested;

    /// <summary>Fires when the user wants to open an album detail view.</summary>
    public event EventHandler<Album>? AlbumOpened;

    public FavoritesViewModel(PlayerViewModel player, ILibraryService library, IPersistenceService persistence, SidebarViewModel sidebar)
    {
        _player = player;
        _library = library;
        _persistence = persistence;
        _sidebar = sidebar;

        // Dispatch to UI thread since scan fires LibraryUpdated from a background thread
        _library.LibraryUpdated += (_, _) => { _isDirty = true; Dispatcher.UIThread.Post(Refresh); };
        _library.FavoritesChanged += (_, _) => { _isDirty = true; Dispatcher.UIThread.Post(Refresh); };
    }

    /// <summary>Forces the next Refresh() call to rebuild even if data hasn't changed.</summary>
    public void MarkDirty() => _isDirty = true;

    /// <summary>Refreshes the Favorites tab content with latest data.</summary>
    public void Refresh()
    {
        if (!_isDirty && FavoriteItems.Count > 0)
            return;
        _isDirty = false;

        // Sync rebuild: navigation sets CurrentView immediately after this
        // returns, so the bound list must be current on first paint to avoid
        // flashing previous content for a frame.
        var favTracks = _library.Tracks
            .Where(t => t.IsFavorite)
            .ToList();

        var items = new List<(FavoriteItem item, DateTime sortKey)>();

        // Most recent favorite event for a track. Falls back to DateAdded so
        // pre-existing favorites (no timestamp yet) keep a stable order.
        static DateTime FavoriteSortKey(Track t) => t.FavoritedAt ?? t.DateAdded;

        // Group favorite tracks by album
        var grouped = favTracks.GroupBy(t => t.AlbumId);
        foreach (var group in grouped)
        {
            var album = _library.GetAlbumById(group.Key);
            if (album != null && album.Tracks.Count > 1 && album.IsAllTracksFavorite)
            {
                // All tracks in the album are favorites → show as one album card.
                // Use the most recent track favorite as the album's sort key.
                var key = album.Tracks.Max(FavoriteSortKey);
                items.Add((new FavoriteItem { Album = album }, key));
            }
            else
            {
                // Individual tracks
                foreach (var track in group)
                    items.Add((new FavoriteItem { Track = track }, FavoriteSortKey(track)));
            }
        }

        // Newest favorites first.
        items.Sort((a, b) => b.sortKey.CompareTo(a.sortKey));
        _allFavoriteItems = items.Select(x => x.item).ToList();
        ApplyFilter(_currentFilter);
    }

    public void ApplyFilter(string query)
    {
        if (SearchText != query)
            SearchText = query;

        _currentFilter = query;
        var filteredItems = _allFavoriteItems.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(query))
        {
            var q = query.Trim();
            filteredItems = filteredItems.Where(item =>
                MatchesSearch(item.Title, q) ||
                MatchesSearch(item.Subtitle, q));
        }

        var visibleItems = filteredItems.ToList();
        FavoriteItems.ReplaceAll(visibleItems);
        FavoriteItemRows.ReplaceAll(BuildRows(visibleItems));
    }

    private static List<FavoriteItemRow> BuildRows(IReadOnlyList<FavoriteItem> items)
    {
        var rows = new List<FavoriteItemRow>();
        for (var i = 0; i < items.Count; i += ColumnsPerRow)
        {
            rows.Add(new FavoriteItemRow
            {
                Items = items.Skip(i).Take(ColumnsPerRow).ToList()
            });
        }

        return rows;
    }

    private static bool MatchesSearch(string? source, string query)
    {
        return !string.IsNullOrWhiteSpace(source) &&
               source.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    // ── Page-level commands ────────────────────────────────────

    [RelayCommand]
    private void PlayAll()
    {
        var allTracks = GetAllFavoriteTracks();
        if (allTracks.Count == 0) return;
        _player.ReplaceQueueAndPlay(allTracks, 0);
    }

    [RelayCommand]
    private void ShuffleAll()
    {
        var allTracks = GetAllFavoriteTracks();
        if (allTracks.Count == 0) return;
        var shuffled = allTracks.OrderBy(_ => Random.Shared.Next()).ToList();
        _player.ReplaceQueueAndPlay(shuffled, 0);
    }

    // ── Track commands ──────────────────────────────────────────

    [RelayCommand]
    private void PlayTrack(Track track)
    {
        // Build a flat list of all favorite tracks for queue
        var allTracks = GetAllFavoriteTracks();
        var idx = allTracks.FindIndex(t => t.Id == track.Id);
        if (idx < 0) idx = 0;
        _player.ReplaceQueueAndPlay(allTracks, idx);
    }

    [RelayCommand]
    private void ShuffleTrack(Track track)
    {
        var allTracks = GetAllFavoriteTracks();
        var shuffled = allTracks.OrderBy(_ => Random.Shared.Next()).ToList();
        // Put selected track first
        var idx = shuffled.FindIndex(t => t.Id == track.Id);
        if (idx > 0) { var t = shuffled[idx]; shuffled.RemoveAt(idx); shuffled.Insert(0, t); }
        _player.ReplaceQueueAndPlay(shuffled, 0);
    }

    [RelayCommand]
    private void PlayNextTrack(Track track) => _player.AddNext(track);

    [RelayCommand]
    private void AddTrackToQueue(Track track) => _player.AddToQueue(track);

    [RelayCommand]
    private async Task ToggleFavorite(Track track)
    {
        track.IsFavorite = !track.IsFavorite;
        await _library.SaveAsync();
        _library.NotifyFavoritesChanged();
        Refresh();
    }

    [RelayCommand]
    private void ShowTrackInExplorer(Track track)
    {
        if (track == null || !File.Exists(track.FilePath)) return;
        Helpers.PlatformHelper.ShowInFileManager(track.FilePath);
    }

    [RelayCommand]
    private void ViewAlbumFromTrack(Track track)
    {
        ViewAlbumRequested?.Invoke(this, track);
    }

    [RelayCommand]
    private async Task AddTrackToNewPlaylist(Track track)
    {
        if (track == null) return;
        await _sidebar.CreatePlaylistWithTracksAsync(new List<Track> { track });
    }

    [RelayCommand]
    private async Task AddTrackToExistingPlaylist(object[] parameters)
    {
        if (parameters == null || parameters.Length != 2) return;
        if (parameters[0] is not Track track || parameters[1] is not Playlist playlist) return;
        await _sidebar.AddTracksToPlaylist(playlist.Id, new List<Track> { track });
    }

    [RelayCommand]
    private async Task OpenTrackMetadata(Track track)
    {
        if (track == null) return;
        await MetadataHelper.OpenMetadataWindow(track);
    }

    [RelayCommand]
    private async Task RemoveTrackFromLibrary(Track track)
    {
        if (track == null) return;
        if (!await Views.ConfirmationDialog.ShowAsync("Do you want to remove the selected item from your Library?"))
            return;
        await _library.RemoveTrackAsync(track.Id);
    }

    // ── Album commands ──────────────────────────────────────────

    [RelayCommand]
    private void PlayAlbum(Album album)
    {
        if (album?.Tracks == null || album.Tracks.Count == 0) return;
        _player.ReplaceQueueAndPlay(album.Tracks, 0);
    }

    [RelayCommand]
    private void ShuffleAlbum(Album album)
    {
        if (album?.Tracks == null || album.Tracks.Count == 0) return;
        var shuffled = album.Tracks.OrderBy(_ => Random.Shared.Next()).ToList();
        _player.ReplaceQueueAndPlay(shuffled, 0);
    }

    [RelayCommand]
    private void PlayNextAlbum(Album album)
    {
        if (album?.Tracks == null || album.Tracks.Count == 0) return;
        var tracks = album.Tracks.ToList();
        for (int i = tracks.Count - 1; i >= 0; i--)
            _player.AddNext(tracks[i]);
    }

    [RelayCommand]
    private void AddAlbumToQueue(Album album)
    {
        if (album?.Tracks == null || album.Tracks.Count == 0) return;
        _player.AddRangeToQueue(album.Tracks.ToList());
    }

    [RelayCommand]
    private async Task ToggleAlbumFavorites(Album album)
    {
        if (album?.Tracks == null || album.Tracks.Count == 0) return;
        var newState = !album.IsAllTracksFavorite;
        foreach (var track in album.Tracks)
            track.IsFavorite = newState;
        await _library.SaveAsync();
        _library.NotifyFavoritesChanged();
        Refresh();
    }

    [RelayCommand]
    private async Task AddAlbumToNewPlaylist(Album album)
    {
        if (album?.Tracks == null || album.Tracks.Count == 0) return;
        await _sidebar.CreatePlaylistWithTracksAsync(album.Tracks.ToList());
    }

    [RelayCommand]
    private async Task AddAlbumToExistingPlaylist(object[] parameters)
    {
        if (parameters == null || parameters.Length != 2) return;
        if (parameters[0] is not Album album || parameters[1] is not Playlist playlist) return;
        if (album.Tracks == null || album.Tracks.Count == 0) return;
        await _sidebar.AddTracksToPlaylist(playlist.Id, album.Tracks.ToList());
    }

    [RelayCommand]
    private async Task OpenAlbumMetadata(Album album)
    {
        if (album?.Tracks == null || album.Tracks.Count == 0) return;
        await MetadataHelper.OpenMetadataWindow(album.Tracks[0]);
    }

    [RelayCommand]
    private void OpenAlbum(Album album)
    {
        AlbumOpened?.Invoke(this, album);
    }

    [RelayCommand]
    private void ShowAlbumInExplorer(Album album)
    {
        if (album?.Tracks == null || album.Tracks.Count == 0) return;
        var filePath = album.Tracks[0].FilePath;
        if (!File.Exists(filePath)) return;
        Helpers.PlatformHelper.ShowInFileManager(filePath);
    }

    [RelayCommand]
    private async Task RemoveAlbumFromLibrary(Album album)
    {
        if (album?.Tracks == null || album.Tracks.Count == 0) return;
        if (!await Views.ConfirmationDialog.ShowAsync("Do you want to remove the selected item from your Library?"))
            return;
        var trackIds = album.Tracks.Select(t => t.Id).ToList();
        await _library.RemoveTracksAsync(trackIds);
    }

    // ── Unified FavoriteItem commands (used by context menu) ────

    [RelayCommand]
    private void PlayItem(FavoriteItem item)
    {
        if (item.IsAlbum) PlayAlbum(item.Album!);
        else PlayTrack(item.Track!);
    }

    [RelayCommand]
    private void ShuffleItem(FavoriteItem item)
    {
        if (item.IsAlbum) ShuffleAlbum(item.Album!);
        else ShuffleTrack(item.Track!);
    }

    [RelayCommand]
    private void PlayNextItem(FavoriteItem item)
    {
        if (item.IsAlbum) PlayNextAlbum(item.Album!);
        else PlayNextTrack(item.Track!);
    }

    [RelayCommand]
    private void AddItemToQueue(FavoriteItem item)
    {
        if (item.IsAlbum) AddAlbumToQueue(item.Album!);
        else AddTrackToQueue(item.Track!);
    }

    [RelayCommand]
    private async Task AddItemToNewPlaylist(FavoriteItem item)
    {
        var items = CtrlSelectedItems.Count > 0 ? CtrlSelectedItems : new List<FavoriteItem> { item };
        var tracks = new List<Track>();
        foreach (var fi in items)
        {
            if (fi.IsAlbum && fi.Album?.Tracks != null)
                tracks.AddRange(fi.Album.Tracks);
            else if (fi.Track != null)
                tracks.Add(fi.Track);
        }
        if (tracks.Count > 0)
            await _sidebar.CreatePlaylistWithTracksAsync(tracks);
        CtrlSelectedItems.Clear();
    }

    [RelayCommand]
    private async Task AddItemToExistingPlaylist(object[] parameters)
    {
        if (parameters == null || parameters.Length != 2) return;
        if (parameters[0] is not FavoriteItem item || parameters[1] is not Playlist playlist) return;
        var items = CtrlSelectedItems.Count > 0 ? CtrlSelectedItems : new List<FavoriteItem> { item };
        var tracks = new List<Track>();
        foreach (var fi in items)
        {
            if (fi.IsAlbum && fi.Album?.Tracks != null)
                tracks.AddRange(fi.Album.Tracks);
            else if (fi.Track != null)
                tracks.Add(fi.Track);
        }
        if (tracks.Count > 0)
            await _sidebar.AddTracksToPlaylist(playlist.Id, tracks);
        CtrlSelectedItems.Clear();
    }

    [RelayCommand]
    private async Task RemoveItemFavorite(FavoriteItem item)
    {
        var items = CtrlSelectedItems.Count > 0 ? CtrlSelectedItems : new List<FavoriteItem> { item };
        foreach (var fi in items)
        {
            if (fi.IsAlbum && fi.Album?.Tracks != null)
            {
                var newState = !fi.Album.IsAllTracksFavorite;
                foreach (var track in fi.Album.Tracks)
                    track.IsFavorite = newState;
            }
            else if (fi.Track != null)
                fi.Track.IsFavorite = !fi.Track.IsFavorite;
        }
        await _library.SaveAsync();
        _library.NotifyFavoritesChanged();
        Refresh();
        CtrlSelectedItems.Clear();
    }

    [RelayCommand]
    private async Task OpenItemMetadata(FavoriteItem item)
    {
        if (item.IsAlbum) await OpenAlbumMetadata(item.Album!);
        else await OpenTrackMetadata(item.Track!);
    }

    [RelayCommand]
    private void SearchItemLyrics(FavoriteItem item)
    {
        var track = item.IsAlbum ? item.Album!.Tracks.FirstOrDefault() : item.Track;
        if (track != null) _searchLyricsAction?.Invoke(track);
    }

    [RelayCommand]
    private void ViewItemAlbum(FavoriteItem item)
    {
        if (item.IsAlbum) OpenAlbum(item.Album!);
        else ViewAlbumFromTrack(item.Track!);
    }

    [RelayCommand]
    private void ShowItemInExplorer(FavoriteItem item)
    {
        if (item.IsAlbum) ShowAlbumInExplorer(item.Album!);
        else ShowTrackInExplorer(item.Track!);
    }

    [RelayCommand]
    private async Task RemoveItemFromLibrary(FavoriteItem item)
    {
        var items = CtrlSelectedItems.Count > 0 ? CtrlSelectedItems : new List<FavoriteItem> { item };
        if (!await Views.ConfirmationDialog.ShowAsync("Do you want to remove the selected item from your Library?"))
            return;
        var trackIds = new List<Guid>();
        foreach (var fi in items)
        {
            if (fi.IsAlbum && fi.Album?.Tracks != null)
                trackIds.AddRange(fi.Album.Tracks.Select(t => t.Id));
            else if (fi.Track != null)
                trackIds.Add(fi.Track.Id);
        }
        if (trackIds.Count > 0)
            await _library.RemoveTracksAsync(trackIds);
        CtrlSelectedItems.Clear();
    }

    // ── Shared helpers ──────────────────────────────────────────

    private Action<Track>? _searchLyricsAction;
    public void SetSearchLyricsAction(Action<Track> action) => _searchLyricsAction = action;

    [RelayCommand]
    private void SearchLyrics(Track track) => _searchLyricsAction?.Invoke(track);

    private Action<string>? _viewArtistAction;
    public void SetViewArtistAction(Action<string> action) => _viewArtistAction = action;

    [RelayCommand]
    private void ViewArtist(string artistName)
    {
        if (!string.IsNullOrWhiteSpace(artistName))
            _viewArtistAction?.Invoke(artistName);
    }

    /// <summary>Flattens all favorite items into a single track list (expanding albums).</summary>
    private List<Track> GetAllFavoriteTracks()
    {
        var tracks = new List<Track>();
        foreach (var item in FavoriteItems)
        {
            if (item.IsAlbum)
                tracks.AddRange(item.Album!.Tracks);
            else
                tracks.Add(item.Track!);
        }
        return tracks;
    }
}
