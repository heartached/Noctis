using System.Text.Json;
using Noctis.Models;

namespace Noctis.Services;

/// <summary>
/// Applies tag-derived file moves and supports undo. Planning is delegated to the pure
/// <see cref="FileOrganizePlanner"/>; this service owns the side effects: moving files,
/// relocating tracks in the library, reporting their id changes (the caller remaps the
/// live playlists), and persisting an undo log per applied batch under
/// <c>%APPDATA%\Noctis\organize_undo\</c>.
/// </summary>
public sealed class FileOrganizerService : IFileOrganizerService
{
    private readonly ILibraryService _library;
    private readonly IPersistenceService _persistence;
    private readonly ILibraryWatcherService? _watcher;
    private readonly string _undoDir;

    /// <summary>Same-basename sidecars that must travel with the audio file. Shared with
    /// the removal helper: the organizer's own list lacked .elrc and .lyricsfile, so
    /// word-synced lyrics were left behind (detached from the track, and keeping the old
    /// folder alive).</summary>
    private static readonly string[] SidecarExtensions = Helpers.LibraryRemovalHelper.SidecarExtensions;

    /// <summary>How long the watcher ignores a path we are about to move.</summary>
    private static readonly TimeSpan SuppressionWindow = TimeSpan.FromSeconds(30);

    public FileOrganizerService(ILibraryService library, IPersistenceService persistence,
        ILibraryWatcherService? watcher = null)
    {
        _library = library;
        _persistence = persistence;
        _watcher = watcher;
        _undoDir = Path.Combine(_persistence.DataDirectory, "organize_undo");
    }

    /// <summary>Folders the cleanup must never remove; a seam for tests.</summary>
    internal Func<Task<HashSet<string>>> ProtectedRootsProvider { get; set; }
        = Helpers.LibraryRemovalHelper.GetProtectedRootsAsync;

    /// <summary>Moves a folder to the OS trash; a seam for tests.</summary>
    internal Helpers.LibraryRemovalHelper.TrashAttempt TrashDirectory { get; set; }
        = Helpers.RecycleBin.TryMoveDirectoryToTrash;

    public IReadOnlyList<OrganizeMove> Plan(IEnumerable<Track> tracks, string pattern, string targetRoot)
        => FileOrganizePlanner.Plan(tracks, pattern, targetRoot, File.Exists);

    public bool CanUndo => Directory.Exists(_undoDir) && Directory.EnumerateFiles(_undoDir, "*.json").Any();

    public Task<OrganizeResult> ApplyAsync(IReadOnlyList<OrganizeMove> moves, CancellationToken ct = default)
    {
        var pending = (moves ?? Array.Empty<OrganizeMove>())
            .Where(m => m.Action != OrganizeAction.Skip && !PathEquals(m.SourcePath, m.TargetPath))
            .Select(m => (m.SourcePath, m.TargetPath))
            .ToList();
        var skipped = (moves?.Count ?? 0) - pending.Count;
        return RunAsync(pending, skipped, writeUndoLog: true, ct);
    }

