using Noctis.Services;
using Noctis.Services.Lyrics;

namespace Noctis.Helpers;

/// <summary>
/// GitHub #186: the player island's Move to Trash button moves the playing song's file into
/// the folder picked in Settings → Player (a holding folder to sort bulk downloads into), not
/// the Recycle Bin. The file keeps its name; a name already taken there gets " (2)", " (3)"…
/// The song's lyric sidecars (same base name, <see cref="LibraryRemovalHelper.SidecarExtensions"/>)
/// go along under the same new name so they still pair with it. Never deletes: a move that
/// can't finish leaves the song where it was.
/// </summary>
public static class TrashFolderMover
{
    public enum Outcome
    {
        /// <summary>The song is in the folder now (<see cref="Result.Destination"/>).</summary>
        Moved,
        /// <summary>Another program (or the player, still letting go) holds the file open.</summary>
        InUse,
        /// <summary>Anything else: the song is gone, the folder is gone or not writable.</summary>
        Failed,
    }

    /// <param name="Destination">Where the song is now (Moved only).</param>
    /// <param name="Sidecars">The lyric sidecars moved with it, at their new paths.</param>
    public sealed record Result(Outcome Outcome, string? Destination, IReadOnlyList<string> Sidecars)
    {
        public bool Moved => Outcome == Outcome.Moved;
        internal static Result Of(Outcome outcome) => new(outcome, null, Array.Empty<string>());
    }

    /// <summary>A folder is set and is on disk. An unset one, or one that's gone (deleted,
    /// a drive that isn't plugged in), sends the song to the Recycle Bin instead.</summary>
    public static bool IsUsable(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return false;
        try { return Directory.Exists(folder); }
        catch { return false; }
    }

    /// <summary>Waits before each attempt: the player lets go of the file it just skipped
    /// off asynchronously (UI-thread post → engine stop), like the Recycle Bin ladder in
    /// <see cref="LibraryRemovalHelper"/>. ~3.75 s worst case.</summary>
    private static readonly int[] RetryDelaysMs = { 0, 250, 500, 1000, 2000 };

    /// <summary>Moves <paramref name="audioPath"/> into <paramref name="folder"/> off the UI
    /// thread, retrying while the file is in use.</summary>
    public static Task<Result> MoveAsync(string audioPath, string folder) =>
        Task.Run(() => MoveWithRetriesAsync(audioPath, folder, RetryDelaysMs, SameVolume));

    /// <summary>Retry orchestration with injectable delays and volume test; internal for tests.</summary>
    internal static async Task<Result> MoveWithRetriesAsync(
        string audioPath, string folder, IReadOnlyList<int> retryDelaysMs, Func<string, string, bool> sameVolume)
    {
        var result = Result.Of(Outcome.Failed);
        foreach (var delayMs in retryDelaysMs)
        {
            if (delayMs > 0) await Task.Delay(delayMs).ConfigureAwait(false);
            result = TryMove(audioPath, folder, sameVolume);
            if (result.Outcome != Outcome.InUse) break;
        }
        if (!result.Moved)
            DebugLog.Write("Library", $"trash folder move {result.Outcome}: {audioPath} -> {folder}");
        return result;
    }

    /// <summary>One attempt: the audio first, then its sidecars (best effort, the song has
    /// moved by then).</summary>
    internal static Result TryMove(string audioPath, string folder, Func<string, string, bool> sameVolume)
    {
        string source, targetDir, stem, destination;
        List<string> sidecars;
        try
        {
            if (string.IsNullOrWhiteSpace(audioPath) || !File.Exists(audioPath) || !IsUsable(folder))
                return Result.Of(Outcome.Failed);
            source = Path.GetFullPath(audioPath);
            targetDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));

            // Played from the trash folder itself: it is already there.
            if (string.Equals(Path.GetDirectoryName(source), targetDir, PathComparison))
                return new Result(Outcome.Moved, source, Array.Empty<string>());

