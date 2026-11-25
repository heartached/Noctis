using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace Noctis.Services.Server;

/// <summary>
/// <c>getNoctisAnimatedCover?id=al-…</c> (or <c>tr-…</c>, that song's album with its own loop
/// first): the album's animated cover as the desktop shows it (<see cref="AnimatedCoverLocator"/>),
/// or 70 when it has none. A Noctis extension for the phone's Now Playing (Discord beta 11-23,
/// "no animated cover support"); other clients never ask. Range requests are served, and the
/// answer carries an ETag (the file's last write and length) so a phone holding the loop asks
/// with If-None-Match and gets 304 instead of the whole video again.
/// </summary>
public sealed partial class NoctisServer
{
    private async Task ServeAnimatedCoverAsync(HttpContext ctx, Params p)
    {
        var id = p.Get("id") ?? throw Missing("id");
        var snap = await _library.SnapshotAsync().ConfigureAwait(false);
        var albumId = Ids.AlbumGuid(id) ?? Ids.Track(snap, id)?.AlbumId ?? throw NotFound();
        var asked = Ids.TrackGuid(id);
        var tracks = snap.Tracks.Where(t => t.AlbumId == albumId).OrderBy(t => t.Id == asked ? 0 : 1).ToList();
        var path = _library.AnimatedCoverPath(albumId, tracks) ?? throw NotFound();
        FileInfo file;
        try
        {
            file = new FileInfo(path);
            if (!file.Exists || file.Length == 0) throw NotFound();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw NotFound(); }
        var tag = new EntityTagHeaderValue("\"" + AnimatedCoverVersion(file) + "\"");
        // The loop may be replaced under the same name (a new one saved for the album): revalidate.
        ctx.Response.Headers.CacheControl = "private, no-cache";
        await Results.File(path, AnimatedCoverLocator.ContentTypeFor(path), lastModified: file.LastWriteTimeUtc,
            entityTag: tag, enableRangeProcessing: true).ExecuteAsync(ctx).ConfigureAwait(false);
    }

    /// <summary>The loop's version: its last write in hex ticks and its length, so a loop replaced
    /// within the same second by one of another size still reads as new.</summary>
    internal static string AnimatedCoverVersion(FileInfo file) =>
        file.LastWriteTimeUtc.Ticks.ToString("x", CultureInfo.InvariantCulture) + "-" + file.Length.ToString("x", CultureInfo.InvariantCulture);
}