    public async Task<OrganizeResult> UndoLastAsync(CancellationToken ct = default)
    {
        var log = LatestUndoLog();
        if (log is null) return OrganizeResult.Empty;

        List<UndoEntry>? entries;
        try
        {
            await using var stream = File.OpenRead(log);
            entries = await JsonSerializer.DeserializeAsync<List<UndoEntry>>(stream, cancellationToken: ct);
        }
        catch (Exception ex)
        {
            return new OrganizeResult(0, 0, 1, new[] { $"Could not read undo log: {ex.Message}" });
        }

        // Reverse each move: To -> From.
        var reverse = (entries ?? new List<UndoEntry>())
            .Select(e => (e.To, e.From))
            .ToList();

        var result = await RunAsync(reverse, skipped: 0, writeUndoLog: false, ct);

        if (result.Failed > 0)
        {
            // Keep the entries that could NOT be moved back so the user can retry.
            // Deleting the log unconditionally made a partial undo unrecoverable through
            // the UI: CanUndo went false and the new-path → original-path mapping was
            // gone, for exactly the files that were still misplaced.
            var reversedOk = new HashSet<string>(
                result.RestoredPaths ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            var remaining = (entries ?? new List<UndoEntry>())
                .Where(e => !reversedOk.Contains(e.To))
                .ToList();

            if (remaining.Count > 0)
            {
                try
                {
                    await using var stream = File.Create(log);
                    await JsonSerializer.SerializeAsync(stream, remaining, cancellationToken: ct);
                }
                catch { /* if the rewrite fails the original log is still on disk */ }
                return result;
            }
        }

        try { File.Delete(log); } catch { /* best effort */ }
        return result;
    }

    /// <summary>
    /// Moves files off the UI thread, then relocates the tracks for the moves that actually
    /// succeeded and returns their id remap. Optionally records an undo log for the applied set.
    /// </summary>
    private Task<OrganizeResult> RunAsync(
        List<(string From, string To)> moves, int skipped, bool writeUndoLog, CancellationToken ct)
        => Task.Run(async () =>
    {
        var errors = new List<string>();
        var done = new List<(string From, string To)>();

        foreach (var (from, to) in moves)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (!File.Exists(from)) { errors.Add($"Missing source: {from}"); continue; }
                var dir = Path.GetDirectoryName(to);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                // Tell the watcher to ignore this path (and its sidecars) before touching
                // it. A batch longer than the watcher's 1.5s debounce would otherwise be
                // flushed mid-run, recording the old paths as deletions — which
                // permanently blacklists them in ExcludedFilePaths and loses play counts,
                // ratings, favorites and playlist membership.
                SuppressForMove(from, to);

                File.Move(from, to);
                MoveSidecars(from, to);
                done.Add((from, to));
            }
            catch (Exception ex)
            {
                errors.Add($"{Path.GetFileName(from)}: {ex.Message}");
            }
        }

        IReadOnlyDictionary<Guid, Guid> remap = new Dictionary<Guid, Guid>();
        if (done.Count > 0)
        {
            remap = await _library.RelocateTracksAsync(
                done.Select(d => (d.From, d.To)).ToList(), ct);

            if (writeUndoLog)
                await WriteUndoLogAsync(done, ct);

            await CleanupAfterMovesAsync(done);
        }

        return new OrganizeResult(done.Count, skipped, errors.Count, errors)
        {
            RestoredPaths = done.Select(d => d.From).ToList(),
            TrackIdRemap = remap
        };
    }, ct);

    private async Task WriteUndoLogAsync(IReadOnlyList<(string From, string To)> done, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(_undoDir);
            var path = Path.Combine(_undoDir, $"{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}.json");
            var entries = done.Select(d => new UndoEntry { From = d.From, To = d.To }).ToList();
            await using var stream = File.Create(path);
            await JsonSerializer.SerializeAsync(stream, entries, cancellationToken: ct);
        }
        catch
        {
            // An unwritable undo log shouldn't fail the organize itself.
        }
    }

    private string? LatestUndoLog()
    {
        if (!Directory.Exists(_undoDir)) return null;
        return Directory.EnumerateFiles(_undoDir, "*.json")
            .OrderByDescending(f => f, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    /// <summary>Registers a move (and its sidecars) with the watcher's ignore list.</summary>
    private void SuppressForMove(string from, string to)
    {
        if (_watcher == null) return;
        var paths = new List<string>(2 + SidecarExtensions.Length * 2) { from, to };
        foreach (var ext in SidecarExtensions)
        {
            paths.Add(Path.ChangeExtension(from, ext));
            paths.Add(Path.ChangeExtension(to, ext));
        }
        _watcher.SuppressPaths(paths, SuppressionWindow);
    }

    /// <summary>
    /// Moves the same-basename lyric sidecars alongside the audio file. Lyrics resolve
    /// sidecar-first by basename, so leaving them behind silently detaches every
    /// hand-timed LRC from its track.
    /// </summary>
    private static void MoveSidecars(string from, string to)
    {
        foreach (var ext in SidecarExtensions)
        {
            try
            {
                var oldSidecar = Path.ChangeExtension(from, ext);
                var newSidecar = Path.ChangeExtension(to, ext);
                if (File.Exists(oldSidecar) && !File.Exists(newSidecar))
                    File.Move(oldSidecar, newSidecar);
            }
            catch { /* best effort — the audio move already succeeded */ }
        }
    }

    // Leftovers that may go with an emptied folder: loose images and OS detritus only.
    // Lyrics are deliberately absent (same-name ones travel with their track; any other
    // .lrc/.ttml is the user's), as are .cue/.m3u/.nfo/.log/.txt/.pdf: any of those keeps
    // the folder. Folder art (cover.jpg, folder.jpg, …) is moved with the album first.
    private static readonly HashSet<string> LeftoverImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp" };

    private static readonly HashSet<string> LeftoverJunkNames = new(StringComparer.OrdinalIgnoreCase)
    { "Thumbs.db", "desktop.ini", ".DS_Store" };

    /// <summary>A folder whose leftovers add up to this much or more is kept (#235: the
    /// reporter's "less than 1MB").</summary>
    internal const long MaxLeftoverBytes = 1024 * 1024;

    /// <summary>
    /// After a batch (organize or undo) moved tracks out of their folders: moves each
    /// emptied album folder's cover art to where its tracks went, then removes the folders
    /// the tracks left once nothing worth keeping is in them, walking up through the
    /// emptied parents (album, then artist).
    /// It only ever looks at folders this batch moved files out of and their parents, and
    /// never climbs to the folder a track's old and new path share (the library root the
    /// files moved within) or above it. Configured music folders, the well-known user
    /// folders and drive roots are never removed. Never throws.
    /// </summary>
    private async Task CleanupAfterMovesAsync(IReadOnlyList<(string From, string To)> done)
    {
        // Never delete a directory the user configured as a music root (or one of the
        // OS media folders). Organizing loose files out of ~/Music into subfolders
        // emptied the root and this deleted it outright — with a hard Directory.Delete,
        // not the recycle bin.
        HashSet<string> protectedRoots;
        try
        {
            protectedRoots = await ProtectedRootsProvider();
        }
        catch (Exception ex)
        {
            // If the protected set can't be resolved, do nothing rather than risk
            // deleting a root.
            DebugLog.Write("Organize", $"folder cleanup skipped: {ex.Message}");
            return;
        }

        try
        {
            // Source folder → the folders its tracks went to, and the deepest folder any
            // of its moves stayed within (the walk-up stops below it).
            var groups = new Dictionary<string, (HashSet<string> Targets, string? Boundary)>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var (from, to) in done)
            {
                var fromDir = FullDir(from);
                var toDir = FullDir(to);
                if (fromDir is null || toDir is null) continue;
                var boundary = CommonAncestor(fromDir, toDir) ?? Path.GetDirectoryName(fromDir);
                if (!groups.TryGetValue(fromDir, out var g))
                    g = (new HashSet<string>(StringComparer.OrdinalIgnoreCase), boundary);
                else if (boundary is null || g.Boundary is null || boundary.Length > g.Boundary.Length)
                    g = (g.Targets, boundary);
                g.Targets.Add(toDir);
                groups[fromDir] = g;
            }

            foreach (var (sourceDir, (targets, boundary)) in groups)
            {
                if (boundary is null || !IsStrictlyUnder(sourceDir, boundary)) continue;
                if (protectedRoots.Contains(sourceDir)) continue;

                if (targets.Count == 1)
                    MoveFolderArt(sourceDir, targets.First());

                var current = sourceDir;
                while (IsStrictlyUnder(current, boundary) && TryRemoveLeftoverFolder(current, protectedRoots))
                {
                    var parent = Path.GetDirectoryName(current);
                    if (string.IsNullOrEmpty(parent)) break;
                    current = parent;
                }
            }
        }
        catch (Exception ex)
        {
            DebugLog.Write("Organize", $"folder cleanup failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Moves the folder cover images (cover/folder/front.jpg …, the names the app reads
    /// for tracks without embedded art) to the folder the album went to, once no audio
    /// is left beside them. Never overwrites an image already there.
    /// </summary>
    private void MoveFolderArt(string sourceDir, string targetDir)
    {
        try
        {
            if (!Directory.Exists(sourceDir) || !Directory.Exists(targetDir)) return;
            var files = Directory.EnumerateFiles(sourceDir).ToList();
            if (files.Any(IsAudioFile)) return; // the tracks still here need their cover

            foreach (var art in files.Where(MetadataService.IsFolderArtCandidate))
            {
                try
                {
                    var dest = Path.Combine(targetDir, Path.GetFileName(art));
                    if (File.Exists(dest)) continue;
                    _watcher?.SuppressPaths(new[] { art, dest }, SuppressionWindow);
                    File.Move(art, dest);
                }
                catch (Exception ex)
                {
                    DebugLog.Write("Organize", $"cover move failed: {art}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            DebugLog.Write("Organize", $"cover move failed: {sourceDir}: {ex.Message}");
        }
    }

    /// <summary>
    /// Removes <paramref name="dir"/> when it holds no subfolder and nothing but loose
    /// images / OS detritus under <see cref="MaxLeftoverBytes"/> in total. An empty folder
    /// is deleted; one with leftovers goes to the OS trash, or, where the drive has no
    /// trash, its allow-listed leftovers are deleted. A "No" to Windows' delete-permanently
    /// prompt keeps it. Returns true when the folder is gone.
    /// </summary>
    private bool TryRemoveLeftoverFolder(string dir, HashSet<string> protectedRoots)
    {
        try
        {
            if (!Directory.Exists(dir)) return true;
            var leftovers = RemovableLeftovers(dir, protectedRoots);
            if (leftovers is null) return false;

            if (leftovers.Count > 0)
            {
                if (TrashDirectory(dir, out var declined) || !Directory.Exists(dir)) return true;
                if (declined) return false;

                // Re-check: the folder may have changed while the trash attempt ran.
                leftovers = RemovableLeftovers(dir, protectedRoots);
                if (leftovers is null) return false;
                foreach (var file in leftovers)
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                    File.Delete(file);
                }
            }

            var info = new DirectoryInfo(dir);
            if (info.Attributes.HasFlag(FileAttributes.ReadOnly))
                info.Attributes &= ~FileAttributes.ReadOnly;
            Directory.Delete(dir, recursive: false);
            return true;
        }
        catch (Exception ex)
        {
            DebugLog.Write("Organize", $"folder cleanup left {dir}: {ex.Message}");
            return false;
        }
    }

    /// <summary>The files to remove with <paramref name="dir"/>, or null when the folder
    /// must stay (protected, a drive root, has a subfolder, audio, any file outside the
    /// allow-list, or leftovers of <see cref="MaxLeftoverBytes"/> or more).</summary>
    private static List<string>? RemovableLeftovers(string dir, HashSet<string> protectedRoots)
    {
        if (protectedRoots.Contains(dir)) return null;
        if (string.Equals(Path.TrimEndingDirectorySeparator(Path.GetPathRoot(dir) ?? string.Empty),
                dir, StringComparison.OrdinalIgnoreCase)) return null;

        var files = new List<string>();
        long total = 0;
        foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos())
        {
            if (entry is not FileInfo file) return null;     // a subfolder (it may hold audio)
            if (IsAudioFile(file.Name)) return null;
            if (!LeftoverJunkNames.Contains(file.Name) && !LeftoverImageExtensions.Contains(file.Extension))
                return null;                                 // something worth keeping
            total += file.Length;
            if (total >= MaxLeftoverBytes) return null;
            files.Add(file.FullName);
        }
        return files;
    }

    private static bool IsAudioFile(string path) => MetadataService.SupportedExtensions.Contains(Path.GetExtension(path));

    private static string? FullDir(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            return string.IsNullOrEmpty(dir) ? null : Path.TrimEndingDirectorySeparator(dir);
        }
        catch { return null; }
    }

    /// <summary>The deepest folder containing both <paramref name="a"/> and
    /// <paramref name="b"/>, or null when they share none (different drives).</summary>
    private static string? CommonAncestor(string a, string b)
    {
        var current = a;
        while (!string.IsNullOrEmpty(current))
        {
            if (PathEquals(current, b) || IsStrictlyUnder(b, current)) return current;
            current = Path.GetDirectoryName(current);
        }
        return null;
    }

    private static bool IsStrictlyUnder(string dir, string ancestor)
    {
        var prefix = Path.TrimEndingDirectorySeparator(ancestor);
        if (!dir.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || dir.Length <= prefix.Length) return false;
        // A drive root keeps its separator ("C:\", "/"); any other prefix must end at one.
        return Path.EndsInDirectorySeparator(prefix)
            || dir[prefix.Length] == Path.DirectorySeparatorChar
            || dir[prefix.Length] == Path.AltDirectorySeparatorChar;
    }

    private static bool PathEquals(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private sealed class UndoEntry
    {
        public string From { get; set; } = string.Empty;
        public string To { get; set; } = string.Empty;
    }
}
