using System.Collections.Specialized;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Models;

namespace Noctis.ViewModels;

/// <summary>
/// ViewModel for the cover-flow "Up Next" view inside the Albums section.
/// Shows the currently playing track centered with previous/next covers on the sides,
/// driven by the real playback queue (normal and shuffle order).
/// </summary>
public partial class CoverFlowViewModel : ViewModelBase, IDisposable
{
    private readonly PlayerViewModel _player;
    private Action<string>? _viewArtistAction;
    private Action<Track>? _viewAlbumAction;

    public void SetViewArtistAction(Action<string> action) => _viewArtistAction = action;
    public void SetViewAlbumAction(Action<Track> action) => _viewAlbumAction = action;

    // ── Current carousel state (center + 7 on each side) ──

    [ObservableProperty] private Track? _centerTrack;

    // Previous side (history, index 0 = most recent)
    [ObservableProperty] private Track? _previousTrack;      // -1
    [ObservableProperty] private Track? _farPreviousTrack;   // -2
    [ObservableProperty] private Track? _edgePreviousTrack;  // -3
    [ObservableProperty] private Track? _offPreviousTrack;   // -4
    [ObservableProperty] private Track? _prev5Track;         // -5
    [ObservableProperty] private Track? _prev6Track;         // -6
    [ObservableProperty] private Track? _prev7Track;         // -7

    // Next side (UpNext queue)
    [ObservableProperty] private Track? _nextTrack;          // +1
    [ObservableProperty] private Track? _farNextTrack;       // +2
    [ObservableProperty] private Track? _edgeNextTrack;      // +3
    [ObservableProperty] private Track? _offNextTrack;       // +4
    [ObservableProperty] private Track? _next5Track;         // +5
    [ObservableProperty] private Track? _next6Track;         // +6
    [ObservableProperty] private Track? _next7Track;         // +7

    [ObservableProperty] private string _centerTitle = string.Empty;
    [ObservableProperty] private string _centerArtist = string.Empty;
    [ObservableProperty] private string _centerAlbum = string.Empty;

    // Artwork paths
    [ObservableProperty] private string? _centerArtworkPath;
    [ObservableProperty] private string? _previousArtworkPath;
    [ObservableProperty] private string? _nextArtworkPath;
    [ObservableProperty] private string? _farPreviousArtworkPath;
    [ObservableProperty] private string? _farNextArtworkPath;
    [ObservableProperty] private string? _edgePreviousArtworkPath;
    [ObservableProperty] private string? _edgeNextArtworkPath;
    [ObservableProperty] private string? _offPreviousArtworkPath;
    [ObservableProperty] private string? _offNextArtworkPath;
    [ObservableProperty] private string? _prev5ArtworkPath;
    [ObservableProperty] private string? _prev6ArtworkPath;
    [ObservableProperty] private string? _prev7ArtworkPath;
    [ObservableProperty] private string? _next5ArtworkPath;
    [ObservableProperty] private string? _next6ArtworkPath;
    [ObservableProperty] private string? _next7ArtworkPath;

    [ObservableProperty] private bool _centerIsExplicit;
    [ObservableProperty] private bool _hasQueue;

    public PlayerViewModel Player => _player;

    public CoverFlowViewModel(PlayerViewModel player)
    {
        _player = player;

        // Subscribe to queue/track changes
        _player.TrackStarted += OnTrackStarted;
        _player.PropertyChanged += OnPlayerPropertyChanged;
        _player.UpNext.CollectionChanged += OnQueueChanged;
        _player.History.CollectionChanged += OnQueueChanged;

        // Initialize from current state
        RefreshCarousel();
    }

    private void OnTrackStarted(object? sender, Track track)
    {
        Dispatcher.UIThread.Post(RefreshCarousel);
    }

