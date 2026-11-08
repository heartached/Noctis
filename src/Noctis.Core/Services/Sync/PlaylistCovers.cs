using System.Collections.Concurrent;
using System.Security.Cryptography;
using Noctis.Models;

namespace Noctis.Services.Sync;

/// <summary>
/// A playlist's custom cover as it travels between devices. The playlist state carries only a
/// short hash of the picture's bytes (<see cref="PlaylistSyncState.Cover"/>): "" = no custom
/// cover, a hash = that picture, absent = "this device does not say" (an older app), which keeps
/// whatever cover the receiver has. The bytes travel separately: the computer serves its cover
/// (getNoctisPlaylistCover) and a phone sends its own first (noctisUploadPlaylistCover, kept in
/// <see cref="StagingDirectory"/> until the playlist push that names its hash lands).
///
/// Every cover written here is named after its bytes (<c>&lt;playlist id&gt;-&lt;hash&gt;.ext</c>):
/// the image caches on both apps are keyed by path, so a new picture needs a new path.
/// </summary>
public static class PlaylistCovers
{
    /// <summary>Largest cover a device may send or receive.</summary>
    public const long MaxBytes = 16L * 1024 * 1024;

    /// <summary>Hex digits of SHA-256 a cover hash keeps (as the phone's avatar file names do).</summary>
    public const int HashLength = 16;

    /// <summary>The folder custom covers live in (both apps: playlists.json points into it).</summary>
    public static string CoversDirectory(string dataDirectory) => Path.Combine(dataDirectory, "playlist_covers");

    /// <summary>Where the server keeps covers a phone sent until its playlist push applies them.</summary>
    public static string StagingDirectory(string dataDirectory) => Path.Combine(dataDirectory, "sync", "playlist-covers");

    public static bool IsHash(string? value) =>
        value is { Length: HashLength } && value.AsSpan().ContainsAnyExcept("0123456789abcdef") == false;

    public static string Hash(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes), 0, HashLength / 2).ToLowerInvariant();

    public static string Hash(Stream stream) =>
        Convert.ToHexString(SHA256.HashData(stream), 0, HashLength / 2).ToLowerInvariant();

    /// <summary>The extension for a picture both apps can show, from its first bytes; null for anything else.</summary>
    public static string? ImageExtension(ReadOnlySpan<byte> head)
    {
        if (head.Length >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF) return ".jpg";
        if (head.Length >= 8 && head[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })) return ".png";
        if (head.Length >= 12 && head[..4].SequenceEqual("RIFF"u8) && head[8..12].SequenceEqual("WEBP"u8)) return ".webp";
        if (head.Length >= 6 && (head[..6].SequenceEqual("GIF87a"u8) || head[..6].SequenceEqual("GIF89a"u8))) return ".gif";
        if (head.Length >= 2 && head[0] == (byte)'B' && head[1] == (byte)'M') return ".bmp";
        return null;
    }

    /// <summary>The extension of the picture in <paramref name="path"/>, or null when it is not one.</summary>
    public static string? ImageExtension(string path)
    {
        Span<byte> head = stackalloc byte[16];
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var read = file.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        return ImageExtension(head[..read]);
    }

    private static readonly ConcurrentDictionary<string, (long Length, DateTime Written, string Hash)> Hashes = new(StringComparer.Ordinal);

    /// <summary>
    /// What a playlist state says about this cover: "" for none (no path, or the file is gone),
    /// its hash, or null when the file cannot be read right now (unknown: never sent as a removal).
    /// Remembered per path, size and write time, since the ledger is refreshed on every pull.
    /// </summary>
    public static string? CoverOf(string? path)
    {
        if (string.IsNullOrEmpty(path)) return string.Empty;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0) return string.Empty;
            if (Hashes.TryGetValue(path, out var known) && known.Length == info.Length && known.Written == info.LastWriteTimeUtc)
                return known.Hash;
            string hash;
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                hash = Hash(file);
            Hashes[path] = (info.Length, info.LastWriteTimeUtc, hash);
            return hash;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Writes <paramref name="source"/> (a picture, checked) as <paramref name="playlistId"/>'s
    /// cover in <paramref name="coversDirectory"/> under a name made from its bytes, and removes
    /// the playlist's other cover files there. Returns the new path, or null when the file is not
    /// a picture or too big. <paramref name="move"/> moves the file instead of copying it.
    /// </summary>
    public static string? Store(string source, Guid playlistId, string coversDirectory, bool move = false)
    {
        var info = new FileInfo(source);
        if (!info.Exists || info.Length == 0 || info.Length > MaxBytes) return null;
        var ext = ImageExtension(source);
        if (ext is null) return null;
        string hash;
        using (var file = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            hash = Hash(file);
        Directory.CreateDirectory(coversDirectory);
        var dest = Path.Combine(coversDirectory, $"{playlistId}-{hash}{ext}");
        if (!File.Exists(dest))
        {
            if (move) File.Move(source, dest);
            else File.Copy(source, dest);
        }
        else if (move) TryDelete(source);
        DeleteOthers(playlistId, coversDirectory, except: dest);
        return dest;
    }

    /// <summary>Removes <paramref name="playlistId"/>'s cover files in <paramref name="coversDirectory"/>
    /// (both apps' names: <c>&lt;id&gt;.ext</c> and <c>&lt;id&gt;-&lt;hash&gt;.ext</c>), all but <paramref name="except"/>.</summary>
    public static void DeleteOthers(Guid playlistId, string coversDirectory, string? except)
    {
        try
        {
            if (!Directory.Exists(coversDirectory)) return;
            foreach (var f in Directory.EnumerateFiles(coversDirectory, $"{playlistId}*"))
                if (!string.Equals(Path.GetFullPath(f), except is null ? null : Path.GetFullPath(except), StringComparison.OrdinalIgnoreCase))
                    TryDelete(f);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Lands a synced cover on the receiving computer's playlist: null leaves it, "" removes it,
    /// a hash takes the staged picture with that hash (sent by the phone just before its push).
    /// A hash the computer already shows, or one with no staged picture, changes nothing.
    /// Returns true when <see cref="Playlist.CoverArtPath"/> changed.
    /// </summary>
    public static bool Apply(Playlist playlist, string? cover, string coversDirectory, string stagingDirectory)
    {
        if (cover is null) return false;
        if (cover.Length == 0)
        {
            if (string.IsNullOrEmpty(playlist.CoverArtPath)) return false;
            if (IsUnder(playlist.CoverArtPath, coversDirectory)) TryDelete(playlist.CoverArtPath);
            DeleteOthers(playlist.Id, coversDirectory, except: null);
            playlist.CoverArtPath = null;
            return true;
        }
        if (!IsHash(cover) || CoverOf(playlist.CoverArtPath) == cover) return false;
        var staged = Path.Combine(stagingDirectory, cover);
        if (!File.Exists(staged)) return false;
        string? path;
        try { path = Store(staged, playlist.Id, coversDirectory); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        if (path is null || CoverOf(path) != cover) return false;
        playlist.CoverArtPath = path;
        return true;
    }

    private static bool IsUnder(string path, string directory)
    {
        try
        {
            var dir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar;
            return Path.GetFullPath(path).StartsWith(dir, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
