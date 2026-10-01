using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Models;
using Noctis.Services;

namespace Noctis.Mobile.ViewModels;

/// <summary>A Top Songs row: "Album · Year" under the title (Apple's), and whether it is the
/// player's track, which shows the playing bars over its cover.</summary>
public sealed partial class ArtistTopSong : ObservableObject
{
    public ArtistTopSong(Track track, int rank, bool showsRule)
    {
        Track = track;
        Rank = rank;
        ShowsRule = showsRule;
    }

    public Track Track { get; }
    public int Rank { get; }

    /// <summary>A hairline under the row: every row of a column but its last.</summary>
    public bool ShowsRule { get; }

    public string Subtitle => Track.DisplayYear > 0 ? $"{Track.Album} · {Track.DisplayYear}" : Track.Album;

    [ObservableProperty] private bool _isCurrent;

    /// <summary>The bars move: this is the player's track, it is playing, and nothing covers the page.</summary>
    [ObservableProperty] private bool _isAnimating;
}

/// <summary>One column of the Top Songs grid (<see cref="ArtistPageViewModel.TopSongRowsPerPage"/> rows).</summary>
public sealed record ArtistTopSongPage(IReadOnlyList<ArtistTopSong> Rows);

/// <summary>A tile on the artist page's rails: the release and the line under its name (the year
/// on the artist's own rails, the album's artist on Appears On).</summary>
public sealed record ArtistRelease(Album Album, string Subtitle)
{
    public static ArtistRelease Own(Album album) =>
        new(album, album.Year > 0 ? album.Year.ToString(CultureInfo.InvariantCulture) : string.Empty);

    public static ArtistRelease Feature(Album album) => new(album, album.Artist);
}

/// <summary>What a carousel card features.</summary>
public enum ArtistCardKind { Featured, MostPlayed, LatestSingle }

/// <summary>
/// A card of the carousel under the hero (Apple's "FEATURED ALBUM" card): the release's cover,
/// a small-caps kicker with its kind and date, its name (with the explicit badge) and its song
/// count, and a round play button.
/// </summary>
public sealed record ArtistCard(ArtistCardKind Kind, Album Album)
{
    /// <summary>"FEATURED ALBUM", "MOST PLAYED", "LATEST SINGLE" / "LATEST EP".</summary>
    public string Label => Kind switch
    {
        ArtistCardKind.Featured => "FEATURED ALBUM",
        ArtistCardKind.MostPlayed => "MOST PLAYED",
        _ => Album.ReleaseType == ReleaseType.EP ? "LATEST EP" : "LATEST SINGLE",
    };

    /// <summary>"SEP 12, 2025" from the release-date tag, else the year, else empty.</summary>
    public string DateText => Album.ReleaseDateShortFormatted.ToUpperInvariant();

    /// <summary>The kicker: the label, then the date when there is one.</summary>
    public string Kicker => DateText.Length > 0 ? $"{Label} · {DateText}" : Label;

    public string SongsText => Album.TrackCount == 1 ? "1 song" : $"{Album.TrackCount.ToString(CultureInfo.InvariantCulture)} songs";

    public bool IsExplicit => Album.IsExplicit;
}

/// <summary>
/// The phone artist page, Apple Music style (spec §5 item 2): the artist's photo full-bleed
/// under the status bar, fading into its own bottom colour, with the name and three round
/// buttons over it; a card carousel (the latest release, the most played release, the latest
/// single); Top Songs as a sideways-paged grid; then Albums, Singles &amp; EPs and Appears On.
/// The photo is Deezer's (<see cref="ShellViewModel.ArtistPhotos"/>, asked as the page opens);
/// until it arrives, or without one, the newest release's cover stands in.
/// </summary>
public sealed partial class ArtistPageViewModel : MobilePage, ITintedPage
{
    /// <summary>How many songs Top Songs ranks, and how many rows a column of its grid holds.</summary>
    public const int TopSongCount = 20, TopSongRowsPerPage = 4;

    private readonly CancellationTokenSource _photoCts = new();
    private string? _photo;
    private string? _coverPath;

