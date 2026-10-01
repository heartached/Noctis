using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Models;

namespace Noctis.Mobile.ViewModels;

/// <summary>One album row: the track, the number shown for it (its tag, else its position) and
/// whether it is the player's track, which shows equalizer bars in place of the number.</summary>
public sealed partial class AlbumTrackRow : ObservableObject
{
    public AlbumTrackRow(Track track, int number)
    {
        Track = track;
        Number = number;
    }

    public Track Track { get; }
    public int Number { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsNumber))]
    private bool _isCurrent;

    /// <summary>The bars move: this is the player's track, it is playing, and nothing covers the page.</summary>
    [ObservableProperty] private bool _isAnimating;

    public bool ShowsNumber => !IsCurrent;
}

/// <summary>A "Disc 2" line above a disc's rows, on albums with more than one disc (the
/// desktop album page's DiscGroup header).</summary>
public sealed record AlbumDiscHeader(int Disc)
{
    public string Label => $"Disc {Disc}";
}

/// <summary>
/// The phone album page (spec §5 item 3): a full-bleed cover fading into the cover's own
/// colour, Apple Music style. Keyed by album id: a rescan rebuilds Album instances, so on
/// each library refresh the page re-resolves its album by id.
/// </summary>
public sealed partial class AlbumPageViewModel : MobilePage
{
    public AlbumPageViewModel(ShellViewModel shell, Album album)
    {
        Shell = shell;
        AlbumId = album.Id;
        Tint = shell.TintFactory();
        Tint.HeroFade = true;
        Shell.Library.Refreshed += OnRefreshed;
        Shell.Player.PropertyChanged += OnPlayerChanged;
        Shell.PropertyChanged += OnShellChanged;
        Apply(album);
    }

