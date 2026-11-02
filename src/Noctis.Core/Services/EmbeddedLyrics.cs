using System.Globalization;
using System.Text;
using Noctis.Helpers;

namespace Noctis.Services;

/// <summary>The lyrics an audio file carries in its tags.</summary>
/// <param name="Text">The lyrics text field (USLT, LYRICS, ©lyr, …, or an alias other taggers use). May itself be LRC.</param>
/// <param name="Synced">Timed lyrics kept apart from the text field, as LRC/ELRC: a SYNCEDLYRICS
/// comment or the ID3v2 SYLT frame. Empty when the file has none.</param>
/// <param name="SyncedFromSylt">The synced text was built from an ID3v2 SYLT frame.</param>
public readonly record struct EmbeddedLyricsText(string Text, string Synced, bool SyncedFromSylt = false)
{
    public static EmbeddedLyricsText Empty { get; } = new(string.Empty, string.Empty);

    public bool IsEmpty => string.IsNullOrWhiteSpace(Text) && string.IsNullOrWhiteSpace(Synced);

    /// <summary>The timed text, wherever it is kept: the synced field, else a text field holding LRC. Empty when untimed.</summary>
    public string TimedText => !string.IsNullOrWhiteSpace(Synced) ? Synced
        : LyricsTextHelper.ContainsTimestamps(Text) ? Text : string.Empty;

    /// <summary>The untimed words: the text field when it is plain, else the timed text with its stamps stripped.</summary>
    public string PlainText => !string.IsNullOrWhiteSpace(Text) && !LyricsTextHelper.ContainsTimestamps(Text) ? Text
        : LyricsTextHelper.StripTimestamps(TimedText);
}

/// <summary>
/// Reads and writes lyrics in an audio file's own tags (GitHub #135). TagLib's
/// <c>Tag.Lyrics</c> only knows the main field (ID3v2 USLT, Vorbis LYRICS, MP4 ©lyr, APE
/// Lyrics, ASF WM/Lyrics); other taggers also use UNSYNCEDLYRICS (Mp3tag's Vorbis default),
/// "UNSYNCED LYRICS" (foobar2000), SYNCEDLYRICS, the same names as ID3v2 TXXX frames, and the
/// ID3v2 SYLT frame. Before #135 the scanner read only <c>Tag.Lyrics</c>, so a FLAC/Ogg/Opus
/// tagged by Mp3tag showed "No lyrics" and an MP3's SYLT never reached the desktop.
/// </summary>
public static class EmbeddedLyrics
{
    /// <summary>Untimed-lyrics names besides LYRICS, Vorbis comments and ID3v2 TXXX alike, as
    /// <see cref="Key"/> spells them: UNSYNCEDLYRICS (Mp3tag), "UNSYNCED LYRICS" (foobar2000),
    /// UNSYNCED_LYRICS, and the ID3v2 frame's own name, "Unsynchronised lyrics" (GitHub #149).</summary>
    private static readonly string[] TextAliases = ["UNSYNCEDLYRICS", "UNSYNCHRONISEDLYRICS", "UNSYNCHRONIZEDLYRICS"];

    /// <summary>Timed-lyrics names, Vorbis comments and ID3v2 TXXX alike, as <see cref="Key"/> spells them.</summary>
    private static readonly string[] SyncedAliases = ["SYNCEDLYRICS", "SYNCHRONISEDLYRICS", "SYNCHRONIZEDLYRICS"];

    /// <summary>A tag name as the alias lists spell it: upper case, without the spaces, underscores
    /// and hyphens taggers put between the words ("Unsynced Lyrics", UNSYNCED_LYRICS).</summary>
    private static string Key(string? name)
    {
        if (string.IsNullOrEmpty(name)) return string.Empty;
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
            if (c is not (' ' or '_' or '-')) sb.Append(char.ToUpperInvariant(c));
        return sb.ToString();
    }

    /// <summary>An untimed alias, or "LYRICS:ENG" / "LYRICS:&lt;description&gt;": the lyrics with
    /// their language or description in the name, as ID3v2's USLT frame carries them.</summary>
    private static bool IsTextAlias(string? name)
    {
        var key = Key(name);
        return TextAliases.Contains(key) || key.StartsWith("LYRICS:", StringComparison.Ordinal);
    }

    private static bool IsSyncedAlias(string? name) => SyncedAliases.Contains(Key(name));

