using System.Buffers;
using Noctis.Models;

namespace Noctis.Services;

public enum SendToFolderAction
{
    /// <summary>Copy to a fresh target path.</summary>
    Copy,

    /// <summary>A file of the same name and size is already there — nothing to do (its
    /// missing lyrics files still go over).</summary>
    SkipIdentical,

    /// <summary>Target name was taken by a different file; a numeric suffix was added.</summary>
    Renamed,
}

/// <summary>One lyrics file going along with its song.</summary>
public sealed record SendToFolderSidecar(string Source, string Target);

/// <summary>
/// One planned copy. <see cref="SidecarSource"/>/<see cref="SidecarTarget"/> are the first of
/// <see cref="Sidecars"/> (every lyrics file next to the track, when requested).
/// </summary>
public sealed record SendToFolderItem(
    Track Track,
    string SourcePath,
    string TargetPath,
    SendToFolderAction Action,
    string? SidecarSource,
    string? SidecarTarget)
{
    /// <summary>Every lyrics file to copy with this song (.lrc, .elrc, .ttml, .lyricsfile).</summary>
    public IReadOnlyList<SendToFolderSidecar> Sidecars { get; init; } =
        SidecarSource is not null && SidecarTarget is not null
            ? new[] { new SendToFolderSidecar(SidecarSource, SidecarTarget) }
            : Array.Empty<SendToFolderSidecar>();
}

/// <summary>What happened to one planned song.</summary>
public enum SendToFolderOutcome
{
    /// <summary>Being copied now.</summary>
    Working,
    Copied,
    /// <summary>Already there (its missing lyrics may have been added).</summary>
    Skipped,
    Failed,
}

/// <summary>
/// Run progress. <see cref="Done"/> songs of <see cref="Total"/> are finished; <see cref="Index"/>
/// is the plan entry this report is about (-1 for the closing report) and <see cref="Outcome"/>
/// its state, with <see cref="Error"/> for a failure.
/// </summary>
public sealed record SendToFolderProgress(
    int Done,
    int Total,
    string CurrentFile,
    int Index = -1,
    SendToFolderOutcome Outcome = SendToFolderOutcome.Working,
    string? Error = null);

/// <summary>A run's result. In a move, <see cref="Copied"/> counts the songs moved.</summary>
public sealed record SendToFolderResult(
    int Copied,
    int Skipped,
    int Failed,
    IReadOnlyList<string> Errors,
    bool Cancelled,
    int LyricsCopied = 0)
{
    /// <summary>Move (GitHub #121): old track id → new id of every moved song (ids derive
    /// from the path). The caller points the live playlists and the play log at them.</summary>
    public IReadOnlyDictionary<Guid, Guid> TrackIdRemap { get; init; } = new Dictionary<Guid, Guid>();
}

/// <summary>Probe of an on-disk path used by the planner (null = does not exist).</summary>
public readonly record struct FileProbe(long Length);

/// <summary>
/// Pure planning for "Send to Folder" (MusicBee's Send To → Folder (Copy)): which files go
/// where, flat or organised by the user's pattern, with identical files skipped and name
/// clashes suffixed. No I/O — existence is answered by a probe so it is unit-testable.
/// </summary>
public static class SendToFolderPlanner
{
    /// <summary>
    /// The lyrics sidecars the app reads by the song's basename (LyricsViewModel's local probe:
    /// .lyricsfile, .ttml, .elrc, .lrc), each with the case spellings it accepts. Only .lrc
    /// went along before, so word-synced lyrics stayed behind.
    /// </summary>
    internal static readonly string[][] LyricsExtensions =
    {
        new[] { ".lrc", ".LRC", ".Lrc" },
        new[] { ".elrc", ".ELRC", ".Elrc" },
        new[] { ".ttml", ".TTML", ".Ttml" },
        new[] { ".lyricsfile", ".LYRICSFILE", ".Lyricsfile" },
    };

    /// <summary>
    /// Longest file or folder name written. NTFS, exFAT and FAT32 all stop at 255 UTF-16
    /// units per name; the organizer pattern turns a long tag (a 300-character title) into a
    /// name that could never be created. 200 leaves room for " (12)" and the extension.
    /// </summary>
    internal const int MaxNameLength = 200;

