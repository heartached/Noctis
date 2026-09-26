using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Helpers;
using Noctis.Localization;
using Noctis.Models;
using Noctis.Services;

namespace Noctis.ViewModels;

/// <summary>
/// The artist page (redesign 2026-09-13, reference mockup "design 4"): a hero header
/// (portrait, genre kicker, name, facts, Play / Shuffle / favourite / options) over a
/// tab strip — <b>Overview</b>, <b>Albums</b>, <b>Singles &amp; EPs</b>, <b>Songs</b>,
/// <b>Similar Artists</b>.
/// <list type="bullet">
/// <item>Overview: <b>Popular</b> (top five by play count) | <b>Latest Release</b> |
/// <b>About</b> card (MusicBrainz + Wikipedia via <see cref="ArtistInfoService"/>, link
/// row, library stat tiles), then <b>Albums</b> | <b>Singles &amp; EPs</b> side by side
/// (four newest each, "See all" jumps to the tab) and <b>Appears On</b> for features.</item>
/// <item>Albums / Singles &amp; EPs: the full grids.</item>
/// <item>Songs: <b>Top Favorites</b> (the user's hearts by this artist) then every song
/// ranked by play count.</item>
/// <item>Similar Artists: Deezer's related artists via <see cref="SimilarArtistsService"/>,
/// fetched the first time the tab opens; the ones in the library open their own page.</item>
/// </list>
/// Matching mirrors the old grid: releases are albums whose album-artist credit contains
/// the artist token (<see cref="LibraryAlbumsViewModel.ContainsArtistToken"/>); songs are
/// every track whose track credit contains it, so features count as "associated with".
/// </summary>
public partial class ArtistDetailViewModel : ViewModelBase, ISearchable, IDisposable
{
    /// <summary>Ranked songs shown in the Overview's Popular section (uncapped while searching).</summary>
    public const int MaxPopular = 5;
    /// <summary>Favourited songs shown in the Songs tab's Top Favorites section.</summary>
    public const int MaxFavorites = 6;
    /// <summary>Tiles per Overview release row: four beside the other row, eight alone.</summary>
    /// <summary>Tiles per row: the same maths as Home and the Albums grid (AlbumGridMetrics —
    /// five across in Auto, else the cover-size setting). The view feeds its usable width.</summary>
    [ObservableProperty] private int _gridColumns = AlbumGridMetrics.ClassicColumns;

    /// <summary>Cover-size setting shared with Home/Albums (defaults when no settings are wired).</summary>
    public bool AlbumTileSizeAuto => _settings?.AlbumTileSizeAuto ?? true;
    public double AlbumTileTargetSize => _settings?.AlbumTileTargetSize ?? 220;

    partial void OnGridColumnsChanged(int value)
    {
        OnPropertyChanged(nameof(OverviewAlbumColumns));
        OnPropertyChanged(nameof(OverviewSingleColumns));
        if (_allReleases.Count > 0 || _allSongs.Count > 0) ApplyLists(); // overview caps follow the column count
    }
    private const int CollapsedBioLines = 7;

    private readonly ILibraryService _library;
    private readonly PlayerViewModel _player;
    private readonly SettingsViewModel? _settings;
    private readonly LibraryArtistsViewModel? _artistsVm;
    private readonly ArtistImageService? _images;
    private readonly ArtistInfoService? _info;
    private readonly SimilarArtistsService? _similar;
    private readonly SidebarViewModel? _sidebar;
    private readonly EventHandler _libraryUpdatedHandler;
    private readonly EventHandler _favoritesChangedHandler;
    private readonly CancellationTokenSource _cts = new();
    private string _query = string.Empty;
    private int _heroGeneration;
    private int _songsGeneration;
    private int _albumsGeneration;
    private int _singlesGeneration;
    private bool _similarRequested;

    // Unfiltered results; the observable collections below hold the searched/filtered view.
    private List<Album> _allReleases = new();
    private List<Album> _allAppearsOn = new();
    private List<Track> _allSongs = new();

    public string ArtistName { get; }

    /// <summary>Scroll offset saved by the view when the page leaves the screen, so a
    /// Back from an album/track lands where the user was (same as AlbumDetailViewModel).</summary>
    public double SavedScrollOffset { get; set; }

    /// <summary>The library's Artist row when the name is a known primary artist, else a
    /// synthesized stand-in (a feature-only credit still gets a page).</summary>
    public Artist Artist { get; }

    /// <summary>Reused Albums VM that owns the album/track context-menu commands.</summary>
    public LibraryAlbumsViewModel? LibraryAlbumsVm { get; }

