using System.Text.RegularExpressions;
using Noctis.Models;

namespace Noctis.Services.LyricsStudio;

/// <summary>Timed lyrics a track already has, ready for the review pane without a model run.</summary>
/// <param name="Format">Word-level (<see cref="LyricsFormat.Elrc"/>) or line-level (<see cref="LyricsFormat.Lrc"/>).</param>
/// <param name="Lines">Line-level input yields lines with an empty word list.</param>
/// <param name="Origin">Where it came from, for the review header: ".elrc file", ".lrc file", "embedded tags".</param>
/// <param name="Path">The sidecar path, null for embedded lyrics.</param>
public sealed record ExistingLyrics(LyricsFormat Format, IReadOnlyList<AlignedLine> Lines, string Origin, string? Path);

/// <summary>
/// Finds and parses the synced lyrics a track already carries, in the order Lyrics Studio
/// trusts them: a <c>.elrc</c> sidecar, then a <c>.lrc</c> sidecar, then the embedded/stored
/// synced text. Plain (untimed) lyrics are not "existing synced lyrics" and return null.
/// </summary>
public static partial class ExistingLyricsLoader
{
    private static readonly string[] ElrcExtensions = { ".elrc", ".ELRC", ".Elrc" };
    private static readonly string[] LrcExtensions = { ".lrc", ".LRC", ".Lrc" };

    [GeneratedRegex(@"^\[(\d{1,3}):(\d{2})(?:[.:](\d{1,3}))?\]")]
    private static partial Regex LeadingTimestamp();

    [GeneratedRegex(@"^\[[A-Za-z][A-Za-z0-9_]*:[^\]]*\]$")]
    private static partial Regex MetadataTag();

    private static readonly TimeSpan DefaultWordSpan = TimeSpan.FromMilliseconds(420);

    public static ExistingLyrics? Load(Track track)
    {
        var path = track.FilePath;
        if (!string.IsNullOrWhiteSpace(path))
        {
            var elrc = FindSidecar(path, ElrcExtensions);
            if (elrc is not null && TryParseFile(elrc, out var elrcLines))
                return new ExistingLyrics(HasWordTimings(elrcLines) ? LyricsFormat.Elrc : LyricsFormat.Lrc, elrcLines, ".elrc file", elrc);

            var lrc = FindSidecar(path, LrcExtensions);
            if (lrc is not null && TryParseFile(lrc, out var lrcLines))
                return new ExistingLyrics(HasWordTimings(lrcLines) ? LyricsFormat.Elrc : LyricsFormat.Lrc, lrcLines, ".lrc file", lrc);
        }

        var embedded = track.SyncedLyrics;
        if (!string.IsNullOrWhiteSpace(embedded))
        {
            var lines = ParseTimed(embedded);
            if (lines.Count > 0)
                return new ExistingLyrics(HasWordTimings(lines) ? LyricsFormat.Elrc : LyricsFormat.Lrc, lines, "embedded tags", null);
        }
        return null;
    }

    /// <summary>Sidecar-aware format check: what a track has on disk or in its tags, without parsing everything.</summary>
    public static LyricsFormat DetectFormat(Track track)
    {
        var path = track.FilePath;
        if (!string.IsNullOrWhiteSpace(path))
        {
            var elrc = FindSidecar(path, ElrcExtensions);
            if (elrc is not null && TryRead(elrc, out var text))
            {
                var f = LyricsFormatDetector.Detect(null, text);
                if (f is LyricsFormat.Elrc or LyricsFormat.Lrc) return f;
            }
            var lrc = FindSidecar(path, LrcExtensions);
            if (lrc is not null && TryRead(lrc, out text))
            {
                var f = LyricsFormatDetector.Detect(null, text);
                if (f is LyricsFormat.Elrc or LyricsFormat.Lrc) return f;
            }
        }
        return LyricsFormatDetector.Detect(track.Lyrics, track.SyncedLyrics);
    }

