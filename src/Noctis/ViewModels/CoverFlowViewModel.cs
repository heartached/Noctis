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
    private Track? _subscribedCenterTrack;

    public void SetViewArtistAction(Action<string> action) => _viewArtistAction = action;
    public void SetViewAlbumAction(Action<Track> action) => _viewAlbumAction = action;

    // ── Current carousel state (center + 7 on each side; collage shows up to ±10) ──

    [ObservableProperty] private Track? _centerTrack;

    // Previous side (history, index 0 = most recent)
    [ObservableProperty] private Track? _previousTrack;      // -1
    [ObservableProperty] private Track? _farPreviousTrack;   // -2
    [ObservableProperty] private Track? _edgePreviousTrack;  // -3
    [ObservableProperty] private Track? _offPreviousTrack;   // -4
    [ObservableProperty] private Track? _prev5Track;         // -5
    [ObservableProperty] private Track? _prev6Track;         // -6
    [ObservableProperty] private Track? _prev7Track;         // -7
    [ObservableProperty] private Track? _prev8Track;         // -8
    [ObservableProperty] private Track? _prev9Track;         // -9
    [ObservableProperty] private Track? _prev10Track;        // -10

    // Next side (UpNext queue)
    [ObservableProperty] private Track? _nextTrack;          // +1
    [ObservableProperty] private Track? _farNextTrack;       // +2
    [ObservableProperty] private Track? _edgeNextTrack;      // +3
    [ObservableProperty] private Track? _offNextTrack;       // +4
    [ObservableProperty] private Track? _next5Track;         // +5
    [ObservableProperty] private Track? _next6Track;         // +6
    [ObservableProperty] private Track? _next7Track;         // +7
    [ObservableProperty] private Track? _next8Track;         // +8
    [ObservableProperty] private Track? _next9Track;         // +9
    [ObservableProperty] private Track? _next10Track;        // +10
    [ObservableProperty] private Track? _next11Track;        // +11 (collage 12th slot)
    // Extra collage-only slots (+12..+23) so the constellation can show up to 24 covers.
    [ObservableProperty] private Track? _next12Track;        // +12
    [ObservableProperty] private Track? _next13Track;        // +13
    [ObservableProperty] private Track? _next14Track;        // +14
    [ObservableProperty] private Track? _next15Track;        // +15
    [ObservableProperty] private Track? _next16Track;        // +16
    [ObservableProperty] private Track? _next17Track;        // +17
    [ObservableProperty] private Track? _next18Track;        // +18
    [ObservableProperty] private Track? _next19Track;        // +19
    [ObservableProperty] private Track? _next20Track;        // +20
    [ObservableProperty] private Track? _next21Track;        // +21
    [ObservableProperty] private Track? _next22Track;        // +22
    [ObservableProperty] private Track? _next23Track;        // +23
    [ObservableProperty] private Track? _next24Track;        // +24
    [ObservableProperty] private Track? _next25Track;        // +25
    [ObservableProperty] private Track? _next26Track;        // +26
    [ObservableProperty] private Track? _next27Track;        // +27
    [ObservableProperty] private Track? _next28Track;        // +28
    [ObservableProperty] private Track? _next29Track;        // +29
    [ObservableProperty] private Track? _next30Track;        // +30
    [ObservableProperty] private Track? _next31Track;        // +31

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
    [ObservableProperty] private string? _prev8ArtworkPath;
    [ObservableProperty] private string? _prev9ArtworkPath;
    [ObservableProperty] private string? _prev10ArtworkPath;
    [ObservableProperty] private string? _next5ArtworkPath;
    [ObservableProperty] private string? _next6ArtworkPath;
    [ObservableProperty] private string? _next7ArtworkPath;
    [ObservableProperty] private string? _next8ArtworkPath;
    [ObservableProperty] private string? _next9ArtworkPath;
    [ObservableProperty] private string? _next10ArtworkPath;

    [ObservableProperty] private bool _centerIsExplicit;
    [ObservableProperty] private bool _centerIsFavorite;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCarousel))]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState))]
    private bool _hasQueue;

    /// <summary>Collage sub-mode: a static, decorative library-artwork showcase instead of the carousel.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCarousel))]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState))]
    private bool _isCollageMode;

    /// <summary>Carousel (queue-driven) shows only outside collage mode and when a queue exists.</summary>
    public bool ShowCarousel => !IsCollageMode && HasQueue;
    /// <summary>"Nothing playing" empty state — carousel mode with no queue.</summary>
    public bool ShowEmptyState => !IsCollageMode && !HasQueue;

    [RelayCommand]
    private void ToggleCollageMode() => IsCollageMode = !IsCollageMode;

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

        // Track center track property changes (e.g. IsFavorite toggle)
        if (_subscribedCenterTrack != current)
        {
            if (_subscribedCenterTrack != null)
                _subscribedCenterTrack.PropertyChanged -= OnCenterTrackPropertyChanged;
            _subscribedCenterTrack = current;
            if (_subscribedCenterTrack != null)
                _subscribedCenterTrack.PropertyChanged += OnCenterTrackPropertyChanged;
        }

        // Center = currently playing track
        CenterTrack = current;
        CenterTitle = current?.Title ?? string.Empty;
        CenterArtist = current?.Artist ?? string.Empty;
        CenterAlbum = current?.Album ?? string.Empty;
        CenterIsExplicit = current?.IsExplicit ?? false;
        CenterIsFavorite = current?.IsFavorite ?? false;
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

        Prev8Track = history.Count > 7 ? history[7] : null;
        Prev8ArtworkPath = Prev8Track?.AlbumArtworkPath;

        Prev9Track = history.Count > 8 ? history[8] : null;
        Prev9ArtworkPath = Prev9Track?.AlbumArtworkPath;

        Prev10Track = history.Count > 9 ? history[9] : null;
        Prev10ArtworkPath = Prev10Track?.AlbumArtworkPath;

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

        Next8Track = upNext.Count > 7 ? upNext[7] : null;
        Next8ArtworkPath = Next8Track?.AlbumArtworkPath;

        Next9Track = upNext.Count > 8 ? upNext[8] : null;
        Next9ArtworkPath = Next9Track?.AlbumArtworkPath;

        Next10Track = upNext.Count > 9 ? upNext[9] : null;
        Next10ArtworkPath = Next10Track?.AlbumArtworkPath;

        // Collage-only slots +11..+23 (artwork bound directly off the Track in the view).
        Next11Track = upNext.Count > 10 ? upNext[10] : null;
        Next12Track = upNext.Count > 11 ? upNext[11] : null;
        Next13Track = upNext.Count > 12 ? upNext[12] : null;
        Next14Track = upNext.Count > 13 ? upNext[13] : null;
        Next15Track = upNext.Count > 14 ? upNext[14] : null;
        Next16Track = upNext.Count > 15 ? upNext[15] : null;
        Next17Track = upNext.Count > 16 ? upNext[16] : null;
        Next18Track = upNext.Count > 17 ? upNext[17] : null;
        Next19Track = upNext.Count > 18 ? upNext[18] : null;
        Next20Track = upNext.Count > 19 ? upNext[19] : null;
        Next21Track = upNext.Count > 20 ? upNext[20] : null;
        Next22Track = upNext.Count > 21 ? upNext[21] : null;
        Next23Track = upNext.Count > 22 ? upNext[22] : null;
        Next24Track = upNext.Count > 23 ? upNext[23] : null;
        Next25Track = upNext.Count > 24 ? upNext[24] : null;
        Next26Track = upNext.Count > 25 ? upNext[25] : null;
        Next27Track = upNext.Count > 26 ? upNext[26] : null;
        Next28Track = upNext.Count > 27 ? upNext[27] : null;
        Next29Track = upNext.Count > 28 ? upNext[28] : null;
        Next30Track = upNext.Count > 29 ? upNext[29] : null;
        Next31Track = upNext.Count > 30 ? upNext[30] : null;
    }

    private void OnCenterTrackPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Track.IsFavorite))
            Dispatcher.UIThread.Post(() => CenterIsFavorite = _subscribedCenterTrack?.IsFavorite ?? false);
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
        if (_subscribedCenterTrack != null)
            _subscribedCenterTrack.PropertyChanged -= OnCenterTrackPropertyChanged;
    }
}