    /// <param name="move">GitHub #121 (2026-10-10): plan a move. A song already at its target
    /// is left alone; a same-size file at the target is never taken for the song (its source
    /// would be deleted on a size match alone), the move gets a numbered name instead.</param>
    public static IReadOnlyList<SendToFolderItem> Plan(
        IEnumerable<Track> tracks,
        string targetRoot,
        string? organizePattern,
        bool includeLyrics,
        Func<string, FileProbe?> probe,
        bool move = false)
    {
        targetRoot = (targetRoot ?? string.Empty).Trim();
        var result = new List<SendToFolderItem>();
        if (string.IsNullOrWhiteSpace(targetRoot)) return result;
        targetRoot = Path.GetFullPath(targetRoot);

        // One entry per file, first come first served. The same track twice (a playlist that
        // holds it twice) went to the organizer twice, whose collision check gave the second
        // "01 Song (2).flac" — and that path, stored under the shared track Id, overwrote the
        // first, so the only copy landed with a "(2)".
        var seenSources = new HashSet<string>(Helpers.PathComparison.Comparer);
        var list = new List<Track>();
        foreach (var t in tracks)
        {
            if (t is null || string.IsNullOrWhiteSpace(t.FilePath)) continue;
            string full;
            try { full = Path.GetFullPath(t.FilePath); }
            catch (Exception) { continue; }
            if (seenSources.Add(full)) list.Add(t);
        }
        if (list.Count == 0) return result;

        // Organised layout borrows the auto-organizer's template engine so both features
        // agree on what "{AlbumArtist}/{Album}/{TrackNo} {Title}" means.
        Dictionary<string, string>? organized = null;
        if (!string.IsNullOrWhiteSpace(organizePattern))
        {
            organized = new Dictionary<string, string>(Helpers.PathComparison.Comparer);
            foreach (var organizeMove in FileOrganizePlanner.Plan(list, organizePattern, targetRoot, _ => false))
                organized[organizeMove.SourcePath] = organizeMove.TargetPath;
        }

        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Basenames (folder + name without extension) taken in this run, and those that carry
        // lyrics: the player pairs lyrics with a song by basename, so "song.flac" and
        // "song.mp3" side by side would share one "song.lrc".
        var reservedStems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lyricStems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var track in list)
        {
            var source = Path.GetFullPath(track.FilePath);
            var baseTarget = organized is not null && organized.TryGetValue(source, out var organizedPath)
                ? ClampNames(targetRoot, organizedPath)
                : Path.GetFullPath(Path.Combine(targetRoot, SafeFileName(Path.GetFileName(source))));

            // A move to where the song already is: nothing to do (and its lyrics stay put).
            if (move && Helpers.PathComparison.Comparer.Equals(source, baseTarget) && !reserved.Contains(source))
            {
                reserved.Add(source);
                reservedStems.Add(Stem(source));
                result.Add(new SendToFolderItem(track, source, source, SendToFolderAction.SkipIdentical, null, null));
                continue;
            }

            var lyrics = includeLyrics ? FindLyrics(source, probe) : new List<string>();
            var hasLyrics = lyrics.Count > 0;

            var action = SendToFolderAction.Copy;
            var target = baseTarget;
            var existing = probe(target);
            var sourceProbe = probe(source);
            if (!move && existing is { } e && sourceProbe is { } s && e.Length == s.Length
                && !reserved.Contains(target) && !lyricStems.Contains(Stem(target)))
            {
                action = SendToFolderAction.SkipIdentical;
            }
            else
            {
                var n = 2;
                while (reserved.Contains(target) || probe(target) is not null
                       || LyricsClash(target, hasLyrics, reservedStems, lyricStems, probe))
                {
                    action = SendToFolderAction.Renamed;
                    var dir = Path.GetDirectoryName(baseTarget) ?? targetRoot;
                    var name = Path.GetFileNameWithoutExtension(baseTarget);
                    target = Path.Combine(dir, $"{name} ({n}){Path.GetExtension(baseTarget)}");
                    n++;
                }
            }
            reserved.Add(target);
            reservedStems.Add(Stem(target));
            if (hasLyrics) lyricStems.Add(Stem(target));

            var sidecars = new List<SendToFolderSidecar>(lyrics.Count);
            foreach (var lyric in lyrics)
            {
                var sidecarTarget = Stem(target) + Path.GetExtension(lyric);
                // An already-copied song only gets the lyrics it is missing; never overwrite.
                if (action == SendToFolderAction.SkipIdentical && probe(sidecarTarget) is not null) continue;
                sidecars.Add(new SendToFolderSidecar(lyric, sidecarTarget));
            }

            result.Add(new SendToFolderItem(track, source, target, action,
                sidecars.FirstOrDefault()?.Source, sidecars.FirstOrDefault()?.Target)
            {
                Sidecars = sidecars,
            });
        }
        return result;
    }

    /// <summary>The track's lyrics files on disk, one per format (first case spelling found).</summary>
    private static List<string> FindLyrics(string source, Func<string, FileProbe?> probe)
    {
        var found = new List<string>();
        var stem = Stem(source);
        foreach (var spellings in LyricsExtensions)
        {
            foreach (var ext in spellings)
            {
                var candidate = stem + ext;
                if (probe(candidate) is null) continue;
                found.Add(candidate);
                break;
            }
        }
        return found;
    }

    /// <summary>
    /// True when this basename would hand lyrics to the wrong song: another song of this run
    /// brings lyrics under it, or this song brings lyrics and the basename is already taken
    /// (by a song of this run or by a lyrics file already in the folder).
    /// </summary>
    private static bool LyricsClash(string target, bool hasLyrics, HashSet<string> reservedStems,
        HashSet<string> lyricStems, Func<string, FileProbe?> probe)
    {
        var stem = Stem(target);
        if (lyricStems.Contains(stem)) return true;
        if (!hasLyrics) return false;
        if (reservedStems.Contains(stem)) return true;
        foreach (var spellings in LyricsExtensions)
            if (probe(stem + spellings[0]) is not null) return true;
        return false;
    }

    private static string Stem(string path)
        => Path.Combine(Path.GetDirectoryName(path) ?? string.Empty, Path.GetFileNameWithoutExtension(path));

    /// <summary>Shortens every name under <paramref name="root"/> to <see cref="MaxNameLength"/>
    /// (the file name keeps its extension).</summary>
    internal static string ClampNames(string root, string fullPath)
    {
        var relative = Path.GetRelativePath(root, fullPath);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative)) return fullPath;
        var parts = relative.Split(Path.DirectorySeparatorChar);
        var changed = false;
        for (var i = 0; i < parts.Length; i++)
        {
            var isFile = i == parts.Length - 1;
            var ext = isFile ? Path.GetExtension(parts[i]) : string.Empty;
            var name = isFile ? Path.GetFileNameWithoutExtension(parts[i]) : parts[i];
            var max = MaxNameLength - ext.Length;
            if (name.Length <= max) continue;
            var cut = max;
            if (char.IsHighSurrogate(name[cut - 1])) cut--; // never split a surrogate pair
            // Windows: a name may not end with a space or dot.
            parts[i] = name[..cut].TrimEnd(' ', '.') + ext;
            changed = true;
        }
        return changed ? Path.Combine(root, Path.Combine(parts)) : fullPath;
    }

    // Characters FAT32/exFAT (USB sticks, SD cards) and NTFS refuse. A Windows source can't
    // hold them; a Linux or macOS one can ("Intro: Live?.flac").
    private static readonly char[] InvalidNameChars = "<>:\"/\\|?*".ToCharArray();

    private static string SafeFileName(string name)
    {
        if (name.IndexOfAny(InvalidNameChars) < 0) return name;
        var chars = name.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
            if (Array.IndexOf(InvalidNameChars, chars[i]) >= 0) chars[i] = '_';
        return new string(chars);
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

    /// <summary>GitHub #121: the plan for a copy, or for a move (see <see cref="SendToFolderPlanner.Plan"/>).</summary>
    IReadOnlyList<SendToFolderItem> Plan(IEnumerable<Track> tracks, string targetRoot, string? organizePattern, bool includeLyrics, bool move)
        => move ? throw new NotSupportedException() : Plan(tracks, targetRoot, organizePattern, includeLyrics);

    /// <summary>GitHub #121: moves the planned songs (and their lyrics files) and points their
    /// library tracks at the new paths. Only the songs that moved are relocated.</summary>
    Task<SendToFolderResult> MoveAsync(IReadOnlyList<SendToFolderItem> plan, IProgress<SendToFolderProgress>? progress, CancellationToken ct)
        => throw new NotSupportedException();
}

