using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Noctis.Models;

namespace Noctis.Services;

/// <summary>
/// Parses TTML lyrics (Apple Music style and generic TTML subtitles) into
/// <see cref="LyricLine"/> objects, preserving optional per-word/syllable timing.
///
/// Each &lt;p&gt; becomes a line (begin/end → line timestamps). Timed &lt;span&gt;
/// children become karaoke words; whitespace text nodes between spans mark word
/// boundaries (adjacent spans with no whitespace are syllables and get merged) —
/// except pure XML indentation in documents that write their own spaces inside the
/// spans, see <see cref="UsesAuthoredSpaces"/>.
/// Untimed wrapper spans (e.g. Apple background vocals, ttm:role="x-bg") are
/// recursed into so their timed descendants still contribute.
/// Apple's translation and romanization layers (&lt;iTunesMetadata&gt; in the head,
/// keyed by each &lt;p&gt;'s itunes:key) attach to their lines — issue #78 — as do lrc.red's
/// copies of them (&lt;sourceMetadata&gt;, keyed by lrc:key).
/// Inline translation (<c>x-translation</c>) and romanization (<c>x-roman</c>) spans
/// become the same layers instead of words; an inline layer wins over the head's.
/// </summary>
public static class TtmlParser
{
    private static readonly XmlReaderSettings UntrustedXml = new()
    {
        DtdProcessing = DtdProcessing.Ignore,
        XmlResolver = null,
    };

    /// <summary>Cheap discriminator — TTML documents open with a &lt;tt&gt; root element.</summary>
    public static bool LooksLikeTtml(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return false;
        var idx = content.IndexOf("<tt", StringComparison.OrdinalIgnoreCase);
        return idx >= 0 && idx < 512;
    }

    /// <summary>
    /// Parses TTML content. Returns (lines, plainText) where plainText is the lines
    /// joined for the Unsync tab. Returns nulls when the content is malformed.
    /// </summary>
    /// <param name="joinSplitWords">
    /// When true (Settings → "Join Split Words"), whitespace that is only XML
    /// indentation stops counting as a word boundary, so a word split across several
    /// timed spans renders unbroken — see <see cref="UsesAuthoredSpaces"/> (issue #32).
    /// </param>
    /// <param name="preferredLanguage">
    /// UI language tag ("en", "es-MX"). When the document carries several translations
    /// (or transliterations), the one whose xml:lang shares its primary subtag wins;
    /// otherwise the first in the file.
    /// </param>
    /// <param name="maxLines">Lines read before the rest of the document is ignored: the hard
    /// guard, or <see cref="LrcParser.ListMaxLines"/> for a list that shows every line.</param>
    public static (List<LyricLine>? Lines, string? Plain) Parse(
        string? content, bool joinSplitWords = true, string? preferredLanguage = null,
        int maxLines = EnhancedLrcParser.MaxLyricLines)
    {
        if (string.IsNullOrWhiteSpace(content)) return (null, null);

        // An XML declaration must be the very first thing in the document: a leading blank
        // line, or a BOM character that survived a re-encode, made the reader throw and the
        // whole file read as "no lyrics" (GitHub #132).
        content = content.TrimStart().TrimStart('\uFEFF').TrimStart();

        XDocument doc;
        try
        {
            // Untrusted input (sidecars, and lyrics a desktop sends the phone): a DOCTYPE is
            // skipped, not processed — XDocument.Parse does process internal DTD entities —
            // and nothing external is ever resolved.
            // PreserveWhitespace keeps the whitespace-only text nodes between <span>s;
            // they are the word boundaries (spans with none between are syllables).
            using var reader = XmlReader.Create(new StringReader(content), UntrustedXml);
            doc = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        }
        catch (Exception)
        {
            // Catch-all, not just XmlException: this is untrusted input sitting next to
            // the user's music, and anything that escapes here reaches a caller whose
            // blanket catch turns it into a silent "no lyrics".
            return (null, null);
        }

        var root = doc.Root;
        if (root == null || !LocalNameIs(root, "tt")) return (null, null);

        // Only the body's word spans decide the convention: Apple head transliterations and
        // inline x-roman spans carry their own authored spaces and say nothing about the lyric.
        var body = root.Elements().FirstOrDefault(e => LocalNameIs(e, "body")) ?? root;
        var ignoreFormattingWhitespace = joinSplitWords && UsesAuthoredSpaces(body);

        var translations = LayerEntries(root, "translation", preferredLanguage);
        var transliterations = LayerEntries(root, "transliteration", preferredLanguage);

        var lines = new List<LyricLine>();
        var lineAgents = new List<string[]>();
        // Lines come from the body only — header metadata never renders or reaches the plain text.
        foreach (var p in root.Descendants().Where(e => LocalNameIs(e, "p") && !IsInHead(e)))
        {
            if (lines.Count >= maxLines) break;
            var line = ParseLine(p, ignoreFormattingWhitespace);
            if (line != null)
            {
                AttachLayers(line, p, translations, transliterations, ignoreFormattingWhitespace);
                lines.Add(line);
                lineAgents.Add(AgentIds(p));
            }
        }

        if (lines.Count == 0) return (null, null);

        AssignVoices(root, lines, lineAgents);

        // Stable: List.Sort is introsort, which reordered lines sharing a begin (overlapping
        // vocals) and every untimed <p> once a file passed 16 lines (GitHub #132).
        lines = lines.OrderBy(l => l.Timestamp).ToList();

        var plain = string.Join('\n', lines.Select(l => l.Text));
        return (lines, plain);
    }

