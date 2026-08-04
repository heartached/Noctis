using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Models;
using Noctis.Services;

namespace Noctis.ViewModels;

/// <summary>One row in the command palette: an action with display metadata.</summary>
public sealed class PaletteItem
{
    public required string Title { get; init; }
    public string Subtitle { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public object? Icon { get; init; }
    public required Action Execute { get; init; }
}

/// <summary>
/// Drives the Ctrl+K command palette: fuzzy-matches pages, player/setting
/// toggles, and library tracks/albums/artists, then runs the chosen action.
/// </summary>
public partial class CommandPaletteViewModel : ViewModelBase
{
    private const int MaxResults = 14;

    private readonly MainWindowViewModel _main;
    private readonly ILibraryService _library;
    private readonly List<PaletteItem> _staticItems;

    [ObservableProperty] private string _query = string.Empty;
    [ObservableProperty] private int _selectedIndex;

    public ObservableCollection<PaletteItem> Results { get; } = new();

    /// <summary>Raised when the palette should close (item executed or dismissed).</summary>
    public event EventHandler? CloseRequested;

    public CommandPaletteViewModel(MainWindowViewModel main, ILibraryService library)
    {
        _main = main;
        _library = library;
        _staticItems = BuildStaticItems();
        Refresh();
    }

    // Debounced. Refresh() scans _library.Tracks, .Albums and .Artists in full;
    // that used to run on the dispatcher thread, synchronously, once per character
    // (~50k string comparisons plus allocations per keystroke on a 50k-track library,
    // so typing in Ctrl+K stuttered). The scan now runs in Task.Run, but the debounce
    // still bounds how often it starts.
    // 200ms matches the debounce every other search surface in the app already uses.
    private const int QueryDebounceMs = 200;
    private CancellationTokenSource? _queryDebounceCts;

    partial void OnQueryChanged(string value)
    {
        _queryDebounceCts?.Cancel();
        _queryDebounceCts?.Dispose();
        var cts = new CancellationTokenSource();
        _queryDebounceCts = cts;

        _ = DebouncedRefreshAsync(cts.Token);
    }

