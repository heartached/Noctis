using System.Text;
using Noctis.Models;

namespace Noctis.Services;

/// <summary>A set of tracks judged to be duplicates, with a suggested copy to keep.</summary>
public sealed record DuplicateGroup(IReadOnlyList<Track> Tracks, Guid SuggestedKeepId);

/// <summary>
/// Pure duplicate detection: groups tracks that share a normalized artist+title and have
/// durations within a tolerance, then nominates the highest-quality copy to keep. No I/O —
/// all inputs come from the already-scanned <see cref="Track"/> metadata.
/// </summary>
public static class DuplicateMatcher
{
    public const int DefaultDurationToleranceSeconds = 2;

    public static IReadOnlyList<DuplicateGroup> FindDuplicates(
        IEnumerable<Track> tracks, int durationToleranceSeconds = DefaultDurationToleranceSeconds)
    {
        var groups = new List<DuplicateGroup>();

        var byKey = tracks
            .Where(t => t is not null && !string.IsNullOrWhiteSpace(t.FilePath))
            .GroupBy(NormalizeKey);

        foreach (var keyGroup in byKey)
        {
            if (string.IsNullOrEmpty(keyGroup.Key)) continue;

            // Within a title/artist key, split into clusters whose durations are close.
            // The cluster anchor is its first (shortest) member, which prevents a long
            // chain of small steps from merging genuinely different-length recordings.
            var ordered = keyGroup.OrderBy(t => t.Duration).ToList();
            var cluster = new List<Track>();
            var anchor = TimeSpan.Zero;

            foreach (var t in ordered)
            {
                if (cluster.Count == 0)
                {
                    cluster.Add(t);
                    anchor = t.Duration;
                }
                else if (Math.Abs((t.Duration - anchor).TotalSeconds) <= durationToleranceSeconds)
                {
                    cluster.Add(t);
                }
                else
                {
                    FlushCluster(cluster, groups);
                    cluster = new List<Track> { t };
                    anchor = t.Duration;
                }
            }
            FlushCluster(cluster, groups);
        }

        return groups;
    }

    private static void FlushCluster(List<Track> cluster, List<DuplicateGroup> groups)
    {
        if (cluster.Count < 2) return;
        groups.Add(new DuplicateGroup(cluster.ToList(), PickKeep(cluster).Id));
    }

    /// <summary>Best copy to keep: lossless first, then depth/rate/bitrate, then largest file.</summary>
    public static Track PickKeep(IEnumerable<Track> tracks) => tracks
        .OrderByDescending(t => t.IsLossless)
        .ThenByDescending(t => t.BitsPerSample)
        .ThenByDescending(t => t.SampleRate)
        .ThenByDescending(t => t.Bitrate)
        .ThenByDescending(t => t.FileSize)
        .First();

    private static string NormalizeKey(Track t)
    {
        var artist = Normalize(t.PrimaryArtist);
        var title = Normalize(t.Title);
        if (artist.Length == 0 || title.Length == 0) return string.Empty;
        return artist + "" + title;
    }

    private static string Normalize(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return string.Empty;
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s.ToLowerInvariant())
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
        return sb.ToString();
    }
}
