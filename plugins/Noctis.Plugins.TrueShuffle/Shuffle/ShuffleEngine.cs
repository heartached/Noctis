using System.Security.Cryptography;

namespace Noctis.Plugins.TrueShuffle.Shuffle;

/// <summary>The shuffle modes (names as the settings and the window show them).</summary>
public enum ShuffleMode { EqualChance, NoRepeats, Discovery, NeverPlayed, ForYou }

public static class ShuffleModes
{
    public static readonly string[] Names = { "Equal chance", "No repeats", "Discovery", "Never played", "For you" };

    public static string Name(ShuffleMode mode) => Names[(int)mode];

    public static ShuffleMode Parse(string? value)
    {
        var v = (value ?? "").Trim();
        for (var i = 0; i < Names.Length; i++)
            if (string.Equals(Names[i], v, StringComparison.OrdinalIgnoreCase)) return (ShuffleMode)i;
        // The original plugin's "Never played only".
        if (v.StartsWith("Never played", StringComparison.OrdinalIgnoreCase)) return ShuffleMode.NeverPlayed;
        return ShuffleMode.NoRepeats;
    }
}

/// <summary>What the engine needs of a library track (a copy: the plugin API hands out copies).</summary>
public sealed record Song(string Id, int PlayCount, bool Skip, bool Disliked, DateTimeOffset? SnoozedUntil)
{
    public static Song From(TrackInfo t) => new(t.Id, t.PlayCount, t.SkipWhenShuffling, t.IsDisliked, t.SnoozedUntil);
}

/// <param name="Songs">The order to play.</param>
/// <param name="Seen">The remembered songs after this plan (a fresh cycle forgets the source's).</param>
/// <param name="NewCycle">Every song of the source had been heard: a new cycle began.</param>
/// <param name="Eligible">Songs of the source the rules allow.</param>
/// <param name="Unplayed">Of those, songs Noctis never played.</param>
public sealed record ShufflePlan(IReadOnlyList<Song> Songs, IReadOnlyCollection<string> Seen, bool NewCycle, int Eligible, int Unplayed);

/// <summary>
/// The orders (ported from True Shuffle's ShuffleEngine). Songs are keyed by their Noctis track id.
/// Equal chance: a uniform Fisher-Yates permutation of every eligible song. No repeats: uniform among
/// the songs not started in the remembered cycle; when none are left a new cycle starts. Discovery:
/// the least played first, random within a play count. Never played: only songs Noctis never played
/// and the cycle never started; it never falls back to familiar songs. For you: ranked by
/// <see cref="ForYouPlanner"/>. Skip when shuffling, snoozes and (optionally) disliked songs are left out.
/// </summary>
public static class ShuffleEngine
{
    public static bool IsEligible(Song s, bool skipDisliked, DateTimeOffset now)
        => !s.Skip && (!skipDisliked || !s.Disliked) && (s.SnoozedUntil == null || s.SnoozedUntil <= now);

    public static List<Song> Eligible(IEnumerable<Song> songs, bool skipDisliked, DateTimeOffset now)
        => songs.Where(s => IsEligible(s, skipDisliked, now)).DistinctBy(s => s.Id).ToList();

    /// <summary>
    /// The rules of a mode, asked again when the rolling queue takes a song from its plan: since the
    /// plan was made the song may have been heard, snoozed or played for the first time.
    /// </summary>
    public static bool StillWanted(Song song, ShuffleMode mode, ISet<string> seen, bool skipDisliked, DateTimeOffset now)
        => IsEligible(song, skipDisliked, now)
           && (mode != ShuffleMode.NeverPlayed || song.PlayCount == 0)
           && (mode is ShuffleMode.EqualChance or ShuffleMode.ForYou || !seen.Contains(song.Id)); // For you rests recent songs itself

    /// <param name="input">The source's songs.</param>
    /// <param name="remembered">Ids started in the remembered cycle.</param>
    /// <param name="current">The playing song's id (not planned again).</param>
    /// <param name="last">The last song of the previous cycle: a new cycle does not start with it again.</param>
    /// <param name="upcoming">Ids already waiting in the queue: the plan does not repeat them.</param>
    /// <param name="draw">Uniform integer in [0, n): the test seam (default: a cryptographic generator).</param>
    /// <param name="rank">For you's ordering of the eligible songs.</param>
    public static ShufflePlan Build(IEnumerable<Song> input, ShuffleMode mode, IEnumerable<string> remembered,
        string? current, string? last, bool skipDisliked = false, DateTimeOffset? now = null,
        Func<int, int>? draw = null, ISet<string>? upcoming = null, Func<List<Song>, List<Song>>? rank = null)
    {
        draw ??= RandomNumberGenerator.GetInt32;
        var pool = Eligible(input, skipDisliked, now ?? DateTimeOffset.UtcNow);
        if (upcoming != null) pool = pool.Where(s => !upcoming.Contains(s.Id)).ToList();
        var seen = new HashSet<string>(remembered, StringComparer.Ordinal);
        if (current != null) seen.Add(current);
        var available = pool.Where(s => s.Id != current).ToList();
        int unplayed = available.Count(s => s.PlayCount == 0);
        bool reset = false;
        List<Song> result;
        if (mode == ShuffleMode.ForYou)
            // Not a cycle: the planner orders the songs by the listener's taste and rests recent ones itself.
            return new(rank != null ? rank(available) : Permute(available, draw), seen, false, pool.Count, unplayed);
        if (mode == ShuffleMode.EqualChance) result = available;
        else
        {
            if (mode == ShuffleMode.NeverPlayed) available = available.Where(s => s.PlayCount == 0).ToList();
            result = available.Where(s => !seen.Contains(s.Id)).ToList();
            if (result.Count == 0 && available.Count > 0 && mode != ShuffleMode.NeverPlayed)
            {
                // The source is used up: a new cycle for it. What was heard outside it is kept.
                foreach (var s in pool) seen.Remove(s.Id);
                if (current != null) seen.Add(current);
                result = available;
                reset = true;
            }
        }
        if (mode == ShuffleMode.Discovery)
        {
            result = result.GroupBy(s => Math.Max(0, s.PlayCount)).OrderBy(g => g.Key)
                .SelectMany(g => Permute(g.ToList(), draw)).ToList();
        }
        else Permute(result, draw);
        // At a new cycle boundary don't start with the last song again when there is another.
        // Equal chance stays a strictly uniform permutation and has no recency rule.
        if (mode != ShuffleMode.EqualChance && result.Count > 1 && result[0].Id == last)
        {
            // Discovery's count order comes first: never pull a familiar song ahead of an unplayed one.
            int alternatives = mode == ShuffleMode.Discovery
                ? result.TakeWhile(s => Math.Max(0, s.PlayCount) == Math.Max(0, result[0].PlayCount)).Count() - 1
                : result.Count - 1;
            if (alternatives > 0)
            {
                int alternative = 1 + draw(alternatives);
                (result[0], result[alternative]) = (result[alternative], result[0]);
            }
        }
        return new(result, seen, reset, pool.Count, unplayed);
    }

    /// <summary>Fisher-Yates, in place.</summary>
    public static List<T> Permute<T>(List<T> list, Func<int, int> draw)
    {
        for (int i = list.Count - 1; i > 0; --i)
        {
            int j = draw(i + 1);
            if (j < 0 || j > i) throw new InvalidOperationException("Invalid random draw");
            (list[i], list[j]) = (list[j], list[i]);
        }
        return list;
    }
}
