using Noctis.Localization;

namespace Noctis.Models;

/// <summary>
/// One row of the Genres page (GitHub #220): a genre with how many albums and songs carry it.
/// Built off the UI thread from the library's albums, so the values are plain; a rebuild hands
/// the list fresh rows (like <see cref="ArtistListRow"/>).
/// </summary>
public sealed class GenreRow
{
    /// <summary>The bucket for songs with no genre tag. A song tagged "Unknown" lands here too:
    /// genres merge case-insensitively, and an untagged album already shows "Unknown"
    /// (<see cref="Album.Genre"/>).</summary>
    public const string UnknownKey = "Unknown";

    /// <summary>The genre as tagged (the first spelling met wins), or <see cref="UnknownKey"/>.</summary>
    public required string Name { get; init; }

    public int AlbumCount { get; init; }
    public int SongCount { get; init; }
    public TimeSpan TotalDuration { get; init; }

    /// <summary>Cover of the genre's first album that has one, for the row's thumbnail.</summary>
    public string? ArtworkPath { get; init; }

    public bool IsUnknown => IsUnknownKey(Name);
    public string DisplayName => IsUnknown ? Loc.T("Genres.Unknown") : Name;
    public string DurationFormatted => Album.FormatHeaderDuration(TotalDuration);

    public static bool IsUnknownKey(string? genre) => string.Equals(genre?.Trim(), UnknownKey, StringComparison.OrdinalIgnoreCase);

    /// <summary>A screen reader reads the row (a ListBoxItem) as the genre's name.</summary>
    public override string ToString() => DisplayName;
}
