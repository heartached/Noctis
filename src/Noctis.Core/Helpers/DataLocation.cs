using System.Text.Json;
using System.Text.Json.Serialization;

namespace Noctis.Helpers;

/// <summary>
/// Where the data folder lives when the user moved it off the default location (GitHub #165).
///
/// The choice has to be known before anything reads the data folder, so it cannot live in
/// settings.json inside that folder: it is a small pointer file, <see cref="PointerFileName"/>,
/// in the HOME root (the default folder, or NOCTIS_DATA_DIR's folder; see
/// <see cref="AppPaths.HomeRoot"/>). No pointer = the home root holds the data, as before.
///
/// A move never leaves the pointer on a half-copied folder:
///  1. Settings copies everything to the new folder while the app runs (a flag file marks the
///     copy incomplete), then records the move as pending in the pointer and asks for a restart.
///  2. The next launch, before any service opens a file, mirrors the old folder onto the new
///     one again (whatever changed since, with nothing running), verifies every file, marks the
///     new folder complete and only then switches the pointer. A crash anywhere before the
///     switch leaves the app on the old, untouched folder; the next launch retries.
///  3. The old copy is moved aside at once and deleted in the background.
/// </summary>
public static class DataLocation
{
    /// <summary>The pointer, in the home root.</summary>
    public const string PointerFileName = "data-location.json";
    /// <summary>Marks a complete Noctis data folder (written when a move or "use this folder" lands).</summary>
    public const string MarkerFileName = ".noctis-data";
    /// <summary>Marks a folder a move is still copying into: never a usable data folder.</summary>
    public const string IncompleteFileName = ".noctis-move-incomplete";
    /// <summary>Prefix of the folder the old copy is moved into until it is deleted.</summary>
    public const string OldDataPrefix = ".noctis-old-data";
    /// <summary>The Unix single-instance lock (SingleInstanceGuard), held open by the running app.</summary>
    private const string InstanceLockFileName = ".instance.lock";

    /// <summary>Top-level names that belong to the folder, not to the data: never copied,
    /// mirrored, moved aside or counted when deciding whether a folder is empty.</summary>
    internal static bool IsReservedRootEntry(string name) =>
        name.Equals(PointerFileName, StringComparison.OrdinalIgnoreCase)
        || name.Equals(MarkerFileName, StringComparison.OrdinalIgnoreCase)
        || name.Equals(IncompleteFileName, StringComparison.OrdinalIgnoreCase)
        || name.Equals(InstanceLockFileName, StringComparison.OrdinalIgnoreCase)
        || name.Equals(PointerFileName + ".tmp", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith(OldDataPrefix, StringComparison.OrdinalIgnoreCase);

    // ── Pointer ───────────────────────────────────────────────

    public sealed class Pointer
    {
        /// <summary>Where the data lives; null = the home root.</summary>
        public string? DataRoot { get; set; }
        /// <summary>A copied move waiting for the next launch to finish it.</summary>
        public string? PendingMoveTo { get; set; }
        /// <summary>The folder that move copies from (the one in use when it was copied).</summary>
        public string? PendingMoveFrom { get; set; }
        /// <summary>The old copy, moved aside, still to delete.</summary>
        public string? OldDataToDelete { get; set; }
        /// <summary>The old data root, when it is not the home root: removed once empty.</summary>
        public string? OldRootToRemove { get; set; }

        [JsonIgnore]
        internal bool IsEmpty => DataRoot is null && PendingMoveTo is null && PendingMoveFrom is null && OldDataToDelete is null && OldRootToRemove is null;
    }

    private static readonly JsonSerializerOptions s_json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The pointer in <paramref name="homeRoot"/>, or null when there is none (or it is unreadable).</summary>
    public static Pointer? ReadPointer(string homeRoot)
    {
        try
        {
            var path = Path.Combine(homeRoot, PointerFileName);
            if (!File.Exists(path)) return null;
            var p = JsonSerializer.Deserialize<Pointer>(File.ReadAllBytes(path), s_json);
            if (p is null) return null;
            p.DataRoot = Clean(p.DataRoot);
            p.PendingMoveTo = Clean(p.PendingMoveTo);
            p.PendingMoveFrom = Clean(p.PendingMoveFrom);
            p.OldDataToDelete = Clean(p.OldDataToDelete);
            p.OldRootToRemove = Clean(p.OldRootToRemove);
            return p;
        }
        catch
        {
            // A damaged pointer must not strand the app: the home root is always a valid answer
            // (the startup check then reports nothing, since the home root needs no drive).
            return null;
        }

        static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }

    /// <summary>Writes the pointer atomically (temp file + rename); an empty pointer deletes the file.</summary>
    public static void WritePointer(string homeRoot, Pointer? pointer)
    {
        var path = Path.Combine(homeRoot, PointerFileName);
        if (pointer is null || pointer.IsEmpty)
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }
        Directory.CreateDirectory(homeRoot);
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(fs, pointer, s_json);
            fs.Flush(flushToDisk: true);
        }
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>The folder the data lives in according to the pointer (the home root without one).</summary>
    public static string ConfiguredRoot(string homeRoot)
    {
        var configured = ReadPointer(homeRoot)?.DataRoot;
        return configured is null ? homeRoot : Normalize(configured);
    }

