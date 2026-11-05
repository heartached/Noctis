using Android.Content;
using Android.Database;
using Android.OS;
using Android.Provider;
using AndroidX.DocumentFile.Provider;
using Microsoft.Win32.SafeHandles;
using Noctis.Mobile.Services;
using Noctis.Services;
using AUri = Android.Net.Uri;

namespace Noctis.Android.Services;

/// <summary>
/// IFileSystemSource over a Storage Access Framework tree. Roots are persisted tree URIs
/// (AppSettings.MusicFolders); entries are document URIs, which become Track.FilePath and
/// are what ExoPlayer plays. Directories are listed with one DocumentsContract query each
/// (DocumentFile.ListFiles would issue one query per child). Folder rules (excludedRoots)
/// are a desktop feature and are ignored here.
/// </summary>
public sealed class AndroidFileSystemSource : IFileSystemSource
{
    private static readonly string[] Projection =
    {
        DocumentsContract.Document.ColumnDocumentId,
        DocumentsContract.Document.ColumnDisplayName,
        DocumentsContract.Document.ColumnMimeType,
        DocumentsContract.Document.ColumnSize,
        DocumentsContract.Document.ColumnLastModified,
    };

    private readonly Context _context;
    private readonly ContentResolver _resolver;

    public AndroidFileSystemSource(Context context)
    {
        _context = context;
        _resolver = context.ContentResolver ?? throw new InvalidOperationException("No ContentResolver");
    }

    public bool RootExists(string root)
    {
        // The phone's music index: reachable while the permission is held. Taken away, the root
        // reads as out of reach and its songs are kept as they were (LibraryService).
        if (DeviceMusicScan.IsDeviceMusicPath(root)) return AndroidDeviceMusic.HasPermissionIn(_context);
        try
        {
            var tree = DocumentFile.FromTreeUri(_context, AUri.Parse(root)!);
            return tree != null && tree.Exists() && tree.CanRead();
        }
        catch (Exception ex)
        {
            // A revoked grant surfaces as SecurityException; "unavailable" is the right answer.
            DebugLog.Write("Library", $"SAF root check failed for {root}: {ex.Message}");
            return false;
        }
    }

    public IEnumerable<ScanEntry> EnumerateAudioFiles(
        string root,
        IReadOnlyCollection<string> excludedRoots,
        IReadOnlySet<string> ignoredFolderNames,
        Action<string> reportFailedDirectory)
    {
        if (DeviceMusicScan.IsDeviceMusicPath(root))
            return EnumerateDeviceMusic(reportFailedDirectory);
        return EnumerateTree(root, ignoredFolderNames, reportFailedDirectory);
    }

    /// <summary>
    /// Find All Music Automatically: every song in MediaStore.Audio, by content URI. Rows are
    /// read whole first (one query), then <see cref="DeviceMusicScan.Select"/> decides which are
    /// songs and leaves those inside a picked folder to that folder. A rescan sees changes the
    /// way it does for folders: a new id is a new song, a vanished id a deleted one, and a
    /// changed size or date_modified a re-read. A failed query reports the whole root failed,
    /// which keeps its known songs rather than dropping them.
    /// </summary>
    private IEnumerable<ScanEntry> EnumerateDeviceMusic(Action<string> reportFailedDirectory)
    {
        var q = Build.VERSION.SdkInt >= BuildVersionCodes.Q;
        var columns = new List<string>
        {
            "_id", "_display_name", "_size", "date_modified", "duration",
            "is_music", "is_ringtone", "is_notification", "is_alarm", "is_podcast", "_data",
        };
        if (q) columns.AddRange(new[] { "volume_name", "relative_path" });
        if (Build.VERSION.SdkInt >= BuildVersionCodes.S) columns.Add("is_recording");

        var rows = new List<DeviceMusicRow>();
        ICursor? cursor = null;
        try
        {
            cursor = _resolver.Query(MediaStore.Audio.Media.ExternalContentUri!, columns.ToArray(), "is_music != 0", null, null);
            if (cursor == null)
            {
                reportFailedDirectory(DeviceMusicScan.Root);
                return Array.Empty<ScanEntry>();
            }
            int Col(string name) => cursor.GetColumnIndex(name);
            var id = cursor.GetColumnIndexOrThrow("_id");
            var name = Col("_display_name");
            var size = Col("_size");
            var modified = Col("date_modified");
            var duration = Col("duration");
            var music = Col("is_music");
            var ringtone = Col("is_ringtone");
            var notification = Col("is_notification");
            var alarm = Col("is_alarm");
            var podcast = Col("is_podcast");
            var recording = Col("is_recording");
            var volume = Col("volume_name");
            var relative = Col("relative_path");
            var data = Col("_data");
            string? Text(int i) => i >= 0 && !cursor.IsNull(i) ? cursor.GetString(i) : null;
            long Number(int i) => i >= 0 && !cursor.IsNull(i) ? cursor.GetLong(i) : 0;
            bool Flag(int i) => Number(i) != 0;
            while (cursor.MoveToNext())
            {
                var file = Text(data);
                var displayName = Text(name) ?? (file != null ? Path.GetFileName(file) : string.Empty);
                rows.Add(new DeviceMusicRow(
                    cursor.GetLong(id), displayName, Number(size), Number(modified), Number(duration),
                    music < 0 || Flag(music), Flag(ringtone), Flag(notification), Flag(alarm), Flag(podcast), Flag(recording),
                    Text(volume), Text(relative), file));
            }
        }
        catch (Exception ex)
        {
            DebugLog.Write("Library", $"Music index query failed: {ex.GetType().Name}: {ex.Message}");
            reportFailedDirectory(DeviceMusicScan.Root);
            return Array.Empty<ScanEntry>();
        }
        finally
        {
            // Close() as well as Dispose(): see EnumerateTree.
            try
            {
                cursor?.Close();
            }
            catch (Exception ex)
            {
                DebugLog.Write("Library", $"Music index cursor close failed: {ex.Message}");
            }
            cursor?.Dispose();
        }

        var folders = AndroidDeviceMusic.Current?.Folders ?? Array.Empty<string>();
        var items = DeviceMusicScan.Select(rows, folders.ToList()).ToList();
        DebugLog.Write("Library", $"Music index: {rows.Count} row(s), {items.Count} song(s) taken ({folders.Count} folder(s) to leave out)");
        return items.Select(item =>
        {
            var uri = AUri.Parse(item.Path)!;
            return new ScanEntry(item.Path, item.Name, item.Size, item.ModifiedUtc, LocalPath: null, OpenRead: () => OpenSeekable(_resolver, uri));
        });
    }

