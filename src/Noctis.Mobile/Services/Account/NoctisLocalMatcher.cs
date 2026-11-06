using Noctis.Models;
using Noctis.Services.MetadataSearch;

namespace Noctis.Mobile.Services.Account;

/// <summary>
/// Finds the desktop songs the phone already has as its own files, so the library shows one song
/// instead of two. A desktop song matches a local file when the titles agree on their base form
/// (a feat. credit, "- Remastered 2011" or "(Deluxe)" do not matter) AND carry the same version
/// markers (a Live, Remix, Acoustic or Radio Edit never matches the studio song), the credited
/// artists share a name, and the durations are within <see cref="DurationToleranceSeconds"/>.
/// The album only breaks ties (a single and its album copy are the same recording).
/// Local files are indexed by title key, so 10k+ songs cost one pass, not a pairwise scan.
/// </summary>
internal static class NoctisLocalMatcher
{
    /// <summary>The desktop reports whole seconds (up to 0.5 s of rounding) next to the file's exact length.</summary>
    internal const double DurationToleranceSeconds = 2.5;

    private sealed record Entry(Track Track, HashSet<string> Artists, string Album, double Seconds);

    /// <summary>desktop song id → the phone's own track. <paramref name="sticky"/> pairs (from the
    /// last sync) hold while both sides still exist, so a tag edit never flips a link.</summary>
    internal static Dictionary<Guid, Guid> Match(IReadOnlyList<RemoteSong> songs, IEnumerable<Track> library,
        IReadOnlyDictionary<Guid, Guid> sticky)
    {
        var result = new Dictionary<Guid, Guid>();
        var index = new Dictionary<string, List<Entry>>(StringComparer.Ordinal);
        var byId = new Dictionary<Guid, Entry>();
        foreach (var t in library)
        {
            if (t.SourceType != SourceType.Local || t.Duration <= TimeSpan.Zero) continue;
            var key = TitleKey(t.Title);
            var artists = Artists(t.Artist);
            if (key.Length == 0 || artists.Count == 0) continue;
            var e = new Entry(t, artists, MatchText.Fold(t.Album), t.Duration.TotalSeconds);
            byId[t.Id] = e;
            if (!index.TryGetValue(key, out var list)) index[key] = list = new List<Entry>();
            list.Add(e);
        }
        if (byId.Count == 0) return result;

        var claimed = new HashSet<Guid>();
        foreach (var s in songs)
            if (sticky.TryGetValue(s.Id, out var local) && byId.ContainsKey(local) && claimed.Add(local))
                result[s.Id] = local;

        // Songs that also agree on the album pair first, so a single and its album copy each find their own file.
        for (var pass = 0; pass < 2; pass++)
        foreach (var s in songs)
        {
            if (result.ContainsKey(s.Id) || s.DurationSeconds <= 0) continue;
            var key = TitleKey(s.Title);
            if (key.Length == 0 || !index.TryGetValue(key, out var candidates)) continue;
            var artists = Artists(s.Artist);
            if (artists.Count == 0) continue;
            var album = MatchText.Fold(s.Album);

            Entry? best = null;
            var bestScore = double.MaxValue;
            foreach (var c in candidates)
            {
                if (claimed.Contains(c.Track.Id)) continue;
                var diff = Math.Abs(c.Seconds - s.DurationSeconds);
                if (diff > DurationToleranceSeconds || !c.Artists.Overlaps(artists)) continue;
                var sameAlbum = album.Length > 0 && album == c.Album;
                if (pass == 0 && !sameAlbum) continue;
                var score = diff;
                if (score < bestScore || (score == bestScore && best is not null && c.Track.Id.CompareTo(best.Track.Id) < 0))
                {
                    best = c;
                    bestScore = score;
                }
            }
            if (best is null) continue;
            claimed.Add(best.Track.Id);
            result[s.Id] = best.Track.Id;
        }
        return result;
    }

    /// <summary>Folded base title plus its sorted version markers; empty when there is no real title.</summary>
    private static string TitleKey(string? title)
    {
        if (string.IsNullOrWhiteSpace(title) || title == "Unknown Title") return string.Empty;
        var (b, markers) = MatchText.AnalyzeTitle(title);
        // A title with no letters or digits ("?", "!!!") has an empty folded form: keep the text itself.
        if (b.Length == 0) b = title.Trim().ToLowerInvariant();
        return markers.Count == 0 ? b : b + "|" + string.Join(",", markers.OrderBy(m => m, StringComparer.Ordinal));
    }

    private static HashSet<string> Artists(string? artist) =>
        string.IsNullOrWhiteSpace(artist) || artist == "Unknown Artist"
            ? new HashSet<string>()
            : new HashSet<string>(MatchText.SplitArtists(artist), StringComparer.Ordinal);
}
