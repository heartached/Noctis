using Noctis.Models;

namespace Noctis.Services;

/// <summary>
/// How long one logged play was actually heard. The phone logs a play the moment its audio
/// starts moving, so counting the song's length per play (what this did) turned a few minutes
/// of skipping through songs into hours.
/// </summary>
public static class PlayListening
{
    /// <summary>A song of unknown length counts as this long.</summary>
    public static readonly TimeSpan FallbackLength = TimeSpan.FromMinutes(3);

    /// <summary>
    /// Fallback for an event with no <see cref="PlayHistoryEvent.ListenedMs"/> (logged before it
    /// existed): half the song. A desktop play that was not marked skipped was heard past half,
    /// so half is the least such a play can have been; a phone play of that era could be any
    /// share, and the full length overstated it by up to the 2x or more skipping gives.
    /// </summary>
    public const double LegacyShare = 0.5;

    /// <param name="length">The song's length; null or zero when unknown.</param>
    public static TimeSpan Heard(PlayHistoryEvent e, TimeSpan? length)
    {
        if (e.Skipped) return TimeSpan.Zero;
        var known = length is { } l && l > TimeSpan.Zero;
        var full = known ? length!.Value : FallbackLength;
        if (e.ListenedMs is { } ms)
        {
            var heard = TimeSpan.FromMilliseconds(Math.Max(0, ms));
            return known && heard > full ? full : heard;
        }
        return TimeSpan.FromTicks((long)(full.Ticks * LegacyShare));
    }
}
