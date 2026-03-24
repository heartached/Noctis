using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Models;
using Noctis.Services;

namespace Noctis.ViewModels;

/// <summary>
/// ViewModel for viewing a single album's track listing.
/// Shown when the user clicks an album in the grid.
/// </summary>
public partial class AlbumDetailViewModel : ViewModelBase, IDisposable
{
    private readonly PlayerViewModel _player;
    private readonly ILibraryService _library;
    private readonly IPersistenceService _persistence;
    private readonly ILastFmService _lastFm;
    private readonly SidebarViewModel _sidebar;
    private readonly System.ComponentModel.PropertyChangedEventHandler _playerPropertyChangedHandler;
    private readonly EventHandler _libraryUpdatedHandler;
    private readonly EventHandler _favoritesChangedHandler;

    /// <summary>Saved scroll offset for restoring position after navigation.</summary>
    public double SavedScrollOffset { get; set; }

    [ObservableProperty] private Album _album;
    [ObservableProperty] private Bitmap? _albumArt;
    [ObservableProperty] private IBrush? _backgroundBrush;
    [ObservableProperty] private Guid? _currentPlayingTrackId;
    [ObservableProperty] private bool _isPlayerPlaying;
    [ObservableProperty] private string _albumDescription = string.Empty;
    [ObservableProperty] private string _albumDescriptionFull = string.Empty;
    [ObservableProperty] private bool _isAlbumDescriptionOpen;
    [ObservableProperty] private bool _isAlbumDescriptionEditing;
    [ObservableProperty] private string _albumDescriptionEditorText = string.Empty;

    /// <summary>Whether this album is a single (1 track only).</summary>
    public bool IsSingle => Album?.TrackCount == 1;

    /// <summary>Whether all tracks in the album are favorited (for metadata row heart).</summary>
    public bool IsAlbumFavorited => Album?.IsAllTracksFavorite ?? false;

    /// <summary>Whether to show hearts on individual track rows (hide for singles and fully-favorited albums).</summary>
    public bool ShowTrackRowHearts => !IsSingle && !IsAlbumFavorited;

    public bool HasAlbumDescription => !string.IsNullOrWhiteSpace(AlbumDescription);
    public bool HasAlbumDescriptionOverflow =>
        !string.IsNullOrWhiteSpace(AlbumDescription) &&
        (
            AlbumDescription.Length > 260 ||
            (
                !string.IsNullOrWhiteSpace(AlbumDescriptionFull) &&
                !string.Equals(
                    AlbumDescription.Trim(),
                    AlbumDescriptionFull.Trim(),
                    StringComparison.Ordinal)
            )
        );
    public bool HasAlbumDescriptionChanges =>
        !string.Equals(
            (AlbumDescriptionEditorText ?? string.Empty).Trim(),
            (!string.IsNullOrWhiteSpace(AlbumDescriptionFull) ? AlbumDescriptionFull : AlbumDescription).Trim(),
            StringComparison.Ordinal);

    /// <summary>Tracks in this album, ordered by disc and track number.</summary>
    public ObservableCollection<Track> Tracks { get; } = new();

    /// <summary>Individual artist names parsed from the album artist field, with separator info for display.</summary>
    public ArtistTokenItem[] ArtistTokens { get; private set; } = Array.Empty<ArtistTokenItem>();

    /// <summary>Whether the album has multiple credited artists.</summary>
    public bool HasMultipleArtists => ArtistTokens.Length > 1;

    /// <summary>Exposes playlists for the Add to Playlist submenu.</summary>
    public ObservableCollection<Playlist> Playlists => _sidebar.Playlists;

    /// <summary>Fires when the user wants to go back to the album grid.</summary>
    public event EventHandler? BackRequested;

    /// <summary>Fires when the user wants to view a track's album (may differ from current album).</summary>
    public event EventHandler<Track>? ViewAlbumRequested;

    private Action<string>? _viewArtistAction;
    private Action<Track>? _searchLyricsAction;

    /// <summary>Sets the action to navigate to an artist's discography.</summary>
    public void SetViewArtistAction(Action<string> action) => _viewArtistAction = action;

    /// <summary>Sets the action to search lyrics for a track.</summary>
    public void SetSearchLyricsAction(Action<Track> action) => _searchLyricsAction = action;

