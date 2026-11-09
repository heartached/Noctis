using Noctis.Models;

namespace Noctis.Services.LyricsPage;

/// <summary>
/// Turns the Lyrics Page spec lines into the lyrics engine's <see cref="LyricLine"/>s, so the
/// video runs on the same timeline rules as the lyrics page: word timing, adlib rows, duet
/// voice, and the intro / interlude dots.
/// </summary>
public static class LyricsPageEngine
{
    public static List<LyricLine> BuildLines(IReadOnlyList<LyricsPageLine> lines)
    {
        var result = new List<LyricLine>(lines.Count + 2);
        foreach (var source in lines)
        {
            if (source.StartSeconds is not { } start) continue;
            var line = new LyricLine
            {
                Timestamp = TimeSpan.FromSeconds(start),
                Text = source.Text,
                Voice = source.Voice,
                IsBackgroundOnly = source.IsBackgroundOnly,
            };
            // The ends first: the word setters derive the held-note and sweep-edge flags from them.
            // A line end tag bounds the line even without words, so line-timed lyrics with end
            // tags get interlude dots too.
            var words = source.Words is { Count: > 0 } w ? w : null;
            double? end = source.EndSeconds ?? words?[^1].EndSeconds;
            if (end is { } e) line.EndTimestamp = TimeSpan.FromSeconds(e);
            if (words != null) line.Words = ToWordTimings(words);
            if (source.BackgroundWords is { Count: > 0 } bg)
            {
                line.BackgroundEndTimestamp = TimeSpan.FromSeconds(bg[^1].EndSeconds);
                line.BackgroundWords = ToWordTimings(bg);
            }
            result.Add(line);
        }
        LrcParser.InsertIntroPlaceholderIfNeeded(result);
        LrcParser.InsertInterludePlaceholders(result);
        return result;
    }

    /// <summary>Tokens joined by a trailing space except the last (the KaraokeFlow pattern).</summary>
    private static List<WordTiming> ToWordTimings(IReadOnlyList<KaraokeWord> words) =>
        words.Select((w, j) => new WordTiming
        {
            Text = j + 1 < words.Count ? w.Token + " " : w.Token,
            Start = TimeSpan.FromSeconds(w.StartSeconds),
            End = TimeSpan.FromSeconds(Math.Max(w.StartSeconds, w.EndSeconds)),
        }).ToList();
}
