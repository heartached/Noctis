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
    }

    /// <summary>True when neither synced nor plain lyrics exist for the current track.</summary>
    public bool HasNoLyrics => Lyrics.LyricLines.Count == 0 && Lyrics.UnsyncedLines.Count == 0;

    // ── Form factor (driven by window size) ──

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIconForm), nameof(IsBarForm), nameof(IsCardForm),
        nameof(IsLargeIconForm), nameof(IsLyricsForm), nameof(SupportsDrawer))]
    private MiniPlayerForm _form = MiniPlayerForm.Bar;

    public bool IsIconForm => Form == MiniPlayerForm.Icon;
    public bool IsBarForm => Form == MiniPlayerForm.Bar;
    public bool IsCardForm => Form == MiniPlayerForm.Card;
    public bool IsLargeIconForm => Form == MiniPlayerForm.LargeIcon;
    public bool IsLyricsForm => Form == MiniPlayerForm.Lyrics;

    /// <summary>Bottom-sheet layers exist on every form with a "…" menu (not the tiny icon).</summary>
    public bool SupportsDrawer => Form is not MiniPlayerForm.Icon;

    /// <summary>
    /// Maps a window size to a form factor. Order matters: the tiny square wins first,
    /// then the short-and-wide bar, then the big split lyrics view, then tall cards.
    /// </summary>
    public static MiniPlayerForm ComputeForm(double width, double height)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
            return MiniPlayerForm.Card;

        if (width <= 230 && height <= 260) return MiniPlayerForm.Icon;
        if (height <= 210) return MiniPlayerForm.Bar;
        if (width >= 540 && height >= 320) return MiniPlayerForm.Lyrics;
        if (height / width >= 1.30) return MiniPlayerForm.LargeIcon;
        return MiniPlayerForm.Card;
    }

    /// <summary>Called by the window on every resize with the current client size.</summary>
    public void UpdateFromSize(double width, double height)
    {
        var next = ComputeForm(width, height);
        if (next == Form) return;

        Form = next;
        // A layer left open in a form that can't host it would linger invisibly
        // and swallow the first toggle back.
        if (!SupportsDrawer && Drawer != MiniDrawer.None)
            Drawer = MiniDrawer.None;
    }

    /// <summary>Canonical size for a form, used when a menu action jumps straight to it.</summary>
    public static (double Width, double Height) CanonicalSize(MiniPlayerForm form) => form switch
    {
        MiniPlayerForm.Icon => (176, 176),
        MiniPlayerForm.Bar => (420, 172),
        MiniPlayerForm.LargeIcon => (340, 520),
        MiniPlayerForm.Lyrics => (640, 384),
        _ => (340, 432),
    };

    /// <summary>Raised when a menu action wants the window resized into a specific form.</summary>
    public event Action<MiniPlayerForm>? FormResizeRequested;

    [RelayCommand]
    private void SwitchToLyricsForm() => FormResizeRequested?.Invoke(MiniPlayerForm.Lyrics);

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

    [ObservableProperty] private string _searchQuery = string.Empty;

    public BulkObservableCollection<Track> SearchResults { get; } = new();

    partial void OnSearchQueryChanged(string value)
    {
        // Debounce so per-keystroke filtering of a large library doesn't churn the UI.
        _searchDebounce?.Stop();
        _searchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _searchDebounce.Tick += (_, _) =>
        {
            _searchDebounce?.Stop();
            _searchDebounce = null;
            SearchResults.ReplaceAll(FilterTracks(_library.Tracks, SearchQuery, 30));
        };
        _searchDebounce.Start();
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

    /// <summary>Queue a search hit to play right after the current track.</summary>
    [RelayCommand]
    private void PlayResultNext(Track track) => Player.AddNext(track);

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
        QueuePreview.ReplaceAll(Player.UpNext.Take(QueuePreviewCap).ToList());
        QueuePreviewTruncated = Player.UpNext.Count > QueuePreviewCap;
    }

    partial void OnDrawerChanged(MiniDrawer value)
    {
        if (value == MiniDrawer.Queue)
            RefreshQueuePreview();
    }

    [RelayCommand]
    private void PlayFromQueue(Track track)
    {
        var index = Player.UpNext.IndexOf(track);
        if (index >= 0)
            Player.PlayFromUpNextAt(index);
    }
}
