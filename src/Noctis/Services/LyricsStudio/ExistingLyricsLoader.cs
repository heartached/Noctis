using Noctis.Helpers;
using System.Text.RegularExpressions;
using Noctis.Models;

namespace Noctis.Services.LyricsStudio;

/// <summary>Timed lyrics a track already has, ready for the review pane without a model run.</summary>
/// <param name="Format">Word-level (<see cref="LyricsFormat.Elrc"/>) or line-level (<see cref="LyricsFormat.Lrc"/>).</param>
/// <param name="Lines">Line-level input yields lines with an empty word list.</param>
/// <param name="Origin">Where it came from, for the review header: ".elrc file", ".lrc file", "embedded tags"
/// (the audio file's own tags), "Noctis's lyrics cache" (lyrics Noctis keeps for the song that the file does not carry).</param>
/// <param name="Path">The sidecar path, null for embedded lyrics.</param>
public sealed record ExistingLyrics(LyricsFormat Format, IReadOnlyList<AlignedLine> Lines, string Origin, string? Path);

/// <summary>
/// Finds and parses the synced lyrics a track already carries, in the order Lyrics Studio
/// trusts them: a <c>.ttml</c> sidecar, then a <c>.elrc</c>, then a <c>.lrc</c>, then the embedded/stored
/// synced text. Plain (untimed) lyrics are not "existing synced lyrics" and return null.
/// </summary>
public static partial class ExistingLyricsLoader
{
    private static readonly string[] ElrcExtensions = { ".elrc", ".ELRC", ".Elrc" };
    private static readonly string[] LrcExtensions = { ".lrc", ".LRC", ".Lrc" };
    private static readonly string[] TtmlExtensions = { ".ttml", ".TTML", ".Ttml" };

    [GeneratedRegex(@"^\[(\d{1,3}):(\d{2,3})(?:[.:](\d{1,3}))?\]")]
    private static partial Regex LeadingTimestamp();

    [GeneratedRegex(@"^\[[A-Za-z][A-Za-z0-9_]*:[^\]]*\]$")]
    private static partial Regex MetadataTag();

    private static readonly TimeSpan DefaultWordSpan = TimeSpan.FromMilliseconds(420);

    /// <summary>An ELRC background-vocal line: "[bg: &lt;mm:ss.xx&gt;word …&lt;mm:ss.xx&gt;]".</summary>
    private const string BgLinePrefix = "[bg:";

    public static ExistingLyrics? Load(Track track)
    {
        var path = track.FilePath;
        if (!string.IsNullOrWhiteSpace(path))
        {
            // A .ttml first, as on the lyrics page and in DetectFormat: a song whose only timed
            // lyrics were a word-timed .ttml showed nothing here, and Start re-timed it from
            // LRCLIB or a transcript instead of its own words.
            var ttml = FindSidecar(path, TtmlExtensions);
            if (ttml is not null && TryRead(ttml, out var ttmlText)
                && TtmlParser.ToLrc(ttmlText) is { } ttmlLrc && ParseTimed(ttmlLrc) is { Count: > 0 } ttmlLines)
                return new ExistingLyrics(HasWordTimings(ttmlLines) ? LyricsFormat.Elrc : LyricsFormat.Lrc, ttmlLines, ".ttml file", ttml);

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
                return new ExistingLyrics(HasWordTimings(lines) ? LyricsFormat.Elrc : LyricsFormat.Lrc, lines,
                    InFileTags(track, embedded) ? "embedded tags" : "Noctis's lyrics cache", null);
        }
        return null;
    }

