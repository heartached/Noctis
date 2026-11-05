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
    [NotifyPropertyChangedFor(nameof(ShowCascade))]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState))]
    private bool _hasQueue;

    /// <summary>Which arrangement the page shows (Appearance → Cover Flow Layout; the top-bar
    /// pill segment steps through them). MainWindowViewModel keeps this two-way with the
    /// persisted setting. Defaults to the classic carousel.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCollageMode))]
    [NotifyPropertyChangedFor(nameof(IsCarouselMode))]
    [NotifyPropertyChangedFor(nameof(IsCascadeMode))]
    [NotifyPropertyChangedFor(nameof(ShowCarousel))]
    [NotifyPropertyChangedFor(nameof(ShowCascade))]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState))]
    [NotifyPropertyChangedFor(nameof(LayoutLabel))]
    private CoverFlowLayout _layout = CoverFlowLayout.Carousel;

    /// <summary>Collage: a static, decorative queue-artwork mosaic instead of the carousel.</summary>
    public bool IsCollageMode => Layout == CoverFlowLayout.Collage;
    public bool IsCarouselMode => Layout == CoverFlowLayout.Carousel;
    public bool IsCascadeMode => Layout == CoverFlowLayout.Cascade;

    /// <summary>Classic row (queue-driven) shows only in carousel layout and when a queue exists.</summary>
    public bool ShowCarousel => IsCarouselMode && HasQueue;
    /// <summary>Cascade pile + text column: cascade layout with a queue.</summary>
    public bool ShowCascade => IsCascadeMode && HasQueue;
    /// <summary>"Nothing playing" empty state — any queue-driven layout with no queue.</summary>
    public bool ShowEmptyState => !IsCollageMode && !HasQueue;

    /// <summary>Top-bar pill segment: Carousel → Cascade → Collage → Carousel.</summary>
    [RelayCommand]
    private void ToggleCollageMode() => Layout = CoverFlowLayouts.Next(Layout);

    /// <summary>Top-bar layout dropdown: pick a layout by name ("Carousel" / "Cascade" / "Collage").</summary>
    [RelayCommand]
    private void SetLayout(string? name) => Layout = CoverFlowLayouts.Parse(name);

    /// <summary>Human label for the current layout (the dropdown's face).</summary>
    public string LayoutLabel => Noctis.Localization.Loc.T("Main." + Layout);

    public PlayerViewModel Player => _player;

    /// <summary>
    /// Raised after the slots are refreshed whenever the centre track changed. The
    /// argument is how many slots the row moved: +1/+2 when the new centre was the next /
    /// far-next card, −1/−2 when it was the previous / far-previous one, 0 when the new
    /// centre came from nowhere in the row (a fresh album, a direct play) — the view
    /// slides the cards for a non-zero step and snaps for zero.
    /// </summary>
    public event EventHandler<int>? CarouselShifted;

    /// <summary>Slots moved between two centre tracks, judged from the OLD row: the new
    /// centre is looked up among the old neighbours. Reference identity — the queue and
    /// history hold the very same Track instances.</summary>
    internal static int StepBetween(Track? oldCenter, Track? newCenter,
        Track? oldPrev1, Track? oldPrev2, Track? oldNext1, Track? oldNext2,
        Track? oldPrev3 = null, Track? oldPrev4 = null, Track? oldNext3 = null, Track? oldNext4 = null) =>
        StepBetween(oldCenter, newCenter,
            new[] { oldPrev1, oldPrev2, oldPrev3, oldPrev4 },
            new[] { oldNext1, oldNext2, oldNext3, oldNext4 });

    /// <summary><see cref="StepBetween(Track?, Track?, Track?, Track?, Track?, Track?, Track?, Track?, Track?, Track?)"/>
    /// over any number of slots: <paramref name="oldPrev"/>[k] / <paramref name="oldNext"/>[k]
    /// sat k + 1 slots from the old centre.</summary>
    internal static int StepBetween(Track? oldCenter, Track? newCenter,
        IReadOnlyList<Track?> oldPrev, IReadOnlyList<Track?> oldNext)
    {
        if (newCenter == null || ReferenceEquals(oldCenter, newCenter)) return 0;
        for (var k = 0; k < oldNext.Count; k++)
            if (ReferenceEquals(newCenter, oldNext[k])) return k + 1;
        for (var k = 0; k < oldPrev.Count; k++)
            if (ReferenceEquals(newCenter, oldPrev[k])) return -(k + 1);
        return 0;
    }

    /// <summary>Play the track <paramref name="offset"/> slots away from the playing one
    /// (+ = up next, − = history): the centred card of a browsed carousel, a Collage tile.
    /// Jumping into history re-queues what was skipped over so the order survives; 0 is a
    /// no-op.</summary>
    [RelayCommand]
    private void JumpTo(int offset)
    {
        if (offset > 0) _player.PlayFromUpNextAt(offset - 1);
        else if (offset < 0) _player.PlayFromHistoryAt(-offset - 1);
    }

    // ── Browsing the carousel (GitHub #152) ───────────────────────────────────
    // Wheel, drag, arrow keys and clicks on a side card move the ROW, not the playback:
    // BrowseOffset is how many slots the centred card sits from the playing track, and Row
    // holds the fifteen cards around it. Clicking the centred card plays it. With Repeat
    // All on the row runs round: past the last up-next track come the ones this queue
    // already played, oldest first (what the wrap will play again).

    /// <summary>Cards each side of the carousel's centre.</summary>
    public const int RowSideSlots = 7;

    /// <summary>The carousel row: slot −7..+7 at index slot + 7, around the browsed card.</summary>
    public CoverFlowRowSlot[] Row { get; } =
        Enumerable.Range(0, 2 * RowSideSlots + 1).Select(_ => new CoverFlowRowSlot()).ToArray();

    /// <summary>The centred card of the carousel.</summary>
    public CoverFlowRowSlot FocusedSlot => Row[RowSideSlots];

    private int _browseOffset;

    /// <summary>Slots between the centred card and the playing track (+ = toward up next).
    /// 0 = the row follows playback.</summary>
    public int BrowseOffset
    {
        get => _browseOffset;
        private set
        {
            if (SetProperty(ref _browseOffset, value))
                OnPropertyChanged(nameof(IsBrowsing));
        }
    }

    public bool IsBrowsing => BrowseOffset != 0;

    /// <summary>Repeat All with a playing track and something to go round to.</summary>
    private bool Wraps(out int played, out int length)
    {
        played = _player.PlayedInThisQueueCount;
        length = played + 1 + _player.UpNext.Count;
        return _player.RepeatMode == RepeatMode.All && _player.CurrentTrack != null && length > 1;
    }

    /// <summary>Folds a position onto the Repeat All ring −<paramref name="played"/> ..
    /// <paramref name="length"/> − played − 1.</summary>
    internal static int Fold(int position, int played, int length)
    {
        var m = ((position + played) % length + length) % length;
        return m - played;
    }

    /// <summary>The track <paramref name="position"/> slots from the playing one (0 = playing,
    /// + = up next, − = history), or null past either end.</summary>
    private Track? TrackAt(int position)
    {
        if (position == 0) return _player.CurrentTrack;
        if (position > 0) return position <= _player.UpNext.Count ? _player.UpNext[position - 1] : null;
        return -position <= _player.History.Count ? _player.History[-position - 1] : null;
    }

    /// <summary>Move the row <paramref name="delta"/> cards (+ = toward up next) without
    /// touching playback. Stops at either end, or runs round with Repeat All. Returns
    /// whether the row moved.</summary>
    public bool BrowseBy(int delta)
    {
        if (delta == 0 || !HasQueue) return false;
        int target;
        if (Wraps(out var played, out var length))
        {
            target = Fold(BrowseOffset + delta, played, length);
        }
        else
        {
            target = Math.Clamp(BrowseOffset + delta, -_player.History.Count, _player.UpNext.Count);
            delta = target - BrowseOffset;
        }
        if (delta == 0) return false;
        BrowseOffset = target;
        FillRow();
        CarouselShifted?.Invoke(this, delta);
        return true;
    }

    /// <summary>Slide the row back so the playing track is centred again.</summary>
    [RelayCommand]
    private void ReturnToPlaying()
    {
        if (BrowseOffset == 0) return;
        var step = -BrowseOffset;
        BrowseOffset = 0;
        FillRow();
        CarouselShifted?.Invoke(this, step);
    }

    /// <summary>Play the centred card's track (no-op when it is already playing).</summary>
    [RelayCommand]
    private void PlayFocused()
    {
        if (FocusedSlot.Track == null || FocusedSlot.IsPlaying) return;
        JumpTo(BrowseOffset);
    }

    /// <summary>The centred card's title link: the playing track opens its album (as it
    /// always did), a browsed one plays.</summary>
    [RelayCommand]
    private void ActivateFocused()
    {
        if (FocusedSlot.IsPlaying) GoToAlbum();
        else PlayFocused();
    }

    /// <summary>The centred card's artist link.</summary>
    [RelayCommand]
    private void GoToFocusedArtist()
    {
        var artist = FocusedSlot.Track?.Artist;
        if (!string.IsNullOrWhiteSpace(artist))
            _viewArtistAction?.Invoke(artist);
    }

    /// <summary>Where the browsed track is after the queue changed: still centred if it is
    /// still in reach (the playing track first, then the copy nearest the old spot), else
    /// back on the playing track.</summary>
    private int RelocateBrowsed(Track? browsed, int oldOffset)
    {
        if (browsed == null || oldOffset == 0) return 0;
        if (ReferenceEquals(browsed, _player.CurrentTrack)) return 0;
        int lo, hi;
        if (Wraps(out var played, out _)) { lo = -played; hi = _player.UpNext.Count; }
        else { lo = -_player.History.Count; hi = _player.UpNext.Count; }
        var best = 0;
        var bestDistance = int.MaxValue;
        for (var p = lo; p <= hi; p++)
        {
            if (p == 0 || !ReferenceEquals(TrackAt(p), browsed)) continue;
            var d = Math.Abs(p - oldOffset);
            if (d < bestDistance) { bestDistance = d; best = p; }
        }
        return best;
    }

    /// <summary>Fill the fifteen carousel cards around <see cref="BrowseOffset"/>. On the
    /// Repeat All ring a short queue shows each track once (no card repeats itself).</summary>
    private void FillRow()
    {
        var wraps = Wraps(out var played, out var length);
        var left = wraps ? (length - 1) / 2 : RowSideSlots;
        var right = wraps ? length - 1 - left : RowSideSlots;
        for (var k = -RowSideSlots; k <= RowSideSlots; k++)
        {
            Track? track = null;
            var playing = false;
            if (k >= -left && k <= right)
            {
                var position = BrowseOffset + k;
                if (wraps) position = Fold(position, played, length);
                track = TrackAt(position);
                playing = position == 0 && track != null;
            }
            var slot = Row[k + RowSideSlots];
            slot.Track = track;
            slot.IsPlaying = playing;
            slot.IsMarked = playing && k != 0;
        }
    }

    partial void OnLayoutChanged(CoverFlowLayout value)
    {
        // Only the carousel browses; any other layout shows the playing track centred.
        if (BrowseOffset == 0) return;
        BrowseOffset = 0;
        FillRow();
        CarouselShifted?.Invoke(this, 0);
    }

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
        if (e.PropertyName is nameof(PlayerViewModel.CurrentTrack) or nameof(PlayerViewModel.State)
            or nameof(PlayerViewModel.RepeatMode))
        {
            Dispatcher.UIThread.Post(RefreshCarousel);
        }
    }

    /// <summary>
    /// Set by MainWindowViewModel when Cover Flow becomes (or stops being) the current
    /// view. This view-model is long-lived and subscribes in its constructor, so without
    /// the gate RefreshCarousel — which assigns ~60 [ObservableProperty] slots — ran on
    /// every queue mutation while the user was on Songs or Settings. Per-track AddNext /
    /// AddToQueue and playlist "Play Next All" each raise one event per track.
    /// </summary>
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive == value) return;
            _isActive = value;
            // Catch up on anything missed while hidden; a page opened anew shows the
            // playing track centred, not where it was last browsed to.
            if (value)
            {
                BrowseOffset = 0;
                RefreshCarousel();
            }
        }
    }

    private bool _isActive;

    private void OnQueueChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!_isActive) return;
        Dispatcher.UIThread.Post(RefreshCarousel);
    }

    private void RefreshCarousel()
    {
        var current = _player.CurrentTrack;
        var upNext = _player.UpNext;
        var history = _player.History;

        HasQueue = current != null || upNext.Count > 0;

        // Judge the slide BEFORE the slots move: the old row is still in place. The row
        // slides by how far its centred card moved (the playing track unless browsing).
        var oldFocused = FocusedSlot.Track;
        var oldPrev = new Track?[RowSideSlots];
        var oldNext = new Track?[RowSideSlots];
        for (var k = 0; k < RowSideSlots; k++)
        {
            oldPrev[k] = Row[RowSideSlots - 1 - k].Track;
            oldNext[k] = Row[RowSideSlots + 1 + k].Track;
        }
        BrowseOffset = RelocateBrowsed(oldFocused, BrowseOffset);

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

        FillRow();
        var newFocused = FocusedSlot.Track;
        if (!ReferenceEquals(oldFocused, newFocused))
            CarouselShifted?.Invoke(this, StepBetween(oldFocused, newFocused, oldPrev, oldNext));
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

/// <summary>One card of the Cover Flow carousel row.</summary>
public partial class CoverFlowRowSlot : ObservableObject
{
    [ObservableProperty] private Track? _track;

    /// <summary>This card is the playing track.</summary>
    [ObservableProperty] private bool _isPlaying;

    /// <summary>The playing track off the centre (the row was browsed away): its card
    /// carries the "now playing" mark so it can be found again.</summary>
    [ObservableProperty] private bool _isMarked;
}