    /// <summary>Playlists for the Add-to-Playlist submenu of the shared track menu.</summary>
    public ObservableCollection<Playlist>? Playlists => _sidebar?.Playlists;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImage))]
    private string? _imagePath;
    public bool HasImage => !string.IsNullOrEmpty(ImagePath);

    /// <summary>Backdrop art for the hero: the portrait when there is one, else the newest
    /// release's cover so the header is never a flat slab.</summary>
    [ObservableProperty] private string? _heroArtPath;

    /// <summary>Colour wash behind the hero, derived from the backdrop art's edge colour
    /// (a photo's edge is its background, a cover's edge its canvas) — so the header reads
    /// as the artist's colour rather than a smeared face. Computed off-thread.</summary>
    [ObservableProperty] private IBrush? _heroBackgroundBrush;

    [ObservableProperty] private bool _isFavorite;

    /// <summary>Deezer fan count for the hero ("7,994,069 fans", 09-15); -1 until known.
    /// The cached number paints at once, the weekly re-check swaps in behind it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFans))]
    [NotifyPropertyChangedFor(nameof(FansDisplay))]
    private long _fanCount = -1;
    public bool HasFans => FanCount > 0;
    public string FansDisplay => HasFans
        ? Loc.T("ArtistDetail.FansCount", FanCount.ToString("N0", System.Globalization.CultureInfo.CurrentCulture))
        : string.Empty;

    /// <summary>Hero kicker above the name ("HIP HOP"): the artist's most common library
    /// genre tag, else the first MusicBrainz genre once About loads, else hidden.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGenreKicker))]
    [NotifyPropertyChangedFor(nameof(GenreFact))]
    [NotifyPropertyChangedFor(nameof(HasGenreFact))]
    [NotifyPropertyChangedFor(nameof(HasAboutFacts))]
    private string _genreKicker = string.Empty;
    public bool HasGenreKicker => GenreKicker.Length > 0;

    /// <summary>The About card's GENRE fact: MusicBrainz's genres ("Pop rap · Alternative
    /// pop"), else the library's dominant tag in title case, else nothing.</summary>
    public string GenreFact => About is { HasGenres: true } a
        ? a.GenresDotted
        : System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(GenreKicker.ToLowerInvariant());
    public bool HasGenreFact => GenreFact.Length > 0;

    // ── Tabs: "overview" / "albums" / "singles" / "songs" / "similar" ──
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTabOverview))]
    [NotifyPropertyChangedFor(nameof(IsTabAlbums))]
    [NotifyPropertyChangedFor(nameof(IsTabSingles))]
    [NotifyPropertyChangedFor(nameof(IsTabSongs))]
    [NotifyPropertyChangedFor(nameof(IsTabSimilar))]
    private string _selectedTab = "overview";
    public bool IsTabOverview => SelectedTab == "overview";
    public bool IsTabAlbums => SelectedTab == "albums";
    public bool IsTabSingles => SelectedTab == "singles";
    public bool IsTabSongs => SelectedTab == "songs";
    public bool IsTabSimilar => SelectedTab == "similar";
    /// <summary>Raised after a tab switch so the view can scroll the new tab to its top.</summary>
    public event EventHandler? TabChanged;

    public ObservableCollection<TopSongRow> PopularSongs { get; } = new();
    public ObservableCollection<TopSongRow> FavoriteSongs { get; } = new();
    /// <summary>Every song ranked by play count (Songs tab); filled in slices when the
    /// tab opens so a 500-song catalogue doesn't inflate 500 rows in one layout pass.</summary>
    public BulkObservableCollection<TopSongRow> AllSongs { get; } = new();
    /// <summary>Every release (albums and singles), newest first, narrowed by search.</summary>
    public ObservableCollection<Album> Releases { get; } = new();
    /// <summary>Albums / Singles &amp; EPs tab grids. Filled in slices when their tab opens
    /// (like <see cref="AllSongs"/>, 09-15): the tab panels are hidden until picked, so the
    /// first visit inflated every tile in one layout pass and the tab's fade-in was over
    /// before the frame with the tiles could paint. One row lands with the switch, the
    /// rest arrive a couple of rows per idle turn under the fade. Counts and the
    /// "has any" flags read the full lists, so the header never shows a partial number.</summary>
    public BulkObservableCollection<Album> AlbumReleases { get; } = new();
    public BulkObservableCollection<Album> SingleReleases { get; } = new();
    private List<Album> _tabAlbums = new();
    private List<Album> _tabSingles = new();
    /// <summary>The lists the tab grids were last filled from (null once cleared).</summary>
    private List<Album>? _albumsFilled;
    private List<Album>? _singlesFilled;
    /// <summary>Overview rows: the newest four (eight when the other row is empty), or
    /// every match while searching.</summary>
    public ObservableCollection<Album> OverviewAlbums { get; } = new();
    public ObservableCollection<Album> OverviewSingles { get; } = new();
    public ObservableCollection<Album> AppearsOn { get; } = new();
    public ObservableCollection<SimilarArtistRow> SimilarArtists { get; } = new();
    /// <summary>The Overview's Similar Artists row: the first <see cref="MaxOverviewSimilar"/>.</summary>
    public ObservableCollection<SimilarArtistRow> OverviewSimilar { get; } = new();
    public const int MaxOverviewSimilar = 8;
    public bool HasOverviewSimilar => OverviewSimilar.Count > 0;

    public bool HasPopular => PopularSongs.Count > 0;
    public bool HasFavorites => FavoriteSongs.Count > 0;
    public bool HasReleases => Releases.Count > 0;
    public bool HasAlbums => _tabAlbums.Count > 0;
    public bool HasSingles => _tabSingles.Count > 0;
    public bool HasOverviewAlbums => OverviewAlbums.Count > 0;
    public bool HasOverviewSingles => OverviewSingles.Count > 0;
    public bool HasAppearsOn => AppearsOn.Count > 0;
    public bool HasAllSongs => AllSongs.Count > 0;
    /// <summary>Overview layout: a row alone spans both columns and shows eight tiles.</summary>
    /// <summary>Overview rows are stacked full-width (09-14, tiles at Home size): one row of each.</summary>
    public int OverviewAlbumColumns => GridColumns;
    public int OverviewSingleColumns => GridColumns;
    public int AlbumCount => _tabAlbums.Count;
    public int SingleCount => _tabSingles.Count;

    // ── Latest release (by release date, falling back to year) ──
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLatestRelease))]
    [NotifyPropertyChangedFor(nameof(LatestReleaseDate))]
    [NotifyPropertyChangedFor(nameof(LatestReleaseKindLine))]
    [NotifyPropertyChangedFor(nameof(LatestReleaseSongsLine))]
    private Album? _latestRelease;
    public bool HasLatestRelease => LatestRelease != null;
    /// <summary>"Feb 8, 2026" when the tag carries a date, else the year.</summary>
    public string LatestReleaseDate => LatestRelease switch
    {
        null => "",
        { HasReleaseDate: true } a => a.ReleaseDateShortFormatted,
        { Year: > 0 } a => a.Year.ToString(),
        _ => "",
    };
    /// <summary>"Single · 2025"</summary>
    public string LatestReleaseKindLine => LatestRelease == null ? "" : LatestReleaseKindLineFor(LatestRelease);
    /// <summary>"1 song · 2:52"</summary>
    public string LatestReleaseSongsLine => LatestRelease == null ? "" : LatestReleaseSongsLineFor(LatestRelease);

    internal static string LatestReleaseKindLineFor(Album a)
        => a.Year > 0 ? $"{a.ReleaseKindTitle} · {a.Year}" : a.ReleaseKindTitle;

    internal static string LatestReleaseSongsLineFor(Album a)
    {
        var count = a.TrackCount == 1 ? "1 song" : $"{a.TrackCount} songs";
        var total = a.TotalDuration > TimeSpan.Zero
            ? a.TotalDuration
            : TimeSpan.FromTicks(a.Tracks?.Sum(t => t.Duration.Ticks) ?? 0);
        if (total <= TimeSpan.Zero) return count;
        var length = total.TotalHours >= 1
            ? $"{(int)total.TotalHours}:{total.Minutes:00}:{total.Seconds:00}"
            : $"{(int)total.TotalMinutes}:{total.Seconds:00}";
        return $"{count} · {length}";
    }

    // ── About ──
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAbout))]
    [NotifyPropertyChangedFor(nameof(HasAboutFacts))]
    [NotifyPropertyChangedFor(nameof(GenreFact))]
    [NotifyPropertyChangedFor(nameof(HasGenreFact))]
    private ArtistInfo? _about;
    public bool HasAbout => About != null;
    /// <summary>True from page open until the web lookup settles; the card says so instead
    /// of "no biography" (which is only true once the lookup has answered).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoBiography))]
    private bool _isAboutLoading;
    public bool ShowNoBiography => !IsAboutLoading && About is not { HasBio: true };
    partial void OnAboutChanged(ArtistInfo? value) => OnPropertyChanged(nameof(ShowNoBiography));
    /// <summary>FROM / BORN (FORMED) / GENRE, Apple Music style; GENRE alone can come from
    /// the library, so the block shows even before (or without) the web lookup.</summary>
    public bool HasAboutFacts => (About is { } a && (a.HasFrom || a.HasBegin)) || HasGenreFact;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BioMaxLines))]
    [NotifyPropertyChangedFor(nameof(BioToggleText))]
    private bool _isBioExpanded;
    public int BioMaxLines => IsBioExpanded ? 0 : CollapsedBioLines;
    public string BioToggleText => IsBioExpanded ? "Show less" : "Read more";
    /// <summary>Set by the view from the text layout: true when the collapsed bio was
    /// actually cut off. "Read more" only shows when there is more to read.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowBioToggle))]
    private bool _bioOverflows;
    public bool ShowBioToggle => BioOverflows || IsBioExpanded;
    partial void OnIsBioExpandedChanged(bool value) => OnPropertyChanged(nameof(ShowBioToggle));
    /// <summary>Library-side facts for the About card — what no web service knows.</summary>
    [ObservableProperty] private string _mostPlayedTitle = string.Empty;
    [ObservableProperty] private string _inLibrarySince = string.Empty;
    [ObservableProperty] private int _favoriteCount;
    [ObservableProperty] private int _releaseCount;
    [ObservableProperty] private int _songCount;
    /// <summary>Summed length of every song by the artist in the library ("44h 21m").</summary>
    [ObservableProperty] private string _totalLengthDisplay = string.Empty;
    public bool HasMostPlayed => MostPlayedTitle.Length > 0;
    public bool HasInLibrarySince => InLibrarySince.Length > 0;

    // ── Similar artists ──
    [ObservableProperty] private bool _isSimilarLoading;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoSimilar))]
    private bool _similarLoaded;
    public bool HasSimilar => SimilarArtists.Count > 0;
    public bool ShowNoSimilar => SimilarLoaded && !HasSimilar;

    // ── Tile sizing ──
    private const double TileLabelHeight = 64;
    [ObservableProperty] private double _tileArtworkSize = 184;
    public double TileHeight => TileArtworkSize + TileLabelHeight;
    partial void OnTileArtworkSizeChanged(double value) => OnPropertyChanged(nameof(TileHeight));

    public event EventHandler? BackRequested;
    public event EventHandler<Album>? AlbumOpened;
    /// <summary>A Similar Artists tile for an artist in the library: open their page.</summary>
    public event EventHandler<string>? ArtistOpened;
    public event EventHandler<Track>? SearchLyricsRequested;

    public ArtistDetailViewModel(
        string artistName,
        ILibraryService library,
        PlayerViewModel player,
        LibraryAlbumsViewModel? libraryAlbumsVm = null,
        LibraryArtistsViewModel? artistsVm = null,
        ArtistImageService? images = null,
        SidebarViewModel? sidebar = null,
        ArtistInfoService? info = null,
        SimilarArtistsService? similar = null,
        SettingsViewModel? settings = null)
    {
        ArtistName = (artistName ?? string.Empty).Trim();
        _settings = settings;
        _library = library;
        _player = player;
        LibraryAlbumsVm = libraryAlbumsVm;
        _artistsVm = artistsVm;
        _images = images;
        _sidebar = sidebar;
        _info = info;
        _similar = similar;

        Artist = library.Artists.FirstOrDefault(a => string.Equals(a.Name, ArtistName, StringComparison.OrdinalIgnoreCase))
                 ?? new Artist { Id = LibraryService.ComputeArtistId(ArtistName), Name = ArtistName };
        IsFavorite = _artistsVm?.IsFavoriteArtist(ArtistName) ?? false;

        Rebuild();
        ResolveImage();
        _ = LoadFansAsync();
        _ = LoadAboutAsync();
        _ = LoadSimilarAsync(); // the Overview carries a Similar Artists row, so load on open

        // A scan's progressive fill publishes only the tracks found SO FAR every 1.5 s, so
        // rebuilding on it shrank the lists mid-scan and re-ran Classify each time; the
        // authoritative publish follows with IsPublishingPartial false (checked at raise
        // time, as on the album page). A page kept in history stays subscribed, so it
        // only marks itself stale and catches up once it is current again (IsActive).
        _libraryUpdatedHandler = (_, _) =>
        {
            if (_library.IsPublishingPartial) return;
            Dispatcher.UIThread.Post(OnLibraryUpdated);
        };
        _library.LibraryUpdated += _libraryUpdatedHandler;
        // Hearts on the rows bind Track.IsFavorite; the Top Favorites section itself
        // must follow the set, so re-derive the lists when favourites change anywhere.
        _favoritesChangedHandler = (_, _) => Dispatcher.UIThread.Post(ApplyLists);
        _library.FavoritesChanged += _favoritesChangedHandler;
    }

    // ── Data ──

    /// <summary>Pure classification shared with the tests: releases / appears-on / songs.
    /// Releases are newest-first by release date (tag date, else year).</summary>
    internal static (List<Album> Releases, List<Album> AppearsOn, List<Track> Songs) Classify(
        IReadOnlyList<Album> allAlbums, string artistName)
    {
        // Parsed once: this walks every track of the library (twice).
        var nameTokens = Track.ParseArtistTokens(artistName);
        bool Credits(string? field) => LibraryAlbumsViewModel.ContainsArtistToken(field, artistName, nameTokens);

        var releases = allAlbums
            .Where(a => Credits(a.Artist))
            .OrderByDescending(ReleaseSortDate)
            .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var releaseIds = releases.Select(a => a.Id).ToHashSet();

        var appearsOn = allAlbums
            .Where(a => !releaseIds.Contains(a.Id)
                        && a.Tracks.Any(t => Credits(t.Artist)))
            .OrderByDescending(ReleaseSortDate)
            .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var songs = allAlbums
            .SelectMany(a => a.Tracks)
            .Where(t => Credits(t.Artist))
            .GroupBy(t => t.Id).Select(g => g.First())
            .ToList();

        return (releases, appearsOn, songs);
    }

    /// <summary>The date a release sorts by: the first track's parseable release-date tag,
    /// else January 1 of the year, else the epoch (untagged sinks to the bottom).</summary>
    internal static DateTime ReleaseSortDate(Album album)
    {
        var tagged = album.Tracks?.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t.ReleaseDate))?.ReleaseDate;
        if (Track.TryParseReleaseDate(tagged, out var date)) return date;
        if (album.Year is > 0 and < 10000) return new DateTime(album.Year, 1, 1);
        return DateTime.MinValue;
    }

    /// <summary>Popular = play count desc, then title. Capped at <paramref name="cap"/>;
    /// pass 0 for no cap (a search must surface a song ranked 40th).</summary>
    internal static List<TopSongRow> RankPopular(IEnumerable<Track> songs, string query, int cap)
    {
        var ranked = songs
            .Where(t => SearchText.Matches(t.Title, query))
            .OrderByDescending(t => t.PlayCount)
            .ThenBy(t => t.Title, StringComparer.OrdinalIgnoreCase);
        var capped = cap > 0 ? ranked.Take(cap) : ranked;
        return capped.Select((t, i) => new TopSongRow { Track = t, Rank = i + 1 }).ToList();
    }

    /// <summary>The most common non-empty genre tag across the artist's songs, upper-cased
    /// for the hero kicker; ties break alphabetically. Null when nothing is tagged.</summary>
    internal static string? DominantGenre(IEnumerable<Track> songs)
    {
        var top = songs
            .Select(t => t.Genre?.Trim() ?? string.Empty)
            .Where(g => g.Length > 0)
            .GroupBy(g => g, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        return top?.Key.ToUpperInvariant();
    }

    /// <summary>Splits releases into the Albums tab (anything that is not a single or EP)
    /// and the Singles &amp; EPs tab.</summary>
    internal static IEnumerable<Album> FilterReleases(IEnumerable<Album> releases, string filter) => filter switch
    {
        "albums" => releases.Where(a => a.ReleaseType is not (ReleaseType.Single or ReleaseType.EP)),
        "singles" => releases.Where(a => a.ReleaseType is ReleaseType.Single or ReleaseType.EP),
        _ => releases,
    };

    /// <summary>An album answers a search when its name or any of its track titles matches,
    /// accent- and punctuation-insensitively ("ultimo" finds "EL ÚLTIMO TOUR DEL MUNDO").</summary>
    internal static bool AlbumMatches(Album album, string query)
        => string.IsNullOrWhiteSpace(query)
           || SearchText.Matches(album.Name, query)
           || album.Tracks.Any(t => SearchText.Matches(t.Title, query));

    /// <summary>Overview row contents: the newest <paramref name="cap"/> (0 = all).</summary>
    internal static List<Album> OverviewRow(IReadOnlyList<Album> releases, int cap)
        => cap > 0 ? releases.Take(cap).ToList() : releases.ToList();

    /// <summary>
    /// Set by MainWindowViewModel as the page becomes (or stops being) the current view.
    /// True from construction: the page is shown as soon as it is built.
    /// </summary>
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive == value) return;
            _isActive = value;
            if (value && _rebuildPending)
            {
                _rebuildPending = false;
                Rebuild();
            }
        }
    }

    private bool _isActive = true;
    private bool _rebuildPending;

    private void OnLibraryUpdated()
    {
        if (!_isActive)
        {
            _rebuildPending = true;
            return;
        }
        Rebuild();
    }

    private void Rebuild()
    {
        var (releases, appearsOn, songs) = Classify(_library.Albums, ArtistName);
        _allReleases = releases;
        _allAppearsOn = appearsOn;
        _allSongs = songs;

        LatestRelease = releases.FirstOrDefault();
        var total = TimeSpan.FromTicks(songs.Sum(t => t.Duration.Ticks));
        ReleaseCount = releases.Count;
        SongCount = songs.Count;
        TotalLengthDisplay = total.TotalHours >= 1 ? $"{(int)total.TotalHours}h {total.Minutes}m" : $"{(int)total.TotalMinutes}m";
        GenreKicker = DominantGenre(songs) ?? (About?.Genres.FirstOrDefault() ?? string.Empty).ToUpperInvariant();

        // Library-side About facts.
        var mostPlayed = songs.OrderByDescending(t => t.PlayCount).FirstOrDefault();
        MostPlayedTitle = mostPlayed is { PlayCount: > 0 } ? mostPlayed.Title : string.Empty;
        InLibrarySince = songs.Count > 0 ? songs.Min(t => t.DateAdded).ToLocalTime().ToString("MMMM yyyy") : string.Empty;
        OnPropertyChanged(nameof(HasMostPlayed));
        OnPropertyChanged(nameof(HasInLibrarySince));

        ApplyLists();
        if (string.IsNullOrEmpty(ImagePath))
            HeroArtPath = LatestRelease?.ArtworkPath;
        // A library rescan can add or drop artists the Similar tab marks "in your library".
        if (SimilarLoaded) RefreshSimilarLibraryFlags();
    }

    /// <summary>Same albums in the same order (reference equality: Album objects are stable).</summary>
    internal static bool SameAlbums(IList<Album> current, IList<Album> next)
        => current.Count == next.Count && current.Zip(next).All(p => ReferenceEquals(p.First, p.Second));

    /// <summary>Same tracks at the same ranks: TopSongRow is rebuilt per pass, so compare what it shows.</summary>
    internal static bool SameRows(IList<TopSongRow> current, IList<TopSongRow> next)
        => current.Count == next.Count &&
           current.Zip(next).All(p => ReferenceEquals(p.First.Track, p.Second.Track) && p.First.Rank == p.Second.Rank);

    private static void ReplaceAlbums(ObservableCollection<Album> target, IList<Album> next)
    {
        if (SameAlbums(target, next)) return;
        target.Clear();
        foreach (var a in next) target.Add(a);
    }

    private static void ReplaceRows(ObservableCollection<TopSongRow> target, IList<TopSongRow> next)
    {
        if (SameRows(target, next)) return;
        target.Clear();
        foreach (var r in next) target.Add(r);
    }

    private void ApplyLists()
    {
        var q = _query.Trim();
        var searching = q.Length > 0;
        var favorites = _allSongs.Where(t => t.IsFavorite).ToList();
        FavoriteCount = favorites.Count;

        // Every list is replaced only when its content changed (09-14). LibraryUpdated
        // fires on each play-count save and FavoritesChanged on every heart, and the
        // unconditional Clear+Add tore down every row and tile each time: the row under
        // the pointer flickered, and a track menu whose owner row left the tree was
        // closed by Avalonia and could not re-open (same root cause as Home, 09-13).
        ReplaceRows(FavoriteSongs, RankPopular(favorites, q, searching ? 0 : MaxFavorites));
        ReplaceRows(PopularSongs, RankPopular(_allSongs, q, searching ? 0 : MaxPopular));

        var matching = _allReleases.Where(a => AlbumMatches(a, q)).ToList();
        var albums = FilterReleases(matching, "albums").ToList();
        var singles = FilterReleases(matching, "singles").ToList();

        ReplaceAlbums(Releases, matching);
        _tabAlbums = albums;
        _tabSingles = singles;
        if (IsTabAlbums) FillTabAlbums();
        else { _albumsFilled = null; ++_albumsGeneration; AlbumReleases.ReplaceAll(Array.Empty<Album>()); }
        if (IsTabSingles) FillTabSingles();
        else { _singlesFilled = null; ++_singlesGeneration; SingleReleases.ReplaceAll(Array.Empty<Album>()); }

        // One full-width row of each on the Overview: the newest GridColumns releases.
        var albumCap = searching ? 0 : GridColumns;
        var singleCap = searching ? 0 : GridColumns;
        ReplaceAlbums(OverviewAlbums, OverviewRow(albums, albumCap).ToList());
        ReplaceAlbums(OverviewSingles, OverviewRow(singles, singleCap).ToList());

        ReplaceAlbums(AppearsOn, _allAppearsOn.Where(a => AlbumMatches(a, q)).ToList());

        if (IsTabSongs) FillAllSongs();
        else { _allSongsRanking = null; ++_songsGeneration; AllSongs.ReplaceAll(Array.Empty<TopSongRow>()); OnPropertyChanged(nameof(HasAllSongs)); }

        OnPropertyChanged(nameof(HasPopular));
        OnPropertyChanged(nameof(HasFavorites));
        OnPropertyChanged(nameof(HasReleases));
        OnPropertyChanged(nameof(HasAlbums));
        OnPropertyChanged(nameof(HasSingles));
        OnPropertyChanged(nameof(HasOverviewAlbums));
        OnPropertyChanged(nameof(HasOverviewSingles));
        OnPropertyChanged(nameof(HasAppearsOn));
        OnPropertyChanged(nameof(OverviewAlbumColumns));
        OnPropertyChanged(nameof(OverviewSingleColumns));
        OnPropertyChanged(nameof(AlbumCount));
        OnPropertyChanged(nameof(SingleCount));
    }

    /// <summary>The ranking the Songs list was last filled from (null once cleared).</summary>
    private List<TopSongRow>? _allSongsRanking;

    /// <summary>Songs tab: the full ranking, streamed in slices (first slice synchronous).
    /// Skipped when the ranking is the one already filled or in flight: a heart (FavoritesChanged)
    /// or a play-count save (LibraryUpdated) used to reset every row of the list each time,
    /// which stalled the page and tore down the row under the pointer or under an open menu.</summary>
    private void FillAllSongs()
    {
        var rows = RankPopular(_allSongs, _query.Trim(), 0);
        if (_allSongsRanking != null && SameRows(_allSongsRanking, rows)) return;
        _allSongsRanking = rows;
        var generation = ++_songsGeneration;
        StreamingFill.Into(AllSongs, rows, generation, () => _songsGeneration, first: 30, chunk: 40);
        OnPropertyChanged(nameof(HasAllSongs));
    }

    /// <summary>Tiles per slice for the tab grids: one row with the switch, two rows per idle turn.</summary>
    private int TabGridFirstSlice => Math.Max(1, GridColumns);
    private int TabGridSlice => Math.Max(1, GridColumns * 2);

    private void FillTabAlbums()
    {
        if (_albumsFilled != null && SameAlbums(_albumsFilled, _tabAlbums)) return;
        _albumsFilled = _tabAlbums;
        var generation = ++_albumsGeneration;
        StreamingFill.Into(AlbumReleases, _tabAlbums, generation, () => _albumsGeneration, first: TabGridFirstSlice, chunk: TabGridSlice);
    }

    private void FillTabSingles()
    {
        if (_singlesFilled != null && SameAlbums(_singlesFilled, _tabSingles)) return;
        _singlesFilled = _tabSingles;
        var generation = ++_singlesGeneration;
        StreamingFill.Into(SingleReleases, _tabSingles, generation, () => _singlesGeneration, first: TabGridFirstSlice, chunk: TabGridSlice);
    }

    partial void OnSelectedTabChanged(string value)
    {
        if (IsTabAlbums) FillTabAlbums();
        if (IsTabSingles) FillTabSingles();
        if (IsTabSongs && AllSongs.Count == 0) FillAllSongs();
        if (IsTabSimilar) _ = LoadSimilarAsync();
        TabChanged?.Invoke(this, EventArgs.Empty);
    }

    // ── Deezer fan count ──

    private async Task LoadFansAsync()
    {
        if (_images == null) return;
        if (_images.TryGetCachedFanCount(Artist.Id) is { } cached) FanCount = cached;
        try
        {
            var fans = await _images.GetFanCountAsync(Artist.Id, ArtistName, _cts.Token).ConfigureAwait(false);
            if (fans is not { } f || _cts.IsCancellationRequested) return;
            await Dispatcher.UIThread.InvokeAsync(() => FanCount = f);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            DebugLogger.Warn(DebugLogger.Category.UI, "Artist.Fans", ex.Message);
        }
    }

    private void ResolveImage()
    {
        if (_images == null)
        {
            ImagePath = Artist.ImagePath;
            return;
        }

        if (_images.HasCachedImage(Artist.Id))
        {
            ImagePath = _images.GetCachedImagePath(Artist.Id);
            return;
        }

        ImagePath = Artist.ImagePath;
        if (!string.IsNullOrEmpty(ImagePath) || _images.IsImageRemoved(Artist.Id)) return;

        // Background fetch (Deezer); the service paces itself and never throws.
        _ = _images.FetchAndCacheAsync(new[] { Artist }, (artist, path) =>
            Dispatcher.UIThread.Post(() =>
            {
                if (artist.Id == Artist.Id) ImagePath = path;
            }));
    }

    partial void OnImagePathChanged(string? value)
        => HeroArtPath = !string.IsNullOrEmpty(value) ? value : LatestRelease?.ArtworkPath;

    partial void OnHeroArtPathChanged(string? value)
    {
        var generation = ++_heroGeneration;
        if (string.IsNullOrEmpty(value)) { HeroBackgroundBrush = null; return; }
        var path = value;
        _ = Task.Run(() =>
        {
            var color = DominantColorExtractor.ExtractEdgeBackgroundColorFromFile(path);
            Dispatcher.UIThread.Post(() =>
            {
                if (generation != _heroGeneration) return;
                HeroBackgroundBrush = color is { } c ? BuildHeroBrush(c) : null;
            });
        });
    }

    /// <summary>Hero wash: the colour, lifted toward a legible mid-tone, fading out toward
    /// the bottom where the page background takes over.</summary>
    public static LinearGradientBrush BuildHeroBrush(Color c)
    {
        // Pull very dark or very light edges toward the middle so the wash always shows.
        static byte Mix(byte v, byte target, double t) => (byte)(v + (target - v) * t);
        var lum = DominantColorExtractor.GetRelativeLuminance(c);
        var tone = lum < 0.08 ? Color.FromRgb(Mix(c.R, 96, 0.5), Mix(c.G, 96, 0.5), Mix(c.B, 96, 0.5))
                 : lum > 0.85 ? Color.FromRgb(Mix(c.R, 128, 0.35), Mix(c.G, 128, 0.35), Mix(c.B, 128, 0.35))
                 : c;
        return new LinearGradientBrush
        {
            StartPoint = new Avalonia.RelativePoint(0, 0, Avalonia.RelativeUnit.Relative),
            EndPoint = new Avalonia.RelativePoint(0, 1, Avalonia.RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0xFF, tone.R, tone.G, tone.B), 0),
                new GradientStop(Color.FromArgb(0xB0, tone.R, tone.G, tone.B), 0.55),
                new GradientStop(Color.FromArgb(0x00, tone.R, tone.G, tone.B), 1),
            }
        };
    }

    private async Task LoadAboutAsync()
    {
        if (_info == null) return;
        // Whatever the cache holds paints immediately; the refresh (if the entry is
        // stale or partial) swaps in behind it, so a revisited artist never shows a spinner.
        About = _info.TryGetCached(Artist.Id);
        IsAboutLoading = true;
        try
        {
            var info = await _info.GetAsync(Artist.Id, ArtistName, _cts.Token).ConfigureAwait(false);
            if (_cts.IsCancellationRequested) return;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                About = info ?? About;
                // Untagged library: the hero kicker falls back to MusicBrainz's top genre.
                if (GenreKicker.Length == 0 && About?.Genres.FirstOrDefault() is { Length: > 0 } genre)
                    GenreKicker = genre.ToUpperInvariant();
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            DebugLogger.Warn(DebugLogger.Category.UI, "Artist.About", ex.Message);
        }
        finally
        {
            if (!_cts.IsCancellationRequested)
                await Dispatcher.UIThread.InvokeAsync(() => IsAboutLoading = false);
        }
    }

    // ── Similar artists (fetched once, the first time the tab opens) ──

    private async Task LoadSimilarAsync()
    {
        if (_similarRequested) return;
        _similarRequested = true;
        if (_similar == null) { SimilarLoaded = true; return; }
        IsSimilarLoading = true;
        try
        {
            var related = await _similar.GetAsync(Artist.Id, ArtistName, _cts.Token).ConfigureAwait(false);
            if (_cts.IsCancellationRequested) return;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                SimilarArtists.Clear();
                OverviewSimilar.Clear();
                foreach (var row in BuildSimilarRows(related, _library.Artists, _images))
                {
                    SimilarArtists.Add(row);
                    if (OverviewSimilar.Count < MaxOverviewSimilar) OverviewSimilar.Add(row);
                }
                OnPropertyChanged(nameof(HasSimilar));
                OnPropertyChanged(nameof(HasOverviewSimilar));
                SimilarLoaded = true;
                OnPropertyChanged(nameof(ShowNoSimilar));
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            DebugLogger.Warn(DebugLogger.Category.UI, "Artist.Similar", ex.Message);
            SimilarLoaded = true;
        }
        finally
        {
            IsSimilarLoading = false;
        }
    }

    /// <summary>Pure: Deezer's related artists joined to the library by name. An artist in
    /// the library uses their library portrait (the user may have picked one) and opens
    /// their page; the rest show Deezer's photo and are inert.</summary>
    internal static List<SimilarArtistRow> BuildSimilarRows(
        IReadOnlyList<SimilarArtist> related, IReadOnlyList<Artist> libraryArtists, ArtistImageService? images)
    {
        var byName = new Dictionary<string, Artist>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in libraryArtists) byName.TryAdd(a.Name.Trim(), a);
        var rows = new List<SimilarArtistRow>(related.Count);
        foreach (var r in related)
        {
            byName.TryGetValue(r.Name.Trim(), out var libraryArtist);
            var image = r.ImagePath;
            if (libraryArtist != null)
            {
                var own = images != null && images.HasCachedImage(libraryArtist.Id)
                    ? images.GetCachedImagePath(libraryArtist.Id)
                    : libraryArtist.ImagePath;
                if (!string.IsNullOrEmpty(own)) image = own;
            }
            rows.Add(new SimilarArtistRow
            {
                Name = libraryArtist?.Name ?? r.Name,
                ImagePath = image ?? string.Empty,
                IsInLibrary = libraryArtist != null,
            });
        }
        return rows;
    }

    private void RefreshSimilarLibraryFlags()
    {
        var names = _library.Artists.Select(a => a.Name.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var row in SimilarArtists) row.IsInLibrary = names.Contains(row.Name.Trim());
    }

    // ── ISearchable ──

    public void ApplyFilter(string query)
    {
        _query = query ?? string.Empty;
        ApplyLists();
    }

    // ── Commands ──

    /// <summary>Every track the artist is on: releases newest-first in album order, then
    /// feature appearances; de-duplicated.</summary>
    internal List<Track> GetAllTracks()
    {
        var tracks = new List<Track>();
        var seen = new HashSet<Guid>();
        foreach (var album in _allReleases)
            foreach (var t in album.Tracks.OrderBy(t => t.DiscNumber).ThenBy(t => t.TrackNumber))
                if (seen.Add(t.Id)) tracks.Add(t);
        foreach (var t in _allSongs)
            if (seen.Add(t.Id)) tracks.Add(t);
        return tracks;
    }

    [RelayCommand]
    private void PlayAll()
    {
        var tracks = GetAllTracks();
        if (tracks.Count == 0) return;
        _player.ReplaceQueueAndPlay(tracks, 0);
    }

    [RelayCommand]
    private void ShuffleAll()
    {
        var tracks = GetAllTracks();
        if (tracks.Count == 0) return;
        _player.ReplaceQueueAndPlay(ShuffleHelper.WeightedShuffle(tracks), 0);
    }

    [RelayCommand]
    private void PlayNextAll()
    {
        // Insert in reverse so the first track ends up first after the current one.
        var tracks = GetAllTracks();
        for (var i = tracks.Count - 1; i >= 0; i--) _player.AddNext(tracks[i]);
    }

    [RelayCommand]
    private void AddAllToQueue()
    {
        foreach (var t in GetAllTracks()) _player.AddToQueue(t);
    }

    [RelayCommand]
    private void PlayLatestRelease()
    {
        var album = LatestRelease;
        if (album == null || album.Tracks.Count == 0) return;
        var tracks = album.Tracks.OrderBy(t => t.DiscNumber).ThenBy(t => t.TrackNumber).ToList();
        _player.ReplaceQueueAndPlay(tracks, 0);
    }

    /// <summary>Plays a row: the queue is the list the row came from — favourites when the
    /// track is one, else the full play-count ranking (Popular is its head) — starting at
    /// the pick.</summary>
    /// <summary>Row artwork hover button (09-14): Play, or Pause/Resume when this is the loaded track.</summary>
    [RelayCommand]
    private void TogglePlaySong(Track track)
    {
        if (track == null) return;
        if (track.IsNowPlaying) { _player.PlayPauseCommand.Execute(null); return; }
        PlaySong(track);
    }

    [RelayCommand]
    private void PlaySong(Track? track)
    {
        if (track == null) return;
        var songs = FavoriteSongs.Any(r => ReferenceEquals(r.Track, track))
            ? FavoriteSongs.Select(r => r.Track).ToList()
            : RankPopular(_allSongs, string.Empty, 0).Select(r => r.Track).ToList();
        var index = songs.IndexOf(track);
        if (index < 0) { songs.Insert(0, track); index = 0; }
        _player.ReplaceQueueAndPlay(songs, index);
    }

    [RelayCommand]
    private void ShufflePopular()
    {
        var songs = PopularSongs.Select(r => r.Track).ToList();
        if (songs.Count == 0) return;
        _player.ReplaceQueueAndPlay(ShuffleHelper.WeightedShuffle(songs), 0);
    }

    [RelayCommand]
    private void ShuffleFavorites()
    {
        var songs = _allSongs.Where(t => t.IsFavorite).ToList();
        if (songs.Count == 0) return;
        _player.ReplaceQueueAndPlay(ShuffleHelper.WeightedShuffle(songs), 0);
    }

    [RelayCommand]
    private void SearchLyrics(Track? track)
    {
        if (track != null) SearchLyricsRequested?.Invoke(this, track);
    }

    [RelayCommand]
    private void OpenAlbum(Album? album)
    {
        if (album != null) AlbumOpened?.Invoke(this, album);
    }

    [RelayCommand]
    private void OpenSimilarArtist(SimilarArtistRow? row)
    {
        if (row is { IsInLibrary: true }) ArtistOpened?.Invoke(this, row.Name);
    }

    [RelayCommand]
    private void SelectTab(string? tab)
        => SelectedTab = tab is "albums" or "singles" or "songs" or "similar" ? tab : "overview";

    [RelayCommand]
    private void ToggleFavorite()
    {
        if (_artistsVm == null) return;
        _artistsVm.ToggleFavoriteArtist(Artist);
        IsFavorite = _artistsVm.IsFavoriteArtist(ArtistName);
    }

    [RelayCommand]
    private void ToggleBio() => IsBioExpanded = !IsBioExpanded;

    [RelayCommand]
    private void OpenLink(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return;
        PlatformHelper.OpenUrl(url);
    }

    [RelayCommand]
    private void GoBack() => BackRequested?.Invoke(this, EventArgs.Empty);

    // ── Portrait ──

    public async Task ChangePictureAsync(byte[] imageData)
    {
        if (_images == null || imageData.Length == 0) return;
        var path = await _images.SetCustomImageAsync(Artist, imageData);
        if (string.IsNullOrEmpty(path)) return;
        ArtworkCache.Invalidate(path);
        DominantColorExtractor.InvalidateEdgeColor(path);
        ImagePath = null; // bounce so CachedImage reloads the same path
        ImagePath = path;
        _artistsVm?.MarkDirty();
    }

    public async Task SearchPictureAsync()
    {
        if (_images == null) return;
        var path = await _images.RefetchImageAsync(Artist);
        if (string.IsNullOrEmpty(path)) return;
        ArtworkCache.Invalidate(path);
        DominantColorExtractor.InvalidateEdgeColor(path);
        ImagePath = null;
        ImagePath = path;
        _artistsVm?.MarkDirty();
    }

    public void RemovePicture()
    {
        if (_images == null) return;
        var old = ImagePath;
        _images.RemoveImage(Artist);
        if (!string.IsNullOrEmpty(old)) ArtworkCache.Invalidate(old);
        ImagePath = null;
        _artistsVm?.MarkDirty();
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        _songsGeneration++; // stops an in-flight Songs-tab slice fill
        _library.LibraryUpdated -= _libraryUpdatedHandler;
        _library.FavoritesChanged -= _favoritesChangedHandler;
    }
}

/// <summary>One tile on the Similar Artists tab.</summary>
public sealed partial class SimilarArtistRow : ObservableObject
{
    public required string Name { get; init; }
    public string ImagePath { get; init; } = string.Empty;
    public bool HasImage => ImagePath.Length > 0;
    /// <summary>True when the library has this artist: the tile opens their page; a
    /// stranger's tile is inert and dimmed.</summary>
    [ObservableProperty] private bool _isInLibrary;
}
