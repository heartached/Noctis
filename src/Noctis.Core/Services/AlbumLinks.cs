using Noctis.Helpers;
using Noctis.Models;

namespace Noctis.Services;

/// <summary>
/// Discord (Halil, 11-11): a deluxe or expanded edition that holds the original album's songs
/// plus a few bonus tracks. With the original's files kept once, "Link Tracks From" makes the
/// edition list them too (<see cref="AlbumLink"/>), as the very same Track instances: one row
/// per file in Songs, one favourite, one play count.
///
/// The rules, all decided here so every album build agrees:
///   * <see cref="Album.Tracks"/> stays the album's own files. Only <see cref="Album.EditionTracks"/>,
///     <see cref="Album.TrackCount"/> and <see cref="Album.TotalDuration"/> count the linked songs.
///   * A link takes the source album's own songs, never what that album links in itself: no
///     chains, no cycles.
///   * A linked song keeps its own disc and track number (an edition lists the original's songs
///     at their places, bonus tracks after them); on a tie the edition's own file comes first.
///   * The edition's own copy of a song (same title, disc and track) wins over the linked one,
///     so a song kept in both folders is not listed twice.
/// </summary>
public static class AlbumLinks
{
    /// <summary>Fills <see cref="Album.EditionTracks"/>, <see cref="Album.LinkedTracks"/>,
    /// <see cref="Album.LinkedAlbumIds"/>, the edition's count and length for every album a link
    /// names. Links to an album not in <paramref name="index"/> (hidden, not scanned yet) wait.</summary>
    public static void Attach(IReadOnlyDictionary<Guid, Album> index, IReadOnlyList<AlbumLink>? links)
    {
        if (links == null || links.Count == 0) return;

        foreach (var group in links.Where(l => l.AlbumId != l.SourceAlbumId).GroupBy(l => l.AlbumId))
        {
            if (!index.TryGetValue(group.Key, out var album)) continue;

            var seen = new HashSet<Guid>(album.Tracks.Select(t => t.Id));
            var ownKeys = new HashSet<string>(album.Tracks.Select(SongKey), StringComparer.Ordinal);
            var linked = new List<Track>();
            var sources = new List<Guid>();
            foreach (var link in group)
            {
                if (!index.TryGetValue(link.SourceAlbumId, out var source) || sources.Contains(source.Id)) continue;
                sources.Add(source.Id);
                foreach (var t in source.Tracks)
                {
                    if (ownKeys.Contains(SongKey(t)) || !seen.Add(t.Id)) continue;
                    linked.Add(t);
                }
            }
            if (sources.Count == 0) continue;

            var own = new HashSet<Track>(album.Tracks, ReferenceEqualityComparer.Instance);
            var edition = album.Tracks.Concat(linked)
                .OrderBy(t => t.DiscNumber <= 0 ? 1 : t.DiscNumber)
                .ThenBy(t => t.TrackNumber <= 0 ? int.MaxValue : t.TrackNumber)
                .ThenBy(t => own.Contains(t) ? 0 : 1)
                .ThenBy(t => t.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();

            album.LinkedTracks = linked;
            album.LinkedAlbumIds = sources;
            album.EditionTracks = edition;
            album.TrackCount = edition.Count;
            album.TotalDuration = TimeSpan.FromTicks(edition.Sum(t => t.Duration.Ticks));
        }
    }

    /// <summary>
    /// Links whose album key is gone from the new build follow that album's songs: an album
    /// renamed in Get Info, or re-keyed by the album grouping (GitHub #178), gets a new key, and
    /// the link moves with it when all of its songs now share one album. Returns null when no
    /// link changes. A key whose songs are simply missing (hidden folder, partial scan, unplugged
    /// drive) keeps its link as it is.
    /// </summary>
    public static List<AlbumLink>? Remap(IReadOnlyList<AlbumLink> links, IReadOnlyDictionary<Guid, Album> oldIndex,
        IReadOnlyDictionary<Guid, Track> trackIndex, IReadOnlyDictionary<Guid, Album> newIndex)
    {
        if (links.Count == 0 || oldIndex.Count == 0) return null;

        var changed = false;
        var result = new List<AlbumLink>(links.Count);
        foreach (var link in links)
        {
            var albumId = Follow(link.AlbumId);
            var sourceId = Follow(link.SourceAlbumId);
            if (albumId != link.AlbumId || sourceId != link.SourceAlbumId) changed = true;
            if (albumId == sourceId || result.Any(l => l.AlbumId == albumId && l.SourceAlbumId == sourceId))
            {
                changed = true;
                continue;
            }
            result.Add(new AlbumLink { AlbumId = albumId, SourceAlbumId = sourceId });
        }
        return changed ? result : null;

        Guid Follow(Guid id)
        {
            if (newIndex.ContainsKey(id) || !oldIndex.TryGetValue(id, out var old)) return id;
            Guid? target = null;
            foreach (var t in old.Tracks)
            {
                if (!trackIndex.TryGetValue(t.Id, out var now)) continue;
                if (target is { } other && other != now.AlbumId) return id;
                target = now.AlbumId;
            }
            return target is { } to && newIndex.ContainsKey(to) ? to : id;
        }
    }

    /// <summary>The links with <paramref name="albumId"/> listing <paramref name="sourceAlbumId"/>'s
    /// songs added; unchanged when that link is there already or names one album twice.</summary>
    public static List<AlbumLink> With(IReadOnlyList<AlbumLink> links, Guid albumId, Guid sourceAlbumId)
    {
        var result = links.ToList();
        if (albumId == Guid.Empty || sourceAlbumId == Guid.Empty || albumId == sourceAlbumId) return result;
        if (!result.Any(l => l.AlbumId == albumId && l.SourceAlbumId == sourceAlbumId))
            result.Add(new AlbumLink { AlbumId = albumId, SourceAlbumId = sourceAlbumId });
        return result;
    }

    /// <summary>The links without the one from <paramref name="albumId"/> to <paramref name="sourceAlbumId"/>.</summary>
    public static List<AlbumLink> Without(IReadOnlyList<AlbumLink> links, Guid albumId, Guid sourceAlbumId)
        => links.Where(l => !(l.AlbumId == albumId && l.SourceAlbumId == sourceAlbumId)).ToList();

    /// <summary>Albums that could lend their songs to <paramref name="album"/>: the same album
    /// artist's other albums that it does not link yet, by name.</summary>
    public static List<Album> Candidates(Album album, IEnumerable<Album> artistAlbums)
        => artistAlbums
            .Where(a => a.Id != album.Id && !album.LinkedAlbumIds.Contains(a.Id) && a.Tracks.Count > 0)
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string SongKey(Track t)
        => $"{(t.DiscNumber <= 0 ? 1 : t.DiscNumber)}\0{t.TrackNumber}\0{SearchText.Normalize(t.Title)}";
}
