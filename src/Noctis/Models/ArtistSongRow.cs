using System.Collections.Generic;

namespace Noctis.Models;

/// <summary>
/// Section header row ("Songs" / "Albums") interleaved with the album grid rows
/// when the Albums view is filtered to a single artist's page.
/// </summary>
public sealed class ArtistSectionHeader
{
    public required string Title { get; init; }
}

/// <summary>
/// One virtualized row of the artist page's Songs section: up to three
/// ranked song pills (same layout as the Home tab's "Most Listened To" grid).
/// </summary>
public sealed class ArtistSongsRow
{
    public required List<TopSongRow> Songs { get; init; }
}
