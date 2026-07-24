using System.Text.Json;
using Noctis.Models;

namespace Noctis.Services;

/// <summary>
/// Applies tag-derived file moves and supports undo. Planning is delegated to the pure
/// <see cref="FileOrganizePlanner"/>; this service owns the side effects: moving files,
/// relocating tracks in the library, remapping playlist references, and persisting an
/// undo log per applied batch under <c>%APPDATA%\Noctis\organize_undo\</c>.
/// </summary>
public sealed class FileOrganizerService : IFileOrganizerService
{
    private readonly ILibraryService _library;
    private readonly IPersistenceService _persistence;
    private readonly ILibraryWatcherService? _watcher;
    private readonly string _undoDir;

    /// <summary>Same-basename sidecars that must travel with the audio file.</summary>
    private static readonly string[] SidecarExtensions = { ".lrc", ".ttml", ".txt" };

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
    /// Moves files off the UI thread, then relocates the tracks + remaps playlists for the
    /// moves that actually succeeded. Optionally records an undo log for the applied set.
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

        if (done.Count > 0)
        {
            var remap = await _library.RelocateTracksAsync(
                done.Select(d => (d.From, d.To)).ToList(), ct);
            await RemapPlaylistsAsync(remap);

            if (writeUndoLog)
                await WriteUndoLogAsync(done, ct);

            await CleanupEmptyDirsAsync(done.Select(d => d.From));
        }

        return new OrganizeResult(done.Count, skipped, errors.Count, errors)
        {
            RestoredPaths = done.Select(d => d.From).ToList()
        };
    }, ct);

    private async Task RemapPlaylistsAsync(IReadOnlyDictionary<Guid, Guid> remap)
    {
        if (remap.Count == 0) return;

        var playlists = await _persistence.LoadPlaylistsAsync();
        var anyChanged = false;
        foreach (var pl in playlists)
        {
            if (pl.TrackIds.Count == 0) continue;
            var changed = false;
            for (var i = 0; i < pl.TrackIds.Count; i++)
            {
                if (remap.TryGetValue(pl.TrackIds[i], out var newId))
                {
                    pl.TrackIds[i] = newId;
                    changed = true;
                }
            }
            if (changed) { pl.ModifiedAt = DateTime.UtcNow; anyChanged = true; }
        }

        if (anyChanged)
            await _persistence.SavePlaylistsAsync(playlists);
    }

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

    private async Task CleanupEmptyDirsAsync(IEnumerable<string> sourcePaths)
    {
        // Never delete a directory the user configured as a music root (or one of the
        // OS media folders). Organizing loose files out of ~/Music into subfolders
        // emptied the root and this deleted it outright — with a hard Directory.Delete,
        // not the recycle bin.
        HashSet<string> protectedRoots;
        try
        {
            protectedRoots = await Helpers.LibraryRemovalHelper.GetProtectedRootsAsync();
        }
        catch
        {
            // If the protected set can't be resolved, do nothing rather than risk
            // deleting a root.
            return;
        }

        foreach (var dir in sourcePaths.Select(Path.GetDirectoryName)
                     .Where(d => !string.IsNullOrEmpty(d)).Distinct())
        {
            try
            {
                if (!Directory.Exists(dir)) continue;
                if (Directory.EnumerateFileSystemEntries(dir!).Any()) continue;

                var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir!));
                if (protectedRoots.Contains(normalized)) continue;

                // Recoverable rather than permanent.
                if (!Helpers.RecycleBin.TryMoveDirectoryToTrash(dir!))
                    Directory.Delete(dir!);
            }
            catch { /* leave non-empty / locked / protected dirs alone */ }
        }
    }

    private static bool PathEquals(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private sealed class UndoEntry
    {
        public string From { get; set; } = string.Empty;
        public string To { get; set; } = string.Empty;
    }
}
