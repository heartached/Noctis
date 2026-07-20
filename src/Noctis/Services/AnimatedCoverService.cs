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

        foreach (var ext in SupportedExtensions)
        {
            var p = Path.ChangeExtension(track.FilePath, ext);
            if (File.Exists(p)) return p;
        }

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

        return null;
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