    private async Task DebouncedRefreshAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(QueryDebounceMs, token);
            if (token.IsCancellationRequested) return;
            Refresh();
        }
        catch (OperationCanceledException) { /* superseded by a newer keystroke */ }
    }

    private static object? Icon(string key) =>
        Application.Current?.TryFindResource(key, out var res) == true ? res : null;

    private List<PaletteItem> BuildStaticItems()
    {
        var items = new List<PaletteItem>();

        void Page(string title, string key, string icon) => items.Add(new PaletteItem
        {
            Title = title,
            Category = "Page",
            Icon = Icon(icon),
            Execute = () => _main.NavigateCommand.Execute(key),
        });

        Page("Go to Home", "home", "HomeIcon");
        Page("Go to Songs", "songs", "SongsIcon");
        Page("Go to Albums", "albums", "AlbumsIcon");
        Page("Go to Artists", "artists", "ArtistsIcon");
        Page("Go to Folders", "folders", "FolderIcon");
        Page("Go to Playlists", "playlists", "PlaylistsIcon");
        Page("Go to Favorites", "favorites", "HeartFillIcon");
        Page("Go to Statistics", "statistics", "StatisticsIcon");
        Page("Go to Queue", "queue", "PlaylistsIcon");
        Page("Go to Lyrics", "lyrics", "LyricsIcon");
        Page("Go to Settings", "settings", "SettingsIcon");

        void Action(string title, string icon, Action run, string subtitle = "") =>
            items.Add(new PaletteItem
            {
                Title = title,
                Subtitle = subtitle,
                Category = "Action",
                Icon = Icon(icon),
                Execute = run,
            });

        Action("Play / Pause", "PlayIcon", () => _main.Player.PlayPauseCommand.Execute(null));
        Action("Next track", "NextIcon", () => _main.Player.NextCommand.Execute(null));
        Action("Previous track", "PreviousIcon", () => _main.Player.PreviousCommand.Execute(null));
        Action("Toggle shuffle", "ShuffleIcon", () => _main.Player.ToggleShuffleCommand.Execute(null));
        Action("Cycle repeat mode", "RepeatAllIcon", () => _main.Player.CycleRepeatCommand.Execute(null));
        Action("Toggle crossfade", "SettingsIcon",
            () => _main.Settings.CrossfadeEnabled = !_main.Settings.CrossfadeEnabled,
            "Audio setting");
        Action("Toggle song transitions", "SettingsIcon",
            () => _main.Settings.SongTransitionsEnabled = !_main.Settings.SongTransitionsEnabled,
            "Audio setting");
        Action("Toggle animated covers", "SettingsIcon",
            () => _main.Settings.EnableAnimatedCovers = !_main.Settings.EnableAnimatedCovers,
            "Appearance setting");

        return items;
    }

    /// <summary>Stale-result guard: only the newest Refresh may touch Results.</summary>
    private int _refreshGeneration;

    private async void Refresh()
    {
        var query = Query.Trim();

        if (query.Length == 0)
        {
            // Invalidate any in-flight scan so a stale result can't overwrite this.
            Interlocked.Increment(ref _refreshGeneration);
            Results.Clear();
            foreach (var item in _staticItems.Take(MaxResults))
                Results.Add(item);
            SelectedIndex = Results.Count > 0 ? 0 : -1;
            return;
        }

        try
        {
            var generation = Interlocked.Increment(ref _refreshGeneration);

            // Resource lookups must stay on the UI thread; they are constant per
            // category, so resolve them once per refresh instead of once per match.
            var songIcon = Icon("SongsIcon");
            var albumIcon = Icon("AlbumsIcon");
            var artistIcon = Icon("ArtistsIcon");

            var staticItems = _staticItems;
            var library = _library;

            // Full-library scan runs off the dispatcher thread; matches are scored as
            // value tuples (no per-match PaletteItem/closure/subtitle allocations) and
            // only the winning MaxResults rows are materialized below. .NET 8's
            // OrderBy+Take is a partial sort, so this never fully sorts the match set.
            var top = await Task.Run(() =>
            {
                var scored = new List<(object Entity, string Title, int Score)>();

                foreach (var item in staticItems)
                {
                    var score = MatchScore(item.Title, query);
                    if (score > 0) scored.Add((item, item.Title, score + 5)); // slight bias to commands
                }

                foreach (var track in library.Tracks)
                {
                    var score = MatchScore(track.Title, query);
                    if (score > 0) scored.Add((track, track.Title, score));
                }

                foreach (var album in library.Albums)
                {
                    var score = MatchScore(album.Name, query);
                    if (score > 0) scored.Add((album, album.Name, score));
                }

                foreach (var artist in library.Artists)
                {
                    var score = MatchScore(artist.Name, query);
                    if (score > 0) scored.Add((artist, artist.Name, score));
                }

                return scored
                    .OrderByDescending(x => x.Score)
                    .ThenBy(x => x.Title, StringComparer.OrdinalIgnoreCase)
                    .Take(MaxResults)
                    .ToList();
            });

            if (generation != _refreshGeneration) return;

            Results.Clear();
            foreach (var (entity, _, _) in top)
            {
                var row = entity switch
                {
                    PaletteItem item => item,
                    Track t => new PaletteItem
                    {
                        Title = t.Title,
                        Subtitle = $"{t.ArtistDisplay} · Song",
                        Category = "Song",
                        Icon = songIcon,
                        Execute = () => _main.Player.ReplaceQueueAndPlay(new List<Track> { t }, 0),
                    },
                    Album a => new PaletteItem
                    {
                        Title = a.Name,
                        Subtitle = $"{a.Artist} · Album",
                        Category = "Album",
                        Icon = albumIcon,
                        Execute = () => _main.OpenAlbumDetail(a),
                    },
                    Artist ar => new PaletteItem
                    {
                        Title = ar.Name,
                        Subtitle = "Artist",
                        Category = "Artist",
                        Icon = artistIcon,
                        Execute = () => _main.OpenArtistByName(ar.Name),
                    },
                    _ => null,
                };
                if (row != null) Results.Add(row);
            }

            SelectedIndex = Results.Count > 0 ? 0 : -1;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Palette] Refresh failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Simple ranked matcher: full-prefix beats word-prefix beats substring.
    /// Returns 0 for no match.
    /// </summary>
    public static int MatchScore(string candidate, string query)
    {
        if (string.IsNullOrEmpty(candidate) || string.IsNullOrEmpty(query))
            return 0;

        if (candidate.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            return 100;

        var idx = candidate.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
            return 0;

        // Word-boundary match (after a space/punctuation) ranks above mid-word.
        return idx > 0 && !char.IsLetterOrDigit(candidate[idx - 1]) ? 60 : 30;
    }

    public void MoveSelection(int delta)
    {
        if (Results.Count == 0) return;
        SelectedIndex = Math.Clamp(SelectedIndex + delta, 0, Results.Count - 1);
    }

    [RelayCommand]
    private void ExecuteSelected()
    {
        if (SelectedIndex >= 0 && SelectedIndex < Results.Count)
            ExecuteItem(Results[SelectedIndex]);
    }

    [RelayCommand]
    private void ExecuteItem(PaletteItem? item)
    {
        if (item == null) return;
        CloseRequested?.Invoke(this, EventArgs.Empty);
        item.Execute();
    }

    [RelayCommand]
    private void Dismiss() => CloseRequested?.Invoke(this, EventArgs.Empty);
}
