using Noctis.Models;

namespace Noctis.Services;

/// <summary>
/// Where an album's animated cover is, for a caller that holds no desktop AnimatedCoverService:
/// the server's getNoctisAnimatedCover (a phone asks for the computer's loop). The same files
/// and the same order the desktop's AnimatedCoverService.Resolve looks in, album first:
/// <list type="number">
/// <item><c>cover.mp4</c> / <c>cover.webm</c> beside the music (the user's own file),</item>
/// <item>the album's saved loop in <c>animated_covers</c>,</item>
/// <item>the loop fetched on opening the album (<c>animated_covers/cache</c>, GitHub #167),</item>
/// <item>a loop saved for one of its songs (the first song given that has one).</item>
/// </list>
/// A song's same-name clip is its music video, never its cover (AnimatedCoverService.Resolve).
/// </summary>
public static class AnimatedCoverLocator
{
    /// <summary>The loop's file types, as the desktop keeps them.</summary>
    public static readonly IReadOnlyList<string> Extensions = new[] { ".mp4", ".webm" };

    /// <summary>The names an album's own loop has beside its music: <c>cover.mp4</c>, <c>cover.webm</c>.</summary>
    public static readonly IReadOnlyList<string> SidecarNames = new[] { "cover.mp4", "cover.webm" };

    /// <summary>The album's loop, or null. <paramref name="albumTracks"/> are its songs (their folders
    /// and song-scoped loops are looked in), the song asked for first when there is one.</summary>
    public static string? ResolveAlbum(IPersistenceService persistence, Guid albumId, IReadOnlyList<Track> albumTracks)
    {
        try
        {
            foreach (var folder in albumTracks.Select(t => SafeFolder(t.FilePath)).Where(f => f is not null).Distinct(StringComparer.Ordinal))
                foreach (var name in SidecarNames)
                {
                    var p = Path.Combine(folder!, name);
                    if (File.Exists(p)) return p;
                }

            foreach (var ext in Extensions)
            {
                var p = persistence.GetAnimatedCoverPath(albumId, null, ext);
                if (File.Exists(p)) return p;
            }

            var cached = persistence.GetCachedAnimatedCoverPath(albumId);
            if (File.Exists(cached)) return cached;

            foreach (var track in albumTracks)
                foreach (var ext in Extensions)
                {
                    var p = persistence.GetAnimatedCoverPath(albumId, track.Id, ext);
                    if (File.Exists(p)) return p;
                }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // An unreadable folder: no loop rather than a failed request.
        }
        return null;
    }

    /// <summary>The loop's media type from its extension (<c>video/mp4</c> or <c>video/webm</c>).</summary>
    public static string ContentTypeFor(string path) =>
        string.Equals(Path.GetExtension(path), ".webm", StringComparison.OrdinalIgnoreCase) ? "video/webm" : "video/mp4";

    private static string? SafeFolder(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return null;
        try { return Path.GetDirectoryName(filePath) is { Length: > 0 } dir ? dir : null; }
        catch (ArgumentException) { return null; }
    }
}
