namespace Noctis.Models;

/// <summary>
/// One row of the Artists page's list view (Discord, vari: "list/column view for artists,
/// like the Songs tab"). Built off the UI thread with the grid rows, so the columns are
/// plain values: a rebuild hands the list fresh rows, the same way the grid re-materializes
/// its tiles for the non-observable <see cref="Models.Artist.ImagePath"/>.
/// </summary>
public sealed class ArtistListRow
{
    public required Artist Artist { get; init; }

    /// <summary>Summed length of the artist's library songs (Inbox songs are not in the library).</summary>
    public TimeSpan TotalDuration { get; init; }

    /// <summary>The artist's most common genre tag, title-cased like the artist page's GENRE
    /// fact; empty when none of the songs is tagged.</summary>
    public string Genre { get; init; } = string.Empty;

    /// <summary>Plays of the artist's library songs, summed like Home's Top Artists (GitHub #231).</summary>
    public long PlayCount { get; init; }

    public string Name => Artist.Name;
    public int AlbumCount => Artist.AlbumCount;
    public int TrackCount => Artist.TrackCount;
    public string? ImagePath => Artist.ImagePath;
    public bool IsFavorite => Artist.IsFavorite;
    public string DurationFormatted => Album.FormatHeaderDuration(TotalDuration);

    /// <summary>A screen reader reads the row (a ListBoxItem) as the artist's name.</summary>
    public override string ToString() => Artist.Name;
}
