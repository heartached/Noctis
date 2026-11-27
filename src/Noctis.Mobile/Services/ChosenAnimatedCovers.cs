using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Noctis.Services;

namespace Noctis.Mobile.Services;

/// <summary>What became of a video picked as an album's animated cover.</summary>
public enum ChosenCoverResult
{
    Saved,
    /// <summary>Not an MP4 or WebM file (by its first bytes, not its name).</summary>
    NotVideo,
    /// <summary>Over <see cref="ChosenAnimatedCovers.MaxBytes"/>.</summary>
    TooBig,
    /// <summary>It could not be read or written.</summary>
    Failed,
}

/// <summary>
/// Album ⋯ › Choose Animated Cover (Discord #mobile-beta 11-27, "pick the file for each album ...
/// this would greatly push the offline"): a video picked on the phone, copied into the app's own
/// storage so it plays offline and stays when the original moves, the desktop's Choose Animated
/// Artwork (AnimatedCoverService.ImportAsync copies too). One per album:
/// <c>&lt;albumId&gt;-&lt;stamp&gt;.mp4</c> / <c>.webm</c>, the stamp making each choice a new path,
/// so Now Playing's player never resumes the replaced file and a loop that failed to play does
/// not block the next one chosen. Under NoBackupFilesDir on the phone: a loop is tens of MB, and
/// Android's backup skips an app whose data passes 25 MB. Nothing here throws.
/// </summary>
public sealed class ChosenAnimatedCovers
{
    /// <summary>The largest video kept: the computer's loops are 10-60 MB; this leaves room for a
    /// long 4K one while a wrong pick (a film) is turned away.</summary>
    public const long MaxBytes = 200L * 1024 * 1024;

    private const string PartSuffix = ".part";
    private readonly Func<DateTime> _now;

    private readonly long _maxBytes;

    public ChosenAnimatedCovers(string folder, Func<DateTime>? now = null, long maxBytes = MaxBytes)
    {
        Folder = folder;
        _now = now ?? (() => DateTime.UtcNow);
        _maxBytes = maxBytes;
    }

    public string Folder { get; }

    /// <summary>The album's chosen loop, or null. A file check, cheap enough for the UI thread.</summary>
    public string? Find(Guid albumId)
    {
        if (albumId == Guid.Empty) return null;
        try
        {
            if (!Directory.Exists(Folder)) return null;
            return Directory.EnumerateFiles(Folder, albumId.ToString("N") + "-*")
                .Where(p => AnimatedCoverLocator.Extensions.Contains(Path.GetExtension(p).ToLowerInvariant()))
                .OrderByDescending(p => Path.GetFileName(p), StringComparer.Ordinal)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public bool Has(Guid albumId) => Find(albumId) is not null;

    /// <summary>Copies <paramref name="source"/> in as the album's loop, replacing the one chosen
    /// before. Its type comes from its first bytes (an MP4's <c>ftyp</c> box, WebM's EBML header):
    /// a file manager's name or MIME type can be anything.</summary>
    public async Task<ChosenCoverResult> SaveAsync(Guid albumId, Stream source, CancellationToken ct = default)
    {
        if (albumId == Guid.Empty) return ChosenCoverResult.Failed;
        var key = albumId.ToString("N");
        var part = Path.Combine(Folder, key + PartSuffix);
        try
        {
            Directory.CreateDirectory(Folder);
            var head = new byte[12];
            var got = await ReadAtLeastAsync(source, head, ct).ConfigureAwait(false);
            if (ExtensionFor(head.AsSpan(0, got)) is not { } ext) return ChosenCoverResult.NotVideo;

            long total = got;
            await using (var output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await output.WriteAsync(head.AsMemory(0, got), ct).ConfigureAwait(false);
                var buffer = new byte[81920];
                int read;
                while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > _maxBytes) break;
                    await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                }
            }
            if (total > _maxBytes)
            {
                TryDelete(part);
                return ChosenCoverResult.TooBig;
            }

            var before = Directory.EnumerateFiles(Folder, key + "-*").ToList();
            var path = Path.Combine(Folder, $"{key}-{_now().Ticks:D19}{ext}");
            File.Move(part, path, overwrite: true);
            foreach (var old in before.Where(p => !string.Equals(p, path, StringComparison.Ordinal))) TryDelete(old);
            DebugLog.Write("AnimatedCover", $"chose a loop for an album ({total / 1024} KB)");
            return ChosenCoverResult.Saved;
        }
        catch (OperationCanceledException)
        {
            TryDelete(part);
            return ChosenCoverResult.Failed;
        }
        catch (Exception ex)
        {
            DebugLog.Write("AnimatedCover", $"choosing a loop failed: {ex.GetType().Name}");
            TryDelete(part);
            return ChosenCoverResult.Failed;
        }
    }

    /// <summary>Album ⋯ › Remove Animated Cover: the chosen loop goes (a cover.mp4 beside the
    /// music, or the computer's, then shows again).</summary>
    public void Remove(Guid albumId)
    {
        if (albumId == Guid.Empty) return;
        try
        {
            if (!Directory.Exists(Folder)) return;
            foreach (var p in Directory.EnumerateFiles(Folder, albumId.ToString("N") + "-*").ToList()) TryDelete(p);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DebugLog.Write("AnimatedCover", $"removing a chosen loop failed: {ex.GetType().Name}");
        }
    }

    /// <summary>".mp4" for an ISO media file (bytes 4-7 <c>ftyp</c>), ".webm" for a Matroska/WebM
    /// one (EBML magic 1A 45 DF A3), else null.</summary>
    internal static string? ExtensionFor(ReadOnlySpan<byte> head)
    {
        if (head.Length >= 8 && head[4] == (byte)'f' && head[5] == (byte)'t' && head[6] == (byte)'y' && head[7] == (byte)'p') return ".mp4";
        if (head.Length >= 4 && head[0] == 0x1A && head[1] == 0x45 && head[2] == 0xDF && head[3] == 0xA3) return ".webm";
        return null;
    }

    private static async Task<int> ReadAtLeastAsync(Stream source, byte[] buffer, CancellationToken ct)
    {
        var got = 0;
        while (got < buffer.Length)
        {
            var read = await source.ReadAsync(buffer.AsMemory(got), ct).ConfigureAwait(false);
            if (read == 0) break;
            got += read;
        }
        return got;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
