using System.Globalization;
using System.Text.RegularExpressions;
using Noctis.Services.LyricsStudio;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The saved ELRC's structure on every Lyrics Studio path (Align, AlignWithinLines, Group):
/// every word inside its own line — at or after the line's stamp, before the next line's stamp —
/// in order, the line's end tag no later than the next stamp, nothing past the song's end, and
/// the written text read back identically. Benchmark 09-29 found words running into the next
/// line in 46 of 47 aligned and all 47 upgraded songs; these pin each invariant.
/// </summary>
public class LyricsStudioElrcStructureTests
{
    private static TimeSpan S(double sec) => TimeSpan.FromMilliseconds(Math.Round(sec * 1000));

    private static RecognizedWord W(string text, double start, double dur = 0.3) =>
        new(text, S(start), S(start + dur), 0.9f);

    private static AlignedLine Line(params (string Text, double Start, double End)[] words) =>
        new(string.Join(' ', words.Select(w => w.Text)), S(words[0].Start), S(words[^1].End),
            words.Select(w => new AlignedWord(w.Text, S(w.Start), S(w.End))).ToList(), 0.9, false);

    /// <summary>Checks the invariants on the aligned lines and on the ELRC text written from them.</summary>
    private static void AssertStructure(IReadOnlyList<AlignedLine> lines, IReadOnlyList<string>? input = null, TimeSpan? songEnd = null)
    {
        if (input is not null)
        {
            Assert.Equal(input.Count, lines.Count);
            for (var i = 0; i < input.Count; i++)
                Assert.Equal(input[i].Split(' ', StringSplitOptions.RemoveEmptyEntries), lines[i].Words.Select(w => w.Text).ToArray());
        }
        for (var i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            Assert.True(l.Start >= TimeSpan.Zero, $"line {i} starts before 0");
            Assert.NotEmpty(l.Words);
            if (i > 0) Assert.True(l.Start > lines[i - 1].Start, $"line {i} does not start after line {i - 1}");
            TimeSpan? next = i + 1 < lines.Count ? lines[i + 1].Start : songEnd;
            for (var j = 0; j < l.Words.Count; j++)
            {
                var w = l.Words[j];
                Assert.True(w.Start >= l.Start, $"line {i} word {j} before its line");
                Assert.True(w.End >= w.Start, $"line {i} word {j} ends before it starts");
                if (j > 0) Assert.True(w.Start >= l.Words[j - 1].End, $"line {i} word {j} overlaps the previous word");
                if (next is { } n)
                {
                    Assert.True(w.Start < n || (w.Start == n && i + 1 == lines.Count), $"line {i} word {j} starts at/after the next line");
                    Assert.True(w.End <= n, $"line {i} word {j} ends after the next line starts");
                }
            }
        }

        // Written ELRC: centisecond tags obey the same rules and read back through the Studio's loader.
        var elrc = TimedLyricsBuilder.BuildElrc(lines);
        var written = Regex.Matches(elrc, @"^\[(\d+):(\d{2}\.\d{2})\](.*)$", RegexOptions.Multiline)
            .Select(m => (Stamp: Tag(m.Groups[1].Value, m.Groups[2].Value),
                Tags: Regex.Matches(m.Groups[3].Value, @"<(\d+):(\d{2}\.\d{2})>([^<]*)").Select(t => (Time: Tag(t.Groups[1].Value, t.Groups[2].Value), Text: t.Groups[3].Value.Trim())).ToList()))
            .ToList();
        Assert.Equal(lines.Count, written.Count);
        for (var k = 0; k < written.Count; k++)
        {
            var bound = k + 1 < written.Count ? written[k + 1].Stamp : songEnd is { } e ? Written(e) : TimeSpan.MaxValue;
            var tags = written[k].Tags;
            Assert.True(tags.Count >= 2 && tags[^1].Text.Length == 0, $"written line {k} has no end tag");
            for (var t = 0; t < tags.Count; t++)
            {
                Assert.True(tags[t].Time >= written[k].Stamp, $"written line {k} tag {t} before its stamp");
                Assert.True(tags[t].Time <= bound, $"written line {k} tag {t} after the next stamp");
                if (t > 0) Assert.True(tags[t].Time >= tags[t - 1].Time, $"written line {k} tag {t} out of order");
            }
        }
        var back = ExistingLyricsLoader.ParseTimed(elrc);
        Assert.Equal(written.Count, back.Count);
        for (var k = 0; k < back.Count; k++)
        {
            Assert.Equal(written[k].Stamp, back[k].Start);
            var tags = written[k].Tags;
            Assert.Equal(tags.Count - 1, back[k].Words.Count);
            for (var t = 0; t + 1 < tags.Count; t++)
                Assert.True(Math.Abs((back[k].Words[t].Start - tags[t].Time).TotalMilliseconds) <= 1);
            Assert.True(Math.Abs((back[k].Words[^1].End - tags[^1].Time).TotalMilliseconds) <= 1);
        }
    }

