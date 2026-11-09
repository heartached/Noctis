using System.Text.RegularExpressions;
using Noctis.Models;
using Noctis.Services.MetadataSearch;

namespace Noctis.Services;

/// <summary>
/// LRCLIB's lookup for one track, shared by the desktop lyrics page and the phone: the exact
/// /get, then — when that has no lyrics — the fuzzy /search with every candidate validated
/// against the local track. An instrumental /get answer is returned as is (Instrumental, no
/// lyrics) and never falls through to /search, which would surface a different song's lyrics.
/// A miss is asked once more without a film credit or featured artists (<see cref="SimplifiedQuery"/>).
/// </summary>
public static partial class LrcLibLookup
{
    public static async Task<LrcLibResult?> FetchAsync(
        ILrcLibService lrcLib, string artist, string title, double duration, CancellationToken ct)
    {
        // "Unknown Artist" is the library's placeholder default, not a name —
        // sending it verbatim guarantees a /get miss and poisons /search relevance.
        var hasKnownArtist = !LyricsSearchSelector.IsUnknownArtist(artist);

        // /get needs an exact artist match — pointless with the placeholder. An error there
        // (LRCLIB answers 503 "busy" to /get while /search still works) falls through to
        // /search; the error surfaces only when /search fails too.
        LrcLibResult? result = null;
        LyricsProviderException? getError = null;
        if (hasKnownArtist)
        {
            try { result = await lrcLib.GetLyricsAsync(artist, title, duration, ct); }
            catch (LyricsProviderException ex) { getError = ex; }
        }
        if (result is { Instrumental: true }) return result;

        if (result == null || !result.HasLyrics)
        {
            // /search is fuzzy and relevance-ordered; validate candidates
            // against the local track before preferring richer formats.
            List<LrcLibResult> results;
            try { results = await lrcLib.SearchLyricsAsync(hasKnownArtist ? artist : "", title, ct); }
            catch (LyricsProviderException) when (getError is not null) { throw getError; }
            result = LyricsSearchSelector.PickFromSearchResults(results, artist, title, duration);
            // Nothing from /search after a /get error is not "no lyrics": /get might have had them.
            if (result is null && getError is not null) throw getError;
            result ??= await SearchSimplifiedAsync(lrcLib, artist, title, duration, hasKnownArtist, ct);
        }
        return result;
    }

    /// <summary>
    /// GitHub #219: Indian film songs are often tagged "Title (From "Movie")" or credited "Singer
    /// feat. Other", and LRCLIB's /search finds nothing under those (live 11-09: "Pramadavanam (From
    /// "His Highness Abdullah")" 0 results, "Pramadavanam" its synced lyrics; artist "Sid Sriram feat.
    /// Yuvan Shankar Raja" 0, "Sid Sriram" 20). One more /search without them, only when that changes
    /// the query, and the answer validated against the shorter names and the song's length.
    /// </summary>
    private static async Task<LrcLibResult?> SearchSimplifiedAsync(
        ILrcLibService lrcLib, string artist, string title, double duration, bool hasKnownArtist, CancellationToken ct)
    {
        var (a, t) = SimplifiedQuery(artist, title);
        if (t.Length == 0 || (t == title && (!hasKnownArtist || a == artist))) return null;
        var results = await lrcLib.SearchLyricsAsync(hasKnownArtist ? a : "", t, ct);
        return LyricsSearchSelector.PickFromSearchResults(results, hasKnownArtist ? a : artist, t, duration);
    }

    /// <summary>The artist and title with a film credit ("(From "Movie")", "[From …]", " - From …") and
    /// featured-artist credits left out; unchanged when they have none.</summary>
    public static (string Artist, string Title) SimplifiedQuery(string artist, string title)
    {
        var t = MatchText.StripFeaturing(FromCredit().Replace(title ?? string.Empty, string.Empty)).Trim();
        var a = MatchText.StripFeaturing(artist ?? string.Empty).Trim();
        return (a.Length > 0 ? a : artist ?? string.Empty, t.Length > 0 ? t : (title ?? string.Empty).Trim());
    }

    // "(From "Movie")", "[From 'Movie']", " - From "Movie"" and everything after it: a credit, never the song's name.
    [GeneratedRegex(@"\s*(?:[\(\[]|\s[-–—]\s)\s*from\s.*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FromCredit();
}
