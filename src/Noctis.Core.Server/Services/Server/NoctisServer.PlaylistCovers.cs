using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Noctis.Services.Sync;

namespace Noctis.Services.Server;

/// <summary>
/// Custom playlist covers between this computer and a signed-in phone (see <see cref="PlaylistCovers"/>):
/// <list type="bullet">
/// <item><c>getNoctisPlaylistCover?id=…</c>: the playlist's custom cover file, or 70 when it has none.</item>
/// <item><c>noctisUploadPlaylistCover</c> (POST, the picture as the raw body, at most
/// <see cref="PlaylistCovers.MaxBytes"/>): kept under its hash until the playlist push that names
/// it applies it. Answer <c>noctisPlaylistCover: { cover: "&lt;hash&gt;" }</c>.</item>
/// </list>
/// Both need Sync on (the playlists travel through the ledger); the upload needs the phone's own key.
/// </summary>
public sealed partial class NoctisServer
{
    /// <summary>A staged cover no push named within this long is dropped (at the next upload).</summary>
    private static readonly TimeSpan StagedCoverMaxAge = TimeSpan.FromDays(1);

    private async Task ServePlaylistCoverAsync(HttpContext ctx, Params p)
    {
        RequireSync();
        var raw = p.Get("id") ?? throw Missing("id");
        if (!Guid.TryParseExact(raw, "N", out var id) && !Guid.TryParseExact(raw, "D", out id)) throw NotFound();
        var snap = await _library.SnapshotAsync().ConfigureAwait(false);
        var path = snap.Playlists.FirstOrDefault(x => x.Id == id)?.CoverArtPath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) throw NotFound();
        // playlists.json can name any file: serve it only as a picture.
        string? ext;
        try { ext = PlaylistCovers.ImageExtension(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw NotFound(); }
        if (ext is null) throw NotFound();
        // A changed cover may keep its name (the desktop writes <id>.jpg again): always revalidate.
        ctx.Response.Headers.CacheControl = "private, no-cache";
        await Results.File(path, ContentTypeFor("x" + ext)).ExecuteAsync(ctx).ConfigureAwait(false);
    }

    private async Task<JsonObject> UploadPlaylistCoverAsync(HttpContext ctx)
    {
        RequireSyncDevice(ctx);
        if (!HttpMethods.IsPost(ctx.Request.Method) || ctx.Request.HasFormContentType)
            throw new SubsonicException(SubsonicResponse.ErrGeneric, "Send the picture as the body of a POST.");
        var staging = _library.PlaylistCoverStagingDirectory
            ?? throw new SubsonicException(SubsonicResponse.ErrGeneric, "This computer doesn't take playlist covers.");
        if (ctx.Request.ContentLength > PlaylistCovers.MaxBytes) throw CoverTooLarge();
        var ct = ctx.RequestAborted;
        Directory.CreateDirectory(staging);
        PruneStagedCovers(staging);

        var sizeFeature = ctx.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false }) sizeFeature.MaxRequestBodySize = PlaylistCovers.MaxBytes + 64 * 1024;

        var temp = Path.Combine(staging, ".upload-" + Guid.NewGuid().ToString("N"));
        try
        {
            long length = 0;
            await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                while (true)
                {
                    int read;
                    try { read = await ctx.Request.Body.ReadAsync(buffer, ct).ConfigureAwait(false); }
                    catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge) { throw CoverTooLarge(); }
                    if (read == 0) break;
                    length += read;
                    if (length > PlaylistCovers.MaxBytes) throw CoverTooLarge();
                    await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                }
            }
            if (length == 0 || PlaylistCovers.ImageExtension(temp) is null)
                throw new SubsonicException(SubsonicResponse.ErrGeneric, "The cover must be a JPEG, PNG, WebP, GIF or BMP picture.");
            string hash;
            await using (var read = new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.Read))
                hash = PlaylistCovers.Hash(read);
            File.Move(temp, Path.Combine(staging, hash), overwrite: true);
            return new JsonObject { ["noctisPlaylistCover"] = new JsonObject { ["cover"] = hash } };
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best effort: pruned with the next upload */ }
        }
    }

    private static SubsonicException CoverTooLarge()
        => new(SubsonicResponse.ErrGeneric, $"Covers up to {PlaylistCovers.MaxBytes / (1024 * 1024)} MB can be sent.");

    private static void PruneStagedCovers(string staging)
    {
        try
        {
            var cutoff = DateTime.UtcNow - StagedCoverMaxAge;
            foreach (var f in Directory.EnumerateFiles(staging))
                if (File.GetLastWriteTimeUtc(f) < cutoff) File.Delete(f);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
