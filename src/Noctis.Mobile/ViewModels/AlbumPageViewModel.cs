using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Models;

namespace Noctis.Mobile.ViewModels;

/// <summary>One album row: the track and the number shown for it (its tag, else its position).</summary>
public sealed record AlbumTrackRow(Track Track, int Number);

/// <summary>
/// The phone album page (spec §5 item 3). Keyed by album id: a rescan rebuilds Album
/// instances, so on each library refresh the page re-resolves its album by id.
/// </summary>
public sealed partial class AlbumPageViewModel : MobilePage
{
    public AlbumPageViewModel(ShellViewModel shell, Album album)
    {
        Shell = shell;
        AlbumId = album.Id;
        Tint = shell.TintFactory();
        Shell.Library.Refreshed += OnRefreshed;
        Apply(album);
    }

    public ShellViewModel Shell { get; }
    public Guid AlbumId { get; }
    public PageTint Tint { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(HasArtwork))]
    private Album _album = null!;

    [ObservableProperty] private IReadOnlyList<AlbumTrackRow> _tracks = Array.Empty<AlbumTrackRow>();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMetaLine))]
    private string _metaLine = string.Empty;

    [ObservableProperty] private string _footerText = string.Empty;

    public override string Title => Album.Name;
    public bool HasArtwork => !string.IsNullOrEmpty(Album.ArtworkPath);
    public bool HasMetaLine => MetaLine.Length > 0;

    /// <summary>"Genre · Year · Quality", skipping what a file does not carry (the "Unknown"
    /// genre placeholder, year 0, no codec) so a sparsely tagged album never shows "· ·".</summary>
    public static string BuildMetaLine(Album album)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(album.Genre) && !string.Equals(album.Genre, "Unknown", StringComparison.OrdinalIgnoreCase))
            parts.Add(album.Genre);
        if (album.Year > 0) parts.Add(album.Year.ToString(CultureInfo.InvariantCulture));
        if (!string.IsNullOrEmpty(album.AudioQualityBadge)) parts.Add(album.AudioQualityBadge);
        return string.Join(" · ", parts);
    }

    private void Apply(Album album)
    {
        Album = album;
        Tracks = album.Tracks.Select((t, i) => new AlbumTrackRow(t, t.TrackNumber > 0 ? t.TrackNumber : i + 1)).ToList();
        MetaLine = BuildMetaLine(album);
        FooterText = $"{album.TrackCountText}, {album.HeaderDurationFormatted}";
        Tint.Load(album.ArtworkPath);
    }

    private void OnRefreshed(object? sender, EventArgs e)
    {
        var fresh = Shell.Library.Service.GetAlbumById(AlbumId);
        if (fresh != null && !ReferenceEquals(fresh, Album)) Apply(fresh);
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

    [RelayCommand] private void OpenArtist() => Shell.OpenArtistCommand.Execute(Album.Artist);

    [RelayCommand] private void More() => Shell.OpenAlbumSheetCommand.Execute(Album);

    public override void OnClosed() => Shell.Library.Refreshed -= OnRefreshed;
}
