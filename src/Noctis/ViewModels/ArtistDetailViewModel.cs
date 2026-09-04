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
using Noctis.Models;
using Noctis.Services;

namespace Noctis.ViewModels;

/// <summary>
/// The artist page (Discord ask, 2026-09-03): a hero header (portrait, name, facts,
/// Play / Shuffle / favourite), a <b>Latest Release</b> card beside <b>Top Favorites</b>
/// (the user's favourited songs by this artist) or <b>Popular</b>, the artist's
/// <b>Releases</b> with an All / Albums / Singles &amp; EPs filter, <b>Appears On</b> for
/// albums the artist is only featured on, and an <b>About</b> card fed by
/// <see cref="ArtistInfoService"/> (MusicBrainz + Wikipedia) plus library-derived facts.
/// Replaces the artist-filtered Albums grid as the destination of every artist link.
///
/// Matching mirrors the old grid: releases are albums whose album-artist credit contains
/// the artist token (<see cref="LibraryAlbumsViewModel.ContainsArtistToken"/>); songs are
/// every track whose track credit contains it, so features count as "associated with".
/// </summary>
public partial class ArtistDetailViewModel : ViewModelBase, ISearchable, IDisposable
{
    /// <summary>Ranked songs shown in the Popular section (uncapped while searching).</summary>
    public const int MaxPopular = 10;
    /// <summary>Favourited songs shown in the Top Favorites section.</summary>
    public const int MaxFavorites = 6;
    private const int CollapsedBioLines = 7;

    private readonly ILibraryService _library;
    private readonly PlayerViewModel _player;
    private readonly LibraryArtistsViewModel? _artistsVm;
    private readonly ArtistImageService? _images;
    private readonly ArtistInfoService? _info;
    private readonly SidebarViewModel? _sidebar;
    private readonly EventHandler _libraryUpdatedHandler;
    private readonly EventHandler _favoritesChangedHandler;
    private readonly CancellationTokenSource _cts = new();
    private string _query = string.Empty;
    private int _heroGeneration;

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

    /// <summary>"4 releases · 52 songs · 3h 12m"</summary>
    [ObservableProperty] private string _factsLine = string.Empty;

    public ObservableCollection<TopSongRow> PopularSongs { get; } = new();
    public ObservableCollection<TopSongRow> FavoriteSongs { get; } = new();
    public ObservableCollection<Album> Releases { get; } = new();
    public ObservableCollection<Album> AppearsOn { get; } = new();

    public bool HasPopular => PopularSongs.Count > 0;
    public bool HasFavorites => FavoriteSongs.Count > 0;
    /// <summary>Popular takes the slot beside Latest Release when there are no favourites;
    /// with favourites it moves below them as a full-width section.</summary>
    public bool ShowPopularBesideLatest => HasPopular && !HasFavorites;
    public bool ShowPopularBelow => HasPopular && HasFavorites;
    public bool HasReleases => Releases.Count > 0;
    public bool HasAppearsOn => AppearsOn.Count > 0;

