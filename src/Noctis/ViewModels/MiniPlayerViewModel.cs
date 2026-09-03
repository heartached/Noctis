using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services;

namespace Noctis.ViewModels;

/// <summary>The layout the mini player window is currently morphed into, derived from its size.</summary>
public enum MiniPlayerForm
{
    /// <summary>Tiny square: artwork only, hover reveals play/pause.</summary>
    Icon,
    /// <summary>Wide + short horizontal bar.</summary>
    Bar,
    /// <summary>Default vertical card (art thumb + info + transport).</summary>
    Card,
    /// <summary>Tall artwork-dominant card with a bottom volume bar.</summary>
    LargeIcon,
    /// <summary>Wide split view: controls left, synced lyrics right.</summary>
    Lyrics,
    /// <summary>Fixed design (Settings ▸ Mini Player Design): round cover over a light card.</summary>
    Pill,
    /// <summary>Fixed design (Settings ▸ Mini Player Design): disc peeking out of a light card.</summary>
    Sleeve,
}

/// <summary>Which bottom-sheet layer is open over the card ("…" menu actions).</summary>
public enum MiniDrawer { None, Search, Queue, Volume }

/// <summary>
/// State for the resizable Liqoria-style mini player: exposes the shared player /
/// lyrics / settings ViewModels, derives the window's form factor from its size,
/// and owns the bottom-sheet layers (library search, queue, volume).
/// </summary>
public partial class MiniPlayerViewModel : ViewModelBase
{
    private readonly ILibraryService _library;

    public PlayerViewModel Player { get; }
    public LyricsViewModel Lyrics { get; }
    public SettingsViewModel Settings { get; }

