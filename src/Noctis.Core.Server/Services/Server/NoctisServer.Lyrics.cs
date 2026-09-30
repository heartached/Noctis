using System.Text;
using System.Text.Json.Nodes;
using Noctis.Helpers;
using Noctis.Models;

namespace Noctis.Services.Server;

/// <summary>
/// <c>getNoctisLyrics?id=tr-…</c>: a song's lyrics as the desktop has them, so the phone can show
/// them for a desktop song (streamed or downloaded) through its own loader. Answer:
/// <c>noctisLyrics: { ttml?, elrc?, lrc?, synced?, plain? }</c> — the texts of the .ttml / .elrc /
/// .lrc sidecars beside the song's file (same folder, same stem: <see cref="SidecarNames"/>),
/// then the library's stored synced / plain lyrics, or, when both are empty, the desktop lyrics
/// page's cache of lyrics it fetched online. Every text is capped at
/// <see cref="MaxLyricsTextBytes"/> (a bigger one is left out). Only paths derived from the
/// library track itself are read: its file's folder and stem, and the cache file named by its id.
/// </summary>
public sealed partial class NoctisServer
{
    /// <summary>Largest lyric text served, in UTF-8 bytes; a real TTML is tens of KB.</summary>
    public const int MaxLyricsTextBytes = 2 * 1024 * 1024;

    /// <summary>Response member → sidecar extension, in the loader's order.</summary>
    private static readonly (string Key, string Extension)[] LyricsSidecars =
    {
        ("ttml", ".ttml"), ("elrc", ".elrc"), ("lrc", ".lrc"),
    };

    private static readonly EnumerationOptions SiblingSearch = new()
    {
        MatchType = MatchType.Simple,          // no DOS wildcard rules: '*' and '?' only
        MatchCasing = MatchCasing.CaseInsensitive,
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
        AttributesToSkip = 0,                  // the desktop's File.Exists probe reads hidden sidecars too
    };

    /// <summary>
    /// The desktop lyrics page's cache of lyrics it fetched online (<c>{track id}.lrc</c>, Guid "D"
    /// form); getNoctisLyrics falls back to it when a song has no stored lyrics. Null (the
    /// headless host) = no cache. Set before <see cref="StartAsync"/>.
    /// </summary>
    public string? LyricsCacheDirectory { get; set; }

    /// <summary>The <c>noctisLyrics</c> object for one library track (members only when non-blank).</summary>
    internal static JsonObject LyricsObject(Track track, string? cacheDirectory)
    {
        var obj = new JsonObject();
        var sidecars = ReadLyricsSidecars(track.FilePath);
        foreach (var (key, extension) in LyricsSidecars)
            if (sidecars.TryGetValue(extension, out var text)) obj[key] = text;

        string? synced = null, plain = null;
        try
        {
            synced = CapLyricsText(track.SyncedLyrics);
            plain = CapLyricsText(track.Lyrics);
        }
        catch (Exception ex)
        {
            DebugLogger.Warn(DebugLogger.Category.State, "Server", $"stored lyrics unreadable: {ex.GetType().Name}");
        }

        // The desktop probe's order: stored (embedded) lyrics first, the online cache after them.
        if (synced is null && plain is null && cacheDirectory is not null
            && ReadLyricsFile(Path.Combine(cacheDirectory, track.Id.ToString("D") + ".lrc")) is { } cached)
        {
            if (cached.Contains('[') && LrcParser.ContainsTimestamp(cached)) synced = cached;
            else plain = cached;
        }
        if (synced is not null) obj["synced"] = synced;
        if (plain is not null) obj["plain"] = plain;
        return obj;
    }

    /// <summary>
    /// Sidecar extension → decoded text for each lyric sidecar beside <paramref name="audioPath"/>.
    /// The folder is listed for "&lt;stem&gt;.*" (the pattern only narrows the listing;
    /// <see cref="SidecarNames.Match"/> decides), so every casing of the extension matches, as it
    /// does on the phone. A path that is not a local absolute path (a remote source) has none.
    /// </summary>
    internal static Dictionary<string, string> ReadLyricsSidecars(string? audioPath)
    {
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            if (string.IsNullOrEmpty(audioPath) || !Path.IsPathFullyQualified(audioPath)) return found;
            var dir = Path.GetDirectoryName(audioPath);
            var name = Path.GetFileName(audioPath);
            var stem = Path.GetFileNameWithoutExtension(audioPath);
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(stem) || !Directory.Exists(dir)) return found;

            var siblings = Directory.EnumerateFiles(dir, stem + ".*", SiblingSearch)
                .Select(Path.GetFileName).OfType<string>().ToList();
            foreach (var (_, extension) in LyricsSidecars)
            {
                if (SidecarNames.Match(name, siblings, new[] { extension }) is not { } hit) continue;
                if (ReadLyricsFile(Path.Combine(dir, hit.Name)) is { } text) found[extension] = text;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException
                                       or System.Security.SecurityException)
        {
            DebugLogger.Warn(DebugLogger.Category.State, "Server", $"lyrics sidecar lookup failed: {ex.GetType().Name}");
        }
        return found;
    }

    /// <summary>A lyric file's text (encoding detected like the desktop's probe), or null when it is
    /// missing, unreadable, blank or over <see cref="MaxLyricsTextBytes"/>.</summary>
    internal static string? ReadLyricsFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > MaxLyricsTextBytes) return null;
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
            {
                // The length was checked, but the file may be growing while it is read.
                if (buffer.Length + read > MaxLyricsTextBytes) return null;
                buffer.Write(chunk, 0, read);
            }
            return CapLyricsText(LyricsTextDecoder.Decode(buffer.ToArray()));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException
                                       or System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>The text when it is non-blank and at most <see cref="MaxLyricsTextBytes"/> as UTF-8
    /// (a non-UTF-8 file can grow when decoded), else null. The phone applies the same cap.</summary>
    internal static string? CapLyricsText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxLyricsTextBytes) return null;
        return Encoding.UTF8.GetByteCount(text) <= MaxLyricsTextBytes ? text : null;
    }
}