    public ArtistPageViewModel(ShellViewModel shell, string name)
    {
        Shell = shell;
        Name = name;
        Tint = shell.TintFactory();
        Tint.HeroFade = true;
        _isFavourite = shell.FavoriteArtists.IsFavorite(name);
        _photo = shell.ArtistPhotos?.CachedPhoto(name);
        Shell.Library.Refreshed += OnRefreshed;
        Shell.Player.PropertyChanged += OnPlayerChanged;
        Shell.PropertyChanged += OnShellChanged;
        Rebuild();
        PhotoLoad = _photo == null ? LoadPhotoAsync(_photoCts.Token) : Task.CompletedTask;
    }

    public ShellViewModel Shell { get; }
    public string Name { get; }
    public PageTint Tint { get; }
    public override string Title => Name;

    /// <summary>The photo lookup started with the page; tests await it.</summary>
    internal Task PhotoLoad { get; }

    /// <summary>Newest release first, then track order, then the artist's other songs: what Play plays.</summary>
    public IReadOnlyList<Track> Songs { get; private set; } = Array.Empty<Track>();

    /// <summary>Every release credited to the artist, newest first.</summary>
    public IReadOnlyList<Album> Releases { get; private set; } = Array.Empty<Album>();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCards))]
    private IReadOnlyList<ArtistCard> _cards = Array.Empty<ArtistCard>();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTopSongs))]
    private IReadOnlyList<ArtistTopSong> _topSongs = Array.Empty<ArtistTopSong>();

    /// <summary><see cref="TopSongs"/> in columns of <see cref="TopSongRowsPerPage"/>.</summary>
    [ObservableProperty] private IReadOnlyList<ArtistTopSongPage> _topSongPages = Array.Empty<ArtistTopSongPage>();

    /// <summary>Releases that are not singles or EPs (the desktop artist page's Albums tab rule).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAlbums))]
    private IReadOnlyList<ArtistRelease> _albums = Array.Empty<ArtistRelease>();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSingles))]
    private IReadOnlyList<ArtistRelease> _singles = Array.Empty<ArtistRelease>();

    /// <summary>Other artists' albums with a song crediting this one (a feature).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAppearsOn))]
    private IReadOnlyList<ArtistRelease> _appearsOn = Array.Empty<ArtistRelease>();

    /// <summary>The hero: the artist's photo, else the newest release's cover.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHero))]
    private string? _heroArtworkPath;

    /// <summary>The hero is the artist's own photo, not a cover standing in.</summary>
    [ObservableProperty] private bool _hasPhoto;

    [ObservableProperty] private bool _isFavourite;

    public bool HasCards => Cards.Count > 0;
    public bool HasTopSongs => TopSongs.Count > 0;
    public bool HasAlbums => Albums.Count > 0;
    public bool HasSingles => Singles.Count > 0;
    public bool HasAppearsOn => AppearsOn.Count > 0;
    public bool HasHero => !string.IsNullOrEmpty(HeroArtworkPath);

    private void Rebuild()
    {
        var library = Shell.Library.Service;
        Releases = library.Albums
            .Where(a => string.Equals(a.Artist, Name, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(MobileLibrary.ReleaseSortDate)
            .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        Albums = Releases.Where(a => !IsSingleOrEp(a)).Select(ArtistRelease.Own).ToList();
        Singles = Releases.Where(IsSingleOrEp).Select(ArtistRelease.Own).ToList();
        AppearsOn = BuildAppearsOn(library, Name, Releases).Select(ArtistRelease.Feature).ToList();

        var all = MobileLibrary.SongsBy(library, Name);
        var albumOrder = Releases.SelectMany(a => a.Tracks).ToList();
        var inAlbums = new HashSet<Guid>(albumOrder.Select(t => t.Id));
        Songs = albumOrder.Concat(all.Where(t => !inAlbums.Contains(t.Id))).ToList();

        Cards = BuildCards(Releases);

        var ranked = RankTopSongs(Songs).Take(TopSongCount).ToList();
        TopSongs = ranked
            .Select((t, i) => new ArtistTopSong(t, i + 1, showsRule: i % TopSongRowsPerPage != TopSongRowsPerPage - 1 && i != ranked.Count - 1))
            .ToList();
        TopSongPages = TopSongs.Chunk(TopSongRowsPerPage).Select(rows => new ArtistTopSongPage(rows)).ToList();
        SyncNowPlaying();

        _coverPath = Releases.Select(a => a.ArtworkPath).FirstOrDefault(p => !string.IsNullOrEmpty(p))
                     ?? Songs.Select(t => t.AlbumArtworkPath).FirstOrDefault(p => !string.IsNullOrEmpty(p));
        ApplyHero();
    }

    /// <summary>The desktop artist page's split (ArtistDetailViewModel.FilterReleases): singles
    /// and EPs on their own rail, everything else under Albums.</summary>
    public static bool IsSingleOrEp(Album album) => album.ReleaseType is ReleaseType.Single or ReleaseType.EP;

    /// <summary>Most plays first (the songs' play counts), then title: Apple's Top Songs from the
    /// library's own listening.</summary>
    public static IEnumerable<Track> RankTopSongs(IEnumerable<Track> songs) =>
        songs.OrderByDescending(t => t.PlayCount).ThenBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase);

    /// <summary>
    /// The carousel: the newest release (Featured), then the release played most (its songs'
    /// play counts summed, ties to the newer) and the newest single or EP, each only when it is
    /// a release not already on a card.
    /// </summary>
    public static IReadOnlyList<ArtistCard> BuildCards(IReadOnlyList<Album> releasesNewestFirst)
    {
        var cards = new List<ArtistCard>();
        if (releasesNewestFirst.Count == 0) return cards;
        cards.Add(new ArtistCard(ArtistCardKind.Featured, releasesNewestFirst[0]));

        var mostPlayed = releasesNewestFirst
            .Select((a, i) => (Album: a, Order: i, Plays: a.Tracks.Sum(t => (long)t.PlayCount)))
            .Where(x => x.Plays > 0)
            .OrderByDescending(x => x.Plays)
            .ThenBy(x => x.Order)
            .Select(x => x.Album)
            .FirstOrDefault();
        if (mostPlayed != null && cards.All(c => !ReferenceEquals(c.Album, mostPlayed)))
            cards.Add(new ArtistCard(ArtistCardKind.MostPlayed, mostPlayed));

        var latestSingle = releasesNewestFirst.FirstOrDefault(IsSingleOrEp);
        if (latestSingle != null && cards.All(c => !ReferenceEquals(c.Album, latestSingle)))
            cards.Add(new ArtistCard(ArtistCardKind.LatestSingle, latestSingle));
        return cards;
    }

    /// <summary>Albums credited to someone else with a song whose artist credit names this
    /// artist (Core's credit separators: "A feat. B", "A &amp; B"), newest first.</summary>
    public static IReadOnlyList<Album> BuildAppearsOn(ILibraryService library, string name, IReadOnlyList<Album> releases)
    {
        var own = new HashSet<Guid>(releases.Select(a => a.Id));
        return library.Albums
            .Where(a => !own.Contains(a.Id) && a.Tracks.Any(t => Credits(t, name)))
            .OrderByDescending(MobileLibrary.ReleaseSortDate)
            .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        static bool Credits(Track track, string name) =>
            string.Equals(track.GroupingArtist, name, StringComparison.OrdinalIgnoreCase)
            || ArtistCredit.Split(track.Artist).Any(token => string.Equals(token, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The hero shows the photo when there is one, else the cover; the page takes its
    /// colour from whichever it shows.</summary>
    private void ApplyHero()
    {
        var path = _photo ?? _coverPath;
        HasPhoto = _photo != null;
        if (path == HeroArtworkPath && Tint.HasTint) return;
        HeroArtworkPath = path;
        Tint.Load(path);
    }

    private async Task LoadPhotoAsync(CancellationToken ct)
    {
        if (Shell.ArtistPhotos is not { } photos) return;
        try
        {
            var path = await photos.GetPhotoAsync(Name, ct);
            if (path == null || ct.IsCancellationRequested) return;
            _photo = path;
            ApplyHero();
        }
        catch (OperationCanceledException)
        {
            // The page closed first.
        }
        catch (Exception ex)
        {
            DebugLog.Write("Artist", $"Artist photo failed: {ex.Message}");
        }
    }

    private void OnRefreshed(object? sender, EventArgs e) => Rebuild();

    private void OnPlayerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(NowPlayingViewModel.CurrentTrack) or nameof(NowPlayingViewModel.IsPlaying))
            SyncNowPlaying();
    }

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ShellViewModel.IsNowPlayingOpen) or nameof(ShellViewModel.IsLyricsOpen)
            or nameof(ShellViewModel.IsQueueOpen))
            SyncNowPlaying();
    }

    /// <summary>The album page's rule: the player's song shows the bars, moving only while it
    /// plays and nothing covers the page.</summary>
    private void SyncNowPlaying()
    {
        var current = Shell.Player.CurrentTrack;
        var animate = Shell.Player.IsPlaying && !Shell.IsNowPlayingOpen && !Shell.IsLyricsOpen && !Shell.IsQueueOpen;
        foreach (var row in TopSongs)
        {
            var isCurrent = current != null && row.Track.Id == current.Id;
            row.IsCurrent = isCurrent;
            row.IsAnimating = isCurrent && animate;
        }
    }

    [RelayCommand]
    private void Play()
    {
        if (Songs.Count > 0) Shell.Player.PlayTracks(Songs, 0, Name);
    }

    [RelayCommand]
    private void Shuffle()
    {
        if (Songs.Count > 0) Shell.Player.PlayShuffled(Songs, source: Name);
    }

    [RelayCommand]
    private void ToggleFavourite()
    {
        IsFavourite = !IsFavourite;
        Shell.FavoriteArtists.SetFavorite(Name, IsFavourite);
    }

    /// <summary>A Top Songs row plays the Top Songs list from that rank.</summary>
    [RelayCommand]
    private void PlayTopSong(ArtistTopSong? row)
    {
        if (row == null) return;
        var list = TopSongs.Select(s => s.Track).ToList();
        var index = list.IndexOf(row.Track);
        if (index >= 0) Shell.Player.PlayTracks(list, index, Name);
    }

    /// <summary>Top Songs' ›: every song by the artist, most played first, under the artist's name.</summary>
    [RelayCommand]
    private void OpenAllSongs() =>
        Shell.Navigate(new SongListPageViewModel(Shell, Name, () => RankTopSongs(MobileLibrary.SongsBy(Shell.Library.Service, Name))));

    [RelayCommand] private void OpenCard(ArtistCard? card) => Shell.OpenAlbumCommand.Execute(card?.Album);

    /// <summary>A card's round ▶ (where Apple has its download arrow): plays that release.</summary>
    [RelayCommand]
    private void PlayCard(ArtistCard? card)
    {
        if (card is { Album.Tracks.Count: > 0 }) Shell.Player.PlayTracks(card.Album.Tracks, 0, card.Album.Name);
    }

    [RelayCommand] private void OpenAlbums() => OpenRail("Albums", () => Albums);

    [RelayCommand] private void OpenSingles() => OpenRail("Singles & EPs", () => Singles);

    [RelayCommand] private void OpenAppearsOn() => OpenRail("Appears On", () => AppearsOn);

    private void OpenRail(string title, Func<IReadOnlyList<ArtistRelease>> releases) =>
        Shell.Navigate(new RailGridPageViewModel(Shell, title, () => releases().Select(r => RailItem.ForAlbum(r.Album))));

    /// <summary>The … button: the artist's sheet (play, queue, pin), headed by what the hero shows.</summary>
    [RelayCommand]
    private void More()
    {
        var artist = Shell.Library.Service.Artists.FirstOrDefault(a => string.Equals(a.Name, Name, StringComparison.OrdinalIgnoreCase))
                     ?? new Artist { Name = Name };
        Shell.OpenArtistSheetCommand.Execute(new ArtistListItem(artist, HeroArtworkPath));
    }

    public override void OnClosed()
    {
        _photoCts.Cancel();
        Shell.Library.Refreshed -= OnRefreshed;
        Shell.Player.PropertyChanged -= OnPlayerChanged;
        Shell.PropertyChanged -= OnShellChanged;
    }
}