public sealed class SendToFolderService : ISendToFolderService
{
    private const int BufferSize = 1 << 20;

    /// <summary>How long the watcher ignores a path a move is about to touch (Organize Files' window).</summary>
    private static readonly TimeSpan SuppressionWindow = TimeSpan.FromSeconds(30);

    private readonly ILibraryService? _library;
    private readonly ILibraryWatcherService? _watcher;

    public SendToFolderService() { }

    /// <summary>With the library, a move relocates its tracks (GitHub #121).</summary>
    public SendToFolderService(ILibraryService library, ILibraryWatcherService? watcher = null)
    {
        _library = library;
        _watcher = watcher;
    }

    public IReadOnlyList<SendToFolderItem> Plan(IEnumerable<Track> tracks, string targetRoot, string? organizePattern, bool includeLyrics)
        => SendToFolderPlanner.Plan(tracks, targetRoot, organizePattern, includeLyrics, SendToFolderPlanner.DiskProbe);

    public IReadOnlyList<SendToFolderItem> Plan(IEnumerable<Track> tracks, string targetRoot, string? organizePattern, bool includeLyrics, bool move)
        => SendToFolderPlanner.Plan(tracks, targetRoot, organizePattern, includeLyrics, SendToFolderPlanner.DiskProbe, move);

