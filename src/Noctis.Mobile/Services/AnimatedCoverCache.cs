using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Noctis.Services;

namespace Noctis.Mobile.Services;

/// <summary>
/// The computer's animated covers saved on the phone (getNoctisAnimatedCover), one loop per album
/// (<c>&lt;albumId&gt;.mp4</c>) with the version the computer sent (<c>&lt;albumId&gt;.etag</c>, its
/// write time = when the computer was last asked). Under the app's cache folder, so Android's own
/// "Clear cache" (App info › Storage) empties it, and capped: past <see cref="CapBytes"/> the
/// least recently shown loops go first (a loop's write time is its last use, set when it is saved
/// and each time Now Playing shows it), as the desktop's AnimatedArtworkCache does. A loop is
/// tens of MB (a 1080p one ~10-60 MB), so the default 300 MB holds a handful of albums.
/// </summary>
public sealed class AnimatedCoverCache
{
    public const long DefaultCapBytes = 300L * 1024 * 1024;
    private const string LoopSuffix = ".mp4", TagSuffix = ".etag", PartSuffix = ".download";

    private readonly Func<DateTime> _now;
    private readonly object _gate = new();

    public AnimatedCoverCache(string folder, long capBytes = DefaultCapBytes, Func<DateTime>? now = null)
    {
        Folder = folder;
        CapBytes = capBytes;
        _now = now ?? (() => DateTime.UtcNow);
    }

    public string Folder { get; }
    public long CapBytes { get; }

    public string PathFor(Guid albumId) => Path.Combine(Folder, albumId.ToString("N") + LoopSuffix);

    private string TagPathFor(Guid albumId) => Path.Combine(Folder, albumId.ToString("N") + TagSuffix);

    /// <summary>Where a download lands until it is whole; not an .mp4, so nothing plays it half-written.</summary>
    public string PartPathFor(Guid albumId) => PathFor(albumId) + PartSuffix;

    /// <summary>The album's saved loop, marked as just used; null when there is none.</summary>
    public string? Get(Guid albumId)
    {
        var path = PathFor(albumId);
        try
        {
            if (!File.Exists(path)) return null;
            File.SetLastWriteTimeUtc(path, _now());
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return File.Exists(path) ? path : null;
        }
    }

    /// <summary>The version the computer sent with the saved loop, for If-None-Match; null without one.</summary>
    public string? ETagOf(Guid albumId)
    {
        try
        {
            var path = TagPathFor(albumId);
            if (!File.Exists(PathFor(albumId)) || !File.Exists(path)) return null;
            var tag = File.ReadAllText(path).Trim();
            return tag.Length is > 0 and <= 128 ? tag : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>When the computer was last asked about the saved loop (saved, or answered "unchanged").</summary>
    public DateTime? CheckedAt(Guid albumId)
    {
        try
        {
            var path = TagPathFor(albumId);
            return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The computer said the saved loop is still its loop: asked again only after a while.</summary>
    public void MarkChecked(Guid albumId)
    {
        try
        {
            var path = TagPathFor(albumId);
            if (File.Exists(path)) File.SetLastWriteTimeUtc(path, _now());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Moves a finished download in as the album's loop, keeps its version, then trims the
    /// cache to its cap (never the loop just saved). Returns the loop's path.</summary>
    public string Commit(Guid albumId, string partPath, string? etag)
    {
        var path = PathFor(albumId);
        lock (_gate)
        {
            File.Move(partPath, path, overwrite: true);
            File.SetLastWriteTimeUtc(path, _now());
            var tagPath = TagPathFor(albumId);
            if (etag is { Length: > 0 and <= 128 })
            {
                File.WriteAllText(tagPath, etag);
                File.SetLastWriteTimeUtc(tagPath, _now());   // asked just now (CheckedAt)
            }
            else if (File.Exists(tagPath)) File.Delete(tagPath);
        }
        Trim(keep: albumId);
        return path;
    }

    /// <summary>Forgets the album's loop (the computer has none any more).</summary>
    public void Remove(Guid albumId)
    {
        lock (_gate)
        {
            TryDelete(PathFor(albumId));
            TryDelete(TagPathFor(albumId));
        }
    }

    /// <summary>Bytes the saved loops take up.</summary>
    public long SizeBytes()
    {
        try { return Loops().Sum(f => f.Length); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }

    /// <summary>
    /// Deletes the least recently used loops (and their versions) until the cache fits in
    /// <see cref="CapBytes"/>, leaving <paramref name="keep"/> alone. A loop that is playing right
    /// now cannot be deleted on every system; it is skipped and the next one goes instead. Leftover
    /// partial downloads go too. Returns how many loops were deleted.
    /// </summary>
    public int Trim(Guid? keep = null)
    {
        lock (_gate)
        {
            List<FileInfo> loops;
            try
            {
                var dir = new DirectoryInfo(Folder);
                if (!dir.Exists) return 0;
                foreach (var part in dir.EnumerateFiles("*" + PartSuffix).Where(f => f.LastWriteTimeUtc < _now().AddHours(-1)))
                    TryDelete(part.FullName);
                loops = Loops().OrderBy(f => f.LastWriteTimeUtc).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return 0;
            }
            var total = loops.Sum(f => f.Length);
            var deleted = 0;
            var keepName = keep?.ToString("N") + LoopSuffix;
            foreach (var loop in loops)
            {
                if (total <= CapBytes) break;
                if (string.Equals(loop.Name, keepName, StringComparison.OrdinalIgnoreCase)) continue;
                // The length first: a deleted FileInfo re-reads it, and a gone file throws.
                var length = loop.Length;
                try
                {
                    loop.Delete();
                    TryDelete(Path.ChangeExtension(loop.FullName, TagSuffix));
                    total -= length;
                    deleted++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            if (deleted > 0) DebugLog.Write("AnimatedCover", $"cache trimmed: {deleted} loop(s) removed, {total / (1024 * 1024)} MB kept");
            return deleted;
        }
    }

    private IEnumerable<FileInfo> Loops()
    {
        var dir = new DirectoryInfo(Folder);
        return dir.Exists ? dir.EnumerateFiles("*" + LoopSuffix) : Enumerable.Empty<FileInfo>();
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
