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
        await using var src = File.OpenRead(sourcePath);
        await using var dstStream = File.Create(dst);
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
