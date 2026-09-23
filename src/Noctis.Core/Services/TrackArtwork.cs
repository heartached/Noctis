using System.Security.Cryptography;

namespace Noctis.Services;

/// <summary>
/// Per-track covers: a track whose embedded cover differs from the rest of its album
/// shows its own cover instead of the album's (Discord, veil 2026-09-22 — "i embedded
/// some specific songs within albums with a different cover art than the rest but
/// noctis just displays one artwork for all of them"). The album cover is still the
/// one cached per AlbumId; only the odd tracks get a second file.
/// </summary>
public static class TrackArtwork
{
    /// <summary>Identity of a cover's bytes (SHA-1, hex); null when there is no cover.</summary>
    public static string? Fingerprint(byte[]? art) =>
        art is { Length: > 0 } ? Convert.ToHexString(SHA1.HashData(art)) : null;

    /// <summary>
    /// Picks the album's cover and the tracks that carry a different one. The album cover
    /// is the most common embedded cover among its tracks; on a tie the current album
    /// cover stays. Tracks with no embedded cover neither vote nor get their own (they show
    /// the album's). With no embedded covers at all, <paramref name="currentAlbumHash"/>
    /// (e.g. a folder cover.jpg) is kept and nothing is odd.
    /// </summary>
    public static (string? AlbumHash, List<Guid> OddTracks) Resolve(
        IEnumerable<(Guid TrackId, string? Hash)> tracks, string? currentAlbumHash)
    {
        var hashed = tracks.Where(t => t.Hash != null).ToList();
        if (hashed.Count == 0) return (currentAlbumHash, new List<Guid>());

        var counts = hashed.GroupBy(t => t.Hash!).Select(g => (Hash: g.Key, Count: g.Count())).ToList();
        var max = counts.Max(c => c.Count);
        var top = counts.Where(c => c.Count == max).Select(c => c.Hash).ToList();
        var album = currentAlbumHash != null && top.Contains(currentAlbumHash) ? currentAlbumHash : top[0];

        return (album, hashed.Where(t => t.Hash != album).Select(t => t.TrackId).ToList());
    }
}
