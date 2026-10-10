using Noctis.Models;

namespace Noctis.Services.MetadataSearch;

// Shared contract for the Search Metadata revamp (owner 10-08: "it is not good" — one silent
// best guess, no choice, no artwork, no preview). The search engine (providers, matching,
// artwork) implements IMetadataSearchService; the metadata editor's search panel consumes it.
// Both sides build against this file, so change it only in a way both can follow.

/// <summary>What to look for. Pre-filled from the track/album being edited; the user can edit
/// the free-text parts before searching.</summary>
public sealed record MetadataQuery
{
    public string Title { get; init; } = string.Empty;
    public string Artist { get; init; } = string.Empty;
    public string Album { get; init; } = string.Empty;
    public string AlbumArtist { get; init; } = string.Empty;
    /// <summary>The edited track's length; lets providers rank the right version/edit.</summary>
    public TimeSpan? Duration { get; init; }
    public int? TrackNumber { get; init; }
    public int? DiscNumber { get; init; }
    /// <summary>The edited track's track total (TRCK "n/total") — its edition's shape. An ISRC
    /// match names the recording, not the release (owner 10-08: deluxe track count 17→15), so
    /// this picks which standard/deluxe/explicit release supplies album, count, date, label…</summary>
    public int? TrackCount { get; init; }
    /// <summary>The edited track's disc total; same purpose as <see cref="TrackCount"/>.</summary>
    public int? DiscCount { get; init; }
    public string Isrc { get; init; } = string.Empty;
    public int? Year { get; init; }
    /// <summary>True for the album editor: candidates are releases (with their track lists)
    /// rather than single recordings.</summary>
    public bool AlbumScope { get; init; }
    /// <summary>The album's local tracks when <see cref="AlbumScope"/> (count, numbers, durations
    /// help pick the right edition and map candidate tracks onto local ones).</summary>
    public IReadOnlyList<Track> AlbumTracks { get; init; } = Array.Empty<Track>();
    /// <summary>Provider names to ask (see <see cref="IMetadataSearchService.Providers"/>); empty = all enabled.</summary>
    public IReadOnlyList<string> Providers { get; init; } = Array.Empty<string>();
}

/// <summary>One track of a release candidate (album scope).</summary>
public sealed record CandidateTrack
{
    public string Title { get; init; } = string.Empty;
    public string Artist { get; init; } = string.Empty;
    public int? TrackNumber { get; init; }
    public int? DiscNumber { get; init; }
    public TimeSpan? Duration { get; init; }
    public string Isrc { get; init; } = string.Empty;
    public bool? Explicit { get; init; }
    /// <summary>Id of the local track this candidate track was matched to (album scope), if any.</summary>
    public Guid? MatchedLocalTrackId { get; init; }
}

/// <summary>A single search result. Every value is optional: null/empty means "this provider
/// doesn't know", never "clear the field".</summary>
public sealed record MetadataCandidate
{
    /// <summary>Display name of the source(s), e.g. "Deezer", "MusicBrainz", "Apple Music" or
    /// "Deezer + MusicBrainz" for a merged result.</summary>
    public string Provider { get; init; } = string.Empty;
    /// <summary>Stable id within the provider (used for de-duplication/caching and "open on web").</summary>
    public string ProviderId { get; init; } = string.Empty;
    public Uri? WebUrl { get; init; }
    /// <summary>0..1 match confidence against the query (higher = better); results come sorted by it.</summary>
    public double Confidence { get; init; }
    /// <summary>Short human reasons for the score, e.g. "Duration ±1 s", "Same track count".</summary>
    public IReadOnlyList<string> MatchNotes { get; init; } = Array.Empty<string>();

    public string Title { get; init; } = string.Empty;
    public string Artist { get; init; } = string.Empty;
    public string Album { get; init; } = string.Empty;
    public string AlbumArtist { get; init; } = string.Empty;
    /// <summary>"yyyy", "yyyy-MM" or "yyyy-MM-dd".</summary>
    public string ReleaseDate { get; init; } = string.Empty;
    public int? Year { get; init; }
    public string Genre { get; init; } = string.Empty;
    public int? TrackNumber { get; init; }
    public int? TrackCount { get; init; }
    public int? DiscNumber { get; init; }
    public int? DiscCount { get; init; }
    public string Composer { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public string Copyright { get; init; } = string.Empty;
    public string Isrc { get; init; } = string.Empty;
    public string Barcode { get; init; } = string.Empty;
    public bool? Explicit { get; init; }
    public int? Bpm { get; init; }
    public TimeSpan? Duration { get; init; }
    /// <summary>The source's own words for which edition this release is, when it names one
    /// (MusicBrainz disambiguation: "explicit", "clean", "Walmart exclusive; ultra clear vinyl").
    /// Display-only, never a tag value; the scorer puts it in <see cref="MatchNotes"/>.</summary>
    public string Edition { get; init; } = string.Empty;

    /// <summary>Best (largest) cover the provider offers.</summary>
    public Uri? ArtworkUrl { get; init; }
    /// <summary>Small cover for result lists (≤ 300 px).</summary>
    public Uri? ArtworkThumbUrl { get; init; }
    /// <summary>Pixel size of <see cref="ArtworkUrl"/> when known (square side).</summary>
    public int? ArtworkSize { get; init; }

    /// <summary>Album scope: the release's track list.</summary>
    public IReadOnlyList<CandidateTrack> Tracks { get; init; } = Array.Empty<CandidateTrack>();
}

/// <summary>Outcome of a search: results plus per-provider status so the UI can say
/// "Deezer: 5 results · MusicBrainz: offline" instead of a bare "nothing found".</summary>
public sealed record MetadataSearchResult
{
    public IReadOnlyList<MetadataCandidate> Candidates { get; init; } = Array.Empty<MetadataCandidate>();
    public IReadOnlyList<ProviderStatus> Providers { get; init; } = Array.Empty<ProviderStatus>();
}

public enum ProviderOutcome { Ok, NoResults, Failed, TimedOut, Disabled }

public sealed record ProviderStatus(string Provider, ProviderOutcome Outcome, int ResultCount, string? Message = null);

public interface IMetadataSearchService
{
    /// <summary>Provider names this service can query (for filter chips), in display order.</summary>
    IReadOnlyList<string> Providers { get; }

    /// <summary>Searches every requested+enabled provider concurrently (each rate-limited per its
    /// rules), merges duplicates, scores against the query and returns candidates best-first.
    /// Never throws for network/provider failures (reported in <see cref="MetadataSearchResult.Providers"/>);
    /// throws only <see cref="OperationCanceledException"/> when cancelled.</summary>
    Task<MetadataSearchResult> SearchAsync(MetadataQuery query, CancellationToken ct = default);

    /// <summary>Downloads the candidate's full-size artwork (falls back to the thumbnail).
    /// Null when unavailable. Never throws except on cancellation.</summary>
    Task<byte[]?> DownloadArtworkAsync(MetadataCandidate candidate, CancellationToken ct = default);
}
