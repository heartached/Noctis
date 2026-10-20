using Noctis.Models;

namespace Noctis.Services;

/// <summary>
/// LRCLIB's lookup for one track, shared by the desktop lyrics page and the phone: the exact
/// /get, then — when that has no lyrics — the fuzzy /search with every candidate validated
/// against the local track. An instrumental /get answer is returned as is (Instrumental, no
/// lyrics) and never falls through to /search, which would surface a different song's lyrics.
/// </summary>
public static class LrcLibLookup
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
        }
        return result;
    }
}
