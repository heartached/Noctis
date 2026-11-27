using Android.Content;
using Android.Database;
using Android.Provider;
using Noctis.Services;
using AUri = Android.Net.Uri;

namespace Noctis.Android.Services;

/// <summary>
/// Sidecar and audio access for SAF tracks. A tree document URI has no sibling path to put
/// "song.ttml" beside, so the folder is resolved through the provider, its children are
/// listed, and the sidecar is matched by display name — the desktop's "same folder, same
/// stem" rule (<see cref="SidecarNames"/>). Runs on a worker thread (LyricsLoader); every
/// failure (revoked grant, provider without findDocumentPath, vanished file) is null.
/// </summary>
public sealed class SafTrackFileAccess : ITrackFileAccess
{
    private const string ExternalStorageAuthority = "com.android.externalstorage.documents";

    /// <summary>The largest sidecar read. Real lyric files are kilobytes; the whole file is held
    /// in memory, so a mis-named multi-megabyte file beside a song must not be.</summary>
    internal const int MaxSidecarBytes = 4 * 1024 * 1024;

    // Oversized sidecars already logged: the lookup runs on every track change. Worker threads.
    private static readonly HashSet<string> LoggedOversize = new();

    private static readonly string[] Projection =
    {
        DocumentsContract.Document.ColumnDocumentId,
        DocumentsContract.Document.ColumnDisplayName,
    };

    private readonly ContentResolver _resolver;

    public SafTrackFileAccess(Context context) => _resolver = context.ContentResolver!;