    /// <summary>Opens <paramref name="path"/> (tags only) and reads its lyrics; empty on any failure.</summary>
    public static EmbeddedLyricsText ReadFile(string path)
    {
        try
        {
            using var file = TagLib.File.Create(path, TagLib.ReadStyle.None);
            return Read(file);
        }
        catch
        {
            return EmbeddedLyricsText.Empty;
        }
    }

    /// <summary>Opens <paramref name="audio"/> (type from <paramref name="fileName"/>'s extension) and reads
    /// its lyrics. Takes ownership of the stream. Empty on any failure.</summary>
    public static EmbeddedLyricsText ReadStream(Stream audio, string fileName)
    {
        try
        {
            using var abstraction = new MetadataService.StreamFileAbstraction(fileName, audio);
            using var file = TagLib.File.Create(abstraction, null, TagLib.ReadStyle.None);
            return Read(file);
        }
        catch
        {
            audio.Dispose();
            return EmbeddedLyricsText.Empty;
        }
    }

    /// <summary>Both kinds. A SYLT under a text field that is LRC itself is left out: the text is the
    /// richer copy (SYLT has no word timings; Noctis writes ELRC there with a line-level SYLT beside).</summary>
    public static EmbeddedLyricsText Read(TagLib.File file)
    {
        var text = ReadText(file);
        var synced = ReadSynced(file, out var sylt);
        if (sylt && LyricsTextHelper.ContainsTimestamps(text)) return new(text, string.Empty);
        return new(text, synced, sylt);
    }

    /// <summary>The lyrics text field: <c>Tag.Lyrics</c>, then UNSYNCEDLYRICS / "UNSYNCED LYRICS"
    /// (Vorbis comment, then ID3v2 TXXX), then a TXXX:LYRICS, then a Vorbis SYNCEDLYRICS with no
    /// stamps. Empty when none holds text.</summary>
    public static string ReadText(TagLib.File file)
    {
        // Every LYRICS comment, not only the first one TagLib's Tag.Lyrics returns: some taggers
        // write one comment per line (GitHub #149), and the song showed its first line alone.
        foreach (var xiph in XiphComments(file))
            if (FirstField(xiph, "LYRICS") is { } all) return all;

        string? main = null;
        try { main = file.Tag.Lyrics; } catch { }
        if (!string.IsNullOrWhiteSpace(main)) return main;

        foreach (var xiph in XiphComments(file))
            if (XiphAlias(xiph, IsTextAlias) is { } text) return text;

        if (Id3v2(file) is { } id3
            && Txxx(id3, d => Key(d) == "LYRICS" || IsTextAlias(d)) is { } txxx) return txxx;

        // A "synced" field holding words without a single stamp is still the song's lyrics.
        foreach (var xiph in XiphComments(file))
            if (XiphAlias(xiph, IsSyncedAlias, t => !LyricsTextHelper.ContainsTimestamps(t)) is { } text) return text;

        return string.Empty;
    }

    /// <summary>Timed lyrics kept apart from the text field, as LRC/ELRC: a SYNCEDLYRICS comment
    /// (or TXXX) holding timestamps, else the ID3v2 SYLT frame. Empty when the file has none.</summary>
    public static string ReadSynced(TagLib.File file, out bool fromSylt)
    {
        fromSylt = false;
        foreach (var xiph in XiphComments(file))
            if (XiphAlias(xiph, IsSyncedAlias, LyricsTextHelper.ContainsTimestamps) is { } text) return text;

        if (Id3v2(file) is not { } id3) return string.Empty;
        if (Txxx(id3, IsSyncedAlias, LyricsTextHelper.ContainsTimestamps) is { } timed) return timed;

        var lrc = SyltToLrc(id3);
        fromSylt = lrc.Length > 0;
        return lrc;
    }