    public AlbumDetailViewModel(Album album, PlayerViewModel player, IPersistenceService persistence, ILibraryService library, SidebarViewModel sidebar, ILastFmService lastFm)
    {
        _player = player;
        _library = library;
        _persistence = persistence;
        _lastFm = lastFm;
        _sidebar = sidebar;
        _album = album;

        // Parse individual artist names from the album artist field
        var tokens = Track.ParseArtistTokens(album.Artist);
        if (tokens.Length == 0) tokens = new[] { album.Artist };
        ArtistTokens = tokens.Select((name, i) => new ArtistTokenItem(name, IsLast: i == tokens.Length - 1)).ToArray();

        // Load tracks
        foreach (var track in album.Tracks)
            Tracks.Add(track);

        // Load artwork off UI thread
        var artPath = persistence.GetArtworkPath(album.Id);
        if (File.Exists(artPath))
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    var bmp = new Bitmap(artPath);
                    Dispatcher.UIThread.Post(() => AlbumArt = bmp);
                }
                catch { }
            });
        }

        // Track the currently playing song — store handler so we can unsubscribe in Dispose
        UpdateCurrentPlayingTrack();
        IsPlayerPlaying = _player.State == Models.PlaybackState.Playing;
        _playerPropertyChangedHandler = (_, e) =>
        {
            if (e.PropertyName == nameof(PlayerViewModel.CurrentTrack))
                UpdateCurrentPlayingTrack();
            if (e.PropertyName == nameof(PlayerViewModel.State))
                IsPlayerPlaying = _player.State == Models.PlaybackState.Playing;
        };
        _player.PropertyChanged += _playerPropertyChangedHandler;

        // Refresh when library metadata changes (e.g. metadata editor save)
        _libraryUpdatedHandler = (_, _) => Dispatcher.UIThread.Post(RefreshFromLibrary);
        _library.LibraryUpdated += _libraryUpdatedHandler;

        // Refresh album-level favorite indicators when any favorite changes externally
        _favoritesChangedHandler = (_, _) => Dispatcher.UIThread.Post(() =>
        {
            OnPropertyChanged(nameof(IsAlbumFavorited));
            OnPropertyChanged(nameof(ShowTrackRowHearts));
        });
        _library.FavoritesChanged += _favoritesChangedHandler;

        // Fetch album description asynchronously; fail silently if unavailable.
        _ = LoadAlbumDescriptionAsync();
    }

    partial void OnAlbumChanged(Album value)
    {
        var tokens = Track.ParseArtistTokens(value.Artist);
        if (tokens.Length == 0) tokens = new[] { value.Artist };
        ArtistTokens = tokens.Select((name, i) => new ArtistTokenItem(name, IsLast: i == tokens.Length - 1)).ToArray();
        OnPropertyChanged(nameof(ArtistTokens));
        OnPropertyChanged(nameof(HasMultipleArtists));
    }

    partial void OnAlbumDescriptionChanged(string value)
    {
        OnPropertyChanged(nameof(HasAlbumDescription));
        OnPropertyChanged(nameof(HasAlbumDescriptionOverflow));
        OnPropertyChanged(nameof(HasAlbumDescriptionChanges));
    }

    partial void OnAlbumDescriptionFullChanged(string value)
    {
        // Keep visibility binding stable even if only one payload variant is available.
        OnPropertyChanged(nameof(HasAlbumDescription));
        OnPropertyChanged(nameof(HasAlbumDescriptionOverflow));
        OnPropertyChanged(nameof(HasAlbumDescriptionChanges));
    }

    partial void OnAlbumDescriptionEditorTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasAlbumDescriptionChanges));
    }

    private async Task LoadAlbumDescriptionAsync()
    {
        try
        {
            var summaryTask = _lastFm.GetAlbumDescriptionAsync(Album.Artist, Album.Name);
            var fullTask = _lastFm.GetAlbumDescriptionFullAsync(Album.Artist, Album.Name);
            await Task.WhenAll(summaryTask, fullTask);

            var summary = await summaryTask;
            var full = await fullTask;

            var snippet = !string.IsNullOrWhiteSpace(summary) ? summary : full;
            var fullText = !string.IsNullOrWhiteSpace(full) ? full : summary;
            if (!string.IsNullOrWhiteSpace(snippet))
                AlbumDescription = snippet;
            if (!string.IsNullOrWhiteSpace(fullText))
                AlbumDescriptionFull = fullText;
            if (!IsAlbumDescriptionEditing)
            {
                AlbumDescriptionEditorText = !string.IsNullOrWhiteSpace(AlbumDescriptionFull)
                    ? AlbumDescriptionFull
                    : AlbumDescription;
            }
        }
        catch
        {
            // Fail silently by design.
        }
    }

    private void RefreshFromLibrary()
    {
        // Try to find album by current ID first
        var updatedAlbum = _library.Albums.FirstOrDefault(a => a.Id == Album.Id);

        // If not found, the album/artist name was edited causing AlbumId to change.
        // Find the new album by looking for one that contains any of our tracks.
        if (updatedAlbum == null)
        {
            var trackIds = Tracks.Select(t => t.Id).ToHashSet();
            updatedAlbum = _library.Albums
                .FirstOrDefault(a => a.Tracks.Any(t => trackIds.Contains(t.Id)));
        }

        if (updatedAlbum == null)
        {
            // Album truly removed — navigate back
            BackRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        Album = updatedAlbum;

        Tracks.Clear();
        foreach (var track in updatedAlbum.Tracks)
            Tracks.Add(track);

        // Reload artwork in case it changed
        var oldArt = AlbumArt;
        var artPath = _persistence.GetArtworkPath(updatedAlbum.Id);
        if (File.Exists(artPath))
        {
            try { AlbumArt = new Bitmap(artPath); } catch { AlbumArt = null; }
        }
        else
        {
            AlbumArt = null;
        }
        oldArt?.Dispose();
    }

    partial void OnAlbumArtChanged(Bitmap? value)
    {
        if (value == null)
        {
            BackgroundBrush = null;
            return;
        }

        // Extract dominant color off UI thread, then create gradient
        ThreadPool.QueueUserWorkItem(_ =>
        {
            var color = DominantColorExtractor.ExtractDominantColor(value);
            var brush = DominantColorExtractor.CreateAlbumDetailGradient(color);
            Dispatcher.UIThread.Post(() => BackgroundBrush = brush);
        });
    }

    private void UpdateCurrentPlayingTrack()
    {
        CurrentPlayingTrackId = _player.CurrentTrack?.Id;
    }

    [RelayCommand]
    private void PlayAll()
    {
        if (Tracks.Count == 0) return;
        _player.ReplaceQueueAndPlay(Tracks.ToList(), 0);
    }

    [RelayCommand]
    private void ShufflePlay()
    {
        if (Tracks.Count == 0) return;
        var shuffled = Tracks.OrderBy(_ => Random.Shared.Next()).ToList();
        _player.ReplaceQueueAndPlay(shuffled, 0);
    }

    [RelayCommand]
    private void PlayFrom(Track track)
    {
        var tracks = Tracks.ToList();
        var idx = tracks.IndexOf(track);
        if (idx < 0) idx = 0;
        _player.ReplaceQueueAndPlay(tracks, idx);
    }

    [RelayCommand]
    private void ViewArtist()
    {
        var artist = Album.Artist;
        if (!string.IsNullOrWhiteSpace(artist))
            _viewArtistAction?.Invoke(artist);
    }

    [RelayCommand]
    private void ViewArtistFromTrack(string artistName)
    {
        if (!string.IsNullOrWhiteSpace(artistName))
            _viewArtistAction?.Invoke(artistName);
    }

    [RelayCommand]
    private void ViewIndividualArtist(string artistName)
    {
        if (!string.IsNullOrWhiteSpace(artistName))
            _viewArtistAction?.Invoke(artistName);
    }

    [RelayCommand]
    private void GoBack()
    {
        IsAlbumDescriptionOpen = false;
        BackRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void OpenAlbumDescription()
    {
        if (!HasAlbumDescription) return;
        AlbumDescriptionEditorText = !string.IsNullOrWhiteSpace(AlbumDescriptionFull)
            ? AlbumDescriptionFull
            : AlbumDescription;
        IsAlbumDescriptionEditing = false;
        IsAlbumDescriptionOpen = true;
    }

    [RelayCommand]
    private void CloseAlbumDescription()
    {
        IsAlbumDescriptionEditing = false;
        IsAlbumDescriptionOpen = false;
    }

    [RelayCommand]
    private void StartAlbumDescriptionEdit()
    {
        AlbumDescriptionEditorText = !string.IsNullOrWhiteSpace(AlbumDescriptionFull)
            ? AlbumDescriptionFull
            : AlbumDescription;
        IsAlbumDescriptionEditing = true;
    }

    [RelayCommand]
    private async Task SaveAlbumDescriptionEdit()
    {
        var edited = (AlbumDescriptionEditorText ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(edited))
        {
            await _lastFm.ClearAlbumDescriptionOverrideAsync(Album.Artist, Album.Name);
            await LoadAlbumDescriptionAsync();
        }
        else
        {
            await _lastFm.SetAlbumDescriptionOverrideAsync(Album.Artist, Album.Name, edited);
            AlbumDescription = edited;
            AlbumDescriptionFull = edited;
        }

        IsAlbumDescriptionEditing = false;
    }

    [RelayCommand]
    private void CancelAlbumDescriptionEdit()
    {
        AlbumDescriptionEditorText = !string.IsNullOrWhiteSpace(AlbumDescriptionFull)
            ? AlbumDescriptionFull
            : AlbumDescription;
        IsAlbumDescriptionEditing = false;
    }

    [RelayCommand]
    private void PlayNext(Track track) => _player.AddNext(track);

    [RelayCommand]
    private void AddToQueue(Track track) => _player.AddToQueue(track);
    [RelayCommand]
    private void AddAlbumToQueue()
    {
        if (Tracks.Count == 0) return;

        var tracks = Tracks.ToList();
        foreach (var track in tracks)
            _player.AddToQueue(track);
    }

    [RelayCommand]
    private async Task OpenMetadata(Track track)
    {
        await MetadataHelper.OpenMetadataWindow(track);
    }

    [RelayCommand]
    private async Task OpenAlbumMetadata()
    {
        if (Tracks.Count == 0) return;
        await MetadataHelper.OpenMetadataWindow(Tracks[0], albumScoped: true);
    }

    [RelayCommand]
    private async Task ToggleFavorite(Track track)
    {
        track.IsFavorite = !track.IsFavorite;
        await _library.SaveAsync();
        _library.NotifyFavoritesChanged();
        // Refresh hearts visibility
        OnPropertyChanged(nameof(IsAlbumFavorited));
        OnPropertyChanged(nameof(ShowTrackRowHearts));
    }

    [RelayCommand]
    private async Task ToggleAlbumFavorites()
    {
        if (Tracks.Count == 0) return;

        var newState = !Album.IsAllTracksFavorite;
        foreach (var track in Tracks)
            track.IsFavorite = newState;

        await _library.SaveAsync();
        _library.NotifyFavoritesChanged();
        // Refresh hearts visibility
        OnPropertyChanged(nameof(IsAlbumFavorited));
        OnPropertyChanged(nameof(ShowTrackRowHearts));
    }

    [RelayCommand]
    private async Task AddToNewPlaylist(Track track)
    {
        await _sidebar.CreatePlaylistWithTrackAsync(track);
    }

    [RelayCommand]
    private async Task AddAlbumToNewPlaylist()
    {
        if (Tracks.Count == 0) return;
        await _sidebar.CreatePlaylistWithTracksAsync(Tracks.ToList());
    }

    [RelayCommand]
    private async Task AddToExistingPlaylist(object[] parameters)
    {
        if (parameters == null || parameters.Length != 2) return;
        if (parameters[0] is not Track track || parameters[1] is not Playlist playlist) return;

        await _sidebar.AddTracksToPlaylist(playlist.Id, new[] { track });
    }

    [RelayCommand]
    private async Task AddAlbumToExistingPlaylist(Playlist playlist)
    {
        if (playlist == null || Tracks.Count == 0) return;
        await _sidebar.AddTracksToPlaylist(playlist.Id, Tracks.ToList());
    }

    [RelayCommand]
    private async Task RemoveFromLibrary(Track track)
    {
        if (!await Views.ConfirmationDialog.ShowAsync($"Remove \"{track.Title}\" from your library?"))
            return;
        var idx = Tracks.IndexOf(track);
        if (idx >= 0)
            Tracks.RemoveAt(idx);
        await _library.RemoveTrackAsync(track.Id);
    }

    [RelayCommand]
    private async Task RemoveAlbumFromLibrary()
    {
        if (Tracks.Count == 0) return;
        if (!await Views.ConfirmationDialog.ShowAsync("Remove this entire album from your library?"))
            return;
        var trackIds = Tracks.Select(t => t.Id).ToList();
        Tracks.Clear();
        await _library.RemoveTracksAsync(trackIds);
        BackRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void ShowInExplorer(Track track)
    {
        if (track == null || !File.Exists(track.FilePath)) return;
        Helpers.PlatformHelper.ShowInFileManager(track.FilePath);
    }

    [RelayCommand]
    private void ShowAlbumInExplorer()
    {
        if (Tracks.Count == 0) return;
        var track = Tracks[0];
        if (!File.Exists(track.FilePath)) return;
        Helpers.PlatformHelper.ShowInFileManager(track.FilePath);
    }

    [RelayCommand]
    private void SearchLyrics(Track track) => _searchLyricsAction?.Invoke(track);
    [RelayCommand]
    private void ViewAlbum(Track track) => ViewAlbumRequested?.Invoke(this, track);

    [RelayCommand]
    private void ViewCurrentAlbum()
    {
        if (Tracks.Count == 0) return;
        ViewAlbumRequested?.Invoke(this, Tracks[0]);
    }

    public void Dispose()
    {
        _player.PropertyChanged -= _playerPropertyChangedHandler;
        _library.LibraryUpdated -= _libraryUpdatedHandler;
        _library.FavoritesChanged -= _favoritesChangedHandler;
        AlbumArt?.Dispose();
        AlbumArt = null;
    }
}

/// <summary>Display item for an individual artist name in a multi-artist album header.</summary>
public record ArtistTokenItem(string Name, bool IsLast);

