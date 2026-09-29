using Noctis.Models;

namespace Noctis.Services;

/// <summary>Outcome of an apply or undo pass.</summary>
public sealed record OrganizeResult(int Moved, int Skipped, int Failed, IReadOnlyList<string> Errors)
{
    public static readonly OrganizeResult Empty = new(0, 0, 0, Array.Empty<string>());

    /// <summary>
    /// Source paths that were successfully moved in this run. Undo uses it to keep the
    /// entries it could not reverse in the log instead of discarding the whole thing.
    /// </summary>
    public IReadOnlyList<string> RestoredPaths { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Old track id → new id for every moved track (ids derive from the path). The caller
    /// applies it to the live playlists, which own playlists.json.
    /// </summary>
    public IReadOnlyDictionary<Guid, Guid> TrackIdRemap { get; init; } = new Dictionary<Guid, Guid>();
}

/// <summary>
/// Renames/moves library files into a tag-derived folder structure. Planning is a pure,
/// preview-only step (<see cref="Plan"/>); nothing touches disk until <see cref="ApplyAsync"/>.
/// Every applied batch writes an undo log so the most recent organize can be reversed.
/// </summary>
public interface IFileOrganizerService
{
    /// <summary>Computes the set of moves for the given tracks. Pure — no disk changes.</summary>
    IReadOnlyList<OrganizeMove> Plan(IEnumerable<Track> tracks, string pattern, string targetRoot);

    /// <summary>
    /// Applies the non-skipped moves off the UI thread, relocates the tracks in the library
    /// (preserving user state) and records an undo log. Playlists are not touched: apply
    /// <see cref="OrganizeResult.TrackIdRemap"/> to the live playlists.
    /// </summary>
    Task<OrganizeResult> ApplyAsync(IReadOnlyList<OrganizeMove> moves, CancellationToken ct = default);

    /// <summary>True when there is at least one undo log to reverse.</summary>
    bool CanUndo { get; }

    /// <summary>Reverses the most recent applied batch and discards its undo log.</summary>
    Task<OrganizeResult> UndoLastAsync(CancellationToken ct = default);
}