    public MiniPlayerViewModel(PlayerViewModel player, LyricsViewModel lyrics,
        SettingsViewModel settings, ILibraryService library)
    {
        Player = player;
        Lyrics = lyrics;
        Settings = settings;
        _library = library;

        // Lyrics empty-state for the lyrics form (either tab may hold the lines).
        Lyrics.LyricLines.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoLyrics));
        Lyrics.UnsyncedLines.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoLyrics));

        // Queue preview follows live queue edits, but only while its layer is open.
        Player.UpNext.CollectionChanged += (_, _) =>
        {
            if (Drawer == MiniDrawer.Queue) RefreshQueuePreview();
        };

        // The design picker (Settings ▸ Appearance) drives the form while a fixed design
        // is selected; the window follows Form the same way it follows a size change.
        Settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsViewModel.MiniPlayerStyle))
                ApplyStyle(resizeWindow: true);
        };
        if (StyleForm is { } styled)
            Form = styled;
    }

    // ── Fixed designs (Settings ▸ Mini Player Design) ──

    /// <summary>The selected design; Classic is the size-driven card.</summary>
    public MiniPlayerStyle Style => Settings.MiniPlayerStyleMode;

    /// <summary>True while a fixed design owns the form (window size no longer picks it).</summary>
    public bool IsStyleLocked => StyleForm != null;

    private MiniPlayerForm? StyleForm => Style switch
    {
        MiniPlayerStyle.Pill => MiniPlayerForm.Pill,
        MiniPlayerStyle.Sleeve => MiniPlayerForm.Sleeve,
        _ => null,
    };

    private double _lastWidth = double.NaN, _lastHeight = double.NaN;

    /// <summary>"…" menu ▸ Design segment: same setting the Settings picker writes.</summary>
    [RelayCommand]
    private void SetDesign(string name)
    {
        var style = MiniPlayerStyles.Parse(name);
        if (Settings.MiniPlayerStyleMode == style) return;
        Settings.MiniPlayerStyle = style.ToString();
    }

    /// <summary>Mouse wheel over a design card nudges the volume (5 per notch, like the
    /// playback bar's flyout).</summary>
    public void NudgeVolume(int notches)
    {
        if (notches == 0) return;
        Player.Volume = Math.Clamp(Player.Volume + notches * 5, 0, 100);
    }

    /// <summary>The classic form in use when a fixed design was picked, so Classic comes
    /// back as THAT form (the window restores its exact size too — see the window's
    /// pre-design capture). Running the design's own size through the thresholds
    /// turned every Card into a Bar on the way back.</summary>
    private MiniPlayerForm? _formBeforeDesign;

    private void ApplyStyle(bool resizeWindow)
    {
        OnPropertyChanged(nameof(Style));
        OnPropertyChanged(nameof(IsStyleLocked));
        if (Drawer != MiniDrawer.None) Drawer = MiniDrawer.None;
        if (StyleForm is { } styled)
        {
            if (!IsDesignForm && !IsLyricsForm) _formBeforeDesign = Form;
            // Resize first, while Form still names the form being left: the window
            // captures the outgoing size from it (same order as the Lyrics toggle).
            if (resizeWindow) FormResizeRequested?.Invoke(styled);
            Form = styled;
            return;
        }
        // Back to Classic: the form the design replaced, else whatever the size says.
        var classic = _formBeforeDesign ?? ComputeForm(_lastWidth, _lastHeight);
        _formBeforeDesign = null;
        if (resizeWindow) FormResizeRequested?.Invoke(classic);
        Form = classic;
    }

    /// <summary>True when neither synced nor plain lyrics exist for the current track.</summary>
    public bool HasNoLyrics => Lyrics.LyricLines.Count == 0 && Lyrics.UnsyncedLines.Count == 0;

    // ── Form factor (driven by window size) ──

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIconForm), nameof(IsBarForm), nameof(IsCardForm),
        nameof(IsLargeIconForm), nameof(IsLyricsForm), nameof(IsPillForm), nameof(IsSleeveForm),
        nameof(IsDesignForm), nameof(SupportsDrawer), nameof(ShowVolumeMenuItem), nameof(LyricsMenuLabel))]
    private MiniPlayerForm _form = MiniPlayerForm.Bar;

    public bool IsIconForm => Form == MiniPlayerForm.Icon;
    public bool IsBarForm => Form == MiniPlayerForm.Bar;
    public bool IsCardForm => Form == MiniPlayerForm.Card;
    public bool IsLargeIconForm => Form == MiniPlayerForm.LargeIcon;
    public bool IsLyricsForm => Form == MiniPlayerForm.Lyrics;
    public bool IsPillForm => Form == MiniPlayerForm.Pill;
    public bool IsSleeveForm => Form == MiniPlayerForm.Sleeve;
    /// <summary>One of the fixed designs: their "…" menu carries the heart the classic
    /// forms show as a button.</summary>
    public bool IsDesignForm => Form is MiniPlayerForm.Pill or MiniPlayerForm.Sleeve;

    /// <summary>Bottom-sheet layers exist on every form with a "…" menu (not the tiny icon).
    /// Under a fixed design the sheet paints its own dark ground (see SyncChrome).</summary>
    public bool SupportsDrawer => Form is not MiniPlayerForm.Icon;

    /// <summary>The "…" menu's Volume item: needs a drawer, and the large icon has its own bar.</summary>
    public bool ShowVolumeMenuItem => SupportsDrawer && !IsLargeIconForm;

    /// <summary>Dead-band applied to the current form's thresholds during a live resize, in
    /// pixels and in aspect ratio. Without it a drag creeping along a boundary re-crosses it
    /// every few pixels and the cross-fade restarts on each crossing.</summary>
    private const double FormHysteresisPx = 10;
    private const double FormHysteresisRatio = 0.06;

    /// <summary>
    /// Maps a window size to a form factor. Order matters: the tiny square wins first,
    /// then the short-and-wide bar, then the big split lyrics view, then tall cards.
    /// </summary>
    public static MiniPlayerForm ComputeForm(double width, double height)
        => ComputeForm(width, height, null, 0, 0);

    /// <summary>
    /// Hysteresis overload. Every threshold moves in whichever direction KEEPS
    /// <paramref name="current"/>: its own region grows by the band, every other region
    /// shrinks by it. Pass a null current (or zero bands) for the raw thresholds.
    /// </summary>
    public static MiniPlayerForm ComputeForm(
        double width, double height, MiniPlayerForm? current, double band, double ratioBand)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
            return MiniPlayerForm.Card;

        var iconW = current == MiniPlayerForm.Icon ? 230 + band : 230 - band;
        var iconH = current == MiniPlayerForm.Icon ? 260 + band : 260 - band;
        var barH = current == MiniPlayerForm.Bar ? 210 + band : 210 - band;
        var lyricsW = current == MiniPlayerForm.Lyrics ? 540 - band : 540 + band;
        var lyricsH = current == MiniPlayerForm.Lyrics ? 320 - band : 320 + band;
        var tallRatio = current == MiniPlayerForm.LargeIcon ? 1.30 - ratioBand : 1.30 + ratioBand;

        if (width <= iconW && height <= iconH) return MiniPlayerForm.Icon;
        if (height <= barH) return MiniPlayerForm.Bar;
        if (width >= lyricsW && height >= lyricsH) return MiniPlayerForm.Lyrics;
        if (height / width >= tallRatio) return MiniPlayerForm.LargeIcon;
        return MiniPlayerForm.Card;
    }

    /// <summary>Called by the window on every resize with the current client size.</summary>
    public void UpdateFromSize(double width, double height)
    {
        _lastWidth = width;
        _lastHeight = height;
        // A fixed design keeps its form whatever the window does; Lyrics opened from a
        // design is handed back explicitly by ToggleLyricsForm.
        if (IsStyleLocked) return;

        var next = ComputeForm(width, height, Form, FormHysteresisPx, FormHysteresisRatio);
        if (next == Form) return;

        Form = next;
        // A layer left open in a form that can't host it would linger invisibly
        // and swallow the first toggle back.
        if (!SupportsDrawer && Drawer != MiniDrawer.None)
            Drawer = MiniDrawer.None;
    }

    /// <summary>Canonical size for a form, used when a menu action jumps straight to it.
    /// Proportions follow the iOS Live-Activity references: the Bar is chunkier than a
    /// strip (~2.2:1) and the Lyrics split is closer to 1.55:1 so the flow gets more
    /// vertical room. Each size must still land in its own form through ComputeForm.</summary>
    public static (double Width, double Height) CanonicalSize(MiniPlayerForm form) => form switch
    {
        MiniPlayerForm.Icon => (176, 176),
        MiniPlayerForm.Bar => (420, 188),
        MiniPlayerForm.LargeIcon => (340, 520),
        MiniPlayerForm.Lyrics => (640, 412),
        MiniPlayerForm.Pill => (352, 140),
        MiniPlayerForm.Sleeve => (340, 372),
        _ => (340, 432),
    };

    /// <summary>Raised when a menu action wants the window resized into a specific form.</summary>
    public event Action<MiniPlayerForm>? FormResizeRequested;

    /// <summary>Form to return to when lyrics are dismissed. Only ever written on the way
    /// in, so the split view always hands back the layout the user actually came from.</summary>
    private MiniPlayerForm _formBeforeLyrics = MiniPlayerForm.Card;

    /// <summary>The "…" menu keeps this item in the list while lyrics are open — it is the
    /// only way back out — so its label has to say which direction it goes.</summary>
    public string LyricsMenuLabel => IsLyricsForm ? "Hide Lyrics" : "Lyrics";

    [RelayCommand]
    private void ToggleLyricsForm()
    {
        if (IsLyricsForm)
        {
            // Icon has no "…" menu, so it can't be the form we came from; Lyrics itself
            // would be a no-op. Card is the neutral fallback either way — unless a fixed
            // design is selected, which always takes the window back.
            var back = StyleForm ?? (_formBeforeLyrics is MiniPlayerForm.Lyrics or MiniPlayerForm.Icon
                ? MiniPlayerForm.Card
                : _formBeforeLyrics);
            FormResizeRequested?.Invoke(back);
            // Size no longer drives the form under a design: hand it back explicitly.
            if (IsStyleLocked) Form = back;
            return;
        }

        _formBeforeLyrics = Form;
        FormResizeRequested?.Invoke(MiniPlayerForm.Lyrics);
        if (IsStyleLocked) Form = MiniPlayerForm.Lyrics;
    }

    // ── Bottom-sheet layers ──

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDrawerOpen), nameof(IsSearchDrawer),
        nameof(IsQueueDrawer), nameof(IsVolumeDrawer))]
    private MiniDrawer _drawer = MiniDrawer.None;

    public bool IsDrawerOpen => Drawer != MiniDrawer.None;
    public bool IsSearchDrawer => Drawer == MiniDrawer.Search;
    public bool IsQueueDrawer => Drawer == MiniDrawer.Queue;
    public bool IsVolumeDrawer => Drawer == MiniDrawer.Volume;

    [RelayCommand]
    private void ToggleSearchDrawer() => Drawer = Drawer == MiniDrawer.Search ? MiniDrawer.None : MiniDrawer.Search;

    [RelayCommand]
    private void ToggleQueueDrawer() => Drawer = Drawer == MiniDrawer.Queue ? MiniDrawer.None : MiniDrawer.Queue;

    [RelayCommand]
    private void ToggleVolumeDrawer() => Drawer = Drawer == MiniDrawer.Volume ? MiniDrawer.None : MiniDrawer.Volume;

    [RelayCommand]
    private void CloseDrawer() => Drawer = MiniDrawer.None;

    // ── Library search (Search layer) ──

    private DispatcherTimer? _searchDebounce;
    // Bumped per fill so an in-flight streamed fill (StreamingFill) yields to a newer one.
    private int _searchFillGeneration;
    private int _queueFillGeneration;

    /// <summary>True while the window is sliding the drawer open/closed (set by the window).
    /// Row streaming waits for it: rows inflated mid-slide are re-laid-out on every resize
    /// tick, which is what the "Search lags for a second" hitch was made of.</summary>
    public bool IsDrawerAnimating { get; set; }

    /// <summary>Rows the drawer shows, for both a query's hits and the empty-query shuffle.</summary>
    private const int SearchResultCap = 30;

    [ObservableProperty] private string _searchQuery = string.Empty;

    public BulkObservableCollection<Track> SearchResults { get; } = new();

    partial void OnSearchQueryChanged(string value)
    {
        _searchDebounce?.Stop();

        // Cleared back to empty: no filtering to debounce, and leaving the previous
        // query's hits on screen for another 150ms reads as a stuck list. Drop
        // straight back to a fresh shuffle.
        if (string.IsNullOrWhiteSpace(value))
        {
            _searchDebounce = null;
            ShowShuffledSuggestions();
            return;
        }

        // Debounce so per-keystroke filtering of a large library doesn't churn the UI.
        _searchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _searchDebounce.Tick += (_, _) =>
        {
            _searchDebounce?.Stop();
            _searchDebounce = null;
            StreamingFill.Into(SearchResults, FilterTracks(_library.Tracks, SearchQuery, SearchResultCap),
                ++_searchFillGeneration, () => _searchFillGeneration, gate: () => !IsDrawerAnimating);
        };
        _searchDebounce.Start();
    }

    /// <summary>
    /// Empty-query state: a random slice of the library so the drawer always opens
    /// onto something tappable instead of a blank sheet.
    /// </summary>
    private void ShowShuffledSuggestions()
        => StreamingFill.Into(SearchResults, ShuffleSample(_library.Tracks, SearchResultCap, Random.Shared),
            ++_searchFillGeneration, () => _searchFillGeneration, gate: () => !IsDrawerAnimating);

    /// <summary>
    /// <paramref name="limit"/> tracks drawn at random from <paramref name="tracks"/>, or all
    /// of them when the library is smaller. Partial Fisher–Yates over an index array: draws are
    /// distinct, every track is equally likely, and the caller's collection — which is the live
    /// <see cref="ILibraryService.Tracks"/> backing the Songs list — is never reordered.
    /// </summary>
    public static List<Track> ShuffleSample(IReadOnlyList<Track> tracks, int limit, Random rng)
    {
        var take = Math.Min(limit, tracks.Count);
        if (take <= 0) return new List<Track>();

        var indices = new int[tracks.Count];
        for (var i = 0; i < indices.Length; i++)
            indices[i] = i;

        var sample = new List<Track>(take);
        for (var i = 0; i < take; i++)
        {
            var pick = rng.Next(i, indices.Length);
            (indices[i], indices[pick]) = (indices[pick], indices[i]);
            sample.Add(tracks[indices[i]]);
        }

        return sample;
    }

    /// <summary>
    /// Title/artist/album match over the tracks' cached normalized search keys (same
    /// normalization as app-wide search); prefix matches on title, then artist, rank
    /// above plain substring hits so the expected row lands on top.
    /// </summary>
    public static List<Track> FilterTracks(IReadOnlyList<Track> tracks, string query, int limit)
    {
        var q = SearchText.Normalize(query.Trim());
        if (q.Length == 0) return new List<Track>();

        var ranked = new List<(int Rank, Track Track)>();
        foreach (var t in tracks)
        {
            int rank;
            if (t.SearchTitleKey.StartsWith(q, StringComparison.Ordinal)) rank = 0;
            else if (t.SearchArtistKey.StartsWith(q, StringComparison.Ordinal)) rank = 1;
            else if (t.SearchTitleKey.Contains(q, StringComparison.Ordinal) ||
                     t.SearchArtistKey.Contains(q, StringComparison.Ordinal) ||
                     t.SearchAlbumKey.Contains(q, StringComparison.Ordinal)) rank = 2;
            else continue;

            ranked.Add((rank, t));
        }

        return ranked
            .OrderBy(r => r.Rank)
            .ThenBy(r => r.Track.Title, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .Select(r => r.Track)
            .ToList();
    }

    [RelayCommand]
    private void ClearSearch() => SearchQuery = string.Empty;

    /// <summary>Play a search hit immediately (front of queue + skip), keeping the rest of the queue.</summary>
    [RelayCommand]
    private void PlaySearchResult(Track track)
    {
        Player.AddNext(track);
        Player.NextCommand.Execute(null);
    }

    // ── Queue layer ──

    /// <summary>
    /// The queue can hold tens of thousands of tracks and the drawer's ItemsControl
    /// is not virtualized, so the layer shows a capped preview.
    /// </summary>
    private const int QueuePreviewCap = 100;

    public BulkObservableCollection<Track> QueuePreview { get; } = new();

    [ObservableProperty] private bool _queuePreviewTruncated;

    private void RefreshQueuePreview()
    {
        // Streamed (see StreamingFill): a hundred artwork rows inflated on the open
        // frame was the hitch on the Queue button.
        StreamingFill.Into(QueuePreview, Player.UpNext.Take(QueuePreviewCap).ToList(),
            ++_queueFillGeneration, () => _queueFillGeneration, gate: () => !IsDrawerAnimating);
        QueuePreviewTruncated = Player.UpNext.Count > QueuePreviewCap;
    }

    partial void OnDrawerChanged(MiniDrawer value)
    {
        if (value == MiniDrawer.Queue)
            RefreshQueuePreview();
        // Reshuffle per open so the sheet isn't the same thirty rows every time.
        // A live query survives a close/reopen, so only refill the empty state.
        else if (value == MiniDrawer.Search && string.IsNullOrWhiteSpace(SearchQuery))
            ShowShuffledSuggestions();
    }

    [RelayCommand]
    private void PlayFromQueue(Track track)
    {
        var index = Player.UpNext.IndexOf(track);
        if (index >= 0)
            Player.PlayFromUpNextAt(index);
    }
}
