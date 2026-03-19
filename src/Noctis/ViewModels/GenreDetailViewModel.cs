using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Models;
using Noctis.Services;

namespace Noctis.ViewModels;

public partial class GenreDetailViewModel : ViewModelBase, IDisposable
{
    private readonly PlayerViewModel _player;
    private readonly ILibraryService _library;
    private readonly SidebarViewModel _sidebar;
    private readonly System.ComponentModel.PropertyChangedEventHandler _playerPropertyChangedHandler;

    [ObservableProperty] private string _genreName;
    [ObservableProperty] private string _genreColor;
    [ObservableProperty] private Guid? _currentPlayingTrackId;
    [ObservableProperty] private bool _isPlayerPlaying;

    public ObservableCollection<Track> Tracks { get; } = new();

    public int TrackCount => Tracks.Count;

    /// <summary>Saved scroll offset for restoring position after navigation.</summary>
    public double SavedScrollOffset { get; set; }

    /// <summary>Exposes playlists for the Add to Playlist submenu.</summary>
    public ObservableCollection<Playlist> Playlists => _sidebar.Playlists;

    public event EventHandler? BackRequested;

    private Action<Track>? _searchLyricsAction;
    private Action<string>? _viewArtistAction;

    /// <summary>Sets the action to search lyrics for a track.</summary>
    public void SetSearchLyricsAction(Action<Track> action) => _searchLyricsAction = action;

    /// <summary>Sets the action to navigate to an artist's discography.</summary>
    public void SetViewArtistAction(Action<string> action) => _viewArtistAction = action;

    public GenreDetailViewModel(GenreItem genre, PlayerViewModel player, ILibraryService library, SidebarViewModel sidebar)
    {
        _player = player;
        _library = library;
        _sidebar = sidebar;
        _genreName = genre.Name;
        _genreColor = genre.Color;

        // Resolve track IDs to actual tracks
        foreach (var id in genre.TrackIds)
        {
            var track = _library.GetTrackById(id);
            if (track != null)
                Tracks.Add(track);
        }

        // Track the currently playing song
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
    private void GoBack()
    {
        BackRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void PlayNext(Track track) => _player.AddNext(track);

    [RelayCommand]
    private void AddToQueue(Track track) => _player.AddToQueue(track);

    [RelayCommand]
    private async Task OpenMetadata(Track track)
    {
        await MetadataHelper.OpenMetadataWindow(track);
    }

    [RelayCommand]
    private async Task ToggleFavorite(Track track)
    {
        track.IsFavorite = !track.IsFavorite;
        await _library.SaveAsync();
        _library.NotifyFavoritesChanged();
    }

    [RelayCommand]
    private async Task AddToNewPlaylist(Track track)
    {
        await _sidebar.CreatePlaylistWithTrackAsync(track);
    }

    [RelayCommand]
    private async Task AddToExistingPlaylist(object[] parameters)
    {
        if (parameters == null || parameters.Length != 2) return;
        if (parameters[0] is not Track track || parameters[1] is not Playlist playlist) return;

        await _sidebar.AddTracksToPlaylist(playlist.Id, new[] { track });
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
    private void ShowInExplorer(Track track)
    {
        if (track == null || !File.Exists(track.FilePath)) return;
        Helpers.PlatformHelper.ShowInFileManager(track.FilePath);
    }

    [RelayCommand]
    private void SearchLyrics(Track track) => _searchLyricsAction?.Invoke(track);

    [RelayCommand]
    private void ViewArtist(string artistName)
    {
        if (!string.IsNullOrWhiteSpace(artistName))
            _viewArtistAction?.Invoke(artistName);
    }

    public void Dispose()
    {
        _player.PropertyChanged -= _playerPropertyChangedHandler;
    }
}
