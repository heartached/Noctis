using System.Text;
using Noctis.Models;

namespace Noctis.Services;

/// <summary>What the organizer will do with a given file.</summary>
public enum OrganizeAction
{
    /// <summary>Move the file to a freshly computed location.</summary>
    Move,

    /// <summary>File is already at its target location — nothing to do.</summary>
    Skip,

    /// <summary>Target was occupied, so a numeric suffix was appended.</summary>
    Conflict
}

/// <summary>A single planned move from <see cref="SourcePath"/> to <see cref="TargetPath"/>.</summary>
public sealed record OrganizeMove(
    Guid TrackId,
    string SourcePath,
    string TargetPath,
    OrganizeAction Action,
    string? Note = null);

/// <summary>
/// Pure planning for auto-organize: turns a set of tracks plus a path template into a
/// deterministic list of file moves, resolving collisions both within the batch and
/// against existing files. No file I/O — on-disk existence is supplied via a predicate
/// so the planner stays unit-testable.
///
/// The template uses <c>/</c> as the folder separator and supports the tokens
/// <c>{AlbumArtist} {Artist} {Album} {Title} {TrackNo} {DiscNo} {Year} {Genre}</c>.
/// The original file extension is always preserved (the template never specifies it).
/// </summary>
public static class FileOrganizePlanner
{
    public const string DefaultPattern = "{AlbumArtist}/{Album}/{TrackNo} {Title}";

    public static IReadOnlyList<OrganizeMove> Plan(
        IEnumerable<Track> tracks,
        string pattern,
        string targetRoot,
        Func<string, bool> targetExists)
    {
        if (string.IsNullOrWhiteSpace(pattern)) pattern = DefaultPattern;
        targetRoot = (targetRoot ?? string.Empty).Trim();

        var result = new List<OrganizeMove>();
        // Reserve resolved targets (case-insensitive) so two tracks can't collide.
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var track in tracks)
        {
            if (track is null || string.IsNullOrWhiteSpace(track.FilePath)) continue;

            var ext = Path.GetExtension(track.FilePath);
            var relativeNoExt = BuildRelativePath(track, pattern);
            var source = Path.GetFullPath(track.FilePath);
            var baseTarget = Path.GetFullPath(Path.Combine(targetRoot, relativeNoExt) + ext);

            // Already where it should be.
            if (PathEquals(baseTarget, source))
            {
                result.Add(new OrganizeMove(track.Id, source, source, OrganizeAction.Skip, "Already organized"));
                continue;
            }

            // Resolve collisions against both already-planned targets and existing files.
            var finalTarget = baseTarget;
            var collided = false;
            var n = 2;
            while (reserved.Contains(finalTarget) || (!PathEquals(finalTarget, source) && targetExists(finalTarget)))
            {
                collided = true;
                var dir = Path.GetDirectoryName(baseTarget) ?? targetRoot;
                var name = Path.GetFileNameWithoutExtension(baseTarget);
                finalTarget = Path.Combine(dir, $"{name} ({n}){ext}");
                n++;
            }

            reserved.Add(finalTarget);
            result.Add(new OrganizeMove(
                track.Id, source, finalTarget,
                collided ? OrganizeAction.Conflict : OrganizeAction.Move,
                collided ? "Renamed to avoid a collision" : null));
        }

        return result;
    }

    private static bool PathEquals(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string BuildRelativePath(Track track, string pattern)
    {
        // Each '/'-delimited segment becomes a path component and is sanitized
        // independently, so a token value containing a separator (e.g. "AC/DC")
        // can't escape its folder.
        var segments = pattern.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var parts = new List<string>(segments.Length);
        foreach (var seg in segments)
        {
            var safe = Sanitize(SubstituteTokens(seg, track));
            parts.Add(safe.Length == 0 ? "Unknown" : safe);
        }
        return parts.Count == 0 ? "Unknown" : Path.Combine(parts.ToArray());
    }

    private static string SubstituteTokens(string segment, Track track) => segment
        .Replace("{AlbumArtist}", Nz(track.AlbumArtist, "Unknown Artist"))
        .Replace("{Artist}", Nz(track.Artist, "Unknown Artist"))
        .Replace("{Album}", Nz(track.Album, "Unknown Album"))
        .Replace("{Title}", Nz(track.Title, "Untitled"))
        .Replace("{TrackNo}", track.TrackNumber > 0 ? track.TrackNumber.ToString("00") : "00")
        .Replace("{DiscNo}", track.DiscNumber > 0 ? track.DiscNumber.ToString() : "1")
        .Replace("{Year}", track.Year > 0 ? track.Year.ToString() : string.Empty)
        .Replace("{Genre}", track.Genre ?? string.Empty);

    private static string Nz(string? v, string fallback) => string.IsNullOrWhiteSpace(v) ? fallback : v.Trim();

    private static readonly char[] InvalidChars = BuildInvalidChars();

    private static char[] BuildInvalidChars()
    {
        var set = new HashSet<char>(Path.GetInvalidFileNameChars());
        // Be safe regardless of host OS (Linux only bans '/').
        foreach (var c in "<>:\"/\\|?*") set.Add(c);
        return set.ToArray();
    }

    private static string Sanitize(string segment)
    {
        var sb = new StringBuilder(segment.Length);
        foreach (var ch in segment)
            sb.Append(ch < 32 || Array.IndexOf(InvalidChars, ch) >= 0 ? '_' : ch);
        // Windows: a path component may not end with a space or dot.
        return sb.ToString().Trim().TrimEnd('.', ' ').Trim();
    }
}