    private static TimeSpan Tag(string min, string sec) =>
        TimeSpan.FromMinutes(int.Parse(min, CultureInfo.InvariantCulture)) + TimeSpan.FromMilliseconds(Math.Round(double.Parse(sec, CultureInfo.InvariantCulture) * 1000));

    private static TimeSpan Written(TimeSpan t) => TimeSpan.FromTicks(t.Ticks - t.Ticks % (TimeSpan.TicksPerMillisecond * 10));

    // ── LineWindows.Fit, piece by piece ──────────────────────────────────────

    [Fact]
    public void Fit_TrailingWordsPastTheNextLine_ArePulledInsideTheirLine()
    {
        var lines = new[]
        {
            Line(("one", 1.0, 1.4), ("two", 1.4, 1.8), ("three", 1.8, 2.22), ("four", 2.22, 2.64)),
            Line(("next", 2.0, 2.5)),
        };

        var fit = LineWindows.Fit(lines);

        Assert.Equal(S(1.0), fit[0].Words[0].Start); // words with room stay where they were
        Assert.Equal(S(1.4), fit[0].Words[1].Start);
        Assert.True(fit[0].Words[3].Start < S(2.0));
        Assert.Equal(S(2.0), fit[0].End);
        Assert.Equal(S(2.0), fit[1].Start);           // the next line keeps its start
        AssertStructure(fit);
    }

    [Fact]
    public void Fit_WordsPastTheWindow_ArePulledBackJustEnough_EachKeepingTheMinimumSpan()
    {
        // Three words ran on at 0.42 s/word past the next line (2.6): only the ones without room move.
        var lines = new[]
        {
            Line(("heard", 1.0, 2.0), ("a", 2.0, 2.42), ("b", 2.42, 2.84), ("c", 2.84, 3.26)),
            Line(("next", 2.6, 3.0)),
        };

        var fit = LineWindows.Fit(lines);

        Assert.Equal((S(1.0), S(2.0)), (fit[0].Words[0].Start, fit[0].Words[0].End));
        Assert.Equal((S(2.0), S(2.4)), (fit[0].Words[1].Start, fit[0].Words[1].End));
        Assert.Equal((S(2.4), S(2.5)), (fit[0].Words[2].Start, fit[0].Words[2].End));
        Assert.Equal((S(2.5), S(2.6)), (fit[0].Words[3].Start, fit[0].Words[3].End));
        AssertStructure(fit);
    }

    [Fact]
    public void Fit_HeardWordEndingInsideTheNextLine_OnlyLosesItsTail()
    {
        var lines = new[] { Line(("long", 5.0, 7.5)), Line(("next", 7.1, 7.6)) };

        var fit = LineWindows.Fit(lines);

        Assert.Equal(S(5.0), fit[0].Words[0].Start);
        Assert.Equal(S(7.1), fit[0].Words[0].End);
        AssertStructure(fit);
    }

    [Fact]
    public void Fit_WindowShorterThanTheMinimumSpan_SplitsItEvenly()
    {
        var lines = new[]
        {
            Line(("a", 1.0, 1.5), ("b", 1.5, 2.0), ("c", 2.0, 2.5), ("d", 2.5, 3.0)),
            Line(("next", 1.2, 1.5)),
        };

        var fit = LineWindows.Fit(lines);

        Assert.Equal(new[] { S(1.0), S(1.05), S(1.1), S(1.15) }, fit[0].Words.Select(w => w.Start).ToArray());
        Assert.Equal(S(1.2), fit[0].End);
        AssertStructure(fit);
    }

    [Fact]
    public void Fit_WordsInsideTheirWindow_AreUntouched()
    {
        var lines = new[]
        {
            Line(("a", 1.0, 1.3), ("b", 1.3, 1.9)),
            Line(("c", 4.0, 4.4), ("d", 4.6, 5.0)),
        };

        var fit = LineWindows.Fit(lines, S(60));

        for (var i = 0; i < lines.Length; i++)
            Assert.Equal(lines[i].Words, fit[i].Words);
    }

