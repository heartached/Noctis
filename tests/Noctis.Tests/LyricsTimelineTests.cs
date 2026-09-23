using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The line cursor and word-layer driver lifted out of LyricsViewModel. Lookaheads
/// mirror the desktop: 80 ms for word-timed lines, 280 ms for plain lines.
/// </summary>
public class LyricsTimelineTests
{
    private static LyricLine Line(double startSec, string text, double? endSec = null) => new()
    {
        Timestamp = TimeSpan.FromSeconds(startSec),
        EndTimestamp = endSec is { } e ? TimeSpan.FromSeconds(e) : null,
        Text = text,
    };

    private static LyricLine WordLine(double startSec, params (string tok, double s, double e)[] words)
    {
        var line = Line(startSec, string.Join(' ', words.Select(w => w.tok)));
        line.Words = words.Select(w => new WordTiming
        {
            Text = w.tok, Start = TimeSpan.FromSeconds(w.s), End = TimeSpan.FromSeconds(w.e)
        }).ToList();
        return line;
    }

    private static LyricsTimeline Make(params LyricLine[] lines)
        => new(lines, TimeSpan.FromMilliseconds(80), TimeSpan.FromMilliseconds(280));

    [Fact]
    public void Before_first_line_nothing_is_active()
    {
        var a = Line(5, "a"); var b = Line(10, "b");
        var t = Make(a, b);

        var step = t.Update(TimeSpan.FromSeconds(1));

        Assert.False(step.LineChanged);
        Assert.Equal(-1, step.ActiveIndex);
        Assert.Null(step.ActiveLine);
        Assert.False(a.IsActive);
    }

    [Fact]
    public void Cursor_walks_forward_and_flips_IsActive()
    {
        var a = Line(5, "a"); var b = Line(10, "b"); var c = Line(15, "c");
        var t = Make(a, b, c);

        var first = t.Update(TimeSpan.FromSeconds(6));
        Assert.True(first.LineChanged);
        Assert.True(a.IsActive);

        var step = t.Update(TimeSpan.FromSeconds(12));

        Assert.True(step.LineChanged);
        Assert.Equal(1, step.ActiveIndex);
        Assert.Same(b, step.ActiveLine);
        Assert.False(a.IsActive);
        Assert.True(b.IsActive);
        Assert.False(c.IsActive);

        var same = t.Update(TimeSpan.FromSeconds(13));
        Assert.False(same.LineChanged);
    }

    [Fact]
    public void Plain_line_activates_by_the_line_lookahead_word_line_by_the_word_lookahead()
    {
        var plain = Line(10, "plain");
        var worded = WordLine(20, ("x", 20, 21));
        var t = Make(Line(0, "intro"), plain, worded);

        t.Update(TimeSpan.FromMilliseconds(10000 - 250));
        Assert.True(plain.IsActive);          // 280 ms lead reaches it
        t.Update(TimeSpan.FromMilliseconds(20000 - 250));
        Assert.False(worded.IsActive);        // 80 ms lead does not
        t.Update(TimeSpan.FromMilliseconds(20000 - 70));
        Assert.True(worded.IsActive);
    }

    [Fact]
    public void Seeking_backwards_rewinds_the_cursor()
    {
        var a = Line(5, "a"); var b = Line(10, "b"); var c = Line(15, "c");
        var t = Make(a, b, c);
        t.Update(TimeSpan.FromSeconds(16));
        Assert.True(c.IsActive);

        var step = t.Update(TimeSpan.FromSeconds(6));

        Assert.Equal(0, step.ActiveIndex);
        Assert.True(a.IsActive);
        Assert.False(c.IsActive);
    }

    [Fact]
    public void Small_backward_jitter_keeps_the_current_line()
    {
        var a = Line(5, "a"); var b = Line(10, "b");
        var t = Make(a, b);
        t.Update(TimeSpan.FromSeconds(10.5));
        Assert.True(b.IsActive);

        t.Update(TimeSpan.FromSeconds(10.1)); // 400 ms back, still past b's start

        Assert.True(b.IsActive);
        Assert.Equal(1, t.ActiveIndex);
    }