    /// <summary>
    /// Whether the audio file's own tags carry <paramref name="timed"/>. The library's lyrics
    /// fields start as the tags the scan read, but they also keep lyrics Noctis got for the song
    /// itself: an online pick fills them beside the .elrc/.lrc the lyrics page writes. With those
    /// files deleted they read "From embedded tags" for an MP3 with no lyrics tag (live 10-19).
    /// </summary>
    private static bool InFileTags(Track track, string timed)
    {
        var path = track.FilePath;
        if (track.SourceType != SourceType.Local || string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
        return Comparable(EmbeddedLyrics.ReadFile(path).TimedText) == Comparable(timed);

        static string Comparable(string text) =>
            text.TrimStart('﻿').Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').TrimEnd();
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
    /// A global [offset:] moves every line and word as on the lyrics page; the times come back
    /// with it applied (the Studio writes no offset tag).
    /// </summary>
    public static IReadOnlyList<AlignedLine> ParseTimed(string text)
    {
        var raw = new List<(TimeSpan Start, string Text, List<WordTiming>? Words, LyricVoice Voice, List<AlignedWord>? Background)>();
        // A BOM kept in the text (lyrics copied into a tag from a file) is not part of the first
        // line: Trim leaves it, its stamp then did not start the line, and the line was dropped.
        var rows = text.TrimStart('﻿').Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        // The page shows the song by the offset, so the Studio times and saves it there too: read
        // bare, every line was 0.5 s off the page for "[offset:+500]", and a save moved them.
        var offset = TimeSpan.FromMilliseconds(LrcParser.ParseLrcOffsetMilliseconds(rows));
        foreach (var rawLine in rows)
        {
            var line = rawLine.Trim();
            // A "[bg: …]" background vocal belongs to the timed line above it in the file, as on
            // the lyrics page (LrcParser). It looks like a header tag and was dropped with them,
            // so a Studio save wrote the song without its background vocals.
            if (line.StartsWith(BgLinePrefix, StringComparison.Ordinal))
            {
                if (raw.Count > 0 && ParseBackground(line, raw[^1].Start, offset) is { } bg)
                {
                    var last = raw[^1];
                    raw[^1] = last with { Background = last.Background is null ? bg : [.. last.Background, .. bg] };
                }
                continue;
            }
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
            var lineWords = stamps.Count == 1 ? Shifted(words, offset) : null;
            foreach (var s in stamps) raw.Add((Shift(s, offset), plain.Trim(), lineWords, voice, null));
        }
        // Stable: List.Sort reordered lines sharing a start once a file passed 16 lines.
        raw = raw.OrderBy(r => r.Start).ToList();
        var companions = GroupCompanions(raw);

        var result = new List<AlignedLine>(raw.Count);
        for (var i = 0; i < raw.Count; i++)
        {
            var (start, plain, words, voice, background) = raw[i];
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
                    aligned.Add(new AlignedWord(wText, word.Start, end) { JoinsNext = FollowedWithoutSpace(words, w) });
                }
            }
            var lineEnd = aligned.Count > 0 ? aligned[^1].End
                : nextStart ?? start + DefaultWordSpan * Math.Max(1, plain.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
            if (lineEnd < start) lineEnd = start;
            result.Add(new AlignedLine(plain, start, lineEnd, aligned, Confidence: 1, Interpolated: false)
            {
                Companions = companions[i],
                Voice = voice,
                Background = background,
            });
        }
        return result;
    }

    /// <summary>
    /// Gives re-timed lines back what the source lines they were timed from carried besides the
    /// sung text: the <see cref="AlignedLine.Companions"/>, the duet <see cref="AlignedLine.Voice"/>
    /// and the <see cref="AlignedLine.Background"/> vocal. The engine aligns only the sung text,
    /// so without this a re-time and save dropped the romaji and translation (GitHub #116), put
    /// every line in the first singer's place and lost the adlibs. Nothing times the background
    /// vocal, so it moves by as much as its line did — as the review's line shift moves it.
    /// Matched in order by text.
    /// </summary>
    public static IReadOnlyList<AlignedLine> CarryCompanions(IReadOnlyList<AlignedLine> source, IReadOnlyList<AlignedLine> timed)
    {
        if (!source.Any(l => l.Companions is { Count: > 0 } || l.Background is { Count: > 0 } || l.Voice != LyricVoice.Default))
            return timed;
        var result = new List<AlignedLine>(timed.Count);
        var j = 0;
        foreach (var line in timed)
        {
            var k = j;
            while (k < source.Count && !string.Equals(source[k].Text.Trim(), line.Text.Trim(), StringComparison.Ordinal)) k++;
            if (k >= source.Count)
            {
                result.Add(line);
                continue;
            }
            j = k + 1;
            var from = source[k];
            var carried = line;
            if (from.Companions is { Count: > 0 } companions && line.Companions is null)
                carried = carried with { Companions = companions };
            if (from.Voice != LyricVoice.Default && line.Voice == LyricVoice.Default)
                carried = carried with { Voice = from.Voice };
            if (from.Background is { Count: > 0 } background && line.Background is null)
            {
                var delta = line.Start - from.Start;
                carried = carried with
                {
                    Background = background.Select(w => w with
                    {
                        Start = TimeSpan.FromTicks(Math.Max(0, (w.Start + delta).Ticks)), End = TimeSpan.FromTicks(Math.Max(0, (w.End + delta).Ticks)),
                    }).ToList(),
                };
            }
            result.Add(carried);
        }
        return result;
    }

    /// <summary>
    /// Pulls companion lines (plain, same start, same voice as an earlier line there) out of
    /// <paramref name="raw"/>; returns each remaining line's companions, index-aligned with it.
    /// </summary>
    private static List<List<string>?> GroupCompanions(List<(TimeSpan Start, string Text, List<WordTiming>? Words, LyricVoice Voice, List<AlignedWord>? Background)> raw)
    {
        var companions = new List<List<string>?>(raw.Count);
        var kept = new List<(TimeSpan, string, List<WordTiming>?, LyricVoice, List<AlignedWord>?)>(raw.Count);
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
                // A companion's background vocal is the line's (LrcParser moves it the same way).
                if (other.Background is { } bg) main.Background = main.Background is null ? bg : [.. main.Background, .. bg];
                if (seen.Add(other.Text)) (mine ??= new List<string>()).Add(other.Text);
            }
            kept.Add(main);
            companions.Add(mine);
        }
        raw.Clear();
        raw.AddRange(kept);
        return companions;
    }

    /// <summary>
    /// A "[bg: …]" line as timed words. Word tags give the times (a word with no end runs to the
    /// next one, the last for <see cref="DefaultWordSpan"/>); a body with no tags is one word at
    /// <paramref name="lineStart"/>, where the lyrics page puts it. Null when it holds no text.
    /// Word tags move by the file's <paramref name="offset"/>, as the line's own do.
    /// </summary>
    private static List<AlignedWord>? ParseBackground(string line, TimeSpan lineStart, TimeSpan offset)
    {
        var body = line[BgLinePrefix.Length..];
        if (body.EndsWith(']')) body = body[..^1];
        var (plain, parsed) = EnhancedLrcParser.ParseLine(body);
        var words = Shifted(parsed, offset);
        if (string.IsNullOrWhiteSpace(plain)) return null;
        if (words is not { Count: > 0 })
            return [new AlignedWord(plain.Trim(), lineStart, lineStart + DefaultWordSpan)];

        var result = new List<AlignedWord>(words.Count);
        for (var w = 0; w < words.Count; w++)
        {
            var wText = words[w].Text.Trim();
            if (wText.Length == 0) continue;
            var end = words[w].End ?? (w + 1 < words.Count ? words[w + 1].Start : words[w].Start + DefaultWordSpan);
            result.Add(new AlignedWord(wText, words[w].Start, end < words[w].Start ? words[w].Start : end) { JoinsNext = FollowedWithoutSpace(words, w) });
        }
        return result.Count > 0 ? result : null;
    }

    /// <summary>
    /// The parsed word is followed by another with no space between: the ELRC reader keeps the
    /// space after a word on its text, and leaves CJK characters and lone punctuation unjoined.
    /// Without this a save put a space between every character of CJK lyrics ("偶 尔").
    /// </summary>
    private static bool FollowedWithoutSpace(List<WordTiming> words, int index) =>
        index + 1 < words.Count && words[index].Text is { Length: > 0 } text && !char.IsWhiteSpace(text[^1]);

    /// <summary>A time moved by the file's offset, never below 0:00 (as <see cref="LrcParser"/> does).</summary>
    private static TimeSpan Shift(TimeSpan t, TimeSpan offset) => t + offset < TimeSpan.Zero ? TimeSpan.Zero : t + offset;

    private static List<WordTiming>? Shifted(List<WordTiming>? words, TimeSpan offset) =>
        words is null || offset == TimeSpan.Zero ? words
            : words.Select(w => new WordTiming { Text = w.Text, Start = Shift(w.Start, offset), End = w.End is { } e ? Shift(e, offset) : null }).ToList();

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
