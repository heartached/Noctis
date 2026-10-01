using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Noctis.Services.LyricsStudio;

namespace Noctis.Services.Lyrics;

/// <summary>
/// Kugou's KRC lyrics (issue #113). A download is "krc1" followed by a zlib stream XOR-ed
/// with a fixed 16-byte key (verified 2026-10-01 against lyrics.kugou.com/download?fmt=krc).
/// The decrypted text is LRC-like — header tags, then one line per lyric,
/// <c>[lineStartMs,lineDurMs]&lt;wordOffsetMs,wordDurMs,0&gt;word…</c> — which maps onto the
/// app's ELRC (<c>[mm:ss.xx]&lt;mm:ss.xx&gt;word …&lt;mm:ss.xx&gt;</c>) word for word.
/// </summary>
public static partial class KrcLyrics
{
    private static ReadOnlySpan<byte> Key => [64, 71, 97, 119, 94, 50, 116, 71, 81, 54, 49, 45, 206, 210, 110, 105];

    [GeneratedRegex(@"^\[(\d+),(\d+)\](.*)$")]
    private static partial Regex LineRegex();

    [GeneratedRegex(@"<(\d+),(\d+),\d+>([^<]*)")]
    private static partial Regex WordRegex();

    // "Lyrics by：", "Composed by:", "作词：" — the credit lines Kugou puts above the lyrics.
    [GeneratedRegex(@"^\s*(?:[\p{L}\p{M}]+\s){0,3}by\s*[:：]|^\s*(?:作词|作曲|编曲|制作人|词|曲)\s*[:：]", RegexOptions.IgnoreCase)]
    private static partial Regex CreditRegex();

    /// <summary>Decrypts a KRC download. Throws <see cref="InvalidDataException"/> when it isn't one.</summary>
    public static string Decrypt(byte[] krc)
    {
        if (krc.Length < 4 || krc[0] != (byte)'k' || krc[1] != (byte)'r' || krc[2] != (byte)'c' || krc[3] != (byte)'1')
            throw new InvalidDataException("Not a KRC payload.");

        var key = Key;
        var body = new byte[krc.Length - 4];
        for (var i = 0; i < body.Length; i++)
            body[i] = (byte)(krc[i + 4] ^ key[i % key.Length]);

        using var z = new ZLibStream(new MemoryStream(body), CompressionMode.Decompress);
        using var output = new MemoryStream();
        var chunk = new byte[16384];
        int read;
        while ((read = z.Read(chunk, 0, chunk.Length)) > 0)
        {
            // Same cap as every other remote text payload: a tiny download must not inflate without bound.
            if (output.Length + read > HttpSafety.MaxTextBytes)
                throw new InvalidDataException("KRC payload inflates past the size limit.");
            output.Write(chunk, 0, read);
        }
        return Encoding.UTF8.GetString(output.ToArray());
    }

    /// <summary>
    /// Converts decrypted KRC text to ELRC; null when it holds no lyric lines. Lone-space
    /// tokens fold into the word before them (so CJK lines stay unspaced and Latin lines keep
    /// their spaces), and the title / credit lines Kugou puts above the lyrics are dropped.
    /// </summary>
    public static string? ToElrc(string krc)
    {
        string? title = null, artist = null;
        var output = new List<string>();
        var inHead = true;
        var sawLine = false;

        foreach (var raw in krc.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("[ti:", StringComparison.OrdinalIgnoreCase)) { title = TagValue(line); continue; }
            if (line.StartsWith("[ar:", StringComparison.OrdinalIgnoreCase)) { artist = TagValue(line); continue; }

            var m = LineRegex().Match(line);
            if (!m.Success) continue;

            var lineStart = Ms(m.Groups[1].Value);
            var (text, body) = BuildBody(lineStart, m.Groups[3].Value);
            if (string.IsNullOrWhiteSpace(text)) continue;

            if (inHead)
            {
                if (IsHeadLine(text, title, artist, first: !sawLine)) { sawLine = true; continue; }
                inHead = false;
            }

            sawLine = true;
            output.Add($"[{TimedLyricsBuilder.FormatTimestamp(lineStart)}]{body}");
        }

        return output.Count == 0 ? null : string.Join('\n', output);
    }

    /// <summary>A KRC line body → (display text, ELRC body). Untagged bodies pass through as plain line text.</summary>
    private static (string Text, string Body) BuildBody(TimeSpan lineStart, string krcBody)
    {
        var matches = WordRegex().Matches(krcBody);
        if (matches.Count == 0)
        {
            var plain = krcBody.Trim();
            return (plain, plain);
        }

        var words = new List<(string Text, TimeSpan Start, TimeSpan End)>();
        foreach (Match w in matches)
        {
            var text = w.Groups[3].Value;
            var start = lineStart + Ms(w.Groups[1].Value);
            var end = start + Ms(w.Groups[2].Value);
            if (string.IsNullOrWhiteSpace(text))
            {
                // A space token belongs to the word before it; a leading one is dropped.
                if (words.Count > 0)
                    words[^1] = (words[^1].Text + text, words[^1].Start, end);
                continue;
            }
            words.Add((text, start, end));
        }

        if (words.Count == 0) return (string.Empty, string.Empty);

        var sb = new StringBuilder();
        foreach (var (text, start, _) in words)
            sb.Append('<').Append(TimedLyricsBuilder.FormatTimestamp(start)).Append('>').Append(text);
        var trimmed = sb.ToString().TrimEnd();
        var body = trimmed + "<" + TimedLyricsBuilder.FormatTimestamp(words[^1].End) + ">";
        return (string.Concat(words.Select(w => w.Text)).Trim(), body);
    }

    /// <summary>The lines Kugou puts above the lyrics: "Title (translation) - Artist" first, then credits.</summary>
    private static bool IsHeadLine(string text, string? title, string? artist, bool first)
    {
        if (text.Contains('：') || CreditRegex().IsMatch(text)) return true;
        if (!first || !text.Contains(" - ", StringComparison.Ordinal)) return false;
        var hasTitle = !string.IsNullOrWhiteSpace(title);
        var hasArtist = !string.IsNullOrWhiteSpace(artist);
        // Both names when the header tags carry both, so a first lyric that merely has a dash survives.
        return (hasTitle || hasArtist)
               && (!hasTitle || text.Contains(title!, StringComparison.OrdinalIgnoreCase))
               && (!hasArtist || text.Contains(artist!, StringComparison.OrdinalIgnoreCase));
    }

    private static string TagValue(string line)
    {
        var colon = line.IndexOf(':');
        var close = line.LastIndexOf(']');
        return colon >= 0 && close > colon ? line[(colon + 1)..close].Trim() : string.Empty;
    }

    private static TimeSpan Ms(string value) =>
        TimeSpan.FromMilliseconds(long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var ms) ? ms : 0);
}