    private IEnumerable<ScanEntry> EnumerateTree(string root, IReadOnlySet<string> ignoredFolderNames, Action<string> reportFailedDirectory)
    {
        var treeUri = AUri.Parse(root)!;
        var stack = new Stack<string>();
        // Cycle guard keyed on document id: mirrors LocalFileSystemSource's resolved-path
        // "visited" set. A provider that reports a directory as its own descendant (or a
        // malformed tree) would otherwise loop the DFS forever, re-yielding the same files
        // with the track list growing unbounded.
        var visited = new HashSet<string>(StringComparer.Ordinal);
        stack.Push(DocumentsContract.GetTreeDocumentId(treeUri)!);

        while (stack.Count > 0)
        {
            var docId = stack.Pop();
            if (!visited.Add(docId)) continue;

            var childrenUri = DocumentsContract.BuildChildDocumentsUriUsingTree(treeUri, docId)!;
            // Reported on failure as the directory's DOCUMENT uri: every track under it has a
            // document uri starting with this one, which is how LibraryService.PathIsUnder
            // keeps those known tracks instead of dropping them.
            var directoryUri = DocumentsContract.BuildDocumentUriUsingTree(treeUri, docId)!.ToString()!;

            // Materialize the listing first: a yield inside try/catch is not allowed, and a
            // cursor error mid-listing must be reported, not thrown into the scan.
            var rows = new List<(string Id, string Name, string Mime, long Size, long ModifiedMs)>();
            ICursor? cursor = null;
            try
            {
                cursor = _resolver.Query(childrenUri, Projection, null, null, null);
                if (cursor == null)
                {
                    reportFailedDirectory(directoryUri);
                    continue;
                }

                // GetColumnIndexOrThrow, not positional Projection indices: AOSP providers
                // honour the requested projection, but a provider that ignores it and returns
                // its own default columns would otherwise hand back garbage document ids
                // (unplayable tracks) and zero sizes with no error. A missing column throws
                // here and is caught below, turning it into a reported (skipped) directory
                // instead of silently corrupt entries.
                var idxId = cursor.GetColumnIndexOrThrow(DocumentsContract.Document.ColumnDocumentId);
                var idxName = cursor.GetColumnIndexOrThrow(DocumentsContract.Document.ColumnDisplayName);
                var idxMime = cursor.GetColumnIndexOrThrow(DocumentsContract.Document.ColumnMimeType);
                var idxSize = cursor.GetColumnIndexOrThrow(DocumentsContract.Document.ColumnSize);
                var idxModified = cursor.GetColumnIndexOrThrow(DocumentsContract.Document.ColumnLastModified);
                while (cursor.MoveToNext())
                {
                    rows.Add((
                        cursor.GetString(idxId) ?? string.Empty,
                        cursor.GetString(idxName) ?? string.Empty,
                        cursor.GetString(idxMime) ?? string.Empty,
                        cursor.IsNull(idxSize) ? 0 : cursor.GetLong(idxSize),
                        cursor.IsNull(idxModified) ? 0 : cursor.GetLong(idxModified)));
                }
            }
            catch (Exception ex)
            {
                DebugLog.Write("Library", $"SAF listing failed for {childrenUri}: {ex.Message}");
                // Document uri, not childrenUri: every track under this directory has a
                // document uri starting with directoryUri, which is what
                // LibraryService.PathIsUnder matches to keep those known tracks.
                reportFailedDirectory(directoryUri);
                continue;
            }
            finally
            {
                // Close(), not just Dispose(): ICursor is a Java interface binding, and
                // Dispose() only releases the JNI peer reference — it does not call the
                // Java-side Cursor.close(). Without an explicit Close() the remote provider's
                // CursorWindow (ashmem) stays pinned until the Java GC happens to finalize it,
                // which .NET's Dispose never drives; a few hundred un-closed directory cursors
                // is enough to exhaust CursorWindow memory mid-scan.
                //
                // Close() itself is wrapped so a throwing Close() (a misbehaving provider)
                // cannot skip Dispose() and cannot escape this iterator past
                // Partitioner/Parallel.ForEach and kill the whole root's scan — the same
                // containment shape as every other failure in this method.
                try
                {
                    cursor?.Close();
                }
                catch (Exception ex)
                {
                    DebugLog.Write("Library", $"SAF cursor close failed for {childrenUri}: {ex.Message}");
                }
                cursor?.Dispose();
            }

            // Logged/reported at most once per directory: a provider misreporting
            // COLUMN_LAST_MODIFIED (microseconds instead of milliseconds is the classic case)
            // fails this the same way for every file under the directory, and 500 identical
            // log lines would tell the reader nothing that one didn't.
            var timestampFailureReported = false;

            foreach (var row in rows)
            {
                if (row.Id.Length == 0) continue;
                if (row.Mime == DocumentsContract.Document.MimeTypeDir)
                {
                    if (ignoredFolderNames.Contains(row.Name.ToLowerInvariant())) continue;
                    stack.Push(row.Id);
                    continue;
                }
                if (!MetadataService.SupportedExtensions.Contains(Path.GetExtension(row.Name))) continue;
                if (!TryConvertModified(row.ModifiedMs, out var modified))
                {
                    // A silent per-file skip here is wrong: if a provider is systematically
                    // out of range (the microseconds case), every file under this directory
                    // fails the same way, the scan "succeeds" having yielded nothing, and
                    // LibraryService then wipes every known track under this root — ratings,
                    // favourites, play counts, DateAdded included — with nothing in the log to
                    // explain it. Reporting the directory failed routes it through
                    // LibraryService.SelectTracksUnderFailedDirectories instead (the same path
                    // the GetColumnIndexOrThrow failure above uses), which keeps the known
                    // tracks under directoryUri rather than dropping them.
                    if (!timestampFailureReported)
                    {
                        DebugLog.Write("Library",
                            $"SAF entry '{row.Name}' under {directoryUri} has an unreadable last-modified value ({row.ModifiedMs}); reporting directory as failed.");
                        reportFailedDirectory(directoryUri);
                        timestampFailureReported = true;
                    }
                    continue;
                }

                var docUri = DocumentsContract.BuildDocumentUriUsingTree(treeUri, row.Id)!;
                var path = docUri.ToString()!;
                yield return new ScanEntry(path, row.Name, row.Size, modified, LocalPath: null, OpenRead: () => OpenSeekable(_resolver, docUri));
            }
        }
    }

    private static bool TryConvertModified(long modifiedMs, out DateTime utc)
    {
        try
        {
            utc = DateTimeOffset.FromUnixTimeMilliseconds(modifiedMs).UtcDateTime;
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            utc = default;
            return false;
        }
    }

    /// <summary>
    /// A seekable read stream over the document's file descriptor. OpenInputStream is
    /// forward-only and TagLib seeks, so the descriptor is detached into a FileStream that
    /// owns and closes it.
    /// </summary>
    internal static Stream OpenSeekable(ContentResolver resolver, AUri uri)
    {
        var pfd = resolver.OpenFileDescriptor(uri, "r") ?? throw new IOException($"Cannot open {uri}");
        var fd = pfd.DetachFd();
        pfd.Dispose();
        return new FileStream(new SafeFileHandle((IntPtr)fd, ownsHandle: true), FileAccess.Read, bufferSize: 1 << 16);
    }
}
