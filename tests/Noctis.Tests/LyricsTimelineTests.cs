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

    // ── Early scroll (ScrollIndex) ──────────────────────────────────────────────
    private static readonly TimeSpan Lead = TimeSpan.FromMilliseconds(350);

    [Fact]
    public void ScrollIndex_leads_the_next_word_line_by_the_scroll_lead_after_a_gap()
    {
        // Line a is sung 1–2 s; b starts at 5 s. The list sets off at 5 − 0.35 − 0.08 s.
        var a = WordLine(1, ("a", 1, 2)); var b = WordLine(5, ("b", 5, 6));
        var t = Make(a, b);

        t.Update(TimeSpan.FromSeconds(4.5));
        Assert.Equal(0, t.ActiveIndex);
        Assert.Equal(0, t.ScrollIndex(TimeSpan.FromSeconds(4.5), Lead));

        t.Update(TimeSpan.FromSeconds(4.6));
        Assert.Equal(0, t.ActiveIndex);                      // highlight stays put
        Assert.Equal(1, t.ScrollIndex(TimeSpan.FromSeconds(4.6), Lead)); // list leads
    }

    [Fact]
    public void ScrollIndex_never_leaves_a_line_that_is_still_being_sung()
    {
        // a's last word ends at 4.9 s, inside b's lead window: the scroll waits for it.
        var a = WordLine(1, ("a", 1, 2), ("held", 2, 4.9)); var b = WordLine(5, ("b", 5, 6));
        var t = Make(a, b);

        t.Update(TimeSpan.FromSeconds(4.7));
        Assert.Equal(0, t.ScrollIndex(TimeSpan.FromSeconds(4.7), Lead));
        t.Update(TimeSpan.FromSeconds(4.85));
        Assert.Equal(1, t.ScrollIndex(TimeSpan.FromSeconds(4.85), Lead)); // 4.85 + 0.08 ≥ 4.9
    }

    [Fact]
    public void ScrollIndex_follows_a_seek_back_on_the_same_tick()
    {
        var a = WordLine(1, ("a", 1, 2)); var b = WordLine(5, ("b", 5, 6)); var c = WordLine(9, ("c", 9, 10));
        var t = Make(a, b, c);
        t.Update(TimeSpan.FromSeconds(8.7));
        Assert.Equal(2, t.ScrollIndex(TimeSpan.FromSeconds(8.7), Lead));

        t.Update(TimeSpan.FromSeconds(1.5));
        Assert.Equal(0, t.ActiveIndex);
        Assert.Equal(0, t.ScrollIndex(TimeSpan.FromSeconds(1.5), Lead));
    }

    [Fact]
    public void ScrollIndex_does_not_lead_plain_lines()
    {
        // Plain lines already activate on the longer line lookahead.
        var a = Line(1, "a", 2); var b = Line(5, "b");
        var t = Make(a, b);
        t.Update(TimeSpan.FromSeconds(4.6));
        Assert.Equal(0, t.ScrollIndex(TimeSpan.FromSeconds(4.6), Lead));
    }

    // ── Lead (owner 10-18, Thriller): the line the list leads into gets the active line's
    // opacity, so it must show the unsung look, never its words fully lit. ──
    private static void Step(LyricsTimeline t, double s)
    {
        t.Update(TimeSpan.FromSeconds(s));
        t.Lead(TimeSpan.FromSeconds(s), Lead);
    }

    [Fact]
    public void Lead_marks_the_incoming_line_unsung_until_it_activates()
    {
        var a = WordLine(1, ("a", 1, 2)); var b = WordLine(5, ("b1", 5, 5.5), ("b2", 5.5, 6));
        var t = Make(a, b);

        Step(t, 4.5);
        Assert.False(b.IsLeading);
        Assert.False(b.IsSweepFocused);

        Step(t, 4.6);                                    // list leads into b
        Assert.True(b.IsLeading);
        Assert.True(b.IsSweepFocused);
        Assert.False(b.IsActive);
        Assert.Equal(-1, b.CurrentWordIndex);
        Assert.All(b.Words!, w => Assert.True(w.Progress <= 0));

        // Activation: IsSweepFocused never drops between the lead and the active state.
        var focusDropped = false;
        b.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LyricLine.IsSweepFocused) && !b.IsSweepFocused) focusDropped = true;
        };
        Step(t, 4.95);
        Assert.True(b.IsActive);
        Assert.False(b.IsLeading);
        Assert.True(b.IsSweepFocused);
        Assert.False(focusDropped);
    }

    [Fact]
    public void Lead_into_a_line_sung_before_a_seek_back_resets_its_words_to_unsung()
    {
        var a = WordLine(1, ("a", 1, 2)); var b = WordLine(5, ("b1", 5, 5.5), ("b2", 5.5, 6)); var c = WordLine(9, ("c", 9, 10));
        var t = Make(a, b, c);
        for (var s = 1.0; s <= 9.5; s += 0.1) Step(t, s);   // b sung, then left fully swept
        Assert.Equal(b.Words!.Count, b.CurrentWordIndex);

        Step(t, 4.6);                                        // seek back into b's lead
        Assert.Equal(0, t.ActiveIndex);
        Assert.True(b.IsLeading);
        Assert.Equal(-1, b.CurrentWordIndex);
        Assert.All(b.Words!, w => Assert.True(w.Progress <= 0, $"{w.Text} progress {w.Progress}"));
    }

    [Fact]
    public void Lead_clears_on_a_seek_out_of_the_window_and_on_reset()
    {
        var a = WordLine(1, ("a", 1, 2)); var b = WordLine(5, ("b", 5, 6));
        var t = Make(a, b);
        Step(t, 4.6);
        Assert.True(b.IsLeading);

        Step(t, 1.5);                                        // seek back before the lead
        Assert.False(b.IsLeading);
        Assert.False(b.IsSweepFocused);

        Step(t, 4.6);
        Assert.True(b.IsLeading);
        t.Reset();                                           // track change / reload
        Assert.False(b.IsLeading);
    }

    [Fact]
    public void Lead_does_not_mark_plain_lines()
    {
        var a = WordLine(1, ("a", 1, 2)); var b = Line(5, "b");
        var t = Make(a, b);
        Step(t, 4.6);
        Assert.False(b.IsLeading);
    }

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

        // Adjusted clock lands exactly on 11.5 s: word 1 half sung. Its neighbours read
        // the same front in characters: 1.5 of "two" past "one"'s start is 1.5 "one"s
        // (3 chars each), and 1.5 chars short of "three" (5 chars) is -0.3.
        t.Update(TimeSpan.FromSeconds(11.5 - 0.08));

        Assert.Equal(1, line.CurrentWordIndex);
        Assert.Equal(1.5, line.Words![0].Progress, 3);
        Assert.Equal(0.5, line.Words[1].Progress, 3);
        Assert.Equal(-0.3, line.Words[2].Progress, 3);
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

    // ── Overlapping lines (TTML duets: a second voice starts before the first ends) ──

    /// <summary>A sings 10–13 s, B starts at 12 s: one second of two voices at once.</summary>
    private static (LyricLine A, LyricLine B, LyricLine C, LyricsTimeline T) Duet()
    {
        var a = WordLine(10, ("a1", 10, 11), ("a2", 11, 13));
        var b = WordLine(12, ("b1", 12, 13), ("b2", 13, 14));
        var c = Line(20, "c");
        return (a, b, c, Make(Line(0, "intro"), a, b, c));
    }

    [Fact]
    public void Overlapping_lines_are_both_active_with_their_own_word_progress()
    {
        var (a, b, _, t) = Duet();
        t.Update(TimeSpan.FromSeconds(11));

        var step = t.Update(TimeSpan.FromSeconds(12.5 - 0.08));

        Assert.True(step.LineChanged);
        Assert.Equal(2, step.ActiveIndex);                  // the scroll anchor is still the newest line
        Assert.Same(b, step.ActiveLine);
        Assert.Equal(new[] { 1, 2 }, t.ActiveIndices);
        Assert.Equal(new[] { a, b }, t.ActiveLines);
        Assert.True(a.IsActive);
        Assert.True(b.IsActive);
        Assert.Equal(1, a.CurrentWordIndex);                // a2, 11–13 s
        Assert.Equal(0.75, a.Words![1].Progress, 3);
        Assert.Equal(0, b.CurrentWordIndex);                // b1, 12–13 s
        Assert.Equal(0.5, b.Words![0].Progress, 3);
    }

    [Fact]
    public void Co_active_line_deactivates_fully_swept_once_its_end_passes()
    {
        var (a, b, _, t) = Duet();
        t.Update(TimeSpan.FromSeconds(12.5));
        Assert.True(a.IsActive);

        var step = t.Update(TimeSpan.FromSeconds(13.5));

        Assert.False(step.LineChanged);                     // primary is still B
        Assert.True(step.ActiveSetChanged);
        Assert.Equal(new[] { 2 }, t.ActiveIndices);
        Assert.False(a.IsActive);
        Assert.Equal(2, a.CurrentWordIndex);                // Words.Count: the "fully swept" sentinel
        Assert.All(a.Words!, w => Assert.True(w.IsPast));
        Assert.True(b.IsActive);

        var quiet = t.Update(TimeSpan.FromSeconds(13.6));
        Assert.False(quiet.ActiveSetChanged);
    }

    [Fact]
    public void Overlap_shorter_than_the_minimum_keeps_single_line_behaviour()
    {
        // A's last word ends 200 ms into B: boundary slop, not a second voice.
        var a = WordLine(10, ("a1", 10, 11), ("a2", 11, 12.2));
        var b = WordLine(12, ("b1", 12, 13));
        var t = Make(a, b);
        t.Update(TimeSpan.FromSeconds(11));

        var step = t.Update(TimeSpan.FromSeconds(12.1 - 0.08));

        Assert.True(step.LineChanged);
        Assert.Equal(new[] { 1 }, t.ActiveIndices);
        Assert.False(a.IsActive);
        Assert.Equal(2, a.CurrentWordIndex);
        Assert.True(b.IsActive);
    }

    [Fact]
    public void Plain_lrc_without_end_times_never_co_activates()
    {
        var a = Line(10, "a"); var b = Line(11, "b"); var c = Line(12, "c");
        var t = Make(a, b, c);

        t.Update(TimeSpan.FromSeconds(11.5));

        Assert.Equal(new[] { 1 }, t.ActiveIndices);
        Assert.False(a.IsActive);
        Assert.True(b.IsActive);
    }

    [Fact]
    public void Line_end_timestamp_alone_qualifies_a_plain_line()
    {
        // Line-timed TTML: <p begin end> without spans.
        var a = Line(10, "a", endSec: 14);
        var b = Line(12, "b", endSec: 15);
        var t = Make(a, b);

        t.Update(TimeSpan.FromSeconds(13));
        Assert.Equal(new[] { 0, 1 }, t.ActiveIndices);

        t.Update(TimeSpan.FromSeconds(14.1));
        Assert.Equal(new[] { 1 }, t.ActiveIndices);
        Assert.False(a.IsActive);
    }

    [Fact]
    public void Seeking_back_before_a_co_active_line_clears_it()
    {
        var (a, b, _, t) = Duet();
        t.Update(TimeSpan.FromSeconds(12.5));
        Assert.True(a.IsActive && b.IsActive);

        var step = t.Update(TimeSpan.FromSeconds(5));

        Assert.True(step.ActiveSetChanged);
        Assert.Equal(0, step.ActiveIndex);
        Assert.Equal(new[] { 0 }, t.ActiveIndices);
        Assert.False(a.IsActive);
        Assert.False(b.IsActive);
    }

    [Fact]
    public void Seeking_back_into_the_overlap_restores_both_lines()
    {
        var (a, b, c, t) = Duet();
        t.Update(TimeSpan.FromSeconds(21));
        Assert.True(c.IsActive);

        t.Update(TimeSpan.FromSeconds(12.5));

        Assert.Equal(new[] { 1, 2 }, t.ActiveIndices);
        Assert.True(a.IsActive);
        Assert.True(b.IsActive);
        Assert.False(c.IsActive);
        Assert.Equal(1, a.CurrentWordIndex);                // re-entry recomputes the real index
    }

    [Fact]
    public void Reset_clears_every_active_line()
    {
        var (a, b, _, t) = Duet();
        t.Update(TimeSpan.FromSeconds(12.5));

        t.Reset();

        Assert.Empty(t.ActiveIndices);
        Assert.Empty(t.ActiveLines);
        Assert.False(a.IsActive);
        Assert.False(b.IsActive);
    }

    [Fact]
    public void Seeking_back_before_the_first_lyric_clears_every_lit_line()
    {
        // No intro line: a big seek back to before the first lyric resets the cursor
        // without the backward walk. That reset is a rewind, so the safety branch must
        // not hold the old line lit — it used to keep B (now in the future) highlighted.
        var a = WordLine(10, ("a1", 10, 11), ("a2", 11, 13));
        var b = WordLine(12, ("b1", 12, 13), ("b2", 13, 14));
        var t = Make(a, b);
        t.Update(TimeSpan.FromSeconds(12.5));
        Assert.Equal(new[] { 0, 1 }, t.ActiveIndices);

        var step = t.Update(TimeSpan.FromSeconds(9));

        Assert.True(step.LineChanged);
        Assert.True(step.ActiveSetChanged);
        Assert.Null(step.ActiveLine);
        Assert.Equal(-1, step.ActiveIndex);
        Assert.Empty(t.ActiveIndices);
        Assert.False(a.IsActive);
        Assert.False(b.IsActive);
    }

    [Fact]
    public void Seeking_back_before_the_first_plain_lyric_clears_the_active_line()
    {
        var a = Line(1.5, "a"); var b = Line(5, "b");
        var t = Make(a, b);
        t.Update(TimeSpan.FromSeconds(6));
        Assert.True(b.IsActive);

        var step = t.Update(TimeSpan.FromSeconds(1.1));

        Assert.Equal(-1, step.ActiveIndex);
        Assert.False(b.IsActive);
        Assert.False(a.IsActive);
    }

    [Fact]
    public void Co_active_open_last_word_is_bounded_by_its_own_next_line()
    {
        // A's main layer has a start-tag-only last word and no line end; its adlib runs
        // to 13 s, past B's start, so A stays co-active. The open word must close at B's
        // start (the line after A), not at C's (the line after the primary).
        var a = Line(10, "a1");
        a.BackgroundEndTimestamp = TimeSpan.FromSeconds(13);
        a.BackgroundWords = new List<WordTiming>
        {
            new() { Text = "ah", Start = TimeSpan.FromSeconds(11), End = TimeSpan.FromSeconds(13) },
        };
        a.Words = new List<WordTiming> { new() { Text = "a1", Start = TimeSpan.FromSeconds(10) } };
        var b = WordLine(11, ("b1", 11, 12));
        var c = Line(30, "c");
        var t = Make(a, b, c);

        t.Update(TimeSpan.FromSeconds(11.5 - 0.08));

        Assert.Equal(new[] { 0, 1 }, t.ActiveIndices);
        // Closed at B's start (11 s): fully swept at 11.5. Bounded by C (capped at 12 s)
        // it would still be sweeping (0.75).
        Assert.Equal(1.0, a.Words![0].Progress, 3);
    }

    [Fact]
    public void Nearest_active_distance_measures_from_every_active_line()
    {
        var active = new[] { 3, 4 };
        Assert.Equal(0, LyricsTimeline.DistanceToNearestActive(3, 4, active));
        Assert.Equal(0, LyricsTimeline.DistanceToNearestActive(4, 4, active));
        Assert.Equal(1, LyricsTimeline.DistanceToNearestActive(2, 4, active));
        Assert.Equal(2, LyricsTimeline.DistanceToNearestActive(6, 4, active));
        // A primary outside the set (caller-held index) still counts.
        Assert.Equal(0, LyricsTimeline.DistanceToNearestActive(9, 9, active));
        Assert.Equal(1, LyricsTimeline.DistanceToNearestActive(8, 9, Array.Empty<int>()));
    }

    [Fact]
    public void Empty_list_is_inert()
    {
        var t = new LyricsTimeline(Array.Empty<LyricLine>(), TimeSpan.FromMilliseconds(80), TimeSpan.FromMilliseconds(280));

        var step = t.Update(TimeSpan.FromSeconds(3));

        Assert.False(step.LineChanged);
        Assert.False(step.ActiveSetChanged);
        Assert.Equal(-1, step.ActiveIndex);
    }

    // ── 10-24 flow: the measured 0.7 s lead, duet hold, dots, tempo ─────────────────────
    private static readonly TimeSpan MeasuredLead = TimeSpan.FromMilliseconds(Helpers.LineMotion.ScrollLeadMs);

    [Fact]
    public void Slow_gap_the_list_leads_the_next_line_by_the_measured_lead()
    {
        // a ends at 2 s, b starts at 6 s: the list sets off at 6 − 0.7 − 0.08 = 5.22 s,
        // settled (99% ≤ 0.6 s on every spring) before b's first word.
        var a = WordLine(1, ("a", 1, 2)); var b = WordLine(6, ("b", 6, 7));
        var t = Make(a, b);
        t.Update(TimeSpan.FromSeconds(5.2));
        Assert.Equal(0, t.ScrollIndex(TimeSpan.FromSeconds(5.2), MeasuredLead));
        t.Update(TimeSpan.FromSeconds(5.23));
        Assert.Equal(1, t.ScrollIndex(TimeSpan.FromSeconds(5.23), MeasuredLead));
        Assert.Equal(0, t.ActiveIndex);   // the highlight still waits for the voice
    }

    [Fact]
    public void Fast_lines_the_list_moves_the_moment_the_sung_line_ends_never_before()
    {
        // Rap: a's last word ends at 1.55 s, b starts at 1.6 s — well inside the 0.7 s lead.
        var a = WordLine(1, ("a1", 1, 1.3), ("a2", 1.3, 1.55)); var b = WordLine(1.6, ("b", 1.6, 2.1));
        var t = Make(a, b);
        for (var ms = 1000; ms < 1600; ms += 10)
        {
            if (Math.Abs(ms + 80 - 1550) < 5) continue;   // the boundary frame itself
            var pos = TimeSpan.FromMilliseconds(ms);
            t.Update(pos);
            var scroll = t.ScrollIndex(pos, MeasuredLead);
            if (ms + 80 < 1550)
                Assert.True(scroll == 0, $"left a while it was sung, at {ms} ms");
            else
                Assert.Equal(1, scroll);
        }
    }

    [Fact]
    public void Duet_the_list_holds_on_the_earlier_line_while_it_is_still_sung()
    {
        // A sings 10–13 s, B comes in at 12 s (Apple Music, Dance With Me: the list does not
        // move until the earlier voice finishes, then glides once).
        var (a, b, _, t) = Duet();
        t.Update(TimeSpan.FromSeconds(11));
        t.Update(TimeSpan.FromSeconds(12.5));
        Assert.Equal(2, t.ActiveIndex);
        Assert.Equal(1, t.ScrollIndex(TimeSpan.FromSeconds(12.5), MeasuredLead));
        Assert.Equal(1, t.Lead(TimeSpan.FromSeconds(12.5), MeasuredLead));
        Assert.False(a.IsLeading);   // an earlier line is never "led into"
        Assert.False(b.IsLeading);

        t.Update(TimeSpan.FromSeconds(13.5));   // A has finished
        Assert.Equal(2, t.ScrollIndex(TimeSpan.FromSeconds(13.5), MeasuredLead));
    }

    [Fact]
    public void Duet_hold_gives_way_when_the_earlier_line_is_too_far_back()
    {
        var a = WordLine(10, ("a", 10, 20));
        var b = WordLine(11, ("b", 11, 12)); var c = WordLine(12, ("c", 12, 13)); var d = WordLine(13, ("d", 13, 14));
        var t = Make(a, b, c, d);
        t.Update(TimeSpan.FromSeconds(13.5));
        Assert.Equal(3, t.ActiveIndex);
        Assert.Equal(new[] { 0, 3 }, t.ActiveIndices);
        Assert.Equal(3, t.ScrollIndex(TimeSpan.FromSeconds(13.5), MeasuredLead));
    }

    [Fact]
    public void Intro_dots_lead_out_into_the_first_word_line()
    {
        var dots = new LyricLine { Timestamp = TimeSpan.Zero, Text = "...", IsIntroPlaceholder = true };
        var a = WordLine(8, ("a", 8, 9));
        var t = Make(dots, a);
        t.Update(TimeSpan.FromSeconds(7.0));
        Assert.Equal(0, t.ScrollIndex(TimeSpan.FromSeconds(7.0), MeasuredLead));
        t.Update(TimeSpan.FromSeconds(7.25));
        Assert.Equal(1, t.Lead(TimeSpan.FromSeconds(7.25), MeasuredLead));
        Assert.True(a.IsLeading);
        Assert.Equal(-1, a.CurrentWordIndex);   // comes in unsung: no white flash
    }

    [Fact]
    public void Dots_progress_runs_through_the_gap_and_clears_when_the_line_comes()
    {
        var dots = new LyricLine { Timestamp = TimeSpan.FromSeconds(2), Text = "...", IsIntroPlaceholder = true, IsInterlude = true };
        var a = WordLine(0.5, ("a", 0.5, 1.5)); var b = WordLine(12, ("b", 12, 13));
        var t = Make(a, dots, b);
        t.Update(TimeSpan.FromSeconds(1));
        Assert.True(double.IsNaN(dots.InterludeProgress));
        t.Update(TimeSpan.FromSeconds(7));
        Assert.Same(dots, t.ActiveLine);
        Assert.Equal(0.5, dots.InterludeProgress, 3);
        t.Update(TimeSpan.FromSeconds(11.9));
        Assert.Equal(0.99, dots.InterludeProgress, 3);
        t.Update(TimeSpan.FromSeconds(12));
        Assert.Same(b, t.ActiveLine);
        Assert.True(double.IsNaN(dots.InterludeProgress));
    }

    [Fact]
    public void HoldMs_is_the_time_to_the_next_synced_line()
    {
        var lines = new[] { Line(1, "a"), new LyricLine { Text = "unsynced" }, Line(3.5, "b"), Line(3.5, "c") };
        Assert.Equal(2500, LyricsTimeline.HoldMs(lines, 0), 6);
        Assert.True(double.IsNaN(LyricsTimeline.HoldMs(lines, 1)));
        Assert.True(double.IsNaN(LyricsTimeline.HoldMs(lines, 2)));   // same start: no forward hold
        Assert.True(double.IsNaN(LyricsTimeline.HoldMs(lines, 3)));   // last line
    }
}