    /// <summary>
    /// The first SYLT frame of type Lyrics with millisecond stamps, as LRC. One entry per line,
    /// or — when entries start with a line break, the spec's way of timing words or syllables —
    /// one line per break with the words as ELRC word tags. MPEG-frame stamps need the stream's
    /// frame rate and are skipped. Empty when there is no usable frame.
    /// </summary>
    internal static string SyltToLrc(TagLib.Id3v2.Tag tag)
    {
        foreach (var frame in tag.GetFrames<TagLib.Id3v2.SynchronisedLyricsFrame>())
        {
            if (frame.Type != TagLib.Id3v2.SynchedTextType.Lyrics) continue;
            if (frame.Format != TagLib.Id3v2.TimestampFormat.AbsoluteMilliseconds) continue;
            var entries = (frame.Text ?? [])
                .Where(e => e.Time >= 0 && !string.IsNullOrEmpty(e.Text))
                .OrderBy(e => e.Time) // stable: entries sharing a stamp keep their order
                .Take(LrcParser.MaxLines * 8)
                .ToList();
            if (entries.Count == 0) continue;

            var wordStyle = entries.Skip(1).Count(e => e.Text[0] is '\n' or '\r') > 0
                            && entries.Any(e => e.Text[0] is not ('\n' or '\r'));
            var sb = new StringBuilder();
            if (!wordStyle)
            {
                foreach (var e in entries)
                {
                    var text = OneLine(e.Text);
                    if (text.Length == 0) continue;
                    sb.Append('[').Append(Stamp(e.Time)).Append(']').Append(text).Append('\n');
                }
            }
            else
            {
                var line = new StringBuilder();
                long lineStart = -1;
                foreach (var e in entries)
                {
                    var startsLine = e.Text[0] is '\n' or '\r';
                    if (startsLine && lineStart >= 0)
                    {
                        Flush();
                        lineStart = -1;
                    }
                    var body = e.Text.TrimStart('\r', '\n').Replace('\r', ' ').Replace('\n', ' ');
                    var lead = body.Length - body.TrimStart().Length;
                    var word = body.TrimStart();
                    if (word.Length == 0) continue;
                    if (lineStart < 0) lineStart = e.Time;
                    else if (lead > 0) line.Append(' ');
                    line.Append('<').Append(Stamp(e.Time)).Append('>').Append(word);
                }
                Flush();

                void Flush()
                {
                    if (lineStart >= 0 && line.Length > 0)
                        sb.Append('[').Append(Stamp(lineStart)).Append(']').Append(line).Append('\n');
                    line.Clear();
                }
            }
            if (sb.Length > 0) return sb.ToString().TrimEnd('\n');
        }
        return string.Empty;
    }

    /// <summary>
    /// Puts lyrics in the file's tags, replacing what was there. The text field gets the synced
    /// text when there is any (ELRC word tags kept: the formats' text fields hold anything, and
    /// players that know LRC-in-tags read it), else the plain text. ID3v2 also gets a SYLT frame
    /// with the line timings. The alias fields other taggers use are removed, or the old lyrics
    /// would come back from them. Both empty clears every lyrics field.
    /// </summary>
    public static void Write(TagLib.File file, string? plain, string? synced)
    {
        var hasSynced = LyricsTextHelper.ContainsTimestamps(synced);
        var text = hasSynced ? Normalize(synced) : Normalize(plain);
        var value = text.Length == 0 ? null : text;

        // Main field on every tag the file already has (no foreign tag is created: TagLib's
        // combined setter only writes tags that exist).
        file.Tag.Lyrics = value;

        RemoveAliases(file, synced: true);

        if (Id3v2(file) is { } id3)
        {
            // One USLT holding the lyrics: TagLib's setter targets the frame for the tag's
            // language with no description, so a frame another tagger wrote with a
            // description ("Lyrics") or language would stay and could be read first.
            foreach (var uslt in id3.GetFrames<TagLib.Id3v2.UnsynchronisedLyricsFrame>().ToList()) id3.RemoveFrame(uslt);
            if (value != null)
                id3.AddFrame(new TagLib.Id3v2.UnsynchronisedLyricsFrame(string.Empty, Language(id3)) { Text = value });

            foreach (var frame in id3.GetFrames<TagLib.Id3v2.SynchronisedLyricsFrame>().ToList())
                if (frame.Type == TagLib.Id3v2.SynchedTextType.Lyrics)
                    id3.RemoveFrame(frame);
            if (hasSynced && BuildSylt(text, Language(id3)) is { } sylt)
                id3.AddFrame(sylt);
        }
    }

    /// <summary>The file's lyrics already are what <see cref="Write"/> would leave (nothing to rewrite).</summary>
    public static bool Holds(EmbeddedLyricsText current, string? plain, string? synced)
    {
        var hasSynced = LyricsTextHelper.ContainsTimestamps(synced);
        var want = hasSynced ? Normalize(synced) : Normalize(plain);
        if (want.Length == 0) return current.IsEmpty;
        if (Normalize(current.Text) != want) return false;
        // Plain lyrics: a SYLT / SYNCEDLYRICS left over would still show the old timed lyrics.
        // Synced: the SYLT is rebuilt from the text, so an equal text is enough.
        return hasSynced || string.IsNullOrWhiteSpace(current.Synced);
    }