    /// <summary>
    /// The TTML as the LRC text the lyrics editors work in, for importing a .ttml file
    /// (GitHub #132): enhanced LRC (<c>[mm:ss.xx]&lt;mm:ss.xx&gt;word … &lt;end&gt;</c>) for
    /// word-timed lines, <c>[mm:ss.xx]text</c> for the rest. Duet voices become "v2:"/"v3:"
    /// markers, background vocals a "[bg: …]" line, and the translation / romanization the
    /// same-timestamp lines <see cref="LrcParser"/> reads back as those layers. A document
    /// with no timing at all is its plain lines; null when it does not parse.
    /// </summary>
    public static string? ToLrc(string? content, bool joinSplitWords = true, string? preferredLanguage = null)
    {
        var (lines, plain) = Parse(content, joinSplitWords, preferredLanguage);
        if (lines is not { Count: > 0 }) return null;
        if (!lines.Any(l => l.Timestamp.HasValue)) return plain;
        return ToLrc(lines);
    }

    /// <summary>
    /// Parsed lines back as the LRC text <see cref="LrcParser"/> reads into the same lines: word
    /// timing as ELRC word tags, duet voices as "v2:"/"v3:", background vocals as a "[bg: …]"
    /// line, romanization / translation as lines at the same timestamp. Untimed lines are their
    /// text, in place.
    /// </summary>
    public static string ToLrc(IReadOnlyList<LyricLine> lines)
    {
        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            if (line.Timestamp is not { } start)
            {
                sb.Append(OneLine(line.Text)).Append('\n');
                continue;
            }

            var stamp = "[" + FormatLrcTime(start) + "]" + line.Voice switch
            {
                LyricVoice.Voice2 => "v2:",
                LyricVoice.Group => "v3:",
                _ => string.Empty,
            };

            // A background-only paragraph is its adlib words, sung as the line.
            var words = line.IsBackgroundOnly ? line.BackgroundWords : line.Words;
            sb.Append(stamp).Append(words is { Count: > 0 } ? ElrcBody(words, line.EndTimestamp) : OneLine(line.Text)).Append('\n');

            if (!line.IsBackgroundOnly && line.BackgroundWords is { Count: > 0 } bg)
                sb.Append("[bg: ").Append(ElrcBody(bg, line.BackgroundEndTimestamp)).Append("]\n");

            // The LRC reader takes a word-timed "(…)" line as an adlib of the line before it and
            // gives it no layers: its translation or romanization at the same stamp would come
            // back as a sung line of its own, so an adlib's layers are not written.
            if (words is { Count: > 0 } && IsFullyParenthesized(line.Text)) continue;

            foreach (var companion in LrcParser.CompanionLines(line))
                sb.Append(stamp).Append(OneLine(companion)).Append('\n');
        }
        return sb.ToString().TrimEnd('\n');

        static string OneLine(string text) => text.Replace("\r\n", " ").Replace('\n', ' ').Trim();

        // EnhancedLrcParser.IsBackgroundCandidate's text rule.
        static bool IsFullyParenthesized(string text)
        {
            text = text.Trim();
            if (text.Length < 2 || text[0] is not ('(' or '（') || text[^1] is not (')' or '）')) return false;
            return !text[1..^1].Any(c => c is '(' or ')' or '（' or '）');
        }

