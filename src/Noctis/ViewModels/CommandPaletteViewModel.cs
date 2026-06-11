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

    partial void OnQueryChanged(string value) => Refresh();

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

    private void Refresh()
    {
        var query = Query.Trim();
        Results.Clear();

        if (query.Length == 0)
        {
            foreach (var item in _staticItems.Take(MaxResults))
                Results.Add(item);
            SelectedIndex = Results.Count > 0 ? 0 : -1;
            return;
        }

        var scored = new List<(PaletteItem Item, int Score)>();

        foreach (var item in _staticItems)
        {
            var score = MatchScore(item.Title, query);
            if (score > 0) scored.Add((item, score + 5)); // slight bias to commands
        }

        foreach (var track in _library.Tracks)
        {
            var score = MatchScore(track.Title, query);
            if (score <= 0) continue;
            var t = track;
            scored.Add((new PaletteItem
            {
                Title = t.Title,
                Subtitle = $"{t.ArtistDisplay} · Song",
                Category = "Song",
                Icon = Icon("SongsIcon"),
                Execute = () => _main.Player.ReplaceQueueAndPlay(new List<Track> { t }, 0),
            }, score));
        }

        foreach (var album in _library.Albums)
        {
            var score = MatchScore(album.Name, query);
            if (score <= 0) continue;
            var a = album;
            scored.Add((new PaletteItem
            {
                Title = a.Name,
                Subtitle = $"{a.Artist} · Album",
                Category = "Album",
                Icon = Icon("AlbumsIcon"),
                Execute = () => _main.OpenAlbumDetail(a),
            }, score));
        }

        foreach (var artist in _library.Artists)
        {
            var score = MatchScore(artist.Name, query);
            if (score <= 0) continue;
            var name = artist.Name;
            scored.Add((new PaletteItem
            {
                Title = name,
                Subtitle = "Artist",
                Category = "Artist",
                Icon = Icon("ArtistsIcon"),
                Execute = () => _main.OpenArtistDetailByName(name),
            }, score));
        }

        foreach (var (item, _) in scored
                     .OrderByDescending(x => x.Score)
                     .ThenBy(x => x.Item.Title, StringComparer.OrdinalIgnoreCase)
                     .Take(MaxResults))
            Results.Add(item);

        SelectedIndex = Results.Count > 0 ? 0 : -1;
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
