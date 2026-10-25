using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services;
using SkiaSharp;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// 10-24 lyrics flow: dots through long instrumental gaps (filling with the gap, as Joy
/// Tunes' countdown does), and the Share Lyrics karaoke card running on the page's motion.
/// </summary>
public class LyricsInterludeAndShareFlowTests
{
    private static LyricLine WordLine(double startSec, params (string tok, double s, double e)[] words)
    {
        var line = new LyricLine
        {
            Timestamp = TimeSpan.FromSeconds(startSec),
            Text = string.Join(' ', words.Select(w => w.tok)),
        };
        line.Words = words.Select(w => new WordTiming
        {
            Text = w.tok + " ", Start = TimeSpan.FromSeconds(w.s), End = TimeSpan.FromSeconds(w.e)
        }).ToList();
        return line;
    }

    // ── Interlude placeholders ──────────────────────────────────────────────────

    [Fact]
    public void A_long_gap_after_a_word_timed_line_gets_dots_just_after_it_ends()
    {
        var a = WordLine(10, ("a", 10, 12));
        var b = WordLine(20, ("b", 20, 21));
        var lines = new List<LyricLine> { a, b };
        LrcParser.InsertInterludePlaceholders(lines);

        Assert.Equal(3, lines.Count);
        var dots = lines[1];
        Assert.True(dots.IsIntroPlaceholder);
        Assert.True(dots.IsInterlude);
        Assert.Equal("...", dots.Text);
        Assert.Equal(TimeSpan.FromSeconds(12) + LrcParser.InterludeDelay, dots.Timestamp);

        LrcParser.InsertInterludePlaceholders(lines);   // idempotent
        Assert.Equal(3, lines.Count);
    }

    [Fact]
    public void Breaths_between_phrases_and_plain_lrc_get_no_dots()
    {
        // 5 s of silence: a breath, not an interlude.
        var lines = new List<LyricLine> { WordLine(10, ("a", 10, 12)), WordLine(17, ("b", 17, 18)) };
        LrcParser.InsertInterludePlaceholders(lines);
        Assert.Equal(2, lines.Count);

        // Plain LRC: only starts are known — 20 s could be one long sung line.
        var plain = new List<LyricLine>
        {
            new() { Timestamp = TimeSpan.FromSeconds(10), Text = "a" },
            new() { Timestamp = TimeSpan.FromSeconds(30), Text = "b" },
        };
        LrcParser.InsertInterludePlaceholders(plain);
        Assert.Equal(2, plain.Count);
    }

    [Fact]
    public void A_longer_earlier_line_still_singing_closes_the_gap()
    {
        // A duet: a holds to 19 s over b (ends 12 s); c at 22 s is only 3 s after a's end.
        var a = WordLine(10, ("a", 10, 19));
        var b = WordLine(11, ("b", 11, 12));
        var c = WordLine(22, ("c", 22, 23));
        var lines = new List<LyricLine> { a, b, c };
        LrcParser.InsertInterludePlaceholders(lines);
        Assert.Equal(3, lines.Count);
    }

    [Fact]
    public void Dots_fill_one_after_another_and_rest_full_when_inactive()
    {
        Assert.Equal(1, InterludeDots.DotLevel(double.NaN, 0));
        Assert.Equal(1, InterludeDots.DotLevel(double.NaN, 2));

        Assert.Equal(InterludeDots.RestLevel, InterludeDots.DotLevel(0, 0), 9);
        Assert.Equal(InterludeDots.RestLevel, InterludeDots.DotLevel(0.3, 1), 9);   // dot 2 waits its third
        Assert.Equal(1, InterludeDots.DotLevel(1.0 / 3, 0), 9);
        Assert.InRange(InterludeDots.DotLevel(0.5, 1), 0.6, 0.75);                  // half-filled
        Assert.Equal(1, InterludeDots.DotLevel(1, 2), 9);

        var prev = 0.0;
        for (var p = 0.0; p <= 1.0; p += 0.01)
        {
            var sum = InterludeDots.DotLevel(p, 0) + InterludeDots.DotLevel(p, 1) + InterludeDots.DotLevel(p, 2);
            Assert.True(sum >= prev - 1e-9);
            prev = sum;
        }
    }