    // ── Folder checks ─────────────────────────────────────────

    public enum FolderKind
    {
        /// <summary>Does not exist.</summary>
        Missing,
        /// <summary>Exists with nothing in it (reserved names aside).</summary>
        Empty,
        /// <summary>A complete Noctis data folder.</summary>
        NoctisData,
        /// <summary>A move that never finished copied into it: ours to overwrite.</summary>
        LeftoverMove,
        /// <summary>Holds other things.</summary>
        Other,
    }

    public static FolderKind Classify(string dir)
    {
        if (!Directory.Exists(dir)) return FolderKind.Missing;
        if (File.Exists(Path.Combine(dir, IncompleteFileName))) return FolderKind.LeftoverMove;
        if (File.Exists(Path.Combine(dir, MarkerFileName))
            || File.Exists(Path.Combine(dir, "settings.json"))
            || File.Exists(Path.Combine(dir, "library.db"))
            || File.Exists(Path.Combine(dir, "library.json")))
            return FolderKind.NoctisData;
        foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
        {
            if (!IsReservedRootEntry(Path.GetFileName(entry)))
                return FolderKind.Other;
        }
        return FolderKind.Empty;
    }

    public static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim())));

    private static StringComparison PathComparison =>
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    public static bool SamePath(string a, string b) =>
        string.Equals(Normalize(a), Normalize(b), PathComparison);

    /// <summary>True when <paramref name="path"/> is strictly inside <paramref name="folder"/>.</summary>
    public static bool IsInside(string path, string folder)
    {
        var p = Normalize(path);
        var f = Normalize(folder);
        return p.Length > f.Length
               && p.StartsWith(f, PathComparison)
               && (p[f.Length] == Path.DirectorySeparatorChar || p[f.Length] == Path.AltDirectorySeparatorChar
                   || f.EndsWith(Path.DirectorySeparatorChar));
    }

    // ── Planning a change ─────────────────────────────────────

    public enum PlanKind
    {
        /// <summary>Copy the data to <see cref="MovePlan.Target"/>.</summary>
        Move,
        /// <summary>The target already holds Noctis data: switch to it, copy nothing.</summary>
        UseExisting,
        AlreadyHere,
        InsideDataFolder,
        NotEmpty,
        NotWritable,
    }

    public sealed record MovePlan(PlanKind Kind, string Target);

    /// <summary>
    /// What choosing <paramref name="picked"/> means. An empty folder takes the data; a folder
    /// that already holds Noctis data (copied from another computer, or used before) is offered
    /// as-is; any other folder gets a "Noctis" folder inside it, so picking a drive root or
    /// Documents works without spilling files into it.
    /// </summary>
    public static MovePlan PlanChange(string currentRoot, string picked, string homeRoot)
    {
        var target = Normalize(picked);
        var kind = Classify(target);
        if (kind == FolderKind.Other && !SamePath(target, currentRoot))
        {
            target = Path.Combine(target, "Noctis");
            kind = Classify(target);
        }

        if (SamePath(target, currentRoot))
            return new(PlanKind.AlreadyHere, target);
        // Inside the current folder it would copy into itself; inside the home root it would
        // sit in the folder a later move cleans out. (The home root itself is fine.)
        if (IsInside(target, currentRoot) || IsInside(currentRoot, target)
            || (IsInside(target, homeRoot) && !SamePath(target, homeRoot)))
            return new(PlanKind.InsideDataFolder, target);

        switch (kind)
        {
            case FolderKind.NoctisData:
                return new(PlanKind.UseExisting, target);
            case FolderKind.Other:
                return new(PlanKind.NotEmpty, target);
        }

        return CanWrite(target) ? new(PlanKind.Move, target) : new(PlanKind.NotWritable, target);
    }

    private static bool CanWrite(string dir)
    {
        var existed = Directory.Exists(dir);
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, ".noctis-write-test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (!existed)
            {
                try { Directory.Delete(dir); } catch { /* not empty or gone: leave it */ }
            }
        }
    }

    /// <summary>Bytes the data in <paramref name="root"/> takes (reserved entries aside).</summary>
    public static long MeasureData(string root) =>
        EnumerateDataFiles(root).Sum(f => f.Info.Length);

    /// <summary>Free space on the volume holding <paramref name="dir"/>; null when unknown
    /// (network share, or no matching volume).</summary>
    public static long? FreeSpace(string dir)
    {
        try
        {
            // The volume whose mount point is the longest prefix: on Linux/macOS a USB drive
            // mounted under /media or /Volumes is its own volume, not "/".
            DriveInfo? best = null;
            var bestLength = -1;
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (!d.IsReady) continue;
                    var mount = d.RootDirectory.FullName;
                    if ((SamePath(dir, mount) || IsInside(dir, mount)) && mount.Length > bestLength)
                    {
                        best = d;
                        bestLength = mount.Length;
                    }
                }
                catch { /* a volume that can't answer is not the one */ }
            }
            return best?.AvailableFreeSpace;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Headroom asked for on top of the data itself (the library keeps growing while it copies).</summary>
    public const long SpaceMargin = 256L * 1024 * 1024;

    // ── Moving ────────────────────────────────────────────────

    /// <summary>
    /// Step 1, in the running app: copies the data to <paramref name="target"/> (marked
    /// incomplete), then records the move as pending. Files the running app holds locked are
    /// skipped here; the launch that finishes the move copies them with nothing running.
    /// On failure or cancellation the copies are removed and nothing is pending.
    /// </summary>
    public static void CopyForMove(string homeRoot, string currentRoot, string target,
        IProgress<(long Done, long Total)>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, IncompleteFileName),
            "Noctis is still copying its data here. This folder is not in use yet.");
        try
        {
            WriteManifest(target, SyncTree(currentRoot, target, bestEffort: true, progress, ct));
            var pointer = ReadPointer(homeRoot) ?? new Pointer();
            pointer.PendingMoveTo = Normalize(target);
            pointer.PendingMoveFrom = Normalize(currentRoot);
            WritePointer(homeRoot, pointer);
        }
        catch
        {
            RemoveCopies(target, currentRoot);
            throw;
        }
    }

    /// <summary>Switches to a folder that already holds Noctis data; applies at the next launch.</summary>
    public static void UseExisting(string homeRoot, string target)
    {
        try { File.WriteAllText(Path.Combine(target, MarkerFileName), MarkerText); }
        catch { /* settings.json / library.db already identify it */ }
        var pointer = ReadPointer(homeRoot) ?? new Pointer();
        pointer.DataRoot = SamePath(target, homeRoot) ? null : Normalize(target);
        pointer.PendingMoveTo = null;
        pointer.PendingMoveFrom = null;
        WritePointer(homeRoot, pointer);
    }

    /// <summary>Drops a pending move that has not run yet (its copy is removed).</summary>
    public static void CancelPendingMove(string homeRoot, string currentRoot)
    {
        var pointer = ReadPointer(homeRoot);
        if (pointer?.PendingMoveTo is not { } pending) return;
        pointer.PendingMoveTo = null;
        pointer.PendingMoveFrom = null;
        WritePointer(homeRoot, pointer);
        try { RemoveCopies(pending, currentRoot); } catch { /* best effort */ }
    }

    private const string MarkerText = "This folder holds Noctis's data (library, artwork, settings). Noctis finds it through its settings; don't delete it while Noctis uses it.";

    public enum StartupStatus
    {
        Ok,
        /// <summary>The pointer names a folder that is not there (drive unplugged, share offline).</summary>
        Unavailable,
    }

    public sealed record StartupResult(StartupStatus Status, string ConfiguredRoot, string? MovedFrom, string? MoveError);

    /// <summary>What the launch found and did; Settings reports it.</summary>
    public static StartupResult? LastStartup { get; set; }

    /// <summary>Set when this session runs on the home root because the configured folder was unreachable.</summary>
    public static string? SessionFallbackFrom { get; set; }

    /// <summary>
    /// Step 2, at launch, before anything touches the data folder: finishes a pending move
    /// (only the instance that owns the single-instance guard may), then checks the folder
    /// the pointer names is really there.
    /// </summary>
    public static StartupResult PrepareAtStartup(string homeRoot, bool finishPendingMove = true)
    {
        string? movedFrom = null, moveError = null;
        var pointer = ReadPointer(homeRoot);
        if (finishPendingMove && pointer?.PendingMoveTo is { } to)
        {
            var from = pointer.PendingMoveFrom is { } f ? Normalize(f)
                : pointer.DataRoot is null ? homeRoot : Normalize(pointer.DataRoot);
            try
            {
                if (!SamePath(from, to))
                {
                    FinishMove(homeRoot, pointer, from, Normalize(to));
                    movedFrom = from;
                }
                else
                {
                    pointer.PendingMoveTo = null;
                    pointer.PendingMoveFrom = null;
                    WritePointer(homeRoot, pointer);
                }
            }
            catch (Exception ex)
            {
                moveError = ex.Message;
                try
                {
                    pointer = ReadPointer(homeRoot) ?? new Pointer();
                    if (pointer.PendingMoveTo is not null)
                    {
                        pointer.PendingMoveTo = null;
                        pointer.PendingMoveFrom = null;
                        WritePointer(homeRoot, pointer);
                        RemoveCopies(Normalize(to), from);
                    }
                }
                catch { /* the old folder is still the one in use either way */ }
            }
        }

        var configured = ConfiguredRoot(homeRoot);
        var status = !SamePath(configured, homeRoot) && !IsNoctisData(configured)
            ? StartupStatus.Unavailable
            : StartupStatus.Ok;
        return LastStartup = new StartupResult(status, configured, movedFrom, moveError);
    }

    private static bool IsNoctisData(string dir)
    {
        try { return Classify(dir) == FolderKind.NoctisData; }
        catch { return false; } // access denied, share gone mid-check: not usable either way
    }

    private static void FinishMove(string homeRoot, Pointer pointer, string from, string to)
    {
        if (!Directory.Exists(from))
            throw new IOException($"The current data folder '{from}' is missing.");
        if (!Directory.Exists(to))
            throw new IOException($"'{to}' can't be reached.");
        // Only into the copy step 1 made (or one this launch already completed and crashed
        // before switching to): never over a folder that gained other data meanwhile.
        if (!File.Exists(Path.Combine(to, IncompleteFileName)) && !File.Exists(Path.Combine(to, MarkerFileName)))
            throw new IOException($"'{to}' no longer holds the copy Noctis made.");

        SyncTree(from, to, bestEffort: false, progress: null, CancellationToken.None, ReadManifest(to));
        Verify(from, to);

        File.WriteAllText(Path.Combine(to, MarkerFileName), MarkerText);
        File.Delete(Path.Combine(to, IncompleteFileName));

        // The switch. Everything before it left the old folder untouched.
        pointer.DataRoot = SamePath(to, homeRoot) ? null : to;
        pointer.PendingMoveTo = null;
        pointer.PendingMoveFrom = null;
        WritePointer(homeRoot, pointer);

        // Move the old copy aside at once (a rename, instant on one volume) so it can never be
        // mistaken for live data, then let CleanupOldData delete it.
        try
        {
            var aside = MoveAside(from);
            pointer.OldDataToDelete = aside;
            pointer.OldRootToRemove = SamePath(from, homeRoot) ? null : from;
            // The home root too: a marker left there (from an earlier move back to it) made the
            // emptied folder read as Noctis data, so Use Default offered it as-is.
            try { File.Delete(Path.Combine(from, MarkerFileName)); } catch { }
            WritePointer(homeRoot, pointer);
        }
        catch (Exception ex)
        {
            // The move itself succeeded; an old copy that could not be set aside just stays.
            System.Diagnostics.Debug.WriteLine($"[DataLocation] old data left in place: {ex.Message}");
        }
    }

    /// <summary>Moves every data entry of <paramref name="root"/> into one aside folder inside it.</summary>
    private static string MoveAside(string root)
    {
        var aside = Path.Combine(root, OldDataPrefix + "-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss"));
        Directory.CreateDirectory(aside);
        foreach (var entry in Directory.EnumerateFileSystemEntries(root).ToList())
        {
            var name = Path.GetFileName(entry);
            if (IsReservedRootEntry(name)) continue;
            var dest = Path.Combine(aside, name);
            if (Directory.Exists(entry) && !File.GetAttributes(entry).HasFlag(FileAttributes.ReparsePoint))
                Directory.Move(entry, dest);
            else
                File.Move(entry, dest);
        }
        return aside;
    }

    /// <summary>Step 3, in the background after launch: deletes the old copy a move set aside.</summary>
    public static void CleanupOldData(string homeRoot, string currentRoot)
    {
        var pointer = ReadPointer(homeRoot);
        if (pointer?.OldDataToDelete is not { } aside) return;
        // Never the live folder, whatever the pointer says.
        if (SamePath(aside, currentRoot) || IsInside(currentRoot, aside)
            || !Path.GetFileName(Path.TrimEndingDirectorySeparator(aside)).StartsWith(OldDataPrefix, StringComparison.OrdinalIgnoreCase))
        {
            pointer.OldDataToDelete = null;
            pointer.OldRootToRemove = null;
            WritePointer(homeRoot, pointer);
            return;
        }

        try
        {
            if (Directory.Exists(aside)) Directory.Delete(aside, recursive: true);
            if (pointer.OldRootToRemove is { } oldRoot && !SamePath(oldRoot, currentRoot) && Directory.Exists(oldRoot)
                && !Directory.EnumerateFileSystemEntries(oldRoot).Any())
                Directory.Delete(oldRoot);
        }
        catch (Exception ex)
        {
            // Retried at the next launch.
            System.Diagnostics.Debug.WriteLine($"[DataLocation] old data cleanup: {ex.Message}");
            return;
        }

        pointer = ReadPointer(homeRoot) ?? new Pointer();
        pointer.OldDataToDelete = null;
        pointer.OldRootToRemove = null;
        WritePointer(homeRoot, pointer);
    }

    // ── Copying ───────────────────────────────────────────────

    private readonly record struct DataFile(string Relative, FileInfo Info);

    private static StringComparer PathComparer =>
        OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    /// <summary>Relative paths of every data folder under <paramref name="root"/>.</summary>
    private static IEnumerable<string> EnumerateDataFolders(string root)
    {
        var stack = new Stack<(string Dir, string Rel)>();
        stack.Push((root, ""));
        while (stack.Count > 0)
        {
            var (dir, rel) = stack.Pop();
            foreach (var sub in new DirectoryInfo(dir).EnumerateDirectories())
            {
                if (rel.Length == 0 && IsReservedRootEntry(sub.Name)) continue;
                if (sub.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                var subRel = rel.Length == 0 ? sub.Name : Path.Combine(rel, sub.Name);
                yield return subRel;
                stack.Push((sub.FullName, subRel));
            }
        }
    }

    /// <summary>Every data file under <paramref name="root"/>, reserved top-level entries and
    /// symlinked folders aside.</summary>
    private static IEnumerable<DataFile> EnumerateDataFiles(string root)
    {
        var stack = new Stack<(string Dir, string Rel)>();
        stack.Push((root, ""));
        while (stack.Count > 0)
        {
            var (dir, rel) = stack.Pop();
            var info = new DirectoryInfo(dir);
            if (!info.Exists) continue;
            foreach (var entry in info.EnumerateFileSystemInfos())
            {
                if (rel.Length == 0 && IsReservedRootEntry(entry.Name)) continue;
                var entryRel = rel.Length == 0 ? entry.Name : Path.Combine(rel, entry.Name);
                if (entry is DirectoryInfo sub)
                {
                    if (!sub.Attributes.HasFlag(FileAttributes.ReparsePoint))
                        stack.Push((sub.FullName, entryRel));
                }
                else if (entry is FileInfo file)
                {
                    yield return new DataFile(entryRel, file);
                }
            }
        }
    }

    /// <summary>
    /// Makes <paramref name="target"/> a mirror of <paramref name="source"/>: copies files whose
    /// size or time differ (the copy carries the source's time), deletes target files the source
    /// no longer has (a stale library.db-wal next to a newer library.db would corrupt it).
    /// <paramref name="bestEffort"/> skips source files that cannot be opened (held by the
    /// running app); errors writing the target always stop the copy (a full disk).
    /// </summary>
    internal static Dictionary<string, (long Length, long Ticks)> SyncTree(string source, string target, bool bestEffort,
        IProgress<(long Done, long Total)>? progress, CancellationToken ct,
        IReadOnlyDictionary<string, (long Length, long Ticks)>? copiedBefore = null)
    {
        var files = EnumerateDataFiles(source).ToList();
        var todo = new List<DataFile>();
        // What the target now holds a faithful copy of: the source file's size and time as
        // listed BEFORE it was copied, so a write during or after the copy reads as a change.
        var manifest = new Dictionary<string, (long Length, long Ticks)>(PathComparer);
        foreach (var f in files)
        {
            var state = (f.Info.Length, f.Info.LastWriteTimeUtc.Ticks);
            var dest = new FileInfo(Path.Combine(target, f.Relative));
            if (!dest.Exists || dest.Length != f.Info.Length) { todo.Add(f); continue; }
            // The copy made while the app ran recorded each source file it copied: unchanged
            // since means the copy is good, whatever time the target's file system kept.
            if (copiedBefore is not null && copiedBefore.TryGetValue(f.Relative, out var before))
            {
                if (before == state) manifest[f.Relative] = state;
                else todo.Add(f);
                continue;
            }
            var drift = (dest.LastWriteTimeUtc - f.Info.LastWriteTimeUtc).Duration();
            if (drift == TimeSpan.Zero) { manifest[f.Relative] = state; continue; }
            if (drift > TimeSpan.FromSeconds(2)) { todo.Add(f); continue; }
            // Within 2 s: a FAT32/exFAT target rounds the copied time, or the file was saved
            // again right after it was copied, with the same size (queue-position.json does
            // that every few seconds). The copy while running lets it pass; the final pass,
            // the one the switch relies on, compares the bytes.
            if (!bestEffort && !SameContent(f.Info.FullName, dest.FullName))
            {
                todo.Add(f);
                continue;
            }
            if (!bestEffort) manifest[f.Relative] = state;
        }

        var total = todo.Sum(f => f.Info.Length);
        long done = 0;
        progress?.Report((0, total));
        var buffer = new byte[1024 * 1024];
        foreach (var f in todo)
        {
            ct.ThrowIfCancellationRequested();
            var dest = Path.Combine(target, f.Relative);
            FileStream src;
            try
            {
                src = new FileStream(f.Info.FullName, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                continue; // gone since the listing: the mirror below drops its copy
            }
            catch (Exception) when (bestEffort)
            {
                done += f.Info.Length; // held open by the running app: the launch copies it
                continue;
            }

            using (src)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                using (var dst = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    int read;
                    while ((read = src.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        dst.Write(buffer, 0, read);
                        done += read;
                        progress?.Report((Math.Min(done, total), total));
                    }
                }
            }
            File.SetLastWriteTimeUtc(dest, f.Info.LastWriteTimeUtc);
            manifest[f.Relative] = (f.Info.Length, f.Info.LastWriteTimeUtc.Ticks);
        }

        // Empty folders too: some services create theirs once at startup and then assume them.
        foreach (var dir in EnumerateDataFolders(source))
            Directory.CreateDirectory(Path.Combine(target, dir));

        // Mirror: drop what the source no longer has.
        var keep = new HashSet<string>(files.Select(f => f.Relative), PathComparer);
        foreach (var f in EnumerateDataFiles(target).ToList())
        {
            if (!keep.Contains(f.Relative))
                f.Info.Delete();
        }
        progress?.Report((total, total));
        return manifest;
    }

    /// <summary>The incomplete flag doubles as the record of what the copy while running copied.</summary>
    private static void WriteManifest(string target, Dictionary<string, (long Length, long Ticks)> manifest)
    {
        var path = Path.Combine(target, IncompleteFileName);
        using var w = new StreamWriter(path, append: false, new System.Text.UTF8Encoding(false));
        w.Write("# Noctis is still copying its data here. This folder is not in use yet.\n");
        foreach (var (rel, (length, ticks)) in manifest)
            w.Write($"{ticks}\t{length}\t{rel.Replace('\\', '/')}\n");
    }

    private static Dictionary<string, (long Length, long Ticks)>? ReadManifest(string target)
    {
        try
        {
            var path = Path.Combine(target, IncompleteFileName);
            if (!File.Exists(path)) return null;
            var manifest = new Dictionary<string, (long Length, long Ticks)>(PathComparer);
            foreach (var line in File.ReadLines(path))
            {
                if (line.Length == 0 || line[0] == '#') continue;
                var parts = line.Split('\t', 3);
                if (parts.Length == 3 && long.TryParse(parts[0], out var ticks) && long.TryParse(parts[1], out var length))
                    manifest[parts[2].Replace('/', Path.DirectorySeparatorChar)] = (length, ticks);
            }
            return manifest;
        }
        catch
        {
            return null; // compared the slow way instead
        }
    }

    private static bool SameContent(string a, string b)
    {
        using var fa = new FileStream(a, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var fb = new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (fa.Length != fb.Length) return false;
        var ba = new byte[81920];
        var bb = new byte[81920];
        int read;
        while ((read = fa.Read(ba, 0, ba.Length)) > 0)
        {
            fb.ReadExactly(bb, 0, read);
            if (!ba.AsSpan(0, read).SequenceEqual(bb.AsSpan(0, read))) return false;
        }
        return true;
    }

    /// <summary>Throws unless every source file is in the target with the same size.</summary>
    internal static void Verify(string source, string target)
    {
        foreach (var f in EnumerateDataFiles(source))
        {
            var dest = new FileInfo(Path.Combine(target, f.Relative));
            if (!dest.Exists || dest.Length != f.Info.Length)
                throw new IOException($"'{f.Relative}' did not copy correctly.");
        }
    }

    /// <summary>Removes what a failed move copied into <paramref name="copy"/>: only files that
    /// exist in <paramref name="source"/>, plus the incomplete flag and folders left empty.</summary>
    internal static void RemoveCopies(string copy, string source)
    {
        if (!Directory.Exists(copy)) return;
        var inSource = new HashSet<string>(EnumerateDataFiles(source).Select(f => f.Relative), PathComparer);
        foreach (var f in EnumerateDataFiles(copy).ToList())
        {
            if (inSource.Contains(f.Relative))
            {
                try { f.Info.Delete(); } catch { }
            }
        }
        try { File.Delete(Path.Combine(copy, IncompleteFileName)); } catch { }
        // A marker left behind would offer this emptied folder as Noctis data later.
        try { File.Delete(Path.Combine(copy, MarkerFileName)); } catch { }
        RemoveEmptyFolders(copy, isRoot: true);
        try
        {
            if (!Directory.EnumerateFileSystemEntries(copy).Any()) Directory.Delete(copy);
        }
        catch { }
    }

    private static void RemoveEmptyFolders(string dir, bool isRoot)
    {
        foreach (var sub in Directory.EnumerateDirectories(dir).ToList())
        {
            var name = Path.GetFileName(sub);
            if (isRoot && IsReservedRootEntry(name)) continue;
            if (File.GetAttributes(sub).HasFlag(FileAttributes.ReparsePoint)) continue;
            RemoveEmptyFolders(sub, isRoot: false);
            try
            {
                if (!Directory.EnumerateFileSystemEntries(sub).Any()) Directory.Delete(sub);
            }
            catch { }
        }
    }

    // ── Paths saved inside the data ───────────────────────────

    /// <summary>
    /// A few settings and playlists store full paths to files in the data folder (avatar,
    /// lyrics background, playlist covers). After a move, or with the folder copied from
    /// another computer, those point at the old place: when the stored file is gone but the
    /// same file name sits in <paramref name="subfolder"/> of the current data folder, that one
    /// is meant. Paths saved on another OS split on either separator.
    /// </summary>
    public static string? Rebase(string? stored, string dataRoot, string subfolder)
    {
        if (string.IsNullOrWhiteSpace(stored)) return stored;
        try
        {
            if (File.Exists(stored)) return stored;
            var name = stored.Split('/', '\\').LastOrDefault(s => s.Length > 0);
            if (string.IsNullOrEmpty(name)) return stored;
            var candidate = Path.Combine(dataRoot, subfolder, name);
            return File.Exists(candidate) ? candidate : stored;
        }
        catch
        {
            return stored;
        }
    }
}