    public ShellViewModel Shell { get; }
    public Guid AlbumId { get; }
    public PageTint Tint { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(HasArtwork), nameof(QualityBadge), nameof(HasQualityBadge),
        nameof(QualityDetail), nameof(QualityDescription))]
    private Album _album = null!;

    [ObservableProperty] private IReadOnlyList<AlbumTrackRow> _tracks = Array.Empty<AlbumTrackRow>();

    /// <summary>What the list shows: the track rows, with a <see cref="AlbumDiscHeader"/> before
    /// each disc when there is more than one.</summary>
    [ObservableProperty] private IReadOnlyList<object> _rows = Array.Empty<object>();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMetaLine))]
    private string _metaLine = string.Empty;

    [ObservableProperty] private string _footerText = string.Empty;

    /// <summary>A cover for the artist's avatar beside the artist link (the Artists list's rule).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasArtistArtwork))]
    private string? _artistArtworkPath;

    /// <summary>Every track on the album is a favourite: the heart is filled.</summary>
    [ObservableProperty] private bool _isFavourite;

    public override string Title => Album.Name;
    public bool HasArtwork => !string.IsNullOrEmpty(Album.ArtworkPath);
    public bool HasMetaLine => MetaLine.Length > 0;
    public bool HasArtistArtwork => !string.IsNullOrEmpty(ArtistArtworkPath);

    /// <summary>The desktop's audio-quality badge ("Hi-Res Lossless", "Lossless", "AAC", "Mixed"…),
    /// with its tooltip lines for the tap-to-explain flyout; all from Core's Album.</summary>
    public string QualityBadge => Album.AudioQualityBadge;
    public bool HasQualityBadge => QualityBadge.Length > 0;
    public string QualityDetail => Album.AudioQualityDetailedInfo;
    public string QualityDescription => Album.AudioQualityDescription;

    /// <summary>"Genre · Year", skipping what a file does not carry (the "Unknown" genre
    /// placeholder, year 0) so a sparsely tagged album never shows "· ·". The quality badge
    /// follows it as its own element, as on the desktop.</summary>
    public static string BuildMetaLine(Album album)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(album.Genre) && !string.Equals(album.Genre, "Unknown", StringComparison.OrdinalIgnoreCase))
            parts.Add(album.Genre);
        if (album.Year > 0) parts.Add(album.Year.ToString(CultureInfo.InvariantCulture));
        return string.Join(" · ", parts);
    }

    private void Apply(Album album)
    {
        Album = album;
        Tracks = album.Tracks.Select((t, i) => new AlbumTrackRow(t, t.TrackNumber > 0 ? t.TrackNumber : i + 1)).ToList();
        Rows = BuildRows(Tracks);
        MetaLine = BuildMetaLine(album);
        FooterText = $"{album.TrackCountText}, {album.HeaderDurationFormatted}";
        ArtistArtworkPath = MobileLibrary.ArtistArtworkFor(Shell.Library.Service, album.Artist);
        IsFavourite = album.IsAllTracksFavorite;
        SyncNowPlaying();
        Tint.Load(album.ArtworkPath);
    }

    private static IReadOnlyList<object> BuildRows(IReadOnlyList<AlbumTrackRow> tracks)
    {
        if (tracks.Select(r => r.Track.DiscNumber).Distinct().Count() < 2) return tracks.Cast<object>().ToList();
        var rows = new List<object>();
        int? disc = null;
        foreach (var row in tracks)
        {
            if (row.Track.DiscNumber != disc)
            {
                disc = row.Track.DiscNumber;
                rows.Add(new AlbumDiscHeader(row.Track.DiscNumber));
            }
            rows.Add(row);
        }
        return rows;
    }

    private void OnRefreshed(object? sender, EventArgs e)
    {
        var fresh = Shell.Library.Service.GetAlbumById(AlbumId);
        if (fresh != null && !ReferenceEquals(fresh, Album)) Apply(fresh);
        else IsFavourite = Album.IsAllTracksFavorite;   // a favourite toggled here or elsewhere
    }

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

    /// <summary>Marks the player's track and runs its bars only while it plays and the page is
    /// in view: Now Playing, Lyrics and the Queue cover it, and bars animating under them
    /// would keep the renderer busy for nothing.</summary>
    private void SyncNowPlaying()
    {
        var current = Shell.Player.CurrentTrack;
        var animate = Shell.Player.IsPlaying && !Shell.IsNowPlayingOpen && !Shell.IsLyricsOpen && !Shell.IsQueueOpen;
        foreach (var row in Tracks)
        {
            var isCurrent = current != null && row.Track.Id == current.Id;
            row.IsCurrent = isCurrent;
            row.IsAnimating = isCurrent && animate;
        }
    }

    [RelayCommand] private void Play() => PlayFrom(0);

    [RelayCommand]
    private void Shuffle()
    {
        if (Album.Tracks.Count > 0) Shell.Player.PlayShuffled(Album.Tracks, source: Album.Name);
    }

    [RelayCommand]
    private void PlayTrack(AlbumTrackRow? row)
    {
        if (row == null) return;
        PlayFrom(Tracks.ToList().IndexOf(row));
    }

    private void PlayFrom(int index)
    {
        if (Album.Tracks.Count == 0 || index < 0) return;
        Shell.Player.PlayTracks(Album.Tracks, index, Album.Name);
    }

    /// <summary>The heart: favourites every track on the album, or, when all already are,
    /// unfavourites them all (the desktop album page's rule).</summary>
    [RelayCommand]
    private async Task ToggleFavouriteAsync()
    {
        var favourite = !Album.IsAllTracksFavorite;
        IsFavourite = favourite;
        await Shell.SetFavouriteAsync(Album.Tracks, favourite);
    }

    [RelayCommand] private void OpenArtist() => Shell.OpenArtistCommand.Execute(Album.Artist);

    [RelayCommand] private void More() => Shell.OpenAlbumSheetCommand.Execute(Album);

    public override void OnClosed()
    {
        Shell.Library.Refreshed -= OnRefreshed;
        Shell.Player.PropertyChanged -= OnPlayerChanged;
        Shell.PropertyChanged -= OnShellChanged;
    }
}
