using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Models;
using Noctis.Services;
using Noctis.Services.MetadataSearch;

namespace Noctis.ViewModels;

/// <summary>
/// "Playlist from a Genre or Artist" (Discord, Cosmo 11-02: "an easier way to make playlists,
/// maybe by genre, or an AI DJ makes one"). Lists the library's genres or artists with their
/// song counts; each row makes a playlist of all its songs in one click, or a mix of about 50
/// built offline by <see cref="MixPlaylistBuilder"/>.
/// </summary>
public partial class QuickPlaylistViewModel : ViewModelBase
{
    private static string L(string key) => Localization.Loc.T(key);
    private static string L(string key, params object[] args) => Localization.Loc.T(key, args);

    /// <summary>A genre or artist and how many songs it has.</summary>
    public sealed record Entry(string Name, int Count)
    {
        public string CountText => Count == 1 ? L("QuickPlaylist.OneSong") : L("QuickPlaylist.Songs", Count);
        internal string Key { get; init; } = string.Empty;
    }

    private readonly IReadOnlyList<Track> _library;
    private readonly Func<string, IReadOnlyList<Track>, Task> _create;
    private readonly IReadOnlyList<Entry> _genres;
    private readonly IReadOnlyList<Entry> _artists;

    /// <summary>False: the genre list; true: the artist list.</summary>
    [ObservableProperty] private bool _showArtists;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private IReadOnlyList<Entry> _entries = Array.Empty<Entry>();
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private bool _isBusy;

    public bool ShowGenres => !ShowArtists;
    public bool IsEmpty => Entries.Count == 0;
    public string SearchWatermark => ShowArtists ? L("QuickPlaylist.SearchArtists") : L("QuickPlaylist.SearchGenres");

    public event EventHandler? Closed;

    /// <param name="library">The songs to choose from.</param>
    /// <param name="create">Makes and opens the playlist (name, songs in order).</param>
    public QuickPlaylistViewModel(IReadOnlyList<Track> library, Func<string, IReadOnlyList<Track>, Task> create)
    {
        _library = library;
        _create = create;
        _genres = Count(library.SelectMany(t => Track.SplitGenres(t.Genre)));
        _artists = Count(library.Select(t => Track.GetPrimaryArtist(t.Artist)));
        Refresh();
    }

    // Most songs first; the name each group shows is its most common spelling.
    private static IReadOnlyList<Entry> Count(IEnumerable<string> names)
        => names.Where(n => !string.IsNullOrWhiteSpace(n) && !IsUnknown(n))
            .GroupBy(n => n.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => new Entry(g.GroupBy(x => x.Trim()).OrderByDescending(x => x.Count()).First().Key, g.Count())
            {
                Key = MatchText.Fold(g.Key),
            })
            .OrderByDescending(e => e.Count)
            .ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    private static bool IsUnknown(string name)
        => name.Equals("Unknown Artist", StringComparison.OrdinalIgnoreCase)
           || name.Equals("Unknown Genre", StringComparison.OrdinalIgnoreCase);

    partial void OnShowArtistsChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowGenres));
        OnPropertyChanged(nameof(SearchWatermark));
        Refresh();
    }

    partial void OnSearchTextChanged(string value) => Refresh();
    partial void OnEntriesChanged(IReadOnlyList<Entry> value) => OnPropertyChanged(nameof(IsEmpty));

    private void Refresh()
    {
        var all = ShowArtists ? _artists : _genres;
        var q = MatchText.Fold(SearchText);
        Entries = q.Length == 0 ? all : all.Where(e => e.Key.Contains(q, StringComparison.Ordinal)).ToList();
    }

    [RelayCommand]
    private void ShowGenreList() => ShowArtists = false;

    [RelayCommand]
    private void ShowArtistList() => ShowArtists = true;

    /// <summary>Every song of the genre or artist, by artist, album and track order.</summary>
    internal IReadOnlyList<Track> AllSongsOf(Entry entry)
    {
        var name = new[] { entry.Name };
        return _library
            .Where(t => ShowArtists
                ? string.Equals(Track.GetPrimaryArtist(t.Artist), entry.Name, StringComparison.OrdinalIgnoreCase)
                : Track.SharesGenre(name, t.Genre))
            .OrderBy(t => t.AlbumArtist, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(t => t.Year)
            .ThenBy(t => t.Album, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(t => t.DiscNumber)
            .ThenBy(t => t.TrackNumber)
            .ToList();
    }

    internal MixSeed SeedOf(Entry entry)
        => ShowArtists ? MixSeed.FromArtist(entry.Name) : MixSeed.FromGenre(entry.Name);

    [RelayCommand]
    private Task AllSongs(Entry? entry)
        => entry is null ? Task.CompletedTask : CreateAsync(entry.Name, AllSongsOf(entry));

    [RelayCommand]
    private Task Mix(Entry? entry)
    {
        if (entry is null) return Task.CompletedTask;
        var seed = SeedOf(entry);
        return CreateAsync(seed.PlaylistName, MixPlaylistBuilder.Build(seed, _library));
    }

    private async Task CreateAsync(string name, IReadOnlyList<Track> tracks)
    {
        if (IsBusy) return;
        if (tracks.Count == 0)
        {
            StatusMessage = L("QuickPlaylist.Nothing");
            return;
        }
        IsBusy = true;
        try
        {
            await _create(name, tracks);
            Closed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            StatusMessage = L("QuickPlaylist.Failed", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Close() => Closed?.Invoke(this, EventArgs.Empty);
}