    [Fact]
    public void Fit_LastLine_StaysInsideTheSong()
    {
        var lines = new[] { Line(("a", 1.0, 1.4)), Line(("b", 9.0, 9.42), ("c", 9.42, 9.84), ("d", 9.84, 10.26)) };

        var fit = LineWindows.Fit(lines, S(9.5));

        Assert.Equal(S(9.0), fit[1].Start);
        Assert.True(fit[1].End <= S(9.5));
        AssertStructure(fit, songEnd: S(9.5));
    }

    [Fact]
    public void Fit_LinePlacedPastTheSongsEnd_IsBroughtBackInside()
    {
        var lines = new[] { Line(("a", 1.0, 1.4)), Line(("b", 12.0, 12.4), ("c", 12.4, 12.8)) };

        var fit = LineWindows.Fit(lines, S(10));

        Assert.True(fit[1].Start < S(10));
        AssertStructure(fit, songEnd: S(10));
    }

    [Fact]
    public void Fit_LinesOutOfOrderOrNegative_AreKeptInOrderAndAtZeroOrLater()
    {
        var lines = new[]
        {
            Line(("a", -0.5, -0.1), ("b", -0.1, 0.3)),
            Line(("c", 0.2, 0.6)),
            Line(("d", 0.1, 0.5)),
        };

        var fit = LineWindows.Fit(lines);

        Assert.Equal(TimeSpan.Zero, fit[0].Start);
        Assert.Equal(S(0.2), fit[1].Start);
        Assert.Equal(S(0.21), fit[2].Start);
        AssertStructure(fit);
    }

    [Fact]
    public void Fit_RandomTimelines_AlwaysSatisfyTheContract()
    {
        var rng = new Random(20260929);
        for (var run = 0; run < 400; run++)
        {
            var n = rng.Next(1, 12);
            var lines = new List<AlignedLine>();
            var t = rng.NextDouble() * 3 - 1;
            for (var i = 0; i < n; i++)
            {
                var words = new List<(string, double, double)>();
                var m = rng.Next(1, 9);
                var w = t + rng.NextDouble() * 0.5 - 0.3;
                for (var j = 0; j < m; j++)
                {
                    var len = rng.NextDouble() < 0.1 ? 0 : rng.NextDouble() * 0.8;
                    words.Add(($"w{j}", w, w + len));
                    w += len + (rng.NextDouble() < 0.2 ? -0.1 : rng.NextDouble() * 0.3);
                }
                lines.Add(Line(words.ToArray()));
                t += rng.NextDouble() * 3 - 0.4; // sometimes backwards
            }
            TimeSpan? end = rng.NextDouble() < 0.5 ? S(Math.Max(0.5, t + rng.NextDouble() * 2 - 1.5)) : null;

            var fit = LineWindows.Fit(lines, end);

            AssertStructure(fit, lines.Select(l => l.Text).ToList(), end);
        }
    }

    // ── Every Studio path ────────────────────────────────────────────────────

    [Fact]
    public void Align_UnheardTrailingWords_StayBeforeTheNextLine()
    {
        var lines = new[] { "Walk along the quiet street tonight oh", "Open every window here now" };
        var heard = new[]
        {
            W("walk", 1.0), W("along", 1.3), W("the", 1.6), W("quiet", 1.9), W("street", 2.2, 0.4),
            W("open", 3.0), W("every", 3.3), W("window", 3.6), W("here", 3.9), W("now", 4.2),
        };

        var aligned = LyricsAligner.Align(lines, heard, S(30));

        // "tonight oh" would run on to 3.44 at the default pace: they share 2.6–3.0 instead.
        Assert.Equal(S(3.0), aligned[1].Start);
        Assert.Equal((S(2.6), S(2.8)), (aligned[0].Words[5].Start, aligned[0].Words[5].End));
        Assert.Equal((S(2.8), S(3.0)), (aligned[0].Words[6].Start, aligned[0].Words[6].End));
        AssertStructure(aligned, lines, S(30));
    }

    [Fact]
    public void AlignWithinLines_UnheardLineBeforeALineHeardEarly_KeepsItsWordsInside()
    {
        // Line 1 is not heard; line 2's first word is heard 0.5 s before its stamp.
        var lines = new[] { "First line heard", "Nobody sang these words", "Third line heard" };
        var stamps = new[] { S(1.0), S(4.0), S(8.0) };
        var heard = new[]
        {
            W("first", 1.0), W("line", 1.3), W("heard", 1.6),
            W("third", 7.5), W("line", 7.8), W("heard", 8.1),
        };

        var aligned = LyricsAligner.AlignWithinLines(lines, stamps, heard, S(20));

        Assert.True(aligned[1].Interpolated);
        Assert.Equal(S(4.0), aligned[1].Start);
        Assert.Equal(S(7.5), aligned[2].Start);
        Assert.Equal(S(4.0 + 4 * 0.42), aligned[1].End); // a natural pace, not across the rest of the window
        Assert.Equal(S(4.42), aligned[1].Words[1].Start);
        AssertStructure(aligned, lines, S(20));
    }