        static string ElrcBody(IReadOnlyList<WordTiming> words, TimeSpan? lineEnd)
        {
            var body = new StringBuilder();
            for (var i = 0; i < words.Count; i++)
            {
                // The space between words rides on the word before it ("Paper "), or on the word
                // itself when its span wrote it first (" world"); spans with none between them
                // that stay separate words (CJK characters, lone punctuation) get none, or an
                // imported "偶尔" came back as "偶 尔".
                if (i > 0 && (words[i - 1].Text is { Length: > 0 } before && char.IsWhiteSpace(before[^1])
                    || words[i].Text is { Length: > 0 } own && char.IsWhiteSpace(own[0]))) body.Append(' ');
                var text = OneLine(words[i].Text);
                // A word sung in syllables (spans with no space between) tags each syllable,
                // which the ELRC reader joins back into the word with the same clocks.
                if (words[i].Syllables is { Count: > 1 } syllables
                    && words[i].Text.TrimEnd() == text
                    && syllables.Sum(s => s.Length) == text.Length
                    && syllables.All(s => s.Length > 0))
                {
                    var at = 0;
                    foreach (var s in syllables)
                    {
                        body.Append('<').Append(FormatLrcTime(s.Start)).Append('>').Append(text, at, s.Length);
                        at += s.Length;
                    }
                    continue;
                }
                body.Append('<').Append(FormatLrcTime(words[i].Start)).Append('>').Append(text);
            }
            var end = words[^1].End ?? lineEnd ?? words[^1].Start;
            return body.Append('<').Append(FormatLrcTime(end)).Append('>').ToString();
        }
    }

    /// <summary>mm:ss.xx, the stamp every LRC writer in the app uses.</summary>
    private static string FormatLrcTime(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalMinutes:00}:{t.Seconds:00}.{t.Milliseconds / 10:00}");
    }

    /// <summary>
    /// True when the document separates words with real spaces written inside the timed
    /// spans (<c>&lt;span&gt;Is &lt;/span&gt;</c>) rather than relying on the whitespace
    /// between elements. Those files are usually pretty-printed, and their newline+indent
    /// between spans is XML formatting, not a word break — treating it as one is what
    /// rendered "compromise" as "com pro mise" (issue #32).
    ///
    /// Documents with no authored space anywhere use the opposite convention (the markup
    /// whitespace IS the separator), so they keep the original handling; dropping it there
    /// would fuse a whole line into one unwrappable word. A file mixing both conventions
    /// picks this branch and over-joins the lines that rely on indentation — the Settings
    /// toggle is the escape hatch.
    /// </summary>
    private static bool UsesAuthoredSpaces(XElement scope) =>
        scope.Descendants().Any(e =>
            LocalNameIs(e, "span")
            && e.Attribute("begin") != null
            && !e.Elements().Any()
            && e.Value.Length > 0
            && (char.IsWhiteSpace(e.Value[0]) || char.IsWhiteSpace(e.Value[^1]))
            // Cheap checks above filter out almost every span before this walk runs, so
            // the ancestor chain (worst case O(depth) per span) stays off the hot path
            // for ordinary documents — see MaxNestingDepth for why depth is untrusted.
            && !e.AncestorsAndSelf().Any(a => AuxiliaryRole(a) != null));

    /// <summary>Whitespace-only text carrying a line break is XML indentation, not content.</summary>
    private static bool IsFormattingWhitespace(string value) =>
        string.IsNullOrWhiteSpace(value) && (value.Contains('\n') || value.Contains('\r'));

    private static LyricLine? ParseLine(XElement p, bool ignoreFormattingWhitespace)
    {
        var start = ParseTime(p.Attribute("begin")?.Value);
        var end = ParseTime(p.Attribute("end")?.Value);
        if (end.HasValue && start.HasValue && end < start) end = null;

        var text = new StringBuilder();
        var words = new List<WordTiming>();
        var bgText = new StringBuilder();
        var bgWords = new List<WordTiming>();
        var layers = new LineLayers();
        CollectContent(p, text, words, bgText, bgWords, layers, inBackground: false,
            ignoreFormattingWhitespace: ignoreFormattingWhitespace);

        var lineText = text.ToString().Trim();
        var bgLineText = bgText.ToString().Trim();

        // Untimed text ahead of the first timed span (mixed content) is a word of its own from
        // the line's begin, like an ELRC line's untagged first word. The word row replaces the
        // line text on screen, so text left out of it was never shown.
        if (words.Count > 0 && layers.LeadText is { } lead && lead.Trim().Length > 0)
        {
            var first = words[0].Start;
            words.Insert(0, new WordTiming { Text = lead.TrimStart(), Start = start is { } s && s < first ? s : first, End = first });
        }

        // Too many words to be a real karaoke line (each realizes its own cell): keep the
        // text, drop word timing. An oversized background row is dropped, as in
        // EnhancedLrcParser.AppendBackground.
        if (words.Count > EnhancedLrcParser.MaxWordsPerLine) words.Clear();
        if (bgWords.Count > EnhancedLrcParser.MaxWordsPerLine) bgWords.Clear();

        // Background-only paragraph (entire <p> is an x-bg span): keep it — it renders
        // as just the small bg row; its Text feeds the Unsync tab.
        var backgroundOnly = lineText.Length == 0 && bgWords.Count > 0;
        if (lineText.Length == 0 && !backgroundOnly) return null;

        var line = new LyricLine
        {
            Timestamp = start,
            EndTimestamp = end,
            Text = backgroundOnly ? bgLineText : lineText,
            IsBackgroundOnly = backgroundOnly,
            Translation = layers.Translation,
            Transliteration = layers.Romanization,
        };

        if (words.Count > 0)
            line.Words = FinishWords(words, end);

        if (bgWords.Count > 0)
        {
            var finished = FinishWords(bgWords, end);
            line.BackgroundEndTimestamp = finished[^1].End;
            line.BackgroundWords = finished;
        }

        return line;
    }

    private static bool IsInHead(XElement e) => e.Ancestors().Any(a => LocalNameIs(a, "head"));

    /// <summary>
    /// The agent ids (ttm:agent, an IDREFS list) singing a paragraph: its own, else the
    /// nearest ancestor's (a div can carry the agent for all its lines). Empty when none.
    /// </summary>
    private static string[] AgentIds(XElement p)
    {
        foreach (var e in p.AncestorsAndSelf())
        {
            var attr = e.Attributes().FirstOrDefault(a =>
                string.Equals(a.Name.LocalName, "agent", StringComparison.OrdinalIgnoreCase));
            if (attr != null)
                return attr.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).ToArray();
        }
        return Array.Empty<string>();
    }

    /// <summary>
    /// Maps TTML agents onto the lyrics page's duet layout (<see cref="LyricVoice"/>, the same
    /// one ELRC "v1:"/"v2:"/"v3:" markers drive). The first person singing (in head
    /// declaration order, then order of appearance for undeclared ids) keeps the normal
    /// side; the second person takes the opposite side. An agent declared type="group", or
    /// several agents on one line, is <see cref="LyricVoice.Group"/>. Further persons, and
    /// lines with no agent, stay on the normal side — so a single-singer file looks exactly
    /// as it did before agents were read.
    /// </summary>
    private static void AssignVoices(XElement root, List<LyricLine> lines, List<string[]> lineAgents)
    {
        if (lineAgents.All(a => a.Length == 0)) return;

        var declaredOrder = new Dictionary<string, int>(StringComparer.Ordinal);
        var groups = new HashSet<string>(StringComparer.Ordinal);
        foreach (var agent in root.Descendants().Where(e => LocalNameIs(e, "agent") && IsInHead(e)))
        {
            if (agent.Attribute(XNamespace.Xml + "id")?.Value.Trim() is not { Length: > 0 } id) continue;
            declaredOrder.TryAdd(id, declaredOrder.Count);
            if (string.Equals(agent.Attribute("type")?.Value.Trim(), "group", StringComparison.OrdinalIgnoreCase))
                groups.Add(id);
        }

        var persons = lineAgents
            .Where(a => a.Length == 1 && !groups.Contains(a[0]))
            .Select((a, appearance) => (Id: a[0], Appearance: appearance))
            .GroupBy(x => x.Id, StringComparer.Ordinal)
            .Select(g => (g.Key, Appearance: g.Min(x => x.Appearance)))
            .OrderBy(x => declaredOrder.TryGetValue(x.Key, out var order) ? order : int.MaxValue)
            .ThenBy(x => x.Appearance)
            .Select(x => x.Key)
            .ToList();
        var secondPerson = persons.Count > 1 ? persons[1] : null;

        for (var i = 0; i < lines.Count; i++)
        {
            var ids = lineAgents[i];
            lines[i].Voice = ids.Length > 1 || (ids.Length == 1 && groups.Contains(ids[0])) ? LyricVoice.Group
                : ids.Length == 1 && ids[0] == secondPerson ? LyricVoice.Voice2
                : LyricVoice.Default;
        }
    }

    /// <summary>
    /// Maps one Apple layer's &lt;text for="L#"&gt; entries by key. The layer elements
    /// (&lt;translation&gt; / &lt;transliteration&gt;) sit under &lt;iTunesMetadata&gt;, or
    /// under &lt;sourceMetadata&gt; in lrc.red's copy of Apple's TTML (only iTunesMetadata was
    /// read, so lrc.red's translations and romaji never showed);
    /// with several languages the one matching <paramref name="preferredLanguage"/>'s
    /// primary subtag is used, else the first in the file. Empty when there is none.
    /// </summary>
    private static Dictionary<string, XElement> LayerEntries(
        XElement root, string layerName, string? preferredLanguage)
    {
        var entries = new Dictionary<string, XElement>(StringComparer.Ordinal);
        var layers = root.Descendants()
            .Where(e => LocalNameIs(e, "iTunesMetadata") || LocalNameIs(e, "sourceMetadata"))
            .SelectMany(m => m.Descendants().Where(e => LocalNameIs(e, layerName)))
            .ToList();
        if (layers.Count == 0) return entries;

        var chosen = layers.FirstOrDefault(l =>
                         SamePrimaryLanguage(l.Attribute(XNamespace.Xml + "lang")?.Value, preferredLanguage))
                     ?? layers[0];
        foreach (var text in chosen.Elements().Where(e => LocalNameIs(e, "text")))
        {
            if (text.Attribute("for")?.Value.Trim() is { Length: > 0 } key)
                entries.TryAdd(key, text);
        }
        return entries;
    }

    private static bool SamePrimaryLanguage(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        return string.Equals(PrimarySubtag(a), PrimarySubtag(b), StringComparison.OrdinalIgnoreCase);

        static string PrimarySubtag(string tag)
        {
            tag = tag.Trim();
            var cut = tag.IndexOfAny(new[] { '-', '_' });
            return cut < 0 ? tag : tag[..cut];
        }
    }

    /// <summary>
    /// Attaches the translation / romanization entries keyed by the paragraph's
    /// itunes:key. Entries are walked like a line body, so romaji syllable spans
    /// ("fu","ri") join into words exactly as the main line's spans do.
    /// </summary>
    private static void AttachLayers(
        LyricLine line, XElement p,
        Dictionary<string, XElement> translations, Dictionary<string, XElement> transliterations,
        bool ignoreFormattingWhitespace)
    {
        if (translations.Count == 0 && transliterations.Count == 0) return;
        var key = p.Attributes()
            .FirstOrDefault(a => string.Equals(a.Name.LocalName, "key", StringComparison.OrdinalIgnoreCase))
            ?.Value.Trim();
        if (string.IsNullOrEmpty(key)) return;

        // An inline x-translation / x-roman span on the paragraph already set the layer; it wins.
        if (line.Translation == null && translations.TryGetValue(key, out var translation))
        {
            var (text, _) = CollectLayer(translation, ignoreFormattingWhitespace);
            if (text.Length > 0) line.Translation = text;
        }

        if (line.Transliteration == null && transliterations.TryGetValue(key, out var transliteration))
        {
            var (text, words) = CollectLayer(transliteration, ignoreFormattingWhitespace);
            if (text.Length > 0) line.Transliteration = text;
            if (words.Count > 0 && words.Count <= EnhancedLrcParser.MaxWordsPerLine)
            {
                var finished = FinishWords(words, line.EndTimestamp);
                line.TransliterationEndTimestamp = finished[^1].End;
                line.TransliterationWords = finished;
            }
        }
    }

    /// <summary>
    /// One layer entry's text and timed words. Background-role text inside the entry
    /// follows the main text; its timed words are only used when the entry has no
    /// main-role words (a background-only line), since the row is a single clock.
    /// </summary>
    private static (string Text, List<WordTiming> Words) CollectLayer(XElement entry, bool ignoreFormattingWhitespace)
    {
        var text = new StringBuilder();
        var words = new List<WordTiming>();
        var bgText = new StringBuilder();
        var bgWords = new List<WordTiming>();
        CollectContent(entry, text, words, bgText, bgWords, new LineLayers(), inBackground: false,
            ignoreFormattingWhitespace: ignoreFormattingWhitespace);

        var main = text.ToString().Trim();
        var bg = bgText.ToString().Trim();
        var joined = main.Length == 0 ? bg : bg.Length == 0 ? main : main + " " + bg;
        return (joined, words.Count > 0 ? words : bgWords);
    }

    private static List<WordTiming> FinishWords(List<WordTiming> words, TimeSpan? end)
    {
        // Trailing spaces are preserved on words except the final one (Lyricsfile
        // convention) — markup whitespace before the closing tag shouldn't widen the last cell.
        var lastWord = words[^1];
        if (lastWord.Text.TrimEnd() is { Length: > 0 } trimmed && trimmed != lastWord.Text)
        {
            words[^1] = new WordTiming { Text = trimmed, Start = lastWord.Start, End = lastWord.End };
        }

        // Backfill missing End times from the next word's Start, and the final word
        // from the line end (falls back to last word's Start + 300ms as a minimum).
        for (int i = 0; i < words.Count; i++)
        {
            if (words[i].End.HasValue) continue;
            TimeSpan fallback;
            if (i + 1 < words.Count)
                fallback = words[i + 1].Start;
            else if (end.HasValue)
                fallback = end.Value;
            else
                fallback = words[i].Start + TimeSpan.FromMilliseconds(300);

            words[i] = new WordTiming
            {
                Text = words[i].Text,
                Start = words[i].Start,
                End = fallback,
            };
        }

        return EnhancedLrcParser.MergeSyllables(words);
    }

    /// <summary>
    /// Walks a line's content in document order. Timed spans append a word; whitespace
    /// between spans is attached to the previous word's tail (Lyricsfile convention:
    /// trailing spaces preserved) so syllable merging keeps word boundaries intact.
    /// Spans marked ttm:role="x-bg" (Apple background vocals) collect into the
    /// background text/word buffers instead of the main line.
    /// </summary>
    /// <summary>
    /// Nesting limit for the recursive content walk. XDocument.Parse builds the tree
    /// iteratively, so a sidecar with thousands of nested &lt;span&gt; elements parsed
    /// fine and then blew the 1 MB stack here — and StackOverflowException cannot be
    /// caught, so the blanket try/catch around the lyrics probe did not help and the
    /// process died. Real Apple TTML nests two or three levels.
    /// </summary>
    private const int MaxNestingDepth = 64;

    private static void CollectContent(
        XElement parent,
        StringBuilder text, List<WordTiming> words,
        StringBuilder bgText, List<WordTiming> bgWords,
        LineLayers layers,
        bool inBackground,
        bool ignoreFormattingWhitespace,
        int depth = 0)
    {
        if (depth > MaxNestingDepth) return;

        var targetText = inBackground ? bgText : text;
        var targetWords = inBackground ? bgWords : words;

        foreach (var node in parent.Nodes())
        {
            switch (node)
            {
                // Indentation between spans of a document that writes its own spaces
                // is formatting — swallowing it keeps split words whole (issue #32).
                case XText t when ignoreFormattingWhitespace && IsFormattingWhitespace(t.Value):
                    break;

                // Text wrapped across source lines is one run of words: XML's default
                // whitespace handling makes the newline + indentation a single space (#132).
                case XText t:
                    AppendText(CollapseLineBreaks(t.Value), targetText, targetWords);
                    break;

                case XElement el when LocalNameIs(el, "br"):
                    AppendText(" ", targetText, targetWords);
                    break;

                // Translation / romanization spans are layers of the line, not words. The
                // untimed-wrapper path below used to walk into them, appending the English
                // to the line and gluing it onto the last Japanese word. First span per
                // role wins (one language per layer); inside background vocals they
                // translate the adlib, not the line, and are dropped.
                case XElement el when LocalNameIs(el, "span") && AuxiliaryRole(el) is { } role:
                    if (!inBackground)
                    {
                        var layerText = CollapseWhitespace(TextWithoutBackground(el));
                        if (layerText.Length > 0)
                        {
                            if (role == LayerRole.Translation) layers.Translation ??= layerText;
                            else layers.Romanization ??= layerText;
                        }
                    }
                    break;

                case XElement el when LocalNameIs(el, "span"):
                    var isBg = inBackground || IsBackgroundRole(el);
                    var spanText = isBg ? bgText : text;
                    var spanWords = isBg ? bgWords : words;

                    var begin = ParseTime(el.Attribute("begin")?.Value);
                    if (begin.HasValue && !el.Elements().Any())
                    {
                        var wordText = el.Value;
                        if (wordText.Length > 0)
                        {
                            if (!isBg && words.Count == 0) layers.LeadText ??= text.ToString();
                            spanText.Append(wordText);
                            spanWords.Add(new WordTiming
                            {
                                Text = wordText,
                                Start = begin.Value,
                                End = ParseTime(el.Attribute("end")?.Value) is { } e && e >= begin ? e : null,
                            });
                        }
                    }
                    else
                    {
                        // Untimed or wrapper span — recurse (background wrappers route
                        // their timed descendants into the background buffers).
                        CollectContent(el, text, words, bgText, bgWords, layers, isBg,
                            ignoreFormattingWhitespace, depth + 1);
                    }
                    break;
            }
        }
    }

    private static bool IsBackgroundRole(XElement el) =>
        el.Attributes().Any(a =>
            string.Equals(a.Name.LocalName, "role", StringComparison.OrdinalIgnoreCase)
            && string.Equals(a.Value, "x-bg", StringComparison.OrdinalIgnoreCase));

    private enum LayerRole { Translation, Romanization }

    /// <summary>Inline layer text collected while walking one &lt;p&gt;.</summary>
    private sealed class LineLayers
    {
        public string? Translation;
        public string? Romanization;
        /// <summary>The line text written before its first timed word (often empty).</summary>
        public string? LeadText;
    }

    private static LayerRole? AuxiliaryRole(XElement el)
    {
        foreach (var a in el.Attributes())
        {
            if (!string.Equals(a.Name.LocalName, "role", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(a.Value, "x-translation", StringComparison.OrdinalIgnoreCase)) return LayerRole.Translation;
            if (string.Equals(a.Value, "x-roman", StringComparison.OrdinalIgnoreCase)) return LayerRole.Romanization;
        }
        return null;
    }

    /// <summary>Text of an element minus any nested x-bg span (background vocals inside a
    /// translation translate the adlib, not the line), with each &lt;br&gt; contributing a
    /// single space (matching the main-line walk in <see cref="CollectContent"/>). An
    /// explicit-stack pre-order walk: linear time (each node visited once, no per-text
    /// ancestor climb) and non-recursive, so hostile nesting cannot recurse the call stack —
    /// descent stops past <see cref="MaxNestingDepth"/>.</summary>
    private static string TextWithoutBackground(XElement el)
    {
        var sb = new StringBuilder();
        var stack = new Stack<(XNode Node, int Depth)>();
        foreach (var child in el.Nodes().Reverse())
            stack.Push((child, 1));

        while (stack.Count > 0)
        {
            var (node, depth) = stack.Pop();
            if (depth > MaxNestingDepth) continue;

            switch (node)
            {
                case XText t:
                    sb.Append(t.Value);
                    break;
                case XElement e when LocalNameIs(e, "br"):
                    sb.Append(' ');
                    break;
                case XElement e when IsBackgroundRole(e):
                    // Skip the whole x-bg subtree — it translates the adlib, not the line.
                    break;
                case XElement e:
                    foreach (var grandchild in e.Nodes().Reverse())
                        stack.Push((grandchild, depth + 1));
                    break;
            }
        }

        return sb.ToString();
    }

    /// <summary>Each whitespace run holding a line break becomes one space; other text is untouched.</summary>
    private static string CollapseLineBreaks(string s)
    {
        if (s.IndexOfAny(new[] { '\n', '\r' }) < 0 || string.IsNullOrWhiteSpace(s)) return s;
        var sb = new StringBuilder(s.Length);
        var i = 0;
        while (i < s.Length)
        {
            if (!char.IsWhiteSpace(s[i])) { sb.Append(s[i++]); continue; }
            var start = i;
            var hasBreak = false;
            while (i < s.Length && char.IsWhiteSpace(s[i]))
            {
                if (s[i] is '\n' or '\r') hasBreak = true;
                i++;
            }
            if (hasBreak) sb.Append(' ');
            else sb.Append(s, start, i - start);
        }
        return sb.ToString();
    }

    private static string CollapseWhitespace(string s) =>
        string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static void AppendText(string value, StringBuilder text, List<WordTiming> words)
    {
        if (value.Length == 0) return;

        // Collapse whitespace runs (markup indentation/newlines) to a single space.
        var isWhitespace = string.IsNullOrWhiteSpace(value);
        if (isWhitespace)
        {
            if (text.Length > 0 && text[^1] != ' ')
                text.Append(' ');
        }
        else
        {
            text.Append(value);
        }

        // Whitespace after a timed word marks the word boundary; keep it on the word's
        // tail so MergeSyllables won't join across it. Non-whitespace inter-span text
        // (rare) also rides along rather than becoming an untimed orphan cell.
        if (words.Count > 0)
        {
            var last = words[^1];
            var tail = isWhitespace
                ? (last.Text.EndsWith(' ') ? string.Empty : " ")
                : value;
            if (tail.Length == 0) return;
            words[^1] = new WordTiming
            {
                Text = last.Text + tail,
                Start = last.Start,
                End = last.End,
            };
        }
    }

    private static bool LocalNameIs(XElement e, string name) =>
        string.Equals(e.Name.LocalName, name, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Parses a TTML time expression. Supports clock-time ("mm:ss.fff", "hh:mm:ss.fff")
    /// and offset-time ("7.5s", "1500ms", "2m", "1.5h", bare seconds). Frame/tick
    /// metrics ("25f", "10t") are rare in lyrics TTML and return null.
    /// </summary>
    internal static TimeSpan? ParseTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();

        if (value.Contains(':'))
        {
            var parts = value.Split(':');
            // hh:mm:ss:frames (4 parts) is frame-based — unsupported.
            if (parts.Length is < 2 or > 3) return null;

            double hours = 0, minutes, seconds;
            if (parts.Length == 3)
            {
                if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out hours)) return null;
                if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out minutes)) return null;
                if (!double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out seconds)) return null;
            }
            else
            {
                if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out minutes)) return null;
                if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out seconds)) return null;
            }

            // Since .NET Core 3.0 double.TryParse returns true for "NaN" and for
            // overflowing literals (as +/-Infinity), and TimeSpan.FromHours then throws
            // OverflowException/ArgumentException — out of a method whose only caller
            // wraps it in a blanket catch, so one bad attribute silently turned the whole
            // file into "no lyrics" and triggered an online search that overwrote the
            // neighbouring sidecars.
            if (!IsSaneComponent(hours) || !IsSaneComponent(minutes) || !IsSaneComponent(seconds))
                return null;

            var total = TimeSpan.FromHours(hours) + TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(seconds);
            return total >= TimeSpan.Zero && total <= MaxTime ? total : null;
        }

        // Offset-time: number + optional metric suffix.
        double multiplierMs;
        string numberPart;
        if (value.EndsWith("ms", StringComparison.OrdinalIgnoreCase))
        {
            multiplierMs = 1;
            numberPart = value[..^2];
        }
        else if (value.EndsWith("h", StringComparison.OrdinalIgnoreCase))
        {
            multiplierMs = 3_600_000;
            numberPart = value[..^1];
        }
        else if (value.EndsWith("m", StringComparison.OrdinalIgnoreCase))
        {
            multiplierMs = 60_000;
            numberPart = value[..^1];
        }
        else if (value.EndsWith("s", StringComparison.OrdinalIgnoreCase))
        {
            multiplierMs = 1000;
            numberPart = value[..^1];
        }
        else if (value.EndsWith("f", StringComparison.OrdinalIgnoreCase)
              || value.EndsWith("t", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        else
        {
            // Bare number — treat as seconds (seen in the wild, not spec-strict).
            multiplierMs = 1000;
            numberPart = value;
        }

        if (!double.TryParse(numberPart, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)) return null;

        var ms = n * multiplierMs;
        if (!double.IsFinite(ms) || ms < 0 || ms > MaxTime.TotalMilliseconds) return null;

        var result = TimeSpan.FromMilliseconds(ms);
        return result >= TimeSpan.Zero ? result : null;
    }

    /// <summary>Upper bound for any parsed timestamp. No song is 24 hours long.</summary>
    private static readonly TimeSpan MaxTime = TimeSpan.FromHours(24);

    /// <summary>Finite and within a range TimeSpan can represent for this use.</summary>
    private static bool IsSaneComponent(double v)
        => double.IsFinite(v) && v >= 0 && v < 100_000;
}