    public static string? FindSidecar(string trackFilePath, string[] extensions)
    {
        var dir = System.IO.Path.GetDirectoryName(trackFilePath);
        var stem = System.IO.Path.GetFileNameWithoutExtension(trackFilePath);
        if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(stem)) return null;
        foreach (var ext in extensions)
        {
            var candidate = System.IO.Path.Combine(dir, stem + ext);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    public static bool HasWordTimings(IReadOnlyList<AlignedLine> lines) => lines.Any(l => l.Words.Count > 0);

    /// <summary>
    /// LRC or enhanced LRC text → aligned lines. Word tags become words; a line without word
    /// tags keeps an empty word list so it stays line-level until it is upgraded. Header tags
    /// and empty end-marker lines are dropped; lines come back in time order.
    /// </summary>
    public static IReadOnlyList<AlignedLine> ParseTimed(string text)
    {
        var raw = new List<(TimeSpan Start, string Text, List<WordTiming>? Words)>();
        foreach (var rawLine in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || MetadataTag().IsMatch(line)) continue;

            // Compressed lines carry several timestamps: one entry per stamp.
            var stamps = new List<TimeSpan>();
            var rest = line;
            while (LeadingTimestamp().Match(rest) is { Success: true } m)
            {
                var min = int.Parse(m.Groups[1].Value);
                var sec = int.Parse(m.Groups[2].Value);
                var frac = m.Groups[3].Success ? m.Groups[3].Value : "0";
                var ms = frac.Length switch { 1 => int.Parse(frac) * 100, 2 => int.Parse(frac) * 10, _ => int.Parse(frac[..3]) };
                stamps.Add(new TimeSpan(0, 0, min, sec, ms));
                rest = rest[m.Length..];
            }
            if (stamps.Count == 0) continue;

            var (body, _) = EnhancedLrcParser.StripVoiceMarker(rest);
            var (plain, words) = EnhancedLrcParser.ParseLine(body);
            if (string.IsNullOrWhiteSpace(plain)) continue;
            // Word times are absolute, so they only make sense on a single-stamp line.
            var lineWords = stamps.Count == 1 ? words : null;
            foreach (var s in stamps) raw.Add((s, plain.Trim(), lineWords));
        }
        raw.Sort((a, b) => a.Start.CompareTo(b.Start));

        var result = new List<AlignedLine>(raw.Count);
        for (var i = 0; i < raw.Count; i++)
        {
            var (start, plain, words) = raw[i];
            var nextStart = i + 1 < raw.Count ? raw[i + 1].Start : (TimeSpan?)null;
            var aligned = new List<AlignedWord>();
            if (words is { Count: > 0 })
            {
                for (var w = 0; w < words.Count; w++)
                {
                    var word = words[w];
                    var wText = word.Text.Trim();
                    if (wText.Length == 0) continue;
                    var end = word.End ?? (w + 1 < words.Count ? words[w + 1].Start : nextStart ?? word.Start + DefaultWordSpan);
                    if (end < word.Start) end = word.Start;
                    aligned.Add(new AlignedWord(wText, word.Start, end));
                }
            }
            var lineEnd = aligned.Count > 0 ? aligned[^1].End
                : nextStart ?? start + DefaultWordSpan * Math.Max(1, plain.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
            if (lineEnd < start) lineEnd = start;
            result.Add(new AlignedLine(plain, start, lineEnd, aligned, Confidence: 1, Interpolated: false));
        }
        return result;
    }

    private static bool TryParseFile(string path, out IReadOnlyList<AlignedLine> lines)
    {
        lines = Array.Empty<AlignedLine>();
        if (!TryRead(path, out var text)) return false;
        lines = ParseTimed(text);
        return lines.Count > 0;
    }

    private static bool TryRead(string path, out string text)
    {
        try { text = File.ReadAllText(path); return true; }
        catch { text = string.Empty; return false; }
    }
}