    public Task<SendToFolderResult> CopyAsync(IReadOnlyList<SendToFolderItem> plan, IProgress<SendToFolderProgress>? progress, CancellationToken ct)
        => Task.Run(() => RunAsync(plan, progress, ct, null), CancellationToken.None);

    public Task<SendToFolderResult> MoveAsync(IReadOnlyList<SendToFolderItem> plan, IProgress<SendToFolderProgress>? progress, CancellationToken ct)
        => Task.Run(() => MoveRunAsync(plan, progress, ct, null), CancellationToken.None);

    /// <summary>
    /// A move: each song is moved on its own (never deleted before its new copy is whole), then
    /// the library tracks of the songs that moved — and only those — get their new paths, on
    /// every way out (done, stopped, drive gone), keeping favorites, plays, ratings and lyrics.
    /// </summary>
    internal async Task<SendToFolderResult> MoveRunAsync(IReadOnlyList<SendToFolderItem> plan,
        IProgress<SendToFolderProgress>? progress, CancellationToken ct, Action<string>? afterChunk)
    {
        var moved = new List<(string From, string To)>();
        var result = await RunAsync(plan, progress, ct, afterChunk, moved, SuppressForMove);
        if (moved.Count == 0 || _library is null) return result;
        try
        {
            var remap = await _library.RelocateTracksAsync(moved, CancellationToken.None);
            return result with { TrackIdRemap = remap };
        }
        catch (Exception ex)
        {
            // The files did move; say so rather than lose the error.
            DebugLog.Write("SendToFolder", $"Relocate after move failed: {ex.Message}");
            var errors = result.Errors.ToList();
            errors.Add(ex.Message);
            return result with { Errors = errors };
        }
    }

    /// <summary>Registers a move (and its lyrics files) with the watcher's ignore list, so the
    /// source vanishing isn't recorded as a deletion before the library relocates the track.</summary>
    private void SuppressForMove(SendToFolderItem item)
    {
        if (_watcher is null) return;
        var paths = new List<string> { item.SourcePath, item.TargetPath };
        foreach (var sidecar in item.Sidecars)
        {
            paths.Add(sidecar.Source);
            paths.Add(sidecar.Target);
        }
        _watcher.SuppressPaths(paths, SuppressionWindow);
    }

