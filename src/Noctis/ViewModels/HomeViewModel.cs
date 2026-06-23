using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services;

namespace Noctis.ViewModels;

/// <summary>
/// ViewModel for the Home tab — shows top songs by play count and recently played albums.
/// </summary>
public partial class HomeViewModel : ViewModelBase, IDisposable
{
    private const int MaxTopArtists = 6;
    private readonly PlayerViewModel _player;
    private readonly ILibraryService _library;
    private readonly SidebarViewModel _sidebar;
    private readonly ArtistImageService? _artistImages;
    private readonly IPlayHistoryService? _playHistory;
    private readonly DispatcherTimer _refreshDebounce;
    private readonly EventHandler _libraryUpdatedHandler;
    private readonly EventHandler _favoritesChangedHandler;
    private bool _isDirty = true;

    /// <summary>Saved scroll offset for restoring position after navigation.</summary>
    public double SavedScrollOffset { get; set; }

    /// <summary>Albums currently Ctrl-selected in the view. Set by code-behind.</summary>
    public List<Album> CtrlSelectedAlbums { get; set; } = new();

    /// <summary>Top songs sorted by play count descending.</summary>
    public BulkObservableCollection<Track> TopSongs { get; } = new();

    /// <summary>Recently played albums (grouped from playback history).</summary>
    public BulkObservableCollection<Album> RecentlyPlayedAlbums { get; } = new();

    /// <summary>Top artists by total play count across all their tracks.</summary>
    public BulkObservableCollection<Artist> TopArtists { get; } = new();

    // ── Time-aware rows (local play history) ──

    /// <summary>Tracks the user keeps playing around this time of day.</summary>
    public BulkObservableCollection<Track> TimeRotationTracks { get; } = new();

    /// <summary>Most-played tracks of the last two weeks.</summary>
    public BulkObservableCollection<Track> HeavyRotationTracks { get; } = new();

    /// <summary>Tracks recently played again after a long break.</summary>
    public BulkObservableCollection<Track> RediscoveredTracks { get; } = new();

    /// <summary>Title of the time-of-day row ("Morning rotation" etc.).</summary>
    [ObservableProperty] private string _timeRotationTitle = HomeRowsBuilder.DaypartLabel(DateTime.Now.Hour);

    [ObservableProperty] private string _greeting = GetGreeting();

    /// <summary>Fires when the user wants to open an album's detail view.</summary>
    public event EventHandler<Album>? AlbumOpened;

    /// <summary>Exposes the sidebar's playlists for the Add to Playlist submenu.</summary>
    public ObservableCollection<Playlist> Playlists => _sidebar.Playlists;

    public HomeViewModel(PlayerViewModel player, ILibraryService library, SidebarViewModel sidebar,
        ArtistImageService? artistImages = null, IPlayHistoryService? playHistory = null)
    {
        _player = player;
        _library = library;
        _sidebar = sidebar;
        _artistImages = artistImages;
        _playHistory = playHistory;

        // Subscribe to track changes for real-time updates
        _player.TrackStarted += OnTrackStarted;

        // Subscribe to library changes with debounce to avoid flooding UI thread
        _refreshDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _refreshDebounce.Tick += (_, _) =>
        {
            _refreshDebounce.Stop();
            Refresh();
        };
        _libraryUpdatedHandler = (_, _) => { _isDirty = true; Dispatcher.UIThread.Post(() =>
        {
            _refreshDebounce.Stop();
            _refreshDebounce.Start();
        }); };
        _favoritesChangedHandler = (_, _) => { _isDirty = true; Dispatcher.UIThread.Post(Refresh); };
        _library.LibraryUpdated += _libraryUpdatedHandler;
        _library.FavoritesChanged += _favoritesChangedHandler;
    }

    private void OnTrackStarted(object? sender, Track track)
    {
        Dispatcher.UIThread.Post(() =>
        {
            // Update greeting in case time changed
            Greeting = GetGreeting();

            var album = _library.GetAlbumById(track.AlbumId);
            if (album != null)
            {
                var existing = RecentlyPlayedAlbums.FirstOrDefault(a => a.Id == album.Id);
                if (existing != null)
                    RecentlyPlayedAlbums.Remove(existing);

                RecentlyPlayedAlbums.Insert(0, album);

                while (RecentlyPlayedAlbums.Count > 10)
                    RecentlyPlayedAlbums.RemoveAt(RecentlyPlayedAlbums.Count - 1);
            }
        });
    }

