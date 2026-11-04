using Noctis.Models;

namespace Noctis.Services;

public class AnimatedCoverService : IAnimatedCoverService
{
    private static readonly string[] SupportedExtensions = { ".mp4", ".webm" };

    private readonly IPersistenceService _persistence;

    public AnimatedCoverService(IPersistenceService persistence)
    {
        _persistence = persistence;
    }

    public string? Resolve(Track track)
    {
        if (string.IsNullOrWhiteSpace(track.FilePath))
            return null;

        // A clip with the song's own name next to it is its MUSIC VIDEO
        // (Helpers.MusicVideoLocator, 09-15), not an animated cover: resolving it here
        // too made the lyrics page show the clip cropped to the square cover slot the
        // moment "Music videos" was switched off, and every album tile play it (Discord,
        // aaron 2026-09-21). Animated covers are cover.mp4/.webm beside the album or the
        // app's own animated_covers cache.
        var folder = Path.GetDirectoryName(track.FilePath);
        if (!string.IsNullOrEmpty(folder))
        {
            foreach (var ext in SupportedExtensions)
            {
                var p = Path.Combine(folder, "cover" + ext);
                if (File.Exists(p)) return p;
            }
        }

        foreach (var ext in SupportedExtensions)
        {
            var p = _persistence.GetAnimatedCoverPath(track.AlbumId, track.Id, ext);
            if (File.Exists(p)) return p;
        }

        foreach (var ext in SupportedExtensions)
        {
            var p = _persistence.GetAnimatedCoverPath(track.AlbumId, null, ext);
            if (File.Exists(p)) return p;
        }

        // Last: a loop fetched on opening the album (GitHub #167). Anything the user saved wins.
        var cached = _persistence.GetCachedAnimatedCoverPath(track.AlbumId);
        return File.Exists(cached) ? cached : null;
    }

    public bool HasStoredCover(IReadOnlyList<Track> albumTracks)
    {
        if (albumTracks.Count == 0) return false;
        var albumId = albumTracks[0].AlbumId;
        if (File.Exists(_persistence.GetCachedAnimatedCoverPath(albumId))) return true;
        foreach (var ext in SupportedExtensions)
        {
            if (File.Exists(_persistence.GetAnimatedCoverPath(albumId, null, ext))) return true;
            foreach (var t in albumTracks)
                if (File.Exists(_persistence.GetAnimatedCoverPath(albumId, t.Id, ext))) return true;
        }
        return false;
    }

    public async Task<string> ImportAsync(Track track, string sourcePath, AnimatedCoverScope scope)
    {
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("Source not found", sourcePath);

        _persistence.EnsureAnimatedCoverDir();

        var ext = NormalizeExtension(Path.GetExtension(sourcePath));
        var trackId = scope == AnimatedCoverScope.Track ? (Guid?)track.Id : null;

        foreach (var other in SupportedExtensions)
        {
            var p = _persistence.GetAnimatedCoverPath(track.AlbumId, trackId, other);
            if (File.Exists(p)) try { File.Delete(p); } catch { }
        }
        // A saved album cover replaces a fetched one: the cached copy would only take up room.
        if (trackId is null)
        {
            var cached = _persistence.GetCachedAnimatedCoverPath(track.AlbumId);
            if (File.Exists(cached)) try { File.Delete(cached); } catch { }
        }

        var dst = _persistence.GetAnimatedCoverPath(track.AlbumId, trackId, ext);
        // Open both handles for true async I/O (FileOptions.Asynchronous). File.OpenRead /
        // File.Create return synchronous handles, so CopyToAsync would block the calling
        // (UI) thread for the whole copy — a multi-MB animated cover froze the window for
        // 10s+. Overlapped I/O lets the copy run without holding the thread.
        // Retry on sharing violations: replacing the cover that is currently playing
        // races the asynchronous release of the old file's LibVLC session.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await using var src = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await using var dstStream = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
                await src.CopyToAsync(dstStream);
                return dst;
            }
            catch (IOException) when (attempt < 5)
            {
                await Task.Delay(300);
            }
        }
    }

    public async Task RemoveAsync(Track track, AnimatedCoverScope scope)
    {
        // Delete BOTH scopes: Resolve falls back track → album, so a track-scoped
        // dialog removing only its own file would leave an album-scoped cover
        // showing (and vice versa). Sidecar files next to the music are untouched.
        var candidates = new List<string>();
        foreach (var ext in SupportedExtensions)
        {
            candidates.Add(_persistence.GetAnimatedCoverPath(track.AlbumId, track.Id, ext));
            candidates.Add(_persistence.GetAnimatedCoverPath(track.AlbumId, null, ext));
        }
        // And the fetched one: Resolve falls back to it too, so leaving it kept the cover showing.
        candidates.Add(_persistence.GetCachedAnimatedCoverPath(track.AlbumId));

        // The visible cover is held open by a LibVLC session; the UI releases it just
        // before this runs, but the session teardown is asynchronous — retry briefly
        // instead of letting a sharing violation silently keep the file.
        for (var attempt = 0; attempt < 6; attempt++)
        {
            foreach (var p in candidates)
            {
                try { if (File.Exists(p)) File.Delete(p); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }

            if (!candidates.Any(File.Exists))
                return;

            await Task.Delay(300);
        }
    }

    private static string NormalizeExtension(string ext)
    {
        ext = (ext ?? string.Empty).ToLowerInvariant();
        return SupportedExtensions.Contains(ext) ? ext : ".mp4";
    }
}
