using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Models;
using Noctis.Services;

namespace Noctis.ViewModels;

/// <summary>
/// Apple-Music-style artist landing page. Aggregates the artist's local library
/// content into hero + Top Songs + Featured Album + Essential / Studio Albums /
/// Singles &amp; EPs / About sections, and lazily enriches the page with bio data
/// (MusicBrainz) and artist image (Deezer).
/// </summary>
public partial class ArtistDetailViewModel : ViewModelBase
{
    private readonly Artist _artist;
    private readonly ILibraryService _library;
    private readonly PlayerViewModel _player;
    private readonly ArtistImageService? _images;
    private readonly ArtistBioService? _bios;
    private readonly LibraryAlbumsViewModel? _libraryAlbumsVm;

    public string ArtistName => _artist.Name;
    public int AlbumCount => _artist.AlbumCount;
    public int TrackCount => _artist.TrackCount;

    [ObservableProperty] private string? _heroImagePath;
    [ObservableProperty] private bool _hasHeroImage;
    [ObservableProperty] private string _heroTagline = string.Empty;

    public ObservableCollection<Track> TopSongs { get; } = new();
    public ObservableCollection<Album> EssentialAlbums { get; } = new();
    public ObservableCollection<Album> StudioAlbums { get; } = new();
    public ObservableCollection<Album> Singles { get; } = new();
    public ObservableCollection<Album> Eps { get; } = new();
    public ObservableCollection<Album> Compilations { get; } = new();
    [ObservableProperty] private Album? _featuredAlbum;

    // ── Bio fields ──
    [ObservableProperty] private string _bioText = string.Empty;
    [ObservableProperty] private bool _hasBio;
    [ObservableProperty] private string _bioFrom = string.Empty;
    [ObservableProperty] private string _bioBorn = string.Empty;
    [ObservableProperty] private string _bioBornLabel = "BORN";
    [ObservableProperty] private string _bioGenre = string.Empty;
    [ObservableProperty] private string _bioType = string.Empty;
    [ObservableProperty] private string _bioArea = string.Empty;

    public bool ShowFrom  => !string.IsNullOrWhiteSpace(BioFrom);
    public bool ShowBorn  => !string.IsNullOrWhiteSpace(BioBorn);
    public bool ShowGenre => !string.IsNullOrWhiteSpace(BioGenre);
    public bool ShowType  => !string.IsNullOrWhiteSpace(BioType);
    public bool ShowArea  => !string.IsNullOrWhiteSpace(BioArea) && !string.Equals(BioArea, BioFrom, StringComparison.OrdinalIgnoreCase);

    public LibraryAlbumsViewModel? LibraryAlbumsVm => _libraryAlbumsVm;

    public event EventHandler? BackRequested;
    public event EventHandler<Album>? AlbumOpened;
    public event EventHandler? SeeAllAlbumsRequested;

    public ArtistDetailViewModel(
        Artist artist,
        ILibraryService library,
        PlayerViewModel player,
        ArtistImageService? images,
        ArtistBioService? bios,
        LibraryAlbumsViewModel? libraryAlbumsVm)
    {
        _artist = artist;
        _library = library;
        _player = player;
        _images = images;
        _bios = bios;
        _libraryAlbumsVm = libraryAlbumsVm;

        HeroImagePath = _artist.ImagePath;
        HasHeroImage = !string.IsNullOrWhiteSpace(_artist.ImagePath) && File.Exists(_artist.ImagePath);
        HeroTagline = BuildTagline();

        BuildSections();
        _ = LoadEnrichmentsAsync();
    }

    private string BuildTagline()
    {
        var parts = new List<string>();
        if (AlbumCount > 0) parts.Add(AlbumCount == 1 ? "1 album" : $"{AlbumCount} albums");
        if (TrackCount > 0) parts.Add(TrackCount == 1 ? "1 track" : $"{TrackCount} tracks");
        return string.Join("  ·  ", parts);
    }