    /// <summary>Forces the next Refresh() call to rebuild even if data hasn't changed.</summary>
    public void MarkDirty() => _isDirty = true;

    /// <summary>Refreshes the Home tab content with latest data.</summary>
    public async void Refresh()
    {
        if (!_isDirty && TopSongs.Count > 0)
        {
            Greeting = GetGreeting();
            // Time-aware rows depend on the clock and the play log, both of which
            // move without dirtying the library — rebuild them on every visit.
            _ = RefreshTimeAwareRowsAsync();
            return;
        }
        _isDirty = false;

        try
        {
            Greeting = GetGreeting();

            // Top songs: tracks with highest play count
            var allTracks = _library.Tracks;
            if (allTracks.Count > 0)
            {
                var top = await Task.Run(() =>
                    allTracks
                        .Where(t => t.PlayCount > 0)
                        .OrderByDescending(t => t.PlayCount)
                        .Take(6)
                        .ToList());
                TopSongs.ReplaceAll(top);
            }
            else
            {
                TopSongs.ReplaceAll(Array.Empty<Track>());
            }

            // Recently played albums: O(1) lookups via GetAlbumById
            var recentAlbums = _player.History
                .Take(50)
                .Select(t => t.AlbumId)
                .Distinct()
                .Take(10)
                .Select(id => _library.GetAlbumById(id))
                .OfType<Album>()
                .ToList();
            RecentlyPlayedAlbums.ReplaceAll(recentAlbums);

            // Top Artists: aggregate play count by artist name (using album-artist
            // grouping that the library already maintains), drop the "Unknown Artist"
            // sentinel, and keep the top 6 for the home row.
            var topArtists = await Task.Run(() =>
            {
                var plays = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                foreach (var t in allTracks)
                {
                    if (t.PlayCount <= 0) continue;
                    var name = t.PrimaryArtist;
                    if (string.IsNullOrWhiteSpace(name) ||
                        string.Equals(name, "Unknown Artist", StringComparison.OrdinalIgnoreCase))
                        continue;
                    plays.TryGetValue(name, out var c);
                    plays[name] = c + t.PlayCount;
                }

                if (plays.Count == 0) return new List<Artist>();

                // Resolve the play-count ranking back to actual Artist rows so the row
                // benefits from the existing image-cache flow.
                var byName = _library.Artists.ToDictionary(a => a.Name, a => a, StringComparer.OrdinalIgnoreCase);
                return plays
                    .OrderByDescending(kv => kv.Value)
                    .Take(MaxTopArtists)
                    .Select(kv => byName.TryGetValue(kv.Key, out var artist)
                        ? artist
                        : new Artist { Name = kv.Key, Id = ComputeArtistId(kv.Key) })
                    .ToList();
            });
            ReplaceTopArtistsIfChanged(topArtists);

            // Kick a background image-fetch for any artist that doesn't have a cached
            // picture yet. Updates flow back through Artist.ImagePath, which the row's
            // CachedImage binding watches.
            if (_artistImages != null && topArtists.Count > 0)
                _ = _artistImages.FetchAndCacheAsync(topArtists, (artist, _) =>
                {
                    Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(TopArtists)));
                });

            await RefreshTimeAwareRowsAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[HomeVM] Refresh failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Rebuilds the three play-history rows (time-of-day, heavy rotation,
    /// rediscovered). Ranking runs off the UI thread; track resolution drops
    /// IDs that no longer exist in the library.
    /// </summary>
    private async Task RefreshTimeAwareRowsAsync()
    {
        if (_playHistory == null) return;

        var now = DateTime.Now;
        TimeRotationTitle = HomeRowsBuilder.DaypartLabel(now.Hour);

        var events = _playHistory.Events;

        // Dedupe rows against Most Listened To and each other so every row
        // shows tracks the user hasn't already seen further up the page.
        var exclude = TopSongs.Select(t => t.Id).ToHashSet();
        var (timeIds, heavyIds, rediscoveredIds) = await Task.Run(() =>
        {
            var heavy = HomeRowsBuilder.BuildHeavyRotation(events, now, exclude: exclude);
            exclude.UnionWith(heavy);
            var time = HomeRowsBuilder.BuildTimeOfDayRotation(events, now, exclude: exclude);
            exclude.UnionWith(time);
            var rediscovered = HomeRowsBuilder.BuildRediscovered(events, now, exclude: exclude);
            return (time, heavy, rediscovered);
        });

        // Hide a row entirely when it has too few tracks to earn its header.
        List<Track> Resolve(List<Guid> ids)
        {
            var tracks = ids.Select(_library.GetTrackById).OfType<Track>().ToList();
            return tracks.Count >= HomeRowsBuilder.MinRowItems ? tracks : new List<Track>();
        }

        TimeRotationTracks.ReplaceAll(Resolve(timeIds));
        HeavyRotationTracks.ReplaceAll(Resolve(heavyIds));
        RediscoveredTracks.ReplaceAll(Resolve(rediscoveredIds));
    }

    /// <summary>Plays a track from one of the time-aware rows, queueing the rest of its row.</summary>
    [RelayCommand]
    private void PlayTimeRotation(Track track) => PlayFromRow(TimeRotationTracks, track);

    [RelayCommand]
    private void PlayHeavyRotation(Track track) => PlayFromRow(HeavyRotationTracks, track);

    [RelayCommand]
    private void PlayRediscovered(Track track) => PlayFromRow(RediscoveredTracks, track);

    private void PlayFromRow(BulkObservableCollection<Track> row, Track track)
    {
        var tracks = row.ToList();
        var index = tracks.IndexOf(track);
        if (index < 0) index = 0;
        if (tracks.Count == 0) return;
        _player.ReplaceQueueAndPlay(tracks, index);
    }

    private static string GetGreeting()
    {
        var hour = DateTime.Now.Hour;
        return hour switch
        {
            >= 5 and < 12 => "Good morning",
            >= 12 and < 17 => "Good afternoon",
            >= 17 and < 21 => "Good evening",
            _ => "Good night"
        };
    }

    private void ReplaceTopArtistsIfChanged(IReadOnlyList<Artist> artists)
    {
        if (TopArtists.Count == artists.Count &&
            TopArtists.Zip(artists).All(pair => pair.First.Id == pair.Second.Id))
            return;

        TopArtists.ReplaceAll(artists);
    }

    private static Guid ComputeArtistId(string artistName)
    {
        var hash = System.Security.Cryptography.MD5.HashData(
            System.Text.Encoding.UTF8.GetBytes(artistName.Trim().ToLowerInvariant()));
        return new Guid(hash);
    }

    // ── Top Song commands ──

    [RelayCommand]
    private void PlayTopSong(Track track)
    {
        var tracks = TopSongs.ToList();
        var index = tracks.IndexOf(track);
        if (index < 0) index = 0;
        _player.ReplaceQueueAndPlay(tracks, index);
    }

    [RelayCommand]
    private void PlayNext(Track track) => _player.AddNext(track);

    [RelayCommand]
    private void AddToQueue(Track track) => _player.AddToQueue(track);

    [RelayCommand]
    private async Task AddTrackToNewPlaylist(Track track)
    {
        await _sidebar.CreatePlaylistWithTrackAsync(track);
    }

    [RelayCommand]
    private async Task AddTrackToExistingPlaylist(object[] parameters)
    {
        if (parameters == null || parameters.Length != 2) return;
        if (parameters[0] is not Track track || parameters[1] is not Playlist playlist) return;
        await _sidebar.AddTracksToPlaylist(playlist.Id, new[] { track });
    }

    [RelayCommand]
    private async Task ToggleTrackFavorite(Track track)
    {
        track.IsFavorite = !track.IsFavorite;
        await _library.SaveAsync();
        _library.NotifyFavoritesChanged();
    }

    [RelayCommand]
    private void ShowInExplorerTrack(Track track)
    {
        if (track == null || !File.Exists(track.FilePath)) return;
        Helpers.PlatformHelper.ShowInFileManager(track.FilePath);
    }

    [RelayCommand]
    private async Task OpenTrackMetadata(Track track)
    {
        await MetadataHelper.OpenMetadataWindow(track);
    }

    [RelayCommand]
    private async Task ConvertTrack(Track track)
        => await MetadataHelper.OpenAudioConverterDialog(new List<Track> { track });

    [RelayCommand]
    private async Task ScanTrackReplayGain(Track track)
        => await MetadataHelper.OpenReplayGainScannerDialog(new List<Track> { track });

    [RelayCommand]
    private void SearchLyricsTrack(Track track)
    {
        _searchLyricsAction?.Invoke(track);
    }

    [RelayCommand]
    private void ShuffleTopSongs() => ShuffleRow(TopSongs);

    [RelayCommand]
    private void ShuffleTimeRotation() => ShuffleRow(TimeRotationTracks);

    [RelayCommand]
    private void ShuffleHeavyRotation() => ShuffleRow(HeavyRotationTracks);

    [RelayCommand]
    private void ShuffleRediscovered() => ShuffleRow(RediscoveredTracks);

    private void ShuffleRow(IEnumerable<Track> row)
    {
        var tracks = row.ToList();
        if (tracks.Count == 0) return;
        var shuffled = Helpers.ShuffleHelper.WeightedShuffle(tracks);
        _player.ReplaceQueueAndPlay(shuffled, 0);
    }

    [RelayCommand]
    private void StartRadio(Track track) => _player.StartRadioCommand.Execute(track);

    [RelayCommand]
    private void SnoozeForMonth(Track track) => _player.SnoozeForMonthCommand.Execute(track);

    /// <summary>Fires when the user wants to view a track's album.</summary>
    public event EventHandler<Track>? ViewAlbumRequested;

    [RelayCommand]
    private void ViewAlbumFromTrack(Track track)
    {
        ViewAlbumRequested?.Invoke(this, track);
    }

    [RelayCommand]
    private async Task RemoveTrackFromLibrary(Track track)
    {
        if (!await Views.ConfirmationDialog.ShowAsync("Do you want to remove the selected item from your Library?"))
            return;
        await _library.RemoveTrackAsync(track.Id);
    }

    // ── Album commands ──

    [RelayCommand]
    private void OpenAlbum(Album album)
    {
        AlbumOpened?.Invoke(this, album);
    }

    [RelayCommand]
    private void PlayAlbum(Album album)
    {
        if (album == null || album.Tracks == null || album.Tracks.Count == 0) return;
        _player.ReplaceQueueAndPlay(album.Tracks, 0);
    }

    [RelayCommand]
    private void ShuffleAlbum(Album album)
    {
        if (album == null || album.Tracks == null || album.Tracks.Count == 0) return;
        var shuffled = Helpers.ShuffleHelper.WeightedShuffle(album.Tracks);
        _player.ReplaceQueueAndPlay(shuffled, 0);
    }

    [RelayCommand]
    private void PlayNextAlbum(Album album)
    {
        if (album == null || album.Tracks == null || album.Tracks.Count == 0) return;

        var tracks = album.Tracks.ToList();
        for (int i = tracks.Count - 1; i >= 0; i--)
        {
            _player.AddNext(tracks[i]);
        }
    }

    [RelayCommand]
    private void AddAlbumToQueue(Album album)
    {
        if (album == null || album.Tracks == null || album.Tracks.Count == 0) return;

        _player.AddRangeToQueue(album.Tracks.ToList());
    }

    [RelayCommand]
    private async Task AddAlbumToNewPlaylist(Album album)
    {
        var albums = CtrlSelectedAlbums.Count > 0 ? CtrlSelectedAlbums : (album != null ? new List<Album> { album } : new List<Album>());
        var tracks = albums.SelectMany(a => a.Tracks ?? new()).ToList();
        if (tracks.Count == 0) return;
        await _sidebar.CreatePlaylistWithTracksAsync(tracks);
        CtrlSelectedAlbums.Clear();
    }

    [RelayCommand]
    private async Task AddAlbumToExistingPlaylist(object[] parameters)
    {
        if (parameters == null || parameters.Length != 2) return;
        if (parameters[0] is not Album album || parameters[1] is not Playlist playlist) return;
        var albums = CtrlSelectedAlbums.Count > 0 ? CtrlSelectedAlbums : new List<Album> { album };
        var tracks = albums.SelectMany(a => a.Tracks ?? new()).ToList();
        if (tracks.Count == 0) return;
        await _sidebar.AddTracksToPlaylist(playlist.Id, tracks);
        CtrlSelectedAlbums.Clear();
    }

    [RelayCommand]
    private async Task ToggleAlbumFavorites(Album album)
    {
        var albums = CtrlSelectedAlbums.Count > 0 ? CtrlSelectedAlbums : (album != null ? new List<Album> { album } : new List<Album>());
        if (albums.Count == 0) return;
        foreach (var a in albums)
        {
            if (a.Tracks == null || a.Tracks.Count == 0) continue;
            var newState = !a.IsAllTracksFavorite;
            foreach (var track in a.Tracks)
                track.IsFavorite = newState;
        }
        await _library.SaveAsync();
        _library.NotifyFavoritesChanged();
        CtrlSelectedAlbums.Clear();
    }

    [RelayCommand]
    private async Task OpenMetadata(Album album)
    {
        // Multi-album selection: edit every track across the selected albums in the
        // shared multi-select editor (Mixed fields, edits fan out to all tracks).
        if (CtrlSelectedAlbums.Count > 1)
        {
            var tracks = CtrlSelectedAlbums.SelectMany(a => a.Tracks ?? new()).ToList();
            CtrlSelectedAlbums.Clear();
            await MetadataHelper.OpenBatchMetadataWindow(tracks);
            return;
        }

        if (album == null || album.Tracks == null || album.Tracks.Count == 0) return;
        await MetadataHelper.OpenMetadataWindow(album.Tracks[0], albumScoped: true);
    }

    [RelayCommand]
    private async Task BatchEditAlbum(Album album)
    {
        if (album?.Tracks == null || album.Tracks.Count == 0) return;
        await MetadataHelper.OpenBatchMetadataWindow(album.Tracks.ToList());
    }

    [RelayCommand]
    private async Task ConvertAlbum(Album album)
    {
        if (album?.Tracks == null || album.Tracks.Count == 0) return;
        await MetadataHelper.OpenAudioConverterDialog(album.Tracks.ToList());
    }

    [RelayCommand]
    private async Task ScanAlbumReplayGain(Album album)
    {
        if (album?.Tracks == null || album.Tracks.Count == 0) return;
        await MetadataHelper.OpenReplayGainScannerDialog(album.Tracks.ToList());
    }

    [RelayCommand]
    private async Task RemoveFromLibrary(Album album)
    {
        var albums = CtrlSelectedAlbums.Count > 0 ? CtrlSelectedAlbums : (album != null ? new List<Album> { album } : new List<Album>());
        if (albums.Count == 0) return;
        if (!await Views.ConfirmationDialog.ShowAsync("Do you want to remove the selected item from your Library?"))
            return;
        var trackIds = albums.SelectMany(a => a.Tracks ?? new()).Select(t => t.Id).ToList();
        if (trackIds.Count > 0)
            await _library.RemoveTracksAsync(trackIds);
        CtrlSelectedAlbums.Clear();
    }

    [RelayCommand]
    private void ShowInExplorerAlbum(Album album)
    {
        if (album?.Tracks == null || album.Tracks.Count == 0) return;
        var filePath = album.Tracks[0].FilePath;
        if (!File.Exists(filePath)) return;
        Helpers.PlatformHelper.ShowInFileManager(filePath);
    }

    private Action<Track>? _searchLyricsAction;
    public void SetSearchLyricsAction(Action<Track> action) => _searchLyricsAction = action;

    [RelayCommand]
    private void SearchLyricsAlbum(Album album)
    {
        if (album?.Tracks == null || album.Tracks.Count == 0) return;
        _searchLyricsAction?.Invoke(album.Tracks[0]);
    }

    private Action<string>? _viewArtistAction;
    public void SetViewArtistAction(Action<string> action) => _viewArtistAction = action;

    [RelayCommand]
    private void ViewArtist(string artistName)
    {
        if (!string.IsNullOrWhiteSpace(artistName))
            _viewArtistAction?.Invoke(artistName);
    }

    [RelayCommand]
    private void OpenTopArtist(Artist? artist)
    {
        if (artist == null || string.IsNullOrWhiteSpace(artist.Name)) return;
        _viewArtistAction?.Invoke(artist.Name);
    }

    public void Dispose()
    {
        _refreshDebounce.Stop();
        _player.TrackStarted -= OnTrackStarted;
        _library.LibraryUpdated -= _libraryUpdatedHandler;
        _library.FavoritesChanged -= _favoritesChangedHandler;
    }
}
