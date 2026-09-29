using Noctis.Services.LyricsStudio;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Synthetic reproductions of the aligner's tail errors (benchmark 09-24): lines pulled to
/// the wrong repeat of a hook, to a later ad-lib, to a DTW collapse or to a stray match.
/// Placeholder words only; each case failed on the previous aligner.
/// </summary>
public class LyricsAlignerRepeatsTests
{
    private static RecognizedWord W(string text, double startSec, double durSec = 0.3) =>
        new(text, TimeSpan.FromSeconds(startSec), TimeSpan.FromSeconds(startSec + durSec), 0.9f);

    private static TimeSpan S(double sec) => TimeSpan.FromSeconds(sec);

    private static double Sec(TimeSpan t) => t.TotalSeconds;

    [Fact]
    public void Align_LineHeldOnlyByAFiller_IsNotPulledToALaterAdlib()
    {
        // The middle line's only heard word is "yeah"; ad-lib "yeah"s follow in the gap.
        // Before: the line took the last ad-lib (9.0 s instead of ~3 s).
        var lines = new[] { "alpha beta gamma yeah", "yeah the delta", "epsilon zeta eta" };
        var heard = new[]
        {
            W("alpha", 1.0), W("beta", 1.4), W("gamma", 1.8), W("yeah", 2.3), W("yeah", 3.2),
            W("hey", 6.0), W("yeah", 9.0), W("hey", 10.0),
            W("epsilon", 12.0), W("zeta", 12.4), W("eta", 12.8),
        };

        var aligned = LyricsAligner.Align(lines, heard, S(20));

        Assert.InRange(Sec(aligned[1].Start), 2.3, 4.0);
        Assert.Equal(S(12.0), aligned[2].Start);
    }

    [Fact]
    public void Align_RepeatedHook_HeardOccurrencesGoToTheLinesTheirTimingFits()
    {
        // A verse at 4 s per line, then a hook sung five times (116..132 s) of which only the
        // 1st, 2nd and 5th were heard. Before: they went to hook lines 3–5 and lines 1–2 were
        // squeezed in front of them.
        var lines = new List<string> { "alpha beta gamma delta", "epsilon zeta eta theta", "iota kappa lambda mu", "nu xi omicron pi" };
        for (var i = 0; i < 5; i++) lines.Add("hold the line now");
        lines.Add("rho sigma tau upsilon");
        var heard = new List<RecognizedWord>();
        void Sing(string text, double t) { foreach (var w in text.Split(' ')) { heard.Add(W(w, t)); t += 0.8; } }
        Sing(lines[0], 100); Sing(lines[1], 104); Sing(lines[2], 108); Sing(lines[3], 112);
        Sing("hold the line now", 116); Sing("hold the line now", 120); Sing("hold the line now", 132);
        Sing(lines[9], 136);

        var aligned = LyricsAligner.Align(lines, heard, S(145));

        Assert.Equal(S(116), aligned[4].Start);
        Assert.Equal(S(120), aligned[5].Start);
        Assert.InRange(Sec(aligned[6].Start), 122, 126);
        Assert.InRange(Sec(aligned[7].Start), 126, 130);
        Assert.Equal(S(132), aligned[8].Start);
        Assert.True(aligned[6].Interpolated && aligned[7].Interpolated);
    }

    [Fact]
    public void Align_EchoHeardFarAfterItsLine_DoesNotStretchTheLine()
    {
        // The parenthetical echo is matched 7 s after the rest of its line. Before: the line
        // ran to 18.7 s and the unheard line after it was squeezed into 1.3 s.
        var lines = new[] { "alpha beta (beta gamma)", "delta epsilon zeta", "eta theta iota" };
        var heard = new[]
        {
            W("alpha", 10.0), W("beta", 10.4), W("beta", 18.0), W("gamma", 18.4),
            W("eta", 20.0), W("theta", 20.4), W("iota", 20.8),
        };

        var aligned = LyricsAligner.Align(lines, heard, S(30));

        Assert.True(aligned[0].Words[^1].Start < S(13));
        Assert.InRange(Sec(aligned[1].Start), 10.5, 13);
    }