    private void BuildSections()
    {
        var albums = _library.GetAlbumsByArtist(_artist.Name);

        var allTracks = albums.SelectMany(a => a.Tracks ?? new List<Track>())
                              .Where(t => t != null)
                              .ToList();

        // Top Songs — prefer PlayCount; if no plays anywhere, fall back to favorites then to date added.
        var anyPlayed = allTracks.Any(t => t.PlayCount > 0);
        IEnumerable<Track> ranked = anyPlayed
            ? allTracks.OrderByDescending(t => t.PlayCount)
                       .ThenByDescending(t => t.LastPlayed ?? DateTime.MinValue)
            : allTracks.OrderByDescending(t => t.IsFavorite)
                       .ThenByDescending(t => t.DateAdded);

        TopSongs.Clear();
        foreach (var t in ranked.Take(9)) TopSongs.Add(t);

        // Featured Album = newest by year (then by name as tiebreak).
        FeaturedAlbum = albums.OrderByDescending(a => a.Year)
                              .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                              .FirstOrDefault();

        // Sections now key off the real release-type metadata. Albums with a
        // ReleaseType of Album, Live, Remix, Soundtrack, or Other are grouped
        // under Studio Albums; Compilations get their own section; Singles and
        // EPs each become their own row.
        var studio = albums
            .Where(a => a.ReleaseType is ReleaseType.Album
                                       or ReleaseType.Live
                                       or ReleaseType.Remix
                                       or ReleaseType.Soundtrack
                                       or ReleaseType.Other)
            .ToList();
        var singles = albums.Where(a => a.ReleaseType == ReleaseType.Single).ToList();
        var eps = albums.Where(a => a.ReleaseType == ReleaseType.EP).ToList();
        var comps = albums.Where(a => a.ReleaseType == ReleaseType.Compilation).ToList();

        // Essential Albums = top 5 studio albums by track count.
        EssentialAlbums.Clear();
        foreach (var a in studio.OrderByDescending(a => a.TrackCount)
                                .ThenByDescending(a => a.Year)
                                .Take(5))
            EssentialAlbums.Add(a);

        StudioAlbums.Clear();
        foreach (var a in studio.OrderByDescending(a => a.Year)
                                .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase))
            StudioAlbums.Add(a);

