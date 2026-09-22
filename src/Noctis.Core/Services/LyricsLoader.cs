using Noctis.Helpers;
using Noctis.Models;

namespace Noctis.Services;

public enum LyricsSource { None, SidecarTtml, SidecarElrc, SidecarLrc, EmbeddedSynced, EmbeddedSylt, EmbeddedPlain }

/// <summary>What <see cref="LyricsLoader.Load"/> found. Plain lines are pre-activated.</summary>
public sealed record LoadedLyrics(IReadOnlyList<LyricLine> Lines, bool IsSynced, LyricsSource Source)
{
    public static LoadedLyrics None { get; } = new(Array.Empty<LyricLine>(), false, LyricsSource.None);
}

/// <summary>
/// Finds and parses a track's lyrics without UI or network: sidecars (.ttml, .elrc, .lrc —
/// the desktop order minus the desktop-only .lyricsfile), then the lyrics store, then an LRC
/// in the plain field, then ID3v2 SYLT, then plain text. Blocking I/O: call it off the UI
/// thread. The desktop keeps its own probe (it adds .lyricsfile and the online cache).
/// </summary>
public static class LyricsLoader
{
    private static readonly string[] SidecarOrder = [".ttml", ".elrc", ".lrc"];

    public static LoadedLyrics Load(Track track, ITrackFileAccess files, bool joinSplitWords)
    {
        var remaining = SidecarOrder;
        while (remaining.Length > 0)
        {
            var hit = files.ReadSidecar(track.FilePath, remaining);
            if (hit == null) break;
            var text = LyricsTextDecoder.Decode(hit.Bytes);
            var parsed = hit.Extension == ".ttml"
                ? FromTtml(text, joinSplitWords)
                : FromLrc(text, hit.Extension == ".elrc" ? LyricsSource.SidecarElrc : LyricsSource.SidecarLrc);
            if (parsed != null) return parsed;
            // Unusable (malformed TTML, empty file): try the next format, as the desktop probe does.
            var index = Array.IndexOf(remaining, hit.Extension);
            remaining = index < 0 ? Array.Empty<string>() : remaining[(index + 1)..];
        }

        var synced = track.SyncedLyrics;
        if (!string.IsNullOrWhiteSpace(synced) && FromLrc(synced, LyricsSource.EmbeddedSynced) is { } stored)
            return stored;

        var plain = track.Lyrics;
        if (!string.IsNullOrWhiteSpace(plain) && LrcParser.ContainsTimestamp(plain)
            && FromLrc(plain, LyricsSource.EmbeddedSynced) is { } legacy)
            return legacy;

        if (ReadSylt(track, files) is { Count: > 0 } sylt)
            return Synced(sylt, LyricsSource.EmbeddedSylt);

        if (!string.IsNullOrWhiteSpace(plain))
            return Plain(LrcParser.SplitPlain(plain), LyricsSource.EmbeddedPlain);

        return LoadedLyrics.None;
    }

    private static LoadedLyrics? FromTtml(string text, bool joinSplitWords)
    {
        var (lines, _) = TtmlParser.Parse(text, joinSplitWords);
        return lines is { Count: > 0 } ? Synced(lines, LyricsSource.SidecarTtml) : null;
    }

    /// <summary>An LRC with stamps is synced; one without is unsynced text (still a hit, so an
    /// .lrc of plain words is shown rather than skipped). Null only when it yields no line.</summary>
    private static LoadedLyrics? FromLrc(string text, LyricsSource source)
    {
        var lines = LrcParser.Parse(text);
        if (lines.Count == 0) return null;
        if (lines.Any(l => l.IsSynced)) return Synced(lines, source);
        foreach (var line in lines) line.IsActive = true;
        return new LoadedLyrics(lines, false, source);
    }

    private static LoadedLyrics Synced(List<LyricLine> lines, LyricsSource source)
    {
        LrcParser.InsertIntroPlaceholderIfNeeded(lines);
        return new LoadedLyrics(lines, true, source);
    }

    private static LoadedLyrics Plain(IEnumerable<string> rows, LyricsSource source) =>
        new(rows.Select(r => new LyricLine { Text = LrcParser.SoftWrap(r), IsActive = true }).ToList(), false, source);

    /// <summary>SYLT needs TagLib to pick a reader from the file name; a SAF URI's last
    /// segment is the percent-encoded document id, whose tail is the display name on the
    /// external-storage provider. An opaque id has no extension, TagLib refuses it, and the
    /// read is simply null.</summary>
    private static List<LyricLine>? ReadSylt(Track track, ITrackFileAccess files)
    {
        var stream = files.OpenAudio(track.FilePath);
        if (stream == null) return null;
        var name = Path.GetFileName(Uri.UnescapeDataString(track.FilePath));
        return SyltLyrics.Read(stream, name);
    }
}
