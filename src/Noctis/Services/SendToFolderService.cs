using Noctis.Models;

namespace Noctis.Services;

public enum SendToFolderAction
{
    /// <summary>Copy to a fresh target path.</summary>
    Copy,

    /// <summary>A file of the same name and size is already there — nothing to do.</summary>
    SkipIdentical,

    /// <summary>Target name was taken by a different file; a numeric suffix was added.</summary>
    Renamed,
}

/// <summary>One planned copy. <see cref="SidecarSource"/>/<see cref="SidecarTarget"/> carry the .lrc next to the track when requested.</summary>
public sealed record SendToFolderItem(
    Track Track,
    string SourcePath,
    string TargetPath,
    SendToFolderAction Action,
    string? SidecarSource,
    string? SidecarTarget);

public sealed record SendToFolderProgress(int Done, int Total, string CurrentFile);

public sealed record SendToFolderResult(int Copied, int Skipped, int Failed, IReadOnlyList<string> Errors, bool Cancelled);

/// <summary>Probe of an on-disk path used by the planner (null = does not exist).</summary>
public readonly record struct FileProbe(long Length);

/// <summary>
/// Pure planning for "Send to Folder" (MusicBee's Send To → Folder (Copy)): which files go
/// where, flat or organised by the user's pattern, with identical files skipped and name
/// clashes suffixed. No I/O — existence is answered by a probe so it is unit-testable.
/// </summary>
public static class SendToFolderPlanner
{
    public static IReadOnlyList<SendToFolderItem> Plan(
        IEnumerable<Track> tracks,
        string targetRoot,
        string? organizePattern,
        bool includeLyrics,
        Func<string, FileProbe?> probe)
    {
        targetRoot = (targetRoot ?? string.Empty).Trim();
        var result = new List<SendToFolderItem>();
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenSources = new HashSet<string>(Helpers.PathComparison.Comparer);

        var list = tracks.Where(t => t is not null && !string.IsNullOrWhiteSpace(t.FilePath)).ToList();
        if (list.Count == 0 || string.IsNullOrWhiteSpace(targetRoot)) return result;

        // Organised layout borrows the auto-organizer's template engine so both features
        // agree on what "{AlbumArtist}/{Album}/{TrackNo} {Title}" means.
        Dictionary<Guid, string>? organized = null;
        if (!string.IsNullOrWhiteSpace(organizePattern))
        {
            organized = new Dictionary<Guid, string>();
            foreach (var move in FileOrganizePlanner.Plan(list, organizePattern, targetRoot, _ => false))
                organized[move.TrackId] = move.TargetPath;
        }

        foreach (var track in list)
        {
            var source = Path.GetFullPath(track.FilePath);
            if (!seenSources.Add(source)) continue;

            var baseTarget = organized is not null && organized.TryGetValue(track.Id, out var organizedPath)
                ? organizedPath
                : Path.GetFullPath(Path.Combine(targetRoot, Path.GetFileName(source)));

            var action = SendToFolderAction.Copy;
            var target = baseTarget;
            var existing = probe(target);
            var sourceProbe = probe(source);
            if (existing is { } e && sourceProbe is { } s && e.Length == s.Length && !reserved.Contains(target))
            {
                action = SendToFolderAction.SkipIdentical;
            }
            else
            {
                var n = 2;
                while (reserved.Contains(target) || probe(target) is not null)
                {
                    action = SendToFolderAction.Renamed;
                    var dir = Path.GetDirectoryName(baseTarget) ?? targetRoot;
                    var name = Path.GetFileNameWithoutExtension(baseTarget);
                    target = Path.Combine(dir, $"{name} ({n}){Path.GetExtension(baseTarget)}");
                    n++;
                }
            }
            reserved.Add(target);

            string? sidecarSource = null, sidecarTarget = null;
            if (includeLyrics)
            {
                var lrc = Path.ChangeExtension(source, ".lrc");
                if (probe(lrc) is not null)
                {
                    sidecarSource = lrc;
                    sidecarTarget = Path.ChangeExtension(target, ".lrc");
                }
            }

            result.Add(new SendToFolderItem(track, source, target, action, sidecarSource, sidecarTarget));
        }
        return result;
    }

    /// <summary>Default probe: real file system.</summary>
    public static FileProbe? DiskProbe(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? new FileProbe(info.Length) : null;
        }
        catch { return null; }
    }
}

public interface ISendToFolderService
{
    IReadOnlyList<SendToFolderItem> Plan(IEnumerable<Track> tracks, string targetRoot, string? organizePattern, bool includeLyrics);
    Task<SendToFolderResult> CopyAsync(IReadOnlyList<SendToFolderItem> plan, IProgress<SendToFolderProgress>? progress, CancellationToken ct);
}

public sealed class SendToFolderService : ISendToFolderService
{
    public IReadOnlyList<SendToFolderItem> Plan(IEnumerable<Track> tracks, string targetRoot, string? organizePattern, bool includeLyrics)
        => SendToFolderPlanner.Plan(tracks, targetRoot, organizePattern, includeLyrics, SendToFolderPlanner.DiskProbe);

    public Task<SendToFolderResult> CopyAsync(IReadOnlyList<SendToFolderItem> plan, IProgress<SendToFolderProgress>? progress, CancellationToken ct)
        => Task.Run(() =>
        {
            int copied = 0, skipped = 0, failed = 0;
            var errors = new List<string>();
            var total = plan.Count;
            for (var i = 0; i < total; i++)
            {
                if (ct.IsCancellationRequested)
                    return new SendToFolderResult(copied, skipped, failed, errors, Cancelled: true);

                var item = plan[i];
                progress?.Report(new SendToFolderProgress(i, total, Path.GetFileName(item.SourcePath)));
                if (item.Action == SendToFolderAction.SkipIdentical)
                {
                    skipped++;
                    continue;
                }
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(item.TargetPath)!);
                    File.Copy(item.SourcePath, item.TargetPath, overwrite: false);
                    if (item.SidecarSource is not null && item.SidecarTarget is not null && !File.Exists(item.SidecarTarget))
                    {
                        try { File.Copy(item.SidecarSource, item.SidecarTarget, overwrite: false); } catch { /* lyrics are a bonus */ }
                    }
                    copied++;
                }
                catch (Exception ex)
                {
                    failed++;
                    errors.Add($"{Path.GetFileName(item.SourcePath)}: {ex.Message}");
                }
            }
            progress?.Report(new SendToFolderProgress(total, total, string.Empty));
            return new SendToFolderResult(copied, skipped, failed, errors, Cancelled: false);
        }, CancellationToken.None);
}