    // ── Latest release (by release date, falling back to year) ──
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLatestRelease))]
    [NotifyPropertyChangedFor(nameof(LatestReleaseDate))]
    [NotifyPropertyChangedFor(nameof(LatestReleaseSubtitle))]
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
    public string LatestReleaseSubtitle => LatestRelease == null
        ? ""
        : $"{LatestRelease.ReleaseKindLabel} · {(LatestRelease.TrackCount == 1 ? "1 song" : $"{LatestRelease.TrackCount} songs")}";

    // ── Releases filter: "all" / "albums" / "singles" ──
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFilterAll))]
    [NotifyPropertyChangedFor(nameof(IsFilterAlbums))]
    [NotifyPropertyChangedFor(nameof(IsFilterSingles))]
    private string _releaseFilter = "all";
    public bool IsFilterAll => ReleaseFilter == "all";
    public bool IsFilterAlbums => ReleaseFilter == "albums";
    public bool IsFilterSingles => ReleaseFilter == "singles";

    // ── About ──
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAbout))]
    [NotifyPropertyChangedFor(nameof(HasAboutFacts))]
    private ArtistInfo? _about;
    public bool HasAbout => About != null;
    public bool HasAboutFacts => About is { } a && (a.HasFrom || a.HasBegin || a.HasGenres || a.HasType || a.HasActive);
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

    // ── Tile sizing (mirrors MoreByArtistView) ──
    private const double TileLabelHeight = 64;
    [ObservableProperty] private double _tileArtworkSize = 220;
    public double TileHeight => TileArtworkSize + TileLabelHeight;
    partial void OnTileArtworkSizeChanged(double value) => OnPropertyChanged(nameof(TileHeight));

    public event EventHandler? BackRequested;
    public event EventHandler<Album>? AlbumOpened;
    public event EventHandler<Track>? SearchLyricsRequested;

    public ArtistDetailViewModel(
        string artistName,
        ILibraryService library,
        PlayerViewModel player,
        LibraryAlbumsViewModel? libraryAlbumsVm = null,
        LibraryArtistsViewModel? artistsVm = null,
        ArtistImageService? images = null,
        SidebarViewModel? sidebar = null,
        ArtistInfoService? info = null)
    {
        ArtistName = (artistName ?? string.Empty).Trim();
        _library = library;
        _player = player;
        LibraryAlbumsVm = libraryAlbumsVm;
        _artistsVm = artistsVm;
        _images = images;
        _sidebar = sidebar;
        _info = info;

        Artist = library.Artists.FirstOrDefault(a => string.Equals(a.Name, ArtistName, StringComparison.OrdinalIgnoreCase))
                 ?? new Artist { Id = LibraryService.ComputeArtistId(ArtistName), Name = ArtistName };
        IsFavorite = _artistsVm?.IsFavoriteArtist(ArtistName) ?? false;

        Rebuild();
        ResolveImage();
        _ = LoadAboutAsync();

        _libraryUpdatedHandler = (_, _) => Dispatcher.UIThread.Post(Rebuild);
        _library.LibraryUpdated += _libraryUpdatedHandler;
        // Hearts on the pills bind Track.IsFavorite; the Top Favorites section itself
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
        var releases = allAlbums
            .Where(a => LibraryAlbumsViewModel.ContainsArtistToken(a.Artist, artistName))
            .OrderByDescending(ReleaseSortDate)
            .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var releaseIds = releases.Select(a => a.Id).ToHashSet();

        var appearsOn = allAlbums
            .Where(a => !releaseIds.Contains(a.Id)
                        && a.Tracks.Any(t => LibraryAlbumsViewModel.ContainsArtistToken(t.Artist, artistName)))
            .OrderByDescending(ReleaseSortDate)
            .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var songs = allAlbums
            .SelectMany(a => a.Tracks)
            .Where(t => LibraryAlbumsViewModel.ContainsArtistToken(t.Artist, artistName))
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

    /// <summary>Applies the All / Albums / Singles &amp; EPs chip.</summary>
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
        FactsLine = string.Join(" · ", new[]
        {
            releases.Count == 1 ? "1 release" : $"{releases.Count} releases",
            songs.Count == 1 ? "1 song" : $"{songs.Count} songs",
            TotalLengthDisplay,
        });

        // Library-side About facts.
        var mostPlayed = songs.OrderByDescending(t => t.PlayCount).FirstOrDefault();
        MostPlayedTitle = mostPlayed is { PlayCount: > 0 } ? mostPlayed.Title : string.Empty;
        InLibrarySince = songs.Count > 0 ? songs.Min(t => t.DateAdded).ToLocalTime().ToString("MMMM yyyy") : string.Empty;
        OnPropertyChanged(nameof(HasMostPlayed));
        OnPropertyChanged(nameof(HasInLibrarySince));

        ApplyLists();
        if (string.IsNullOrEmpty(ImagePath))
            HeroArtPath = LatestRelease?.ArtworkPath;
    }

    private void ApplyLists()
    {
        var q = _query.Trim();
        var favorites = _allSongs.Where(t => t.IsFavorite).ToList();
        FavoriteCount = favorites.Count;

        FavoriteSongs.Clear();
        foreach (var row in RankPopular(favorites, q, q.Length == 0 ? MaxFavorites : 0)) FavoriteSongs.Add(row);

        PopularSongs.Clear();
        foreach (var row in RankPopular(_allSongs, q, q.Length == 0 ? MaxPopular : 0)) PopularSongs.Add(row);

        Releases.Clear();
        foreach (var a in FilterReleases(_allReleases, ReleaseFilter).Where(a => AlbumMatches(a, q))) Releases.Add(a);

        AppearsOn.Clear();
        foreach (var a in _allAppearsOn.Where(a => AlbumMatches(a, q))) AppearsOn.Add(a);

        OnPropertyChanged(nameof(HasPopular));
        OnPropertyChanged(nameof(HasFavorites));
        OnPropertyChanged(nameof(ShowPopularBesideLatest));
        OnPropertyChanged(nameof(ShowPopularBelow));
        OnPropertyChanged(nameof(HasReleases));
        OnPropertyChanged(nameof(HasAppearsOn));
    }

    partial void OnReleaseFilterChanged(string value) => ApplyLists();

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
        try
        {
            var info = await _info.GetAsync(Artist.Id, ArtistName, _cts.Token).ConfigureAwait(false);
            if (_cts.IsCancellationRequested) return;
            await Dispatcher.UIThread.InvokeAsync(() => About = info);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            DebugLogger.Warn(DebugLogger.Category.UI, "Artist.About", ex.Message);
        }
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

    /// <summary>Plays a pill: the queue is the list the pill came from (favourites when
    /// the track is one, else Popular), starting at the pick.</summary>
    [RelayCommand]
    private void PlaySong(Track? track)
    {
        if (track == null) return;
        var source = FavoriteSongs.Any(r => ReferenceEquals(r.Track, track)) ? FavoriteSongs : PopularSongs;
        var songs = source.Select(r => r.Track).ToList();
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
    private void SetReleaseFilter(string? filter) => ReleaseFilter = filter is "albums" or "singles" ? filter : "all";

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
        _library.LibraryUpdated -= _libraryUpdatedHandler;
        _library.FavoritesChanged -= _favoritesChangedHandler;
    }
}