    [Fact]
    public void An_interlude_glides_in_after_the_line_and_leads_out_before_the_next()
    {
        var a = WordLine(10, ("a", 10, 12));
        var b = WordLine(20, ("b", 20, 21));
        var lines = new List<LyricLine> { a, b };
        LrcParser.InsertInterludePlaceholders(lines);
        var t = new LyricsTimeline(lines, TimeSpan.FromMilliseconds(80),
            TimeSpan.FromMilliseconds(80 + LineMotion.HalfTravelMs));
        var lead = TimeSpan.FromMilliseconds(LineMotion.ScrollLeadMs);

        // a's last word is still sweeping at 11.9 s: the dots wait.
        t.Update(TimeSpan.FromSeconds(11.9));
        Assert.Same(a, t.ActiveLine);
        Assert.Equal(1.0, Math.Min(1, a.Words![0].Progress), 1);

        // The dots take over just after a ends (12 s + 350 ms − the plain-line lookahead).
        t.Update(TimeSpan.FromSeconds(12.2));
        Assert.True(t.ActiveLine!.IsInterlude);
        Assert.True(t.ActiveLine.InterludeProgress < 0.05);

        // Lead out into b 0.7 s before its first word.
        t.Update(TimeSpan.FromSeconds(19.15));
        Assert.Equal(1, t.ScrollIndex(TimeSpan.FromSeconds(19.15), lead));
        t.Update(TimeSpan.FromSeconds(19.25));
        Assert.Equal(2, t.ScrollIndex(TimeSpan.FromSeconds(19.25), lead));
    }

    // ── Share Lyrics karaoke card ───────────────────────────────────────────────

    private static IReadOnlyList<KaraokeLine> CardLines() => new[]
    {
        new KaraokeLine
        {
            StartSeconds = 10,
            Words = new[] { new KaraokeWord("Hold", 10, 10.4), new KaraokeWord("me", 10.4, 12.0) },
        },
        new KaraokeLine
        {
            StartSeconds = 14,
            Words = new[] { new KaraokeWord("closer", 14, 14.5), new KaraokeWord("now", 14.5, 15) },
        },
    };

    [Fact]
    public void Card_lines_focus_like_the_page_list_the_next_line_leads_by_the_measured_lead()
    {
        var flow = new KaraokeFlow(CardLines());
        flow.Advance(9.0);
        Assert.Equal(0, flow.FocusAt(0, 9.0), 6);      // before the lead: nothing focused
        flow.Advance(9.5);
        Assert.True(flow.FocusAt(0, 9.6) > 0);          // first line leads by 0.7 s
        flow.Advance(11.0);
        Assert.Equal(1, flow.FocusAt(0, 11.0), 6);
        Assert.Equal(0, flow.FocusAt(1, 11.0), 6);

        // Line 1 takes the focus 0.7 s before its first word (line 0 ended at 12 s).
        for (var t = 11.0; t < 13.25; t += 1 / 60.0) flow.Advance(t);
        Assert.Equal(0, flow.FocusAt(1, 13.25), 6);
        for (var t = 13.25; t < 13.32; t += 1 / 60.0) flow.Advance(t);
        flow.Advance(13.32);
        Assert.True(flow.FocusAt(1, 13.32) > 0);
        flow.Advance(13.9);
        Assert.Equal(1, flow.FocusAt(1, 13.9), 6);
        Assert.Equal(0, flow.FocusAt(0, 13.9), 6);
        Assert.Equal(KaraokeFlow.InactiveOpacity, flow.LineOpacity(0, 13.9), 6);
    }

