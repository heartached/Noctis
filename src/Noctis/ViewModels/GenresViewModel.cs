using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services;

namespace Noctis.ViewModels;

/// <summary>
/// The Genres page (GitHub #220, MusicBee's genre browser as the example): every genre in the
/// library with its album and song counts. Multi-genre tags count under each of their genres
/// (<see cref="Track.SplitGenres"/>: ';' plus the Genre Separators setting, GitHub #260), spellings merge
/// case-insensitively, and untagged songs gather under "Unknown". Opening a genre shows its
/// albums and songs on the Albums page (<see cref="LibraryAlbumsViewModel.SetGenreFilter"/>).
/// </summary>
public partial class GenresViewModel : ViewModelBase, ISearchable, IDisposable
{
    private readonly ILibraryService _library;
    private readonly PlayerViewModel _player;

    private List<GenreRow> _allRows = new();
    private string _currentFilter = string.Empty;
    private bool _isDirty = true;
    private bool _isActive;
    private int _buildGeneration;
    private DispatcherTimer? _searchDebounce;
    private EventHandler? _libraryUpdatedHandler;

    /// <summary>The rows shown, in name order (Unknown last), narrowed by the search.</summary>
    public BulkObservableCollection<GenreRow> Rows { get; } = new();

    [ObservableProperty] private string _searchText = string.Empty;

    /// <summary>False until the first build lands, so the empty state doesn't flash on open.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private bool _isLoaded;

    public bool HasActiveFilter => !string.IsNullOrWhiteSpace(_currentFilter);
    public bool IsSearchCleared => string.IsNullOrEmpty(_currentFilter) && string.IsNullOrEmpty(SearchText);
    public bool IsEmpty => IsLoaded && Rows.Count == 0;

    /// <summary>A genre row was clicked: MainWindowViewModel opens the genre.</summary>
    public event EventHandler<string>? GenreOpened;

