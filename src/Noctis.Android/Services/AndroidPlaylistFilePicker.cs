using Android.Content;
using Android.Provider;
using Noctis.Mobile.Services;
using Noctis.Services;

namespace Noctis.Android.Services;

/// <summary>
/// IPlaylistFilePicker over the activity's ACTION_OPEN_DOCUMENT round-trip (several files at
/// once). Each picked file is read whole while the result's one-time grant lasts; a file that
/// cannot be read is left out (and logged). The MIME list covers what file managers call an
/// .m3u (audio/x-mpegurl, audio/mpegurl, application/vnd.apple.mpegurl…), plus the generic
/// types some report for it; the import itself goes by the file's extension.
/// </summary>
public sealed class AndroidPlaylistFilePicker : IPlaylistFilePicker
{
    // Big enough for any real playlist, small enough that a wrong pick (a video) is not read whole.
    private const long MaxBytes = 16 * 1024 * 1024;

    private static readonly string[] MimeTypes =
    {
        "audio/x-mpegurl", "audio/mpegurl", "application/x-mpegurl", "application/vnd.apple.mpegurl",
        "application/mpegurl", "audio/mpeg-url", "text/plain", "application/octet-stream",
    };

    private readonly Context _context;

    public AndroidPlaylistFilePicker(Context context) => _context = context;

    public async Task<IReadOnlyList<PickedPlaylistFile>> PickPlaylistFilesAsync()
    {
        if (MainActivity.Current is not { } activity) return Array.Empty<PickedPlaylistFile>();
        var uris = await activity.OpenDocumentsAsync(MimeTypes);
        if (uris.Count == 0) return Array.Empty<PickedPlaylistFile>();
        return await Task.Run(() => uris.Select(Read).OfType<PickedPlaylistFile>().ToList());
    }

    private PickedPlaylistFile? Read(global::Android.Net.Uri uri)
    {
        try
        {
            var resolver = _context.ContentResolver!;
            var name = DisplayName(resolver, uri) ?? uri.LastPathSegment ?? "Playlist.m3u";
            using var input = resolver.OpenInputStream(uri);
            if (input == null) return null;
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = input.Read(chunk, 0, chunk.Length)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > MaxBytes) return null;
            }
            return new PickedPlaylistFile(name, buffer.ToArray());
        }
        catch (Exception ex)
        {
            DebugLog.Write("Import", $"Reading a picked playlist file failed: {ex.Message}");
            return null;
        }
    }

    private static string? DisplayName(ContentResolver resolver, global::Android.Net.Uri uri)
    {
        try
        {
            using var cursor = resolver.Query(uri, new[] { IOpenableColumns.DisplayName }, null, null, null);
            if (cursor != null && cursor.MoveToFirst())
            {
                var index = cursor.GetColumnIndex(IOpenableColumns.DisplayName);
                if (index >= 0 && cursor.GetString(index) is { Length: > 0 } name) return name;
            }
        }
        catch (Exception ex)
        {
            DebugLog.Write("Import", $"A picked file's name could not be read: {ex.Message}");
        }
        return null;
    }
}