    [Fact]
    public void Card_words_sweep_and_float_exactly_as_the_page_timeline_drives_them()
    {
        var flow = new KaraokeFlow(CardLines());
        // The page's engine over the same words (the card's times already carry the 80 ms
        // word lookahead, so the reference runs with none either).
        var page = new[]
        {
            WordLine(10, ("Hold", 10, 10.4), ("me", 10.4, 12.0)),
            WordLine(14, ("closer", 14, 14.5), ("now", 14.5, 15)),
        };
        page[0].EndTimestamp = TimeSpan.FromSeconds(12);
        page[0].Words = page[0].Words;
        var reference = new LyricsTimeline(page, TimeSpan.Zero, TimeSpan.FromMilliseconds(LineMotion.HalfTravelMs));

        for (var t = 9.5; t < 15.5; t += 1 / 30.0)
        {
            flow.Advance(t);
            reference.Update(TimeSpan.FromSeconds(t));
            for (var l = 0; l < 2; l++)
            for (var w = 0; w < 2; w++)
            {
                var card = flow.Line(l)!.Words![w];
                var want = page[l].Words![w];
                Assert.Equal(want.Progress, card.Progress, 9);
                Assert.Equal(want.Lift, card.Lift, 9);
            }
        }
        // A sung word has floated up, an unsung one has not.
        flow.Advance(11.5);
        Assert.True(flow.Line(0)!.Words![0].Lift > 0.5);
    }