    public GenresViewModel(ILibraryService library, PlayerViewModel player)
    {
        _library = library;
        _player = player;
        Rows.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsEmpty));

        // Like the other library pages: rebuild at once only while shown; a scan raises
        // LibraryUpdated every ~1.5 s and a hidden page catches up when it is opened.
        _libraryUpdatedHandler = (_, _) =>
        {
            _isDirty = true;
            if (_isActive) Dispatcher.UIThread.Post(Refresh);
        };
        _library.LibraryUpdated += _libraryUpdatedHandler;
    }

    /// <summary>Set by MainWindowViewModel while this page is the current view.</summary>
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive == value) return;
            _isActive = value;
            if (value) Refresh();
        }
    }

    /// <summary>Rebuilds the rows off the UI thread when the library changed since the last build.</summary>
    public void Refresh()
    {
        if (!_isDirty && IsLoaded) return;
        _isDirty = false;
        var generation = Interlocked.Increment(ref _buildGeneration);
        ThreadPool.QueueUserWorkItem(_ =>
        {
            List<GenreRow> rows;
            try
            {
                rows = BuildRows(_library.Albums.ToList());
            }
            catch (InvalidOperationException)
            {
                // The album list changed under the snapshot (a scan publishing): the next
                // LibraryUpdated rebuilds.
                Dispatcher.UIThread.Post(() => _isDirty = true);
                return;
            }
            Dispatcher.UIThread.Post(() =>
            {
                if (Volatile.Read(ref _buildGeneration) != generation) return;
                _allRows = rows;
                IsLoaded = true;
                Rows.ReplaceAll(FilterRows(_allRows, _currentFilter));
            });
        });
    }

    public void ApplyFilter(string query)
    {
        if (SearchText != query) SearchText = query;
        _currentFilter = query ?? string.Empty;
        _searchDebounce?.Stop();
        OnPropertyChanged(nameof(HasActiveFilter));
        Rows.ReplaceAll(FilterRows(_allRows, _currentFilter));
    }

    partial void OnSearchTextChanged(string value)
    {
        if (value == _currentFilter) return;
        _searchDebounce ??= CreateDebounce();
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    private DispatcherTimer CreateDebounce()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            ApplyFilter(SearchText);
        };
        return timer;
    }

    [RelayCommand]
    private void OpenGenre(GenreRow? row)
    {
        if (row != null) GenreOpened?.Invoke(this, row.Name);
    }

    [RelayCommand]
    private void PlayGenre(GenreRow? row)
    {
        if (row == null) return;
        var tracks = TracksInGenre(_library.Albums, row.Name);
        if (tracks.Count > 0) _player.WithSource(PlaySource.ForGenre(row.Name)).PlayAll(tracks);
    }

    [RelayCommand]
    private void ShuffleGenre(GenreRow? row)
    {
        if (row == null) return;
        var tracks = TracksInGenre(_library.Albums, row.Name);
        if (tracks.Count > 0) _player.WithSource(PlaySource.ForGenre(row.Name)).PlayShuffled(tracks);
    }

    // ── Grouping (internal for tests) ──

    /// <summary>The genre keys a song counts under: each of its tags, or Unknown when it has none.</summary>
    internal static string[] GenreKeys(Track track)
    {
        var genres = Track.SplitGenres(track.Genre);
        return genres.Length == 0 ? new[] { GenreRow.UnknownKey } : genres;
    }

    /// <summary>True when <paramref name="track"/> belongs to <paramref name="genre"/> (case-insensitive;
    /// "Unknown" also takes the untagged songs). Allocation-free like <see cref="Track.SharesGenre"/>:
    /// a genre page filters every library song with it.</summary>
    public static bool TrackHasGenre(Track track, string genre)
    {
        var target = genre.AsSpan().Trim();
        if (GenreSeparators.Contains(track.Genre ?? string.Empty, default, target, out var tagged)) return true;
        return !tagged && GenreRow.IsUnknownKey(genre);
    }

    /// <summary>One row per genre: songs carrying it, albums with at least one such song.</summary>
    internal static List<GenreRow> BuildRows(IEnumerable<Album> albums)
    {
        var acc = new Dictionary<string, (string Name, int Albums, int Songs, long Ticks, string? Art)>(StringComparer.OrdinalIgnoreCase);
        var inAlbum = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var album in albums)
        {
            inAlbum.Clear();
            foreach (var track in album.Tracks ?? new List<Track>())
            {
                foreach (var genre in GenreKeys(track))
                {
                    var a = acc.TryGetValue(genre, out var found) ? found : (genre, 0, 0, 0L, null);
                    a.Songs++;
                    a.Ticks += track.Duration.Ticks;
                    if (inAlbum.Add(genre))
                    {
                        a.Albums++;
                        if (string.IsNullOrEmpty(a.Art) && !string.IsNullOrEmpty(album.ArtworkPath)) a.Art = album.ArtworkPath;
                    }
                    acc[genre] = a;
                }
            }
        }

        return acc.Values
            .OrderBy(a => GenreRow.IsUnknownKey(a.Name) ? 1 : 0)
            .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(a => new GenreRow
            {
                Name = GenreRow.IsUnknownKey(a.Name) ? GenreRow.UnknownKey : a.Name,
                AlbumCount = a.Albums,
                SongCount = a.Songs,
                TotalDuration = TimeSpan.FromTicks(a.Ticks),
                ArtworkPath = a.Art,
            })
            .ToList();
    }

    internal static List<GenreRow> FilterRows(List<GenreRow> rows, string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return rows.ToList();
        var q = query.Trim();
        return rows.Where(r => Helpers.SearchText.Matches(r.DisplayName, q) || Helpers.SearchText.Matches(r.Name, q)).ToList();
    }

    /// <summary>A genre's songs for Play/Shuffle: albums by album artist and year, each in
    /// disc/track order, keeping only the songs that carry the genre.</summary>
    public static List<Track> TracksInGenre(IEnumerable<Album> albums, string genre)
        => albums
            .Where(a => a.Tracks != null && a.Tracks.Any(t => TrackHasGenre(t, genre)))
            .OrderBy(a => a.Artist, StringComparer.OrdinalIgnoreCase)
            .ThenBy(a => a.Year)
            .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .SelectMany(AlbumTile.OrderedTracks)
            .Where(t => TrackHasGenre(t, genre))
            .ToList();

    public void Dispose()
    {
        if (_libraryUpdatedHandler != null)
        {
            _library.LibraryUpdated -= _libraryUpdatedHandler;
            _libraryUpdatedHandler = null;
        }
        _searchDebounce?.Stop();
    }
}