    /// <summary>The run. <paramref name="afterChunk"/> is a test hook called after each
    /// block written (to cancel or pull the drive mid-file). With <paramref name="moved"/> it
    /// moves instead of copying and records each (track path, new path) that moved.</summary>
    internal static async Task<SendToFolderResult> RunAsync(IReadOnlyList<SendToFolderItem> plan,
        IProgress<SendToFolderProgress>? progress, CancellationToken ct, Action<string>? afterChunk,
        List<(string From, string To)>? moved = null, Action<SendToFolderItem>? beforeMove = null)
    {
        var move = moved is not null;
        int copied = 0, skipped = 0, failed = 0, lyricsCopied = 0;
        var errors = new List<string>();
        var total = plan.Count;
        for (var i = 0; i < total; i++)
        {
            if (ct.IsCancellationRequested)
                return new SendToFolderResult(copied, skipped, failed, errors, Cancelled: true, lyricsCopied);

            var item = plan[i];
            var name = Path.GetFileName(item.SourcePath);
            progress?.Report(new SendToFolderProgress(i, total, name, i));
            if (item.Action == SendToFolderAction.SkipIdentical)
            {
                // Already there: only the lyrics it lacks go over (they used to be dropped,
                // so turning "Include lyrics" on for a second run added nothing).
                if (!move) lyricsCopied += await CopySidecarsAsync(item, ct, afterChunk);
                skipped++;
                progress?.Report(new SendToFolderProgress(i + 1, total, name, i, SendToFolderOutcome.Skipped));
                continue;
            }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(item.TargetPath)!);
                if (move)
                {
                    beforeMove?.Invoke(item);
                    await MoveFileAsync(item.SourcePath, item.TargetPath, ct, afterChunk);
                    // Recorded at once: a stop or a lyrics failure after this point must
                    // still relocate the track, its file is gone from the old path.
                    moved!.Add((item.Track.FilePath, item.TargetPath));
                }
                else
                {
                    await CopyFileAsync(item.SourcePath, item.TargetPath, ct, afterChunk);
                }
                copied++;
                lyricsCopied += move
                    ? await MoveSidecarsAsync(item, ct, afterChunk)
                    : await CopySidecarsAsync(item, ct, afterChunk);
                progress?.Report(new SendToFolderProgress(i + 1, total, name, i, SendToFolderOutcome.Copied));
            }
            catch (OperationCanceledException)
            {
                // CopyFileAsync already removed the half-written file it created.
                return new SendToFolderResult(copied, skipped, failed, errors, Cancelled: true, lyricsCopied);
            }
            catch (Exception ex)
            {
                failed++;
                var message = $"{name}: {ex.Message}";
                errors.Add(message);
                progress?.Report(new SendToFolderProgress(i + 1, total, name, i, SendToFolderOutcome.Failed, ex.Message));

                // The drive went away (USB stick pulled, phone unplugged): every remaining file
                // would fail the same way, one error each. Stop with one clear line instead.
                if (DestinationGone(item.TargetPath))
                {
                    var gone = Localization.Loc.T("SendToFolder.DriveGone");
                    for (var j = i + 1; j < total; j++)
                    {
                        failed++;
                        progress?.Report(new SendToFolderProgress(j + 1, total, Path.GetFileName(plan[j].SourcePath), j,
                            SendToFolderOutcome.Failed, gone));
                    }
                    errors.Add(gone);
                    break;
                }
            }
        }
        progress?.Report(new SendToFolderProgress(total, total, string.Empty));
        return new SendToFolderResult(copied, skipped, failed, errors, Cancelled: false, lyricsCopied);
    }

    private static bool DestinationGone(string targetPath)
    {
        try
        {
            var root = Path.GetPathRoot(targetPath);
            return !string.IsNullOrEmpty(root) && !Directory.Exists(root);
        }
        catch { return false; }
    }

    /// <summary>Lyrics are a bonus: a failed one never fails its song. Returns how many went over.</summary>
    private static async Task<int> CopySidecarsAsync(SendToFolderItem item, CancellationToken ct, Action<string>? afterChunk)
    {
        var count = 0;
        foreach (var sidecar in item.Sidecars)
        {
            try
            {
                if (File.Exists(sidecar.Target)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(sidecar.Target)!);
                await CopyFileAsync(sidecar.Source, sidecar.Target, ct, afterChunk);
                count++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { /* lyrics are a bonus */ }
        }
        return count;
    }

    /// <summary>Move: the song's lyrics files follow it (the player pairs them by basename, so
    /// left behind they'd detach). One that can't move stays where it was; never overwrites.</summary>
    private static async Task<int> MoveSidecarsAsync(SendToFolderItem item, CancellationToken ct, Action<string>? afterChunk)
    {
        var count = 0;
        foreach (var sidecar in item.Sidecars)
        {
            try
            {
                if (File.Exists(sidecar.Target)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(sidecar.Target)!);
                await MoveFileAsync(sidecar.Source, sidecar.Target, ct, afterChunk);
                count++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { /* lyrics are a bonus */ }
        }
        return count;
    }

    /// <summary>
    /// GitHub #121: moves one file, never losing or doubling it. Same volume: a rename
    /// (atomic, refuses an existing target). Another drive: a full copy (CreateNew, a half-written
    /// one removed), checked for size, then the source deleted; a source that can't be deleted
    /// (in use, read-only) takes the copy back out so the song stays in one place, and throws.
    /// </summary>
    /// <param name="sameVolume">Test hook: force the rename (true) or the copy + delete (false).</param>
    internal static async Task MoveFileAsync(string source, string target, CancellationToken ct, Action<string>? afterChunk = null,
        bool? sameVolume = null)
    {
        ct.ThrowIfCancellationRequested();
        if (sameVolume ?? SameVolume(source, target))
        {
            File.Move(source, target, overwrite: false);
            return;
        }
        await CopyFileAsync(source, target, ct, afterChunk);
        try
        {
            var written = new FileInfo(target).Length;
            var expected = new FileInfo(source).Length;
            if (written != expected)
                throw new IOException($"Copy is {written} bytes, the song {expected}.");
            File.Delete(source);
        }
        catch
        {
            try { File.Delete(target); } catch { /* the drive may be gone */ }
            throw;
        }
    }

    /// <summary>True when a rename can move <paramref name="source"/> to <paramref name="target"/>:
    /// same drive on Windows; elsewhere the same mount (the longest mount point holding each).
    /// Unsure → false (copy + delete, which is always safe).</summary>
    private static bool SameVolume(string source, string target)
    {
        try
        {
            var a = Path.GetFullPath(source);
            var b = Path.GetFullPath(target);
            if (OperatingSystem.IsWindows())
                return string.Equals(Path.GetPathRoot(a), Path.GetPathRoot(b), StringComparison.OrdinalIgnoreCase);
            var mounts = DriveInfo.GetDrives().Select(d => d.RootDirectory.FullName).ToList();
            return string.Equals(MountOf(a, mounts), MountOf(b, mounts), StringComparison.Ordinal);
        }
        catch { return false; }
    }

    private static string? MountOf(string path, List<string> mounts)
        => mounts.Where(m => path.StartsWith(Path.TrimEndingDirectorySeparator(m) + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                             || m == "/")
            .OrderByDescending(m => m.Length).FirstOrDefault();

    /// <summary>
    /// Copies in blocks so a cancel (Stop, or closing the dialog) stops mid-file instead of
    /// waiting out a whole FLAC on a slow stick. CreateNew: never overwrites, and whatever is
    /// at <paramref name="target"/> on a failure or cancel is this run's half-written file,
    /// which is removed. The source's modified time is kept, as File.Copy did.
    /// </summary>
    internal static async Task CopyFileAsync(string source, string target, CancellationToken ct, Action<string>? afterChunk = null)
    {
        var created = false;
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1, FileOptions.SequentialScan))
            await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1))
            {
                created = true;
                int read;
                while ((read = await input.ReadAsync(buffer.AsMemory(0, BufferSize), CancellationToken.None)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    await output.WriteAsync(buffer.AsMemory(0, read), CancellationToken.None);
                    afterChunk?.Invoke(target);
                }
                ct.ThrowIfCancellationRequested();
            }
            try { File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(source)); } catch { /* cosmetic */ }
        }
        catch
        {
            if (created)
            {
                try { File.Delete(target); } catch { /* the drive may be gone */ }
            }
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