    /// <summary>Line-level SYLT (milliseconds) of LRC/ELRC text; null when no line is timed.</summary>
    private static TagLib.Id3v2.SynchronisedLyricsFrame? BuildSylt(string lrc, string language)
    {
        var entries = new List<TagLib.Id3v2.SynchedText>();
        foreach (var line in LrcParser.Parse(lrc))
        {
            if (line.Timestamp is not { } at) continue;
            var words = line.Text.Replace("\r\n", " ").Replace('\n', ' ').Trim();
            if (words.Length == 0 || words == "...") continue;
            entries.Add(new TagLib.Id3v2.SynchedText((long)at.TotalMilliseconds, words));
        }
        if (entries.Count == 0) return null;
        return new TagLib.Id3v2.SynchronisedLyricsFrame(string.Empty, language, TagLib.Id3v2.SynchedTextType.Lyrics)
        {
            Format = TagLib.Id3v2.TimestampFormat.AbsoluteMilliseconds,
            Text = entries.ToArray(),
        };
    }

    private static string Language(TagLib.Id3v2.Tag id3)
    {
        var lang = id3.GetFrames<TagLib.Id3v2.UnsynchronisedLyricsFrame>().FirstOrDefault()?.Language;
        return lang is { Length: 3 } ? lang : "XXX";
    }

    private static IEnumerable<TagLib.Ogg.XiphComment> XiphComments(TagLib.File file)
    {
        TagLib.Tag? tag = null;
        try { tag = file.GetTag(TagLib.TagTypes.Xiph, false); } catch { }
        if (tag is TagLib.Ogg.XiphComment single) return [single];
        // Ogg containers group one comment per stream.
        if (file.Tag is TagLib.Ogg.GroupedComment grouped) return grouped.Comments;
        return [];
    }

    private static TagLib.Id3v2.Tag? Id3v2(TagLib.File file)
    {
        try { return file.GetTag(TagLib.TagTypes.Id3v2, false) as TagLib.Id3v2.Tag; }
        catch { return null; }
    }

    /// <summary>
    /// Removes the lyrics fields besides the main one, or the old lyrics would come back from
    /// them: the untimed aliases (Vorbis comments, and ID3v2 TXXX with TXXX:LYRICS), and with
    /// <paramref name="synced"/> the timed ones too.
    /// </summary>
    public static void RemoveAliases(TagLib.File file, bool synced)
    {
        bool Alias(string? name) => IsTextAlias(name) || (synced && IsSyncedAlias(name));
        foreach (var xiph in XiphComments(file))
            foreach (var name in xiph.Where(Alias).ToList())
                xiph.RemoveField(name);
        if (Id3v2(file) is { } id3)
            foreach (var frame in id3.GetFrames<TagLib.Id3v2.UserTextInformationFrame>().ToList())
                if (Key(frame.Description) == "LYRICS" || Alias(frame.Description))
                    id3.RemoveFrame(frame);
    }

    /// <summary>The first comment, in the file's order, whose name <paramref name="isName"/> accepts
    /// and whose text <paramref name="accept"/> does; a repeated comment's values joined by line.</summary>
    private static string? XiphAlias(TagLib.Ogg.XiphComment xiph, Func<string, bool> isName, Func<string, bool>? accept = null)
    {
        foreach (var name in xiph)
            if (isName(name) && FirstField(xiph, name) is { } text && (accept == null || accept(text)))
                return text;
        return null;
    }

    private static string? FirstField(TagLib.Ogg.XiphComment xiph, string name)
    {
        var values = xiph.GetField(name);
        var text = values is { Length: > 0 } ? string.Join("\n", values) : null;
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static string? Txxx(TagLib.Id3v2.Tag id3, Func<string, bool> isName, Func<string, bool>? accept = null)
    {
        foreach (var frame in id3.GetFrames<TagLib.Id3v2.UserTextInformationFrame>())
        {
            if (!isName(frame.Description ?? string.Empty)) continue;
            var text = string.Join("\n", frame.Text ?? []);
            if (!string.IsNullOrWhiteSpace(text) && (accept == null || accept(text))) return text;
        }
        return null;
    }

    private static string OneLine(string text) => text.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ').Trim();

    private static string Stamp(long ms)
    {
        var t = TimeSpan.FromMilliseconds(ms);
        return string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalMinutes:00}:{t.Seconds:00}.{t.Milliseconds / 10:00}");
    }

    /// <summary>LF line ends, no trailing blank space: how lyrics are compared and stored in tags.</summary>
    internal static string Normalize(string? text) =>
        (text ?? string.Empty).TrimStart('﻿').Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').TrimEnd();
}