    [Fact]
    public void Word_index_and_band_progress_follow_the_adjusted_clock()
    {
        var line = WordLine(10, ("one", 10, 11), ("two", 11, 12), ("three", 12, 13));
        var t = Make(line);

        // Adjusted clock lands exactly on 11.5 s: word 1 half sung, word 0 half a
        // word past its end (the band overshoots [0,1] on neighbours by design).
        t.Update(TimeSpan.FromSeconds(11.5 - 0.08));

        Assert.Equal(1, line.CurrentWordIndex);
        Assert.Equal(1.5, line.Words![0].Progress, 3);
        Assert.Equal(0.5, line.Words[1].Progress, 3);
        Assert.Equal(-0.5, line.Words[2].Progress, 3);
    }

    [Fact]
    public void Leaving_a_word_line_parks_its_index_past_the_end()
    {
        var w = WordLine(10, ("one", 10, 11));
        var next = Line(20, "next");
        var t = Make(w, next);
        t.Update(TimeSpan.FromSeconds(10.5));
        Assert.Equal(0, w.CurrentWordIndex);

        t.Update(TimeSpan.FromSeconds(21));

        Assert.False(w.IsActive);
        Assert.Equal(1, w.CurrentWordIndex); // Words.Count: the "fully swept" sentinel
        Assert.True(next.IsActive);
    }

    // ── Word-timed romanization (TTML transliteration, GitHub #78) ──

    private static LyricLine RomanizedLine(double startSec, params (string tok, double s, double e)[] romaji)
    {
        var line = Line(startSec, "main");
        line.TransliterationEndTimestamp = TimeSpan.FromSeconds(romaji[^1].e);
        line.TransliterationWords = romaji.Select(w => new WordTiming
        {
            Text = w.tok, Start = TimeSpan.FromSeconds(w.s), End = TimeSpan.FromSeconds(w.e)
        }).ToList();
        return line;
    }

    [Fact]
    public void Transliteration_index_follows_the_clock_and_parks_past_the_end_on_leaving()
    {
        var line = RomanizedLine(10, ("fu", 10, 11), ("ri", 11, 12));
        var next = Line(20, "next");
        var t = Make(line, next);

        t.Update(TimeSpan.FromSeconds(11.5 - 0.08));
        Assert.Equal(1, line.TransliterationWordIndex);
        Assert.Equal(0.5, line.TransliterationWords![1].Progress, 3);
        Assert.True(line.TransliterationWords[0].IsPast);
        Assert.True(line.TransliterationWords[1].IsCurrent);

        t.Update(TimeSpan.FromSeconds(21));

        Assert.False(line.IsActive);
        Assert.Equal(2, line.TransliterationWordIndex);            // "fully swept" sentinel
        Assert.All(line.TransliterationWords, w => Assert.True(w.IsPast));
    }

    [Fact]
    public void A_line_with_only_romanization_words_activates_on_the_word_lookahead()
    {
        var romanized = RomanizedLine(20, ("fu", 20, 21));
        var t = Make(Line(0, "intro"), romanized);

        t.Update(TimeSpan.FromMilliseconds(20000 - 250));
        Assert.False(romanized.IsActive);     // the 280 ms line lead would have reached it
        t.Update(TimeSpan.FromMilliseconds(20000 - 70));
        Assert.True(romanized.IsActive);
    }

    [Fact]
    public void Reset_forgets_everything()
    {
        var a = Line(5, "a");
        var t = Make(a);
        t.Update(TimeSpan.FromSeconds(6));
        Assert.True(a.IsActive);

        t.Reset();

        Assert.Null(t.ActiveLine);
        Assert.Equal(-1, t.ActiveIndex);
        Assert.False(a.IsActive);
    }

    [Fact]
    public void Empty_list_is_inert()
    {
        var t = new LyricsTimeline(Array.Empty<LyricLine>(), TimeSpan.FromMilliseconds(80), TimeSpan.FromMilliseconds(280));

        var step = t.Update(TimeSpan.FromSeconds(3));

        Assert.False(step.LineChanged);
        Assert.Equal(-1, step.ActiveIndex);
    }
}
