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
        await using var src = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var dstStream = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
        await src.CopyToAsync(dstStream);
        return dst;
    }

    public void Remove(Track track, AnimatedCoverScope scope)
    {
        var trackId = scope == AnimatedCoverScope.Track ? (Guid?)track.Id : null;
        foreach (var ext in SupportedExtensions)
        {
            var p = _persistence.GetAnimatedCoverPath(track.AlbumId, trackId, ext);
            if (File.Exists(p))
            {
                try { File.Delete(p); } catch { }
            }
        }
    }

    private static string NormalizeExtension(string ext)
    {
        ext = (ext ?? string.Empty).ToLowerInvariant();
        return SupportedExtensions.Contains(ext) ? ext : ".mp4";
    }
}
