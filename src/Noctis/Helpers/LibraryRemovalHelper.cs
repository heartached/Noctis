using Noctis.Models;
using Noctis.Services;
using Noctis.Views;

namespace Noctis.Helpers;

/// <summary>
/// Shared "Remove from Library" flow: prompts the user whether to keep files on
/// disk or move them to the OS trash, then removes the tracks from the library.
/// </summary>
public static class LibraryRemovalHelper
{
    /// <summary>
    /// Prompts and applies the user's choice for the given tracks:
    /// Cancel → nothing happens; Keep Files → tracks removed, files left on disk;
    /// Recycle Bin/Trash → local files trashed, then tracks removed.
    /// Returns true when the tracks were removed (caller should update its UI).
    /// </summary>
    public static async Task<bool> RemoveWithPromptAsync(ILibraryService library, IReadOnlyList<Track> tracks)
    {
        if (library == null || tracks == null || tracks.Count == 0) return false;

        var choice = await RemoveFromLibraryDialog.ShowAsync(tracks.Count);
        if (choice == RemoveFromLibraryChoice.Cancel) return false;

        if (choice == RemoveFromLibraryChoice.Trash)
            await TrashLocalFilesAsync(tracks);

        await library.RemoveTracksAsync(tracks.Select(t => t.Id));
        return true;
    }

    /// <summary>
    /// Moves the tracks' files to the OS trash off the UI thread. Network/remote
    /// sources (SMB, WebDAV, Navidrome, …) are skipped — only
    /// <see cref="SourceType.Local"/> tracks own a deletable local file.
    /// </summary>
    public static Task TrashLocalFilesAsync(IEnumerable<Track> tracks)
    {
        var paths = SelectTrashablePaths(tracks);
        if (paths.Count == 0) return Task.CompletedTask;
        return Task.Run(() =>
        {
            foreach (var p in paths)
                RecycleBin.TryMoveToTrash(p);
        });
    }

    /// <summary>Local, non-empty, de-duplicated file paths eligible for trashing.</summary>
    internal static IReadOnlyList<string> SelectTrashablePaths(IEnumerable<Track> tracks) =>
        tracks
            .Where(t => t.SourceType == SourceType.Local && !string.IsNullOrWhiteSpace(t.FilePath))
            .Select(t => t.FilePath)
            .Distinct()
            .ToList();
}