            sidecars = SidecarsOf(source);
            var extension = Path.GetExtension(source);
            stem = FreeStem(targetDir, Path.GetFileNameWithoutExtension(source), extension,
                sidecars.Select(Path.GetExtension).OfType<string>().ToList(), File.Exists);
            destination = Path.Combine(targetDir, stem + extension);
            MoveFile(source, destination, sameVolume(source, destination));
        }
        catch (IOException ex) when (IsInUse(ex))
        {
            return Result.Of(Outcome.InUse);
        }
        catch (Exception ex)
        {
            DebugLog.Write("Library", $"trash folder move failed: {ex.GetType().Name}: {ex.Message}");
            return Result.Of(Outcome.Failed);
        }

        var moved = new List<string>(sidecars.Count);
        foreach (var sidecar in sidecars)
        {
            var to = Path.Combine(targetDir, stem + Path.GetExtension(sidecar));
            try
            {
                MoveFile(sidecar, to, sameVolume(sidecar, to));
                moved.Add(to);
            }
            catch (Exception ex)
            {
                DebugLog.Write("Library", $"trash folder sidecar left: {sidecar} ({ex.GetType().Name})");
            }
        }
        return new Result(Outcome.Moved, destination, moved);
    }

    /// <summary>The song's lyric sidecars on disk, in any letter case of the extension.</summary>
    internal static List<string> SidecarsOf(string audioPath) =>
        LibraryRemovalHelper.SidecarExtensions
            .SelectMany(ext => LyricsWriter.SidecarsOnDisk(audioPath, ext))
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// The first of <c>stem</c>, <c>stem (2)</c>, <c>stem (3)</c>… under which neither the
    /// song (<paramref name="audioExtension"/>) nor any of its sidecars would land on a file
    /// already in <paramref name="folder"/>, so nothing there is overwritten and the moved
    /// sidecars keep pairing with the moved song.
    /// </summary>
    internal static string FreeStem(string folder, string stem, string audioExtension,
        IReadOnlyCollection<string> sidecarExtensions, Func<string, bool> exists)
    {
        for (var n = 1; n <= 10_000; n++)
        {
            var candidate = n == 1 ? stem : $"{stem} ({n})";
            if (exists(Path.Combine(folder, candidate + audioExtension))) continue;
            if (sidecarExtensions.Any(ext => exists(Path.Combine(folder, candidate + ext)))) continue;
            return candidate;
        }
        throw new IOException($"No free name for {stem}{audioExtension} in {folder}");
    }

    /// <summary>
    /// Same volume: a rename. Another volume: copy, then delete the original; if the original
    /// can't be deleted (in use) the copy is removed again, so the song never ends up in both
    /// places. On macOS/Linux every path roots at "/", so this is always File.Move, which
    /// copies and deletes by itself when the rename crosses filesystems.
    /// </summary>
    internal static void MoveFile(string source, string destination, bool sameVolume)
    {
        if (sameVolume)
        {
            File.Move(source, destination, overwrite: false);
            return;
        }

        if (File.Exists(destination))
            throw new IOException($"{destination} already exists");
        try
        {
            File.Copy(source, destination, overwrite: false);
        }
        catch
        {
            TryDelete(destination); // a half-written copy (disk full, drive pulled)
            throw;
        }

        try
        {
            File.Delete(source);
        }
        catch
        {
            TryDelete(destination);
            throw;
        }
    }

    /// <summary>Both paths on the same drive letter / share root (Windows); on Unix the
    /// roots are all "/" and File.Move handles a cross-filesystem move itself.</summary>
    internal static bool SameVolume(string a, string b) =>
        string.Equals(Path.GetPathRoot(a), Path.GetPathRoot(b), PathComparison);

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>ERROR_SHARING_VIOLATION (32) / ERROR_LOCK_VIOLATION (33): another handle
    /// holds the file. Unix never refuses a move for an open file.</summary>
    internal static bool IsInUse(IOException ex)
    {
        var code = ex.HResult & 0xFFFF;
        return code is 32 or 33;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best effort */ }
    }
}
