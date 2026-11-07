using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.Localization;

namespace Noctis.Mobile.ViewModels;

/// <summary>Which results Search shows: everything, or one kind with a longer list.</summary>
public enum SearchScope { All, Songs, Albums, Artists, Playlists }

/// <summary>
/// The Search tab's root. Re-runs on every keystroke, scope or sort change and after every
/// library refresh. Under All the results come grouped — the artist the query names with their
/// songs, then Songs, Albums, Artists, Playlists — and a scope chip shows one kind in full.
/// </summary>
public sealed partial class SearchPageViewModel : ObservableObject, ISortable
{
    // Under All: a short list per kind (the chip above shows the rest).
    internal const int SongCap = 25;
    internal const int AlbumCap = 12;
    internal const int ArtistCap = 12;
    internal const int PlaylistCap = 8;

    /// <summary>The longer list a scope chip shows.</summary>
    internal const int ScopedCap = 300;

    /// <summary>The featured artist's songs shown under their card.</summary>
    internal const int FeaturedSongCap = 5;

    /// <summary>How long an artist result stays before its photo is asked for: the next
    /// keystroke usually replaces it first.</summary>
    internal static readonly TimeSpan PhotoDelay = TimeSpan.FromMilliseconds(350);

    public SearchPageViewModel(ShellViewModel shell)
    {
        Shell = shell;
        Photos = new ArtistPhotoRequests(() => shell.ArtistPhotos);
        SortChoices = new[]
        {
            new SortChoice("Mobile.Sort.MostRelevant", SearchSort.Relevance) { IsSelected = true },
            new SortChoice("Mobile.RecentlyAdded", SearchSort.RecentlyAdded),
            new SortChoice("Mobile.Sort.ReleaseYear", SearchSort.ReleaseYear),
            new SortChoice("Mobile.RecentlyPlayed", SearchSort.RecentlyPlayed),
            new SortChoice("Mobile.Sort.MostPlayed", SearchSort.MostPlayed),
            new SortChoice("Mobile.Sort.Title", SearchSort.Title),
        };
        shell.Library.Refreshed += (_, _) =>
        {
            _artistArtwork = null;
            Run();
        };
    }

    // The artist → cover map the artist results read, built once per library state (it walks
    // every song) instead of at every keystroke.
    private IReadOnlyDictionary<string, string?>? _artistArtwork;

    private IReadOnlyDictionary<string, string?> ArtistArtwork() =>
        _artistArtwork ??= MobileLibrary.ArtistArtwork(Shell.Library.Service);

    /// <summary>
    /// Typing: a keystroke within this long of the one before waits until the typing pauses
    /// for it, so a fast word is one search and one re-realised result list, not one per letter
    /// (the first letter and a cleared field still answer at once). Zero (the default, tests)
    /// searches every keystroke; the phone sets it.
    /// </summary>
    public TimeSpan TypingDebounce { get; set; }

    private long _lastKeystrokeTicks = long.MinValue / 2;
    private Avalonia.Threading.DispatcherTimer? _typingTimer;

    /// <summary>Searches run so far (tests count them).</summary>
    internal int RunCount { get; private set; }

    /// <summary>A search is waiting for the typing to pause (tests read it).</summary>
    internal bool HasPendingSearch => _typingTimer?.IsEnabled == true;

    private void OnTyped()
    {
        var debounce = TypingDebounce;
        // Since the previous keystroke: the first letter after a pause answers at once.
        var sinceLast = System.Diagnostics.Stopwatch.GetElapsedTime(_lastKeystrokeTicks);
        _lastKeystrokeTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        if (debounce <= TimeSpan.Zero || !HasQuery || (sinceLast >= debounce && _typingTimer?.IsEnabled != true))
        {
            _typingTimer?.Stop();
            Run();
            return;
        }
        if (_typingTimer == null)
        {
            _typingTimer = new Avalonia.Threading.DispatcherTimer();
            _typingTimer.Tick += (_, _) =>
            {
                _typingTimer!.Stop();
                Run();
            };
        }
        _typingTimer.Stop();
        _typingTimer.Interval = debounce;
        _typingTimer.Start();
    }

    public ShellViewModel Shell { get; }

