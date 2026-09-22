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

            var children = ListChildren(uri, parentId);
            var self = children.FirstOrDefault(c => c.Id == docId);
            if (self.Name == null) return null;

            if (SidecarNames.Match(self.Name, children.Select(c => c.Name!), extensions) is not { } hit) return null;
            var sidecarId = children.First(c => c.Name == hit.Name).Id!;

            using var stream = AndroidFileSystemSource.OpenSeekable(_resolver, DocumentsContract.BuildDocumentUriUsingTree(uri, sidecarId)!);
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return new SidecarFile(hit.Extension, buffer.ToArray());
        }
        catch (Exception ex)
        {
            DebugLog.Write("Lyrics", $"Sidecar lookup failed: {ex.GetType().Name}: {ex.Message}");
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

    /// <summary>The folder holding the document: FindDocumentPath's path runs tree top →
    /// document, so the folder is the second-to-last id. Providers that do not implement it
    /// throw; the external-storage provider's ids are "&lt;root&gt;:&lt;relative path&gt;", so
    /// there the folder is the id up to its last '/'.</summary>
    private string? ParentDocumentId(AUri uri, string docId)
    {
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
