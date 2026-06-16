namespace Noctis.Services;

/// <summary>
/// Aggregated result of an Auto-match run: the best tag suggestion, a cover-art candidate, and
/// online lyrics. Any field may be null when its provider was disabled, failed, or found nothing.
/// The Studio review UI presents each non-null piece for the user to accept or skip.
/// </summary>
public sealed record AutoMatchProposal(
    TagSuggestion? Tags,
    ITunesArtworkService.ArtworkCandidate? Artwork,
    string? SyncedLyrics,
    string? PlainLyrics)
{
    /// <summary>True when at least one provider produced something worth reviewing.</summary>
    public bool HasAnything =>
        Tags is not null || Artwork is not null ||
        !string.IsNullOrWhiteSpace(SyncedLyrics) || !string.IsNullOrWhiteSpace(PlainLyrics);

    public static AutoMatchProposal Empty { get; } = new(null, null, null, null);
}
