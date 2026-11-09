using Noctis.Models;
using Noctis.Services;
using Noctis.Services.LyricsPage;

namespace Noctis.Tests;

internal static class LyricsPageTestData
{
    // Word-timed line: tokens at 0.5 s each from start.
    internal static LyricsPageLine Words(double start, string text, LyricVoice voice = LyricVoice.Default) =>
        Timed(start, text, 0.5, voice);

    internal static LyricsPageLine Plain(double start, string text) => new() { Text = text, StartSeconds = start };

    // 8 word-timed lines of 3 tokens 4 s apart from 10 s (each sung by +1.5 s, so the 700 ms lead applies),
    // line 5 (at 30 s) has 12 words at 0.25 s each (wraps, still ends before the next line: no overlap),
    // then a 12 s instrumental gap after line 8's sung end, then 2 more lines.
    // Times: lines at 10, 14, 18, 22, 26, 30 (ends 33), 34, 38 (sung end 39.5, dots from 39.85),
    // then 51.5 and 55.5.
    internal static IReadOnlyList<LyricsPageLine> Song() => new List<LyricsPageLine>
    {
        Words(10, "hold me closer"),
        Words(14, "tiny dancer now"),
        Words(18, "count the headlights"),
        Words(22, "on the highway"),
        Words(26, "lay me down"),
        Timed(30, "every single word of this line keeps rolling on and on forever", 0.25, LyricVoice.Default),
        Words(34, "sheets of linen"),
        Words(38, "you had a"),
        Words(51.5, "busy day today"),
        Words(55.5, "jesus freaks out"),
    };

    internal static LyricsPageSpec Spec(ShareCardFormat format = ShareCardFormat.Story, bool safeZones = true,
        IReadOnlyList<LyricsPageLine>? lines = null) =>
        new() { Title = "Title", Artist = "Artist", Format = format, SafeZones = safeZones,
                Lines = lines ?? Song(), ClipStartSeconds = 10 };

    private static LyricsPageLine Timed(double start, string text, double step, LyricVoice voice)
    {
        var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var words = tokens.Select((t, i) => new KaraokeWord(t, start + i * step, start + (i + 1) * step)).ToList();
        return new LyricsPageLine { Text = text, StartSeconds = start, Words = words, Voice = voice };
    }
}
