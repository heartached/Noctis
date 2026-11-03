namespace Noctis.Plugins.AlbumTown.Town;

/// <summary>
/// What Album Town hands to the queue. Snoozed songs stay out, as in Noctis's own shuffle
/// (ShuffleHelper: a song is snoozed while <see cref="TrackInfo.SnoozedUntil"/> is later than now).
/// </summary>
public static class TownQueue
{
    public static bool IsSnoozed(TrackInfo t, DateTimeOffset now) => t.SnoozedUntil is { } until && until > now;

    /// <summary>
    /// <paramref name="ids"/> without the snoozed songs, in order. The same list when none is
    /// snoozed, and also when every song is (a click on Play still plays what was asked for).
    /// Songs <paramref name="find"/> does not know are kept (the host skips unknown ids).
    /// </summary>
    public static IReadOnlyList<string> WithoutSnoozed(IReadOnlyList<string> ids, Func<string, TrackInfo?> find, DateTimeOffset now)
    {
        List<string>? kept = null;
        for (var i = 0; i < ids.Count; i++)
        {
            var snoozed = find(ids[i]) is { } t && IsSnoozed(t, now);
            if (snoozed && kept is null)
            {
                kept = new List<string>(ids.Count);
                for (var j = 0; j < i; j++) kept.Add(ids[j]);
            }
            else if (!snoozed) kept?.Add(ids[i]);
        }
        return kept is null || kept.Count == 0 ? ids : kept;
    }
}
