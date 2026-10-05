using Noctis.Helpers;
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
    private static readonly string[] TtmlExtensions = { ".ttml", ".TTML", ".Ttml" };

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

        // Synced field first; then the plain lyrics tag, which is where the scanner puts the
        // file's USLT / LYRICS frame — timed text embedded there counts too (see
        // LyricsFormatDetector.Detect). Untimed plain text is not "existing" here: the
        // engine re-times it from scratch.
        var embedded = !string.IsNullOrWhiteSpace(track.SyncedLyrics) ? track.SyncedLyrics
            : LyricsTextHelper.ContainsTimestamps(track.Lyrics) ? track.Lyrics
            : null;
        if (!string.IsNullOrWhiteSpace(embedded))
        {
            var lines = ParseTimed(embedded);
            if (lines.Count > 0)
                return new ExistingLyrics(HasWordTimings(lines) ? LyricsFormat.Elrc : LyricsFormat.Lrc, lines, "embedded tags", null);
        }
        return null;
    }

    /// <summary>
    /// Sidecar-aware format check: what a track has on disk or in its tags, without parsing
    /// everything. A .ttml counts first, as on the lyrics page: a song whose only lyrics were a
    /// word-timed .ttml (Apple Music style, or the Studio's own "Also save as TTML") was listed
    /// as having no timings, and "Skip songs already in this format" never skipped it.
    /// </summary>
    public static LyricsFormat DetectFormat(Track track)
    {
        var path = track.FilePath;
        if (!string.IsNullOrWhiteSpace(path))
        {
            var ttml = FindSidecar(path, TtmlExtensions);
            if (ttml is not null && TtmlFormat(ttml) is { } tf) return tf;
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

    /// <summary>
    /// <see cref="DetectFormat"/> for a whole list, in order. Each folder is listed once and
    /// its .elrc/.lrc names kept in a set, so a library-wide pass costs one directory
    /// enumeration per folder instead of six <c>File.Exists</c> probes per track. Sidecar
    /// text and stored lyrics are still read per track, so call this off the UI thread.
    /// </summary>
    public static IReadOnlyList<LyricsFormat> DetectFormats(IReadOnlyList<Track> tracks, CancellationToken ct = default)
    {
        var result = new LyricsFormat[tracks.Count];
        var byDir = new Dictionary<string, Dictionary<string, string>?>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < tracks.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var track = tracks[i];
            var path = track.FilePath;
            string? dir = null, stem = null;
            if (!string.IsNullOrWhiteSpace(path))
            {
                try { dir = System.IO.Path.GetDirectoryName(path); stem = System.IO.Path.GetFileNameWithoutExtension(path); }
                catch { /* malformed path: fall through to stored lyrics */ }
            }
            if (!string.IsNullOrEmpty(dir) && !string.IsNullOrEmpty(stem))
            {
                if (!byDir.TryGetValue(dir, out var sidecars))
                    byDir[dir] = sidecars = ListSidecars(dir);
                if (sidecars is not null && TryDetectSidecar(sidecars, stem, out var timed))
                { result[i] = timed; continue; }
            }
            result[i] = LyricsFormatDetector.Detect(track.Lyrics, track.SyncedLyrics);
        }
        return result;
    }

    private static bool TryDetectSidecar(Dictionary<string, string> sidecars, string stem, out LyricsFormat format)
    {
        if (sidecars.TryGetValue(stem + ".ttml", out var ttml) && TtmlFormat(ttml) is { } tf) { format = tf; return true; }
        foreach (var ext in new[] { ".elrc", ".lrc" })
        {
            if (!sidecars.TryGetValue(stem + ext, out var file) || !TryRead(file, out var text)) continue;
            var f = LyricsFormatDetector.Detect(null, text);
            if (f is LyricsFormat.Elrc or LyricsFormat.Lrc) { format = f; return true; }
        }
        format = LyricsFormat.None;
        return false;
    }

    /// <summary>Case-insensitive "stem.ext" → full path for every .lrc/.elrc/.ttml in <paramref name="dir"/>; null when the folder can't be listed.</summary>
    private static Dictionary<string, string>? ListSidecars(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return null;
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                var ext = System.IO.Path.GetExtension(file);
                if (ext.Equals(".lrc", StringComparison.OrdinalIgnoreCase) || ext.Equals(".elrc", StringComparison.OrdinalIgnoreCase)
                    || ext.Equals(".ttml", StringComparison.OrdinalIgnoreCase))
                    map.TryAdd(System.IO.Path.GetFileName(file), file);
            }
            return map;
        }
        catch { return null; }
    }

    /// <summary>A .ttml sidecar's timing as the lyrics page reads it: word spans → word timings (Elrc), timed lines → Lrc; null when unreadable or untimed.</summary>
    private static LyricsFormat? TtmlFormat(string file)
    {
        if (!TryRead(file, out var text)) return null;
        var (lines, _) = TtmlParser.Parse(text);
        if (lines is not { Count: > 0 }) return null;
        if (lines.Any(l => l.Words is { Count: > 0 })) return LyricsFormat.Elrc;
        return lines.Any(l => l.Timestamp.HasValue) ? LyricsFormat.Lrc : null;
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
    /// and empty end-marker lines are dropped; lines come back in time order, file order kept
    /// among lines sharing a start. Plain lines sharing a line's start in its voice (romaji,
    /// translation — GitHub #116) become its <see cref="AlignedLine.Companions"/>, by the
    /// lyrics page's rule (<see cref="LrcParser"/>): word-timed or other-voice lines stay sung.
    /// </summary>
    public static IReadOnlyList<AlignedLine> ParseTimed(string text)
    {
        var raw = new List<(TimeSpan Start, string Text, List<WordTiming>? Words, LyricVoice Voice)>();
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

            var (body, voice) = EnhancedLrcParser.StripVoiceMarker(rest);
            if (stamps.Count == 1) body = EnhancedLrcParser.TagLeadingText(body, stamps[0]);
            var (plain, words) = EnhancedLrcParser.ParseLine(body);
            if (string.IsNullOrWhiteSpace(plain)) continue;
            // Word times are absolute, so they only make sense on a single-stamp line.
            var lineWords = stamps.Count == 1 ? words : null;
            foreach (var s in stamps) raw.Add((s, plain.Trim(), lineWords, voice));
        }
        // Stable: List.Sort reordered lines sharing a start once a file passed 16 lines.
        raw = raw.OrderBy(r => r.Start).ToList();
        var companions = GroupCompanions(raw);

        var result = new List<AlignedLine>(raw.Count);
        for (var i = 0; i < raw.Count; i++)
        {
            var (start, plain, words, _) = raw[i];
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
            result.Add(new AlignedLine(plain, start, lineEnd, aligned, Confidence: 1, Interpolated: false)
            {
                Companions = companions[i],
            });
        }
        return result;
    }

    /// <summary>
    /// Gives re-timed lines back the <see cref="AlignedLine.Companions"/> of the source lines
    /// they were timed from: the engine aligns only the sung text, so without this a re-time
    /// and save dropped the romaji and translation (GitHub #116). Matched in order by text.
    /// </summary>
    public static IReadOnlyList<AlignedLine> CarryCompanions(IReadOnlyList<AlignedLine> source, IReadOnlyList<AlignedLine> timed)
    {
        if (!source.Any(l => l.Companions is { Count: > 0 })) return timed;
        var result = new List<AlignedLine>(timed.Count);
        var j = 0;
        foreach (var line in timed)
        {
            var k = j;
            while (k < source.Count && !string.Equals(source[k].Text.Trim(), line.Text.Trim(), StringComparison.Ordinal)) k++;
            if (k < source.Count)
            {
                j = k + 1;
                if (source[k].Companions is { Count: > 0 } companions && line.Companions is null)
                {
                    result.Add(line with { Companions = companions });
                    continue;
                }
            }
            result.Add(line);
        }
        return result;
    }

    /// <summary>
    /// Pulls companion lines (plain, same start, same voice as an earlier line there) out of
    /// <paramref name="raw"/>; returns each remaining line's companions, index-aligned with it.
    /// </summary>
    private static List<List<string>?> GroupCompanions(List<(TimeSpan Start, string Text, List<WordTiming>? Words, LyricVoice Voice)> raw)
    {
        var companions = new List<List<string>?>(raw.Count);
        var kept = new List<(TimeSpan, string, List<WordTiming>?, LyricVoice)>(raw.Count);
        var taken = new bool[raw.Count];
        for (var i = 0; i < raw.Count; i++)
        {
            if (taken[i]) continue;
            var main = raw[i];
            List<string>? mine = null;
            var seen = new HashSet<string>(StringComparer.Ordinal) { main.Text };
            // A word-timed "(…)" line is an ad-lib of the line before it, not an entry.
            var adlib = main.Words is { Count: > 0 } && TimedLyricsBuilder.IsFullyParenthesized(main.Text);
            for (var k = i + 1; !adlib && k < raw.Count && raw[k].Start == main.Start; k++)
            {
                var other = raw[k];
                if (taken[k] || other.Words is { Count: > 0 } || other.Voice != main.Voice) continue;
                taken[k] = true;
                if (seen.Add(other.Text)) (mine ??= new List<string>()).Add(other.Text);
            }
            kept.Add(main);
            companions.Add(mine);
        }
        raw.Clear();
        raw.AddRange(kept);
        return companions;
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
