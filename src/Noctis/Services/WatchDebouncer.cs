namespace Noctis.Services;

/// <summary>
/// The net intent for a watched path after a burst of filesystem events is coalesced.
/// </summary>
public enum FileChangeKind
{
    /// <summary>File was created or modified — (re)import it.</summary>
    CreatedOrChanged,

    /// <summary>File was deleted — remove it from the library.</summary>
    Deleted
}

/// <summary>A coalesced batch of import/remove actions ready to apply to the library.</summary>
public readonly record struct WatchBatch(IReadOnlyList<string> ToImport, IReadOnlyList<string> ToRemove, IReadOnlyList<string> ToRemoveDirs)
{
    public bool IsEmpty => ToImport.Count == 0 && ToRemove.Count == 0 && ToRemoveDirs.Count == 0;
}

/// <summary>
/// Pure (not thread-safe) accumulator that coalesces a burst of raw filesystem
/// events into a deduplicated batch of import/remove actions. The owning service
/// handles locking and the quiet-period timing.
///
/// Coalescing rule: the latest event for a path wins. A create-then-delete
/// collapses to a single Delete; a delete-then-create (rename churn, an editor's
/// save-replace, a download's <c>.part</c> → final rename) collapses to a single
/// CreatedOrChanged. Paths are compared case-insensitively.
/// </summary>
public sealed class WatchDebouncer
{
    private readonly Dictionary<string, FileChangeKind> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pendingDirRemovals = new(StringComparer.OrdinalIgnoreCase);

    public bool HasPending => _pending.Count > 0 || _pendingDirRemovals.Count > 0;

    public void Record(string path, FileChangeKind kind)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        _pending[path] = kind;
    }

    /// <summary>
    /// Records the deletion of a whole directory — every library track under it is removed.
    /// Deleting/recycling a folder raises a single directory-level event with no per-file
    /// deletes, so removal has to be by path prefix rather than exact file path.
    /// </summary>
    public void RecordDirectoryDeleted(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        _pendingDirRemovals.Add(path);
    }

    /// <summary>Records a rename as a delete of the old path plus a create of the new path.</summary>
    public void RecordRename(string? oldPath, string? newPath)
    {
        if (!string.IsNullOrWhiteSpace(oldPath)) _pending[oldPath!] = FileChangeKind.Deleted;
        if (!string.IsNullOrWhiteSpace(newPath)) _pending[newPath!] = FileChangeKind.CreatedOrChanged;
    }

    /// <summary>Returns the coalesced batch and clears pending state.</summary>
    public WatchBatch Drain()
    {
        if (_pending.Count == 0 && _pendingDirRemovals.Count == 0)
            return new WatchBatch(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());

        var toImport = new List<string>();
        var toRemove = new List<string>();
        foreach (var (path, kind) in _pending)
        {
            if (kind == FileChangeKind.CreatedOrChanged) toImport.Add(path);
            else toRemove.Add(path);
        }
        var toRemoveDirs = _pendingDirRemovals.ToList();
        _pending.Clear();
        _pendingDirRemovals.Clear();
        return new WatchBatch(toImport, toRemove, toRemoveDirs);
    }
}
