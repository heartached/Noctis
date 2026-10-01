using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Models;

namespace Noctis.Mobile.ViewModels;

/// <summary>A Top Songs row: "album · year" under the title, as in mockup 3.</summary>
public sealed record ArtistTopSong(Track Track, int Rank)
{
    public string Subtitle => Track.DisplayYear > 0 ? $"{Track.Album} · {Track.DisplayYear}" : Track.Album;
}

/// <summary>
/// The phone artist page (spec §5 item 2). Songs are MobileLibrary.SongsBy (albums credited
/// to the name, then every track filed under it); the hero is the newest album's cover
/// (ruling 3: no artist portraits offline); the page tints from it like an album page.
/// </summary>
public sealed partial class ArtistPageViewModel : MobilePage, ITintedPage
{
    private const int TopSongCount = 5;

    public ArtistPageViewModel(ShellViewModel shell, string name)
    {
        Shell = shell;
        Name = name;
        Tint = shell.TintFactory();
        _isFavourite = shell.FavoriteArtists.IsFavorite(name);
        Shell.Library.Refreshed += OnRefreshed;
        Rebuild();
    }

    public ShellViewModel Shell { get; }
    public string Name { get; }
    public PageTint Tint { get; }
    public override string Title => Name;

    /// <summary>Newest release first, then track order: what Play plays.</summary>
    public IReadOnlyList<Track> Songs { get; private set; } = Array.Empty<Track>();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAlbums))]
    private IReadOnlyList<Album> _albums = Array.Empty<Album>();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFeatured))]
    private Album? _featuredAlbum;

    [ObservableProperty] private string _featuredSubtitle = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTopSongs))]
    private IReadOnlyList<ArtistTopSong> _topSongs = Array.Empty<ArtistTopSong>();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHero))]
    private string? _heroArtworkPath;

    [ObservableProperty] private bool _isFavourite;

    public bool HasAlbums => Albums.Count > 0;
    public bool HasFeatured => FeaturedAlbum != null;
    public bool HasTopSongs => TopSongs.Count > 0;
    public bool HasHero => !string.IsNullOrEmpty(HeroArtworkPath);

    private void Rebuild()
    {
        var library = Shell.Library.Service;
        Albums = library.Albums
            .Where(a => string.Equals(a.Artist, Name, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(MobileLibrary.ReleaseSortDate)
            .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var all = MobileLibrary.SongsBy(library, Name);
        var albumOrder = Albums.SelectMany(a => a.Tracks).ToList();
        var inAlbums = new HashSet<Guid>(albumOrder.Select(t => t.Id));
        Songs = albumOrder.Concat(all.Where(t => !inAlbums.Contains(t.Id))).ToList();

        FeaturedAlbum = Albums.FirstOrDefault();
        FeaturedSubtitle = FeaturedAlbum == null
            ? string.Empty
            : string.Join(" · ", new[] { FeaturedAlbum.ReleaseDateFormatted, FeaturedAlbum.TrackCountText }.Where(s => s.Length > 0));

        TopSongs = Songs
            .OrderByDescending(t => t.PlayCount)
            .ThenBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase)
            .Take(TopSongCount)
            .Select((t, i) => new ArtistTopSong(t, i + 1))
            .ToList();

        HeroArtworkPath = Albums.Select(a => a.ArtworkPath).FirstOrDefault(p => !string.IsNullOrEmpty(p))
                          ?? Songs.Select(t => t.AlbumArtworkPath).FirstOrDefault(p => !string.IsNullOrEmpty(p));
        Tint.Load(HeroArtworkPath);
    }

    private void OnRefreshed(object? sender, EventArgs e) => Rebuild();

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

    [RelayCommand]
    private void OpenFeatured()
    {
        if (FeaturedAlbum != null) Shell.OpenAlbumCommand.Execute(FeaturedAlbum);
    }

    public override void OnClosed() => Shell.Library.Refreshed -= OnRefreshed;
}