        Singles.Clear();
        foreach (var a in singles.OrderByDescending(a => a.Year)
                                 .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase))
            Singles.Add(a);

        Eps.Clear();
        foreach (var a in eps.OrderByDescending(a => a.Year)
                             .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase))
            Eps.Add(a);

        Compilations.Clear();
        foreach (var a in comps.OrderByDescending(a => a.Year)
                               .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase))
            Compilations.Add(a);
    }

    private async Task LoadEnrichmentsAsync()
    {
        // Artist image
        if (_images != null && !HasHeroImage)
        {
            try
            {
                await _images.FetchAndCacheAsync(new[] { _artist }, (a, p) =>
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        HeroImagePath = p;
                        HasHeroImage = true;
                    });
                });
            }
            catch { /* image enrichment is best-effort */ }
        }

        // Biography
        if (_bios != null)
        {
            try
            {
                var bio = await _bios.GetAsync(_artist.Name);
                Dispatcher.UIThread.Post(() => ApplyBio(bio));
            }
            catch { /* bio enrichment is best-effort */ }
        }
    }

    private void ApplyBio(ArtistBio bio)
    {
        BioFrom = bio.FromDisplay;
        BioBorn = FormatLifespan(bio);
        BioBornLabel = bio.BornLabel;
        BioGenre = bio.TagsDisplay;
        BioType = bio.Type;
        BioArea = bio.Area;
        BioText = ComposeBioText(bio);
        HasBio = !string.IsNullOrWhiteSpace(BioText) ||
                 !string.IsNullOrWhiteSpace(BioFrom) ||
                 !string.IsNullOrWhiteSpace(BioBorn);
        OnPropertyChanged(nameof(ShowFrom));
        OnPropertyChanged(nameof(ShowBorn));
        OnPropertyChanged(nameof(ShowGenre));
        OnPropertyChanged(nameof(ShowType));
        OnPropertyChanged(nameof(ShowArea));
    }

    private static string FormatLifespan(ArtistBio bio)
    {
        if (string.IsNullOrWhiteSpace(bio.LifeSpanBegin)) return string.Empty;
        return string.IsNullOrWhiteSpace(bio.LifeSpanEnd)
            ? bio.LifeSpanBegin
            : $"{bio.LifeSpanBegin} – {bio.LifeSpanEnd}";
    }

    private string ComposeBioText(ArtistBio bio)
    {
        // MusicBrainz doesn't ship full biographies, but the disambiguation field
        // typically holds a short, factual one-liner ("American R&B singer", etc.).
        // Combine it with a generated summary so the About card never reads empty
        // when at least *some* facts came back from the API.
        var pieces = new List<string>();
        if (!string.IsNullOrWhiteSpace(bio.Disambiguation))
            pieces.Add(Capitalize(bio.Disambiguation.Trim()) + ".");

        if (bio.Tags is { Count: > 0 })
        {
            var first = bio.Tags[0];
            if (!string.IsNullOrWhiteSpace(first))
                pieces.Add($"Known for {first}{(bio.Tags.Count > 1 ? $", {bio.Tags[1]}" : string.Empty)}.");
        }

        if (!string.IsNullOrWhiteSpace(bio.FromDisplay))
            pieces.Add($"From {bio.FromDisplay}.");

        if (!string.IsNullOrWhiteSpace(bio.LifeSpanBegin))
        {
            pieces.Add(string.Equals(bio.Type, "Group", StringComparison.OrdinalIgnoreCase)
                ? $"Active since {bio.LifeSpanBegin}."
                : $"Born {bio.LifeSpanBegin}.");
        }

        return string.Join(" ", pieces);
    }

    private static string Capitalize(string s)
        => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s[1..];

    // ── Commands ──

    [RelayCommand]
    private void GoBack() => BackRequested?.Invoke(this, EventArgs.Empty);

    private IEnumerable<Track> AllArtistTracks() =>
        StudioAlbums.SelectMany(a => a.Tracks ?? new List<Track>())
            .Concat(Singles.SelectMany(a => a.Tracks ?? new List<Track>()))
            .Concat(Eps.SelectMany(a => a.Tracks ?? new List<Track>()))
            .Concat(Compilations.SelectMany(a => a.Tracks ?? new List<Track>()))
            .Where(t => t != null);

    [RelayCommand]
    private void PlayArtist()
    {
        var tracks = AllArtistTracks().ToList();
        if (tracks.Count == 0) return;
        _player.ReplaceQueueAndPlay(tracks, 0);
    }

    [RelayCommand]
    private void ShuffleArtist()
    {
        var tracks = AllArtistTracks().OrderBy(_ => Random.Shared.Next()).ToList();
        if (tracks.Count == 0) return;
        _player.ReplaceQueueAndPlay(tracks, 0);
    }

    [RelayCommand]
    private void PlayTrack(Track? track)
    {
        if (track == null) return;
        var ordered = new List<Track>(TopSongs);
        var idx = ordered.IndexOf(track);
        if (idx < 0) { _player.ReplaceQueueAndPlay(new[] { track }, 0); return; }
        _player.ReplaceQueueAndPlay(ordered, idx);
    }

    [RelayCommand]
    private void OpenAlbum(Album? album)
    {
        if (album == null) return;
        AlbumOpened?.Invoke(this, album);
    }

    [RelayCommand]
    private void SeeAllAlbums() => SeeAllAlbumsRequested?.Invoke(this, EventArgs.Empty);
}