    [Fact]
    public void AlignWithinLines_UnheardLineShortWindow_ShareItUpToWhereTheNextLineStarts()
    {
        var lines = new[] { "First line heard", "Nobody sang these words", "Third line heard" };
        var stamps = new[] { S(1.0), S(4.0), S(5.2) };
        var heard = new[]
        {
            W("first", 1.0), W("line", 1.3), W("heard", 1.6),
            W("third", 4.8), W("line", 5.1), W("heard", 5.4),
        };

        var aligned = LyricsAligner.AlignWithinLines(lines, stamps, heard, S(20));

        // Line 2 would run to its stamp (5.2) at 0.3 s/word; line 3 was heard 0.4 s early, at 4.8,
        // so line 2's four words share 4.0–4.8.
        Assert.Equal(S(4.8), aligned[2].Start);
        Assert.Equal(S(4.8), aligned[1].End);
        Assert.Equal(S(4.4), aligned[1].Words[2].Start);
        AssertStructure(aligned, lines, S(20));
    }

    [Fact]
    public void Group_OverlappingHeardWords_NeverRunIntoTheNextLine()
    {
        // A long word whose DTW end overlaps the next word, split into two lines by the pause rule.
        var heard = new[]
        {
            W("one", 1.0), W("two", 1.3), W("three", 1.6, 3.0),
            W("four", 3.0), W("five", 3.3),
        };

        var lines = TranscriptLines.Group(heard, maxWordsPerLine: 3, totalDuration: S(10));

        Assert.Equal(2, lines.Count);
        AssertStructure(lines, songEnd: S(10));
    }

    [Fact]
    public void Group_HeardTagDelimiters_AreNotWrittenAsText()
    {
        // A transcript word that is (or holds) "<" or ">" would read back as a broken tag.
        var heard = new[] { W("one", 1.0), W("two", 1.3), W("<", 1.6), W("x>", 1.9), W("<b", 2.2) };

        var lines = TranscriptLines.Group(heard, totalDuration: S(10));

        Assert.Equal(new[] { "one", "two", "x", "b" }, lines.SelectMany(l => l.Words).Select(w => w.Text).ToArray());
        AssertStructure(lines, songEnd: S(10));
    }

    [Fact]
    public void AllPaths_RandomTranscripts_AlwaysSatisfyTheContract()
    {
        var rng = new Random(929);
        var vocab = new[] { "love", "night", "baby", "yeah", "fire", "gold", "run", "home", "oh", "sky", "rain", "city" };
        for (var run = 0; run < 150; run++)
        {
            var n = rng.Next(2, 10);
            var lines = Enumerable.Range(0, n).Select(_ => string.Join(' ', Enumerable.Range(0, rng.Next(1, 8)).Select(_ => vocab[rng.Next(vocab.Length)]))).ToList();
            var heard = new List<RecognizedWord>();
            var t = rng.NextDouble() * 5;
            var stamps = new List<TimeSpan>();
            foreach (var line in lines)
            {
                stamps.Add(S(t + rng.NextDouble() * 0.8 - 0.4));
                foreach (var word in line.Split(' '))
                {
                    if (rng.NextDouble() < 0.7) heard.Add(W(rng.NextDouble() < 0.9 ? word : vocab[rng.Next(vocab.Length)], t, rng.NextDouble() * 0.9));
                    t += 0.05 + rng.NextDouble() * 0.5;
                }
                t += rng.NextDouble() * 2;
            }
            for (var i = 1; i < stamps.Count; i++) if (stamps[i] <= stamps[i - 1]) stamps[i] = stamps[i - 1] + S(0.3);
            var end = S(t + rng.NextDouble() * 3);

            AssertStructure(LyricsAligner.Align(lines, heard, end), lines, end);
            AssertStructure(LyricsAligner.AlignWithinLines(lines, stamps, heard, end), lines, end);
            AssertStructure(TranscriptLines.Group(heard, totalDuration: end), songEnd: end);
        }
    }
}