    [Fact]
    public void Align_WordsStampedInADtwCollapse_DoNotTimeTheLine()
    {
        // All words of the middle line stamped within 20 ms, just before the next line: only the
        // run's last word keeps its time. Before: the line started at the collapse (9.5 s).
        var lines = new[] { "alpha beta gamma delta", "epsilon zeta eta theta iota", "kappa lambda mu" };
        var heard = new[]
        {
            W("alpha", 1.0), W("beta", 1.4), W("gamma", 1.8), W("delta", 2.2),
            W("epsilon", 9.50, 0), W("zeta", 9.50, 0), W("eta", 9.51, 0), W("theta", 9.52, 0), W("iota", 9.52, 0),
            W("kappa", 9.8), W("lambda", 10.2), W("mu", 10.6),
        };

        var aligned = LyricsAligner.Align(lines, heard, S(20));

        Assert.True(aligned[1].Start < S(9), $"{aligned[1].Start}");
        Assert.Equal(S(9.52), aligned[1].Words[^1].Start);
    }

    [Fact]
    public void Align_StrayMatchLeavingNoRoomForUnheardLines_IsLetGo()
    {
        // One word of line 4 matched right after line 0, leaving three unheard lines 0.4 s.
        // Before: lines 1–4 were stacked 10 ms apart.
        var lines = new[] { "alpha beta gamma", "delta epsilon zeta", "eta theta iota", "kappa lambda mu", "nu xi omicron", "pi rho sigma" };
        var heard = new[]
        {
            W("alpha", 10.0), W("beta", 10.4), W("gamma", 10.8), W("omicron", 11.5),
            W("pi", 30.0), W("rho", 30.4), W("sigma", 30.8),
        };

        var aligned = LyricsAligner.Align(lines, heard, S(40));

        for (var i = 1; i < 5; i++)
            Assert.True(aligned[i + 1].Start - aligned[i].Start > S(2), $"line {i}");
        Assert.True(aligned[4].Interpolated);
    }

    [Fact]
    public void Align_LineHeardTwice_TakesTheCopyWhereItsNeighboursPutIt()
    {
        // The model heard line 2 at its place (104 s) and again 5.5 s later over line 4.
        // Before: the line took the later copy (tie broken late) and lines 3–4 were squeezed.
        var lines = new[] { "alpha beta gamma", "delta epsilon", "zeta eta theta", "iota kappa", "lambda mu nu xi", "omicron pi" };
        var heard = new[]
        {
            W("alpha", 100.0), W("beta", 100.4), W("gamma", 100.8),
            W("zeta", 104.0), W("eta", 104.4), W("theta", 104.8),
            W("zeta", 109.5), W("eta", 109.9), W("theta", 110.3),
            W("omicron", 113.0), W("pi", 113.4),
        };

        var aligned = LyricsAligner.Align(lines, heard, S(120));

        Assert.Equal(S(104.0), aligned[2].Start);
        Assert.InRange(Sec(aligned[4].Start), 106.5, 111);
    }

    [Fact]
    public void Align_MidLineWordStampedLate_StartsWhereThePreviousWordEnded()
    {
        // DTW caps a word's start to 0.3 s before its end; mid-line it was sung from the
        // previous word's end. A line's first word keeps its stamp.
        var heard = new[] { W("alpha", 5.0), W("beta", 6.2), W("gamma", 6.6) };

        var line = LyricsAligner.Align(new[] { "alpha beta gamma" }, heard, S(10)).Single();

        Assert.Equal(S(5.0), line.Words[0].Start);
        Assert.Equal(S(5.3), line.Words[1].Start);
        Assert.Equal(S(6.6), line.Words[2].Start);
    }

    [Fact]
    public void AlignWithinLines_FirstWordUnheard_LineKeepsItsStamp()
    {
        // Before: the line started 0.42 s before its first heard word, not at its stamp.
        var lines = new[] { "ooh alpha beta", "gamma delta" };
        var starts = new[] { S(10), S(16) };
        var heard = new[] { W("alpha", 12.0), W("beta", 12.5), W("gamma", 16.0), W("delta", 16.4) };

        var aligned = LyricsAligner.AlignWithinLines(lines, starts, heard, S(20));

        Assert.Equal(S(10), aligned[0].Start);
        Assert.Equal(S(12.0), aligned[0].Words[1].Start);
        Assert.Equal(S(16.0), aligned[1].Start);
    }

    [Fact]
    public void AlignWithinLines_WordHeardTwiceInItsWindow_TakesTheOccurrenceNearTheStamp()
    {
        // Before: the later occurrence (an ad-lib 6 s on) won the tie.
        var lines = new[] { "alpha", "beta" };
        var starts = new[] { S(10), S(20) };
        var heard = new[] { W("alpha", 10.2), W("alpha", 16.0), W("beta", 20.1) };

        var aligned = LyricsAligner.AlignWithinLines(lines, starts, heard, S(25));

        Assert.Equal(S(10.2), aligned[0].Start);
        Assert.Equal(S(20.1), aligned[1].Start);
    }
}
