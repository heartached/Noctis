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
    private readonly string _undoDir;

    public FileOrganizerService(ILibraryService library, IPersistenceService persistence)
    {
        _library = library;
        _persistence = persistence;
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
                File.Move(from, to);
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

            CleanupEmptyDirs(done.Select(d => d.From));
        }

        return new OrganizeResult(done.Count, skipped, errors.Count, errors);
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

    private static void CleanupEmptyDirs(IEnumerable<string> sourcePaths)
    {
        foreach (var dir in sourcePaths.Select(Path.GetDirectoryName).Where(d => !string.IsNullOrEmpty(d)).Distinct())
        {
            try
            {
                if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir!).Any())
                    Directory.Delete(dir!);
            }
            catch { /* leave non-empty / locked dirs alone */ }
        }
    }

    private static bool PathEquals(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private sealed class UndoEntry
    {
        public string From { get; set; } = string.Empty;
        public string To { get; set; } = string.Empty;
    }
}