    /// <summary>The artist results' photo asks; a new search drops the last one's.</summary>
    internal ArtistPhotoRequests Photos { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasQuery), nameof(ShowPrompt))]
    private string _query = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAllScope), nameof(IsSongsScope), nameof(IsAlbumsScope), nameof(IsArtistsScope),
        nameof(IsPlaylistsScope))]
    private SearchScope _scope = SearchScope.All;

    public bool IsAllScope => Scope == SearchScope.All;
    public bool IsSongsScope => Scope == SearchScope.Songs;
    public bool IsAlbumsScope => Scope == SearchScope.Albums;
    public bool IsArtistsScope => Scope == SearchScope.Artists;
    public bool IsPlaylistsScope => Scope == SearchScope.Playlists;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSorted))]
    private SearchSort _sort = SearchSort.Relevance;

    /// <summary>The sort button lights up away from the default order.</summary>
    public bool IsSorted => Sort != SearchSort.Relevance;

    public BulkObservableCollection<Track> Songs { get; } = new();
    public BulkObservableCollection<Album> Albums { get; } = new();
    public BulkObservableCollection<ArtistListItem> Artists { get; } = new();
    public BulkObservableCollection<PlaylistResult> Playlists { get; } = new();

    /// <summary>The artist the query names (All only); <see cref="FeaturedSongs"/> are their most played.
    /// Compared by reference: ArtistListItem is a record, and the next keystroke's item for the
    /// same artist (with the photo the phone now has) would otherwise equal the old one and be dropped.</summary>
    public ArtistListItem? Featured
    {
        get => _featured;
        private set
        {
            if (ReferenceEquals(_featured, value)) return;
            _featured = value;
            OnPropertyChanged();
        }
    }
    private ArtistListItem? _featured;
    public BulkObservableCollection<Track> FeaturedSongs { get; } = new();

    [ObservableProperty] private bool _hasSongs;
    [ObservableProperty] private bool _hasAlbums;
    [ObservableProperty] private bool _hasArtists;
    [ObservableProperty] private bool _hasPlaylists;
    [ObservableProperty] private bool _hasFeatured;

    public bool HasQuery => !string.IsNullOrWhiteSpace(Query);
    public bool ShowPrompt => !HasQuery;
    public bool ShowNoResults => HasQuery && !HasSongs && !HasAlbums && !HasArtists && !HasPlaylists && !HasFeatured;

    /// <summary>Albums as a horizontal row under All, as a two-column grid under the Albums chip.</summary>
    public bool ShowAlbumRow => HasAlbums && IsAllScope;
    public bool ShowAlbumGrid => HasAlbums && IsAlbumsScope;

    partial void OnQueryChanged(string value) => OnTyped();
    partial void OnScopeChanged(SearchScope value) => Run();
    partial void OnSortChanged(SearchSort value) => Run();

    internal void Run()
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try { RunCore(); }
        finally { Services.FrameStallMonitor.AddTime("search run", System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds); }
    }

    private void RunCore()
    {
        _typingTimer?.Stop();
        RunCount++;
        bool Shows(SearchScope kind) => Scope == SearchScope.All || Scope == kind;
        int Cap(int allCap) => Scope == SearchScope.All ? allCap : ScopedCap;

        var results = MobileSearch.Find(Shell.Library.Service, Query, Cap(SongCap), Cap(AlbumCap), Cap(ArtistCap), Sort,
            Shell.Library.Playlists, Cap(PlaylistCap), ArtistArtwork);

        // Each list is replaced only when its results differ: a keystroke that narrows nothing
        // (and every library refresh) used to Reset all of them, re-realising every row and
        // every album tile — the typing lag on a broad query.
        MobileLibrary.ReplaceIfChanged(Albums, Shows(SearchScope.Albums) ? results.Albums : Array.Empty<Album>());
        MobileLibrary.ReplaceIfChanged(Playlists, Shows(SearchScope.Playlists)
            ? results.Playlists.Select(p => new PlaylistResult(p, Shell.Library.PlaylistArtwork(p))).ToList()
            : Array.Empty<PlaylistResult>());

        Photos.CancelAll();
        // The featured artist leads under All; under the Artists chip they are simply the first artist.
        var featured = results.Featured is { } f ? Photos.Fill(f) : null;
        var artists = results.Artists.Select(Photos.Fill).ToList();
        if (Scope == SearchScope.Artists && featured != null) artists.Insert(0, featured);
        MobileLibrary.ReplaceIfChanged(Artists, Shows(SearchScope.Artists) ? artists : new List<ArtistListItem>());
        Featured = Scope == SearchScope.All ? featured : null;
        MobileLibrary.ReplaceIfChanged(FeaturedSongs, Featured == null
            ? Array.Empty<Track>()
            : MobileLibrary.SongsBy(Shell.Library.Service, Featured.Name)
                .OrderByDescending(t => t.PlayCount)
                .ThenBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase)
                .Take(FeaturedSongCap)
                .ToList());
        if (Featured != null) Photos.Request(Featured, PhotoDelay);
        foreach (var artist in Artists) Photos.Request(artist, PhotoDelay);

        // Under All the featured artist's card already lists their top songs: the Songs section
        // repeated the same rows right under it (an artist's name matches their songs). It
        // keeps the rest; the Songs chip (and its ›) still lists every match.
        var shownSongs = Shows(SearchScope.Songs) ? results.Songs : Array.Empty<Track>();
        if (FeaturedSongs.Count > 0)
        {
            var inCard = new HashSet<Track>(FeaturedSongs, ReferenceEqualityComparer.Instance);
            shownSongs = shownSongs.Where(t => !inCard.Contains(t)).ToList();
        }
        MobileLibrary.ReplaceIfChanged(Songs, shownSongs);

        HasSongs = Songs.Count > 0;
        HasAlbums = Albums.Count > 0;
        HasArtists = Artists.Count > 0;
        HasPlaylists = Playlists.Count > 0;
        HasFeatured = Featured != null;
        OnPropertyChanged(nameof(ShowNoResults));
        OnPropertyChanged(nameof(ShowAlbumRow));
        OnPropertyChanged(nameof(ShowAlbumGrid));
    }

    /// <summary>A scope chip; tapping the chip in use goes back to All.</summary>
    [RelayCommand]
    private void SetScope(SearchScope scope) => Scope = Scope == scope ? SearchScope.All : scope;

    // ── Sort menu ──

    public IReadOnlyList<SortChoice> SortChoices { get; }
    public bool HasSortDirection => false;
    public bool IsDescending => false;
    /// <summary>The toolbar's sort button: the sort menu on the glass pop menu, out of the button.</summary>
    [RelayCommand]
    private void ShowSortMenu() => Shell.ShowOptions(OptionsMenuViewModel.ForSort(Shell, this));

    public void ChooseDirection(bool descending) { }

    public void ChooseSort(SortChoice choice)
    {
        foreach (var c in SortChoices) c.IsSelected = ReferenceEquals(c, choice);
        Sort = (SearchSort)choice.Key;
    }

    /// <summary>A song result plays the song results from it.</summary>
    [RelayCommand]
    private void PlaySong(Track? track)
    {
        if (track == null) return;
        var list = Songs.ToList();
        var index = list.IndexOf(track);
        if (index >= 0) Shell.Player.PlayTracks(list, index, Loc.T("Mobile.Search"));
    }

    /// <summary>A song under the featured artist plays that artist's top songs from it.</summary>
    [RelayCommand]
    private void PlayFeaturedSong(Track? track)
    {
        if (track == null || Featured == null) return;
        var list = FeaturedSongs.ToList();
        var index = list.IndexOf(track);
        if (index >= 0) Shell.Player.PlayTracks(list, index, Featured.Name);
    }

    /// <summary>The featured card's Play: all of the artist's songs, most played first.</summary>
    [RelayCommand]
    private void PlayFeatured()
    {
        if (Featured == null) return;
        var songs = MobileLibrary.SongsBy(Shell.Library.Service, Featured.Name).OrderByDescending(t => t.PlayCount).ToList();
        if (songs.Count > 0) Shell.Player.PlayTracks(songs, 0, Featured.Name);
    }

    [RelayCommand]
    private void ShuffleFeatured()
    {
        if (Featured == null) return;
        var songs = MobileLibrary.SongsBy(Shell.Library.Service, Featured.Name);
        if (songs.Count > 0) Shell.Player.PlayShuffled(songs, source: Featured.Name);
    }

    [RelayCommand]
    private void OpenArtist(ArtistListItem? item)
    {
        if (item != null) Shell.OpenArtistCommand.Execute(item.Name);
    }
}

/// <summary>A playlist in the search results: the playlist, its cover, and its song count.</summary>
public sealed record PlaylistResult(Playlist Playlist, string? ArtworkPath)
{
    public string Name => Playlist.Name;
    public string Subtitle => Loc.T("Mobile.Search.PlaylistSubtitle", Loc.Plural("Mobile.Count.Songs", Playlist.TrackIds.Count));
}