    public SidecarFile? ReadSidecar(string trackPath, IReadOnlyList<string> extensions)
    {
        try
        {
            if (!trackPath.StartsWith("content://", StringComparison.Ordinal)) return null;
            var uri = AUri.Parse(trackPath)!;
            if (!DocumentsContract.IsTreeUri(uri)) return null;

            var docId = DocumentsContract.GetDocumentId(uri);
            if (docId == null || ParentDocumentId(uri, docId) is not { } parentId) return null;

            // The folder's listing as last read (an album's songs share one): a song not in it
            // came after that read, and the folder is listed again.
            var children = Children(uri, parentId, fresh: false);
            var self = children.FirstOrDefault(c => c.Id == docId);
            if (self.Name == null)
            {
                children = Children(uri, parentId, fresh: true);
                self = children.FirstOrDefault(c => c.Id == docId);
            }
            if (self.Name == null) return null;

            if (SidecarNames.Match(self.Name, children.Select(c => c.Name!), extensions) is not { } hit) return null;
            var sidecarId = children.First(c => c.Name == hit.Name).Id!;

            var sidecarUri = DocumentsContract.BuildDocumentUriUsingTree(uri, sidecarId)!;
            using var stream = AndroidFileSystemSource.OpenSeekable(_resolver, sidecarUri);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
            {
                if (buffer.Length + read > MaxSidecarBytes)
                {
                    bool first;
                    lock (LoggedOversize) first = LoggedOversize.Add(sidecarUri.ToString()!);
                    if (first) DebugLog.Write("Lyrics", $"Sidecar {hit.Name} is over {MaxSidecarBytes / (1024 * 1024)} MB; skipped");
                    return null;
                }
                buffer.Write(chunk, 0, read);
            }
            return new SidecarFile(hit.Extension, buffer.ToArray());
        }
        catch (Exception ex)
        {
            DebugLog.Write("Lyrics", $"Sidecar lookup failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    // Folders already looked in for an animated cover (folder document id → the loop's URI, or null):
    // a whole album plays from one folder, and the lookup runs on every track change. Bounded.
    private readonly Dictionary<string, string?> _siblings = new(StringComparer.Ordinal);

    /// <summary>
    /// A document beside the song whose name is one of <paramref name="names"/> (any case), as a
    /// playable content:// URI under the same tree grant: the album's <c>cover.mp4</c> for Now
    /// Playing's animated cover (AnimatedCoverSource). Null for anything but a SAF tree song, or
    /// when there is none. Worker thread.
    /// </summary>
    public string? FindSibling(string trackPath, IReadOnlyList<string> names)
    {
        try
        {
            if (!trackPath.StartsWith("content://", StringComparison.Ordinal)) return null;
            var uri = AUri.Parse(trackPath)!;
            if (!DocumentsContract.IsTreeUri(uri)) return null;
            var docId = DocumentsContract.GetDocumentId(uri);
            if (docId == null || ParentDocumentId(uri, docId) is not { } parentId) return null;
            lock (_siblings)
                if (_siblings.TryGetValue(parentId, out var known)) return known;

            string? found = null;
            var children = Children(uri, parentId, fresh: false);
            foreach (var name in names)
            {
                var hit = children.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
                if (hit.Id is null) continue;
                found = DocumentsContract.BuildDocumentUriUsingTree(uri, hit.Id)!.ToString();
                break;
            }
            lock (_siblings)
            {
                if (_siblings.Count >= 256) _siblings.Clear();
                _siblings[parentId] = found;
            }
            return found;
        }
        catch (Exception ex)
        {
            DebugLog.Write("AnimatedCover", $"Folder lookup failed: {ex.GetType().Name}");
            return null;
        }
    }

    public Stream? OpenAudio(string trackPath)
    {
        try
        {
            return trackPath.StartsWith("content://", StringComparison.Ordinal)
                ? AndroidFileSystemSource.OpenSeekable(_resolver, AUri.Parse(trackPath)!)
                : null;
        }
        catch (Exception ex)
        {
            DebugLog.Write("Lyrics", $"Audio open for SYLT failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    // Folders already listed (authority + folder document id → its children): the sidecar search
    // ran a provider query listing the whole folder on every track change, the same folder for
    // every song of an album (beta 6: lyrics slow to show after a song change). Bounded; dropped
    // by ForgetFolders (a finished rescan). Worker threads.
    private readonly Dictionary<string, List<(string? Id, string? Name)>> _folders = new(StringComparer.Ordinal);
    private const int MaxFolders = 64;

    private List<(string? Id, string? Name)> Children(AUri treeScopedUri, string parentId, bool fresh)
    {
        var key = treeScopedUri.Authority + "|" + parentId;
        if (!fresh)
            lock (_folders)
                if (_folders.TryGetValue(key, out var known)) return known;
        var children = ListChildren(treeScopedUri, parentId);
        lock (_folders)
        {
            if (_folders.Count >= MaxFolders && !_folders.ContainsKey(key)) _folders.Clear();
            _folders[key] = children;
        }
        return children;
    }

    public void ForgetFolders()
    {
        lock (_folders) _folders.Clear();
        lock (_siblings) _siblings.Clear();
    }

    /// <summary>The folder holding the document. The external-storage provider's ids are
    /// "&lt;root&gt;:&lt;relative path&gt;", so there the folder is the id up to its last '/', with
    /// no provider call (FindDocumentPath is one, on every track change). Elsewhere, and for a
    /// document at its root: FindDocumentPath's path runs tree top → document, so the folder is
    /// the second-to-last id (providers that do not implement it throw).</summary>
    private string? ParentDocumentId(AUri uri, string docId)
    {
        if (uri.Authority == ExternalStorageAuthority && docId.LastIndexOf('/') is > 0 and var cut)
            return docId[..cut];
        try
        {
            var path = DocumentsContract.FindDocumentPath(_resolver, uri)?.GetPath();
            if (path is { Count: >= 2 }) return path[path.Count - 2];
        }
        catch (Exception ex)
        {
            DebugLog.Write("Lyrics", $"FindDocumentPath unsupported ({ex.GetType().Name}); using the id fallback");
        }

        if (uri.Authority != ExternalStorageAuthority) return null;
        var slash = docId.LastIndexOf('/');
        return slash > 0 ? docId[..slash] : null;
    }

    private List<(string? Id, string? Name)> ListChildren(AUri treeScopedUri, string parentId)
    {
        var rows = new List<(string? Id, string? Name)>();
        var childrenUri = DocumentsContract.BuildChildDocumentsUriUsingTree(treeScopedUri, parentId)!;
        ICursor? cursor = null;
        try
        {
            cursor = _resolver.Query(childrenUri, Projection, null, null, null);
            if (cursor == null) return rows;

            // By name, not position: a provider that ignores the projection would otherwise
            // hand back the wrong columns silently (same guard as AndroidFileSystemSource).
            var idxId = cursor.GetColumnIndexOrThrow(DocumentsContract.Document.ColumnDocumentId);
            var idxName = cursor.GetColumnIndexOrThrow(DocumentsContract.Document.ColumnDisplayName);
            while (cursor.MoveToNext())
            {
                var id = cursor.GetString(idxId);
                var name = cursor.GetString(idxName);
                if (id != null && name != null) rows.Add((id, name));
            }
            return rows;
        }
        finally
        {
            // Close(), not just Dispose(): Dispose() only releases the JNI peer, so without
            // Close() the provider's CursorWindow (ashmem) stays pinned until the Java GC
            // finalizes it, and this runs on every track change. A throwing Close() must not
            // skip Dispose().
            try
            {
                cursor?.Close();
            }
            catch (Exception ex)
            {
                DebugLog.Write("Lyrics", $"SAF cursor close failed for {childrenUri}: {ex.Message}");
            }
            cursor?.Dispose();
        }
    }
}