    private void OnPlayerPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PlayerViewModel.CurrentTrack) or nameof(PlayerViewModel.State))
        {
            Dispatcher.UIThread.Post(RefreshCarousel);
        }
    }

    private void OnQueueChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(RefreshCarousel);
    }

    private void RefreshCarousel()
    {
        var current = _player.CurrentTrack;
        var upNext = _player.UpNext;
        var history = _player.History;

        HasQueue = current != null || upNext.Count > 0;

        // Center = currently playing track
        CenterTrack = current;
        CenterTitle = current?.Title ?? string.Empty;
        CenterArtist = current?.Artist ?? string.Empty;
        CenterAlbum = current?.Album ?? string.Empty;
        CenterIsExplicit = current?.IsExplicit ?? false;
        CenterArtworkPath = current?.AlbumArtworkPath;

        // Previous items from history (index 0 = most recent)
        PreviousTrack = history.Count > 0 ? history[0] : null;
        PreviousArtworkPath = PreviousTrack?.AlbumArtworkPath;

        FarPreviousTrack = history.Count > 1 ? history[1] : null;
        FarPreviousArtworkPath = FarPreviousTrack?.AlbumArtworkPath;

        EdgePreviousTrack = history.Count > 2 ? history[2] : null;
        EdgePreviousArtworkPath = EdgePreviousTrack?.AlbumArtworkPath;

        OffPreviousTrack = history.Count > 3 ? history[3] : null;
        OffPreviousArtworkPath = OffPreviousTrack?.AlbumArtworkPath;

        Prev5Track = history.Count > 4 ? history[4] : null;
        Prev5ArtworkPath = Prev5Track?.AlbumArtworkPath;

        Prev6Track = history.Count > 5 ? history[5] : null;
        Prev6ArtworkPath = Prev6Track?.AlbumArtworkPath;

        Prev7Track = history.Count > 6 ? history[6] : null;
        Prev7ArtworkPath = Prev7Track?.AlbumArtworkPath;

        // Next items from UpNext queue
        NextTrack = upNext.Count > 0 ? upNext[0] : null;
        NextArtworkPath = NextTrack?.AlbumArtworkPath;

        FarNextTrack = upNext.Count > 1 ? upNext[1] : null;
        FarNextArtworkPath = FarNextTrack?.AlbumArtworkPath;

        EdgeNextTrack = upNext.Count > 2 ? upNext[2] : null;
        EdgeNextArtworkPath = EdgeNextTrack?.AlbumArtworkPath;

        OffNextTrack = upNext.Count > 3 ? upNext[3] : null;
        OffNextArtworkPath = OffNextTrack?.AlbumArtworkPath;

        Next5Track = upNext.Count > 4 ? upNext[4] : null;
        Next5ArtworkPath = Next5Track?.AlbumArtworkPath;

        Next6Track = upNext.Count > 5 ? upNext[5] : null;
        Next6ArtworkPath = Next6Track?.AlbumArtworkPath;

        Next7Track = upNext.Count > 6 ? upNext[6] : null;
        Next7ArtworkPath = Next7Track?.AlbumArtworkPath;
    }

    [RelayCommand]
    private void GoToArtist()
    {
        if (!string.IsNullOrWhiteSpace(CenterArtist))
            _viewArtistAction?.Invoke(CenterArtist);
    }

    [RelayCommand]
    private void GoToAlbum()
    {
        if (CenterTrack != null)
            _viewAlbumAction?.Invoke(CenterTrack);
    }

    [RelayCommand]
    private void GoNext()
    {
        if (_player.UpNext.Count > 0)
            _player.NextCommand.Execute(null);
    }

    [RelayCommand]
    private void GoPrevious()
    {
        _player.PreviousCommand.Execute(null);
    }

    public void Dispose()
    {
        _player.TrackStarted -= OnTrackStarted;
        _player.PropertyChanged -= OnPlayerPropertyChanged;
        _player.UpNext.CollectionChanged -= OnQueueChanged;
        _player.History.CollectionChanged -= OnQueueChanged;
    }
}
