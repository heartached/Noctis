using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Models;
using Noctis.Services;

namespace Noctis.ViewModels;

/// <summary>
/// ViewModel for the genres library view.
/// Groups tracks by genre and displays them as colored tiles.
/// </summary>
public partial class LibraryGenresViewModel : ViewModelBase, ISearchable
{
    private readonly ILibraryService _library;
    private readonly PlayerViewModel _player;

    private List<GenreItem> _allGenres = new();
    private string _currentFilter = string.Empty;
    private DispatcherTimer? _searchDebounce;

    [ObservableProperty] private bool _isSearchVisible = false;
    [ObservableProperty] private string _searchText = string.Empty;

    /// <summary>Saved scroll offset for restoring position after navigation.</summary>
    public double SavedScrollOffset { get; set; }

    /// <summary>Filtered genre items for display.</summary>
    public ObservableCollection<GenreItem> FilteredGenres { get; } = new();

    /// <summary>Fires when the user wants to view tracks for a specific genre.</summary>
    public event EventHandler<GenreItem>? GenreOpened;

    // Deterministic color palette for genre tiles
    private static readonly string[] GenreColors =
    {
        "#E07A5F", "#4ECDC4", "#45B7D1", "#BB8FCE",
        "#52B788", "#F7DC6F", "#FF6B6B", "#85C1E2",
        "#F8B500", "#98D8C8", "#FFA07A", "#81B29A",
        "#6C5B7B", "#F67280", "#C06C84", "#355C7D"
    };

    public LibraryGenresViewModel(ILibraryService library, PlayerViewModel player)
    {
        _library = library;
        _player = player;

        _library.LibraryUpdated += (_, _) => Dispatcher.UIThread.Post(Refresh);
    }

    public void Refresh()
    {
        // Group all tracks by genre
        _allGenres = _library.Tracks
            .Where(t => !string.IsNullOrWhiteSpace(t.Genre))
            .GroupBy(t => t.Genre.Trim(), StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select((g, index) => new GenreItem
            {
                Name = g.Key,
                TrackCount = g.Count(),
                Color = GenreColors[index % GenreColors.Length],
                TrackIds = g.Select(t => t.Id).ToList()
            })
            .ToList();

        ApplyFilter(_currentFilter);
    }

    public void ApplyFilter(string query)
    {
        _currentFilter = query;

        FilteredGenres.Clear();

        var filtered = _allGenres.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(query))
        {
            filtered = filtered.Where(g =>
                g.Name.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var genre in filtered)
            FilteredGenres.Add(genre);
    }

    partial void OnSearchTextChanged(string value)
    {
        if (_searchDebounce == null)
        {
            _searchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _searchDebounce.Tick += (_, _) =>
            {
                _searchDebounce.Stop();
                ApplyFilter(SearchText);
            };
        }

        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    [RelayCommand]
    private void ToggleSearch()
    {
        IsSearchVisible = !IsSearchVisible;
        if (!IsSearchVisible)
        {
            SearchText = string.Empty;
        }
    }

    [RelayCommand]
    private void OpenGenre(GenreItem genre)
    {
        GenreOpened?.Invoke(this, genre);
    }

    [RelayCommand]
    private void PlayGenre(GenreItem genre)
    {
        var tracks = genre.TrackIds
            .Select(id => _library.GetTrackById(id))
            .Where(t => t != null)
            .Cast<Track>()
            .ToList();

        if (tracks.Count == 0) return;
        _player.ReplaceQueueAndPlay(tracks, 0);
    }
}

/// <summary>
/// Represents a genre grouping with its display properties.
/// </summary>
public class GenreItem
{
    public string Name { get; set; } = string.Empty;
    public int TrackCount { get; set; }
    public string Color { get; set; } = "#808080";
    public List<Guid> TrackIds { get; set; } = new();
}
