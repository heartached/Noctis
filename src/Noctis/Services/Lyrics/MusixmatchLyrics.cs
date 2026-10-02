using System.Text;
using System.Text.Json;
using Noctis.Services.LyricsStudio;

namespace Noctis.Services.Lyrics;

/// <summary>
/// Musixmatch "richsync" (word-level) bodies → the app's ELRC (issue #113). A richsync body
/// is a JSON array of lines: <c>{ "ts": lineStartSec, "te": lineEndSec, "l": [{ "c": token,
/// "o": offsetSecFromTs }], "x": lineText }</c>, where spaces are tokens of their own
/// (shape checked against apic.musixmatch.com track.richsync.get, 2026-10-01).
/// </summary>
public static class MusixmatchLyrics
{
    /// <summary>ELRC for a richsync body; null when it holds no lines. Throws <see cref="JsonException"/> on a malformed body.</summary>
    public static string? RichsyncToElrc(string richsyncBody)
    {
        using var doc = JsonDocument.Parse(richsyncBody);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;

        var output = new List<string>();
        foreach (var line in doc.RootElement.EnumerateArray())
        {
            if (line.ValueKind != JsonValueKind.Object) continue;
            var ts = Seconds(line, "ts");
            var te = Seconds(line, "te");
            if (ts == null) continue;

            var words = new List<(string Text, TimeSpan Start)>();
            if (line.TryGetProperty("l", out var tokens) && tokens.ValueKind == JsonValueKind.Array)
            {
                foreach (var token in tokens.EnumerateArray())
                {
                    var text = token.TryGetProperty("c", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() ?? "" : "";
                    var offset = Seconds(token, "o") ?? TimeSpan.Zero;
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        // A space token belongs to the word before it; a leading one is dropped.
                        if (words.Count > 0) words[^1] = (words[^1].Text + text, words[^1].Start);
                        continue;
                    }
                    words.Add((text, ts.Value + offset));
                }
            }

            var stamp = $"[{TimedLyricsBuilder.FormatTimestamp(ts.Value)}]";
            if (words.Count == 0)
            {
                // No word tokens: keep the line text, line-synced.
                var plain = line.TryGetProperty("x", out var x) && x.ValueKind == JsonValueKind.String ? x.GetString()?.Trim() : null;
                if (!string.IsNullOrWhiteSpace(plain)) output.Add(stamp + plain);
                continue;
            }

            var sb = new StringBuilder(stamp);
            foreach (var (text, start) in words)
                sb.Append('<').Append(TimedLyricsBuilder.FormatTimestamp(start)).Append('>').Append(text);
            var body = sb.ToString().TrimEnd();
            // The trailing tag ends the last word: the line's end, or its own start when te is missing.
            var end = te is { } lineEnd && lineEnd > words[^1].Start ? lineEnd : words[^1].Start;
            output.Add(body + "<" + TimedLyricsBuilder.FormatTimestamp(end) + ">");
        }

        return output.Count == 0 ? null : string.Join('\n', output);
    }

    // Rounded to whole milliseconds: 31.83 s is 31.8299… as a double, and truncating it to
    // ticks would print as 00:31.82.
    private static TimeSpan? Seconds(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var s) && s >= 0
            ? TimeSpan.FromMilliseconds(Math.Round(s * 1000))
            : null;
}