    /// <summary>Writes a few karaoke frames of both layouts to NOCTIS_SHARE_FLOW_DUMP for a
    /// visual check (no-op otherwise).</summary>
    [Fact]
    public void Card_frames_dump_for_visual_check()
    {
        var dump = Environment.GetEnvironmentVariable("NOCTIS_SHARE_FLOW_DUMP");
        if (string.IsNullOrEmpty(dump)) return;
        Directory.CreateDirectory(dump);
        foreach (var layout in new[] { ShareCardLayout.Panel, ShareCardLayout.Poster })
        {
            var spec = new LyricCardSpec
            {
                Title = "Title", Artist = "Artist", Lines = new[] { "Hold me", "closer now" },
                Background = ShareBackground.Solid, SolidColorHex = "#101010", TextColor = ShareTextColor.White,
                Layout = layout,
            };
            using var animator = new ShareCardRenderer.KaraokeCardAnimator(spec, CardLines(), 0.5f);
            var pixels = new byte[animator.PixelWidth * animator.PixelHeight * 4];
            var handle = System.Runtime.InteropServices.GCHandle.Alloc(pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
            try
            {
                var shots = new[] { 10.2, 11.0, 11.9, 13.45, 14.3 };
                var next = 0;
                for (var t = 9.0; t <= 14.5 && next < shots.Length; t += 1 / 60.0)
                {
                    animator.RenderFrame(t, handle.AddrOfPinnedObject(), animator.PixelWidth * 4);
                    if (t < shots[next]) continue;
                    using var bmp = new SKBitmap(new SKImageInfo(animator.PixelWidth, animator.PixelHeight, SKColorType.Bgra8888, SKAlphaType.Premul));
                    System.Runtime.InteropServices.Marshal.Copy(pixels, 0, bmp.GetPixels(), pixels.Length);
                    using var data = SKImage.FromBitmap(bmp).Encode(SKEncodedImageFormat.Png, 100);
                    File.WriteAllBytes(Path.Combine(dump, $"share-{layout}-{shots[next]:0.00}s.png"), data.ToArray());
                    next++;
                }
            }
            finally { handle.Free(); }
        }
    }

    [Fact]
    public void Card_frame_renders_the_focused_line_brighter_than_the_inactive_one()
    {
        var spec = new LyricCardSpec
        {
            Title = "Title",
            Artist = "Artist",
            Lines = new[] { "Hold me", "closer now" },
            Background = ShareBackground.Solid,
            SolidColorHex = "#101010",
            TextColor = ShareTextColor.White,
        };
        using var animator = new ShareCardRenderer.KaraokeCardAnimator(spec, CardLines(), 0.5f);
        var w = animator.PixelWidth;
        var h = animator.PixelHeight;
        var pixels = new byte[w * h * 4];
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            // Walk playback order up to mid line 0 ("me" being sung, line 1 still to come).
            for (var t = 9.0; t <= 11.0; t += 1 / 30.0)
                animator.RenderFrame(t, handle.AddrOfPinnedObject(), w * 4);

            var dump = Environment.GetEnvironmentVariable("NOCTIS_SHARE_FLOW_DUMP");
            if (!string.IsNullOrEmpty(dump))
            {
                using var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
                System.Runtime.InteropServices.Marshal.Copy(pixels, 0, bmp.GetPixels(), pixels.Length);
                using var data = SKImage.FromBitmap(bmp).Encode(SKEncodedImageFormat.Png, 100);
                Directory.CreateDirectory(dump);
                File.WriteAllBytes(Path.Combine(dump, "share-flow-11.0s.png"), data.ToArray());
            }

            // Brightest text pixel per row band: the focused (upper) lyric row must out-shine
            // the inactive one below it.
            var rows = Enumerable.Range(0, h).Select(y =>
            {
                var max = 0;
                for (var x = 0; x < w; x++) max = Math.Max(max, pixels[(y * w + x) * 4 + 1]);
                return max;
            }).ToArray();
            var lit = rows.Select((v, y) => (v, y)).Where(p => p.v > 60).Select(p => p.y).ToList();
            Assert.NotEmpty(lit);
            // Split the lyric rows (the two lowest bright bands above the wordmark) by gap.
            var bands = new List<(int From, int To, int Max)>();
            foreach (var y in lit)
            {
                if (bands.Count > 0 && y - bands[^1].To <= 2)
                    bands[^1] = (bands[^1].From, y, Math.Max(bands[^1].Max, rows[y]));
                else
                    bands.Add((y, y, rows[y]));
            }
            Assert.True(bands.Count >= 3, $"expected title/artist/lyric bands, got {bands.Count}");
            var lyricBands = bands.OrderByDescending(b => b.From).Skip(1).Take(2).OrderBy(b => b.From).ToList();
            Assert.True(lyricBands[0].Max > lyricBands[1].Max + 40,
                $"focused row max {lyricBands[0].Max} vs inactive row max {lyricBands[1].Max}");
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>Owner 10-25: the card keeps only the word-sync sweep — no line scale, no word
    /// float. The lyric text covers exactly the same pixel rows before line 0 is reached and
    /// while its first word is sung (it used to grow 0.96 → 1 and float the sung word up).</summary>
    [Fact]
    public void Card_lyrics_never_move_only_the_sweep_changes()
    {
        var spec = new LyricCardSpec
        {
            Title = "Title",
            Artist = "Artist",
            Lines = new[] { "Hold me", "closer now" },
            Background = ShareBackground.Solid,
            SolidColorHex = "#101010",
            TextColor = ShareTextColor.White,
        };
        // Full size: the old motion (0.055 em float, 0.96 → 1 scale) is sub-pixel at 0.5.
        using var animator = new ShareCardRenderer.KaraokeCardAnimator(spec, CardLines(), 1f);
        var w = animator.PixelWidth;
        var h = animator.PixelHeight;
        var pixels = new byte[w * h * 4];
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            // Rows holding text: more bright pixels than the card's 1.5 px side borders give a row.
            bool[] InkRows() => Enumerable.Range(0, h).Select(y =>
            {
                var bright = 0;
                for (var x = 0; x < w; x++)
                    if (pixels[(y * w + x) * 4 + 1] > 60) bright++;
                return bright > 6;
            }).ToArray();

            animator.RenderFrame(9.0, handle.AddrOfPinnedObject(), w * 4);
            var before = InkRows();
            for (var t = 9.0; t <= 11.5; t += 1 / 30.0)
                animator.RenderFrame(t, handle.AddrOfPinnedObject(), w * 4);
            var during = InkRows();

            var moved = Enumerable.Range(0, h).Where(y => before[y] != during[y]).ToList();
            Assert.True(moved.Count == 0, $"lyric ink moved on rows {string.Join(",", moved.Take(12))}");
        }
        finally
        {
            handle.Free();
        }
    }
}
