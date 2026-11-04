namespace Noctis.Plugins.LyricMotion.Motion;

/// <summary>
/// The motion itself, as Lyric Motion does it where it is built into Noctis Plus (LetterLayer.cs
/// there), turned into a pure function of what Noctis reports for a word this frame: where the
/// highlight is (<see cref="LyricsWord.Progress"/>), how long the word is sung and Noctis's float
/// (<see cref="LyricsWord.Lift"/>). No state and no allocation, so it is exactly in time with the
/// sweep Noctis draws, whatever the frame rate, and a seek simply lands on the right pose.
///
/// <list type="bullet">
/// <item>A letter comes up while a soft band passes over it: it opens 0.3 em ahead of the
/// highlight and closes 0.35 em behind it, stopping at the word's own ends. In Smooth and Flowing
/// the band is left standing past the end of the word (half of it, or all of it) and a letter never
/// comes up faster than the eye can follow (0.12 s, or 0.2 s), so the last letters finish a moment
/// into the next word (see <see cref="Rise"/> for how far). In Crisp every letter is up exactly when
/// the highlight has crossed it.</item>
/// <item>Each letter floats up by whole pixels as the highlight reaches it. Grown letters push their neighbours
/// aside, the word staying centred where it was, and grow from just above the baseline.</item>
/// <item>A sung word then stays exactly as it is until its line is over, and comes down with
/// Noctis's own float. (Noctis Plus lets long notes relax a second after they are sung. Noctis stops
/// redrawing a word once the highlight is two words past it, or at once after a pause, so a word
/// relaxing then would drop the rest of the way in one frame: a visible jump. Holding still is the
/// only way that never jumps.)</item>
/// </list>
/// </summary>
public static class LetterMotion
{
    /// <summary>The band a letter arrives in: how far ahead of the highlight it opens and how far behind it closes, in em.</summary>
    public const double BandAheadEm = 0.3, BandBehindEm = 0.35;

    /// <summary>
    /// How wide a letter is, in em, for spacing grown letters apart (the API gives letter counts,
    /// not widths): about what an Inter Bold letter is.
    /// </summary>
    public const double LetterWidthEm = 0.58;

    /// <summary>
    /// Noctis Plus grows a letter from just above its baseline (0.62 of the line box); Noctis scales
    /// a posed letter about its centre. This lifts the centre by the difference, per unit of growth
    /// (0.12 of a line box of about 1.21 em).
    /// </summary>
    public const double OriginLiftEm = 0.145;

    /// <summary>Noctis reports -2 for words far ahead and 3 for words long sung.</summary>
    private const double FarAhead = -1.5, LongSung = 2.5;

    public static LyricsWordMode Mode(Tuning t, in LyricsWord word)
    {
        if (!t.Moves) return LyricsWordMode.Native;
        if (word.Surface != LyricsSurface.Page && !t.OtherViews) return LyricsWordMode.Native;
        if (t.Letters && !word.IsRightToLeft && word.LetterCount > 1 && word.LetterCount <= Tuning.MaxLetters
            && (t.Flow || t.Grow(Duration(word.DurationMs), word.IsLastInLine) >= Tuning.LetterGrowFloor))
            return LyricsWordMode.Letters;
        return LyricsWordMode.Whole;
    }

    /// <summary>How long a word is sung, in ms: Noctis's figure, or 320 ms for a word without one (as Noctis Plus).</summary>
    public static double Duration(double ms) => double.IsFinite(ms) && ms > 1 ? ms : 320;

    private static double Seconds(double ms) => Duration(ms) / 1000;

    /// <summary>Where letter <paramref name="i"/>'s band starts and how wide it is, as shares of the word.</summary>
    private static void Band(int i, int n, double tail, out double start, out double width)
    {
        var wordEm = n * LetterWidthEm;
        var ahead = Math.Min((double)i / n, BandAheadEm / wordEm);
        var behind = Math.Min((double)(n - 1 - i) / n, BandBehindEm / wordEm);
        if (tail > 0) behind += Curves.Clamp01(tail) * (BandBehindEm / wordEm - behind);
        start = (double)i / n - ahead;
        width = 1.0 / n + ahead + behind;
    }

    /// <summary>How far the highlight has lit letter <paramref name="i"/> of <paramref name="n"/> (0..1, the fill itself).</summary>
    public static double Light(double progress, int i, int n)
    {
        if (progress <= FarAhead) return 0;
        if (progress >= LongSung) return 1;
        n = Math.Max(1, n);
        Band(i, n, 0, out var start, out var width);
        return Curves.SmoothStep(0, 1, Curves.Clamp01((progress - start) / width));
    }

    /// <summary>
    /// How far after a word letters may still be coming up (Smooth and Flowing), in word lengths.
    /// Noctis stops redrawing a word once the highlight is two words on, so a letter still on its
    /// way then would jump the rest of the way: a quarter of the word is less than the next word
    /// almost always takes.
    /// </summary>
    public const double RunOn = 0.25;

    /// <summary>
    /// How far letter <paramref name="i"/> of <paramref name="n"/> has come up (0..1). In Smooth and
    /// Flowing the last letters finish a moment into the next word (by <see cref="RunOn"/> at the
    /// latest); a line's last word finishes on its note in every look, because Noctis holds its
    /// highlight at the end until the line is over and then stops redrawing it.
    /// </summary>
    public static double Rise(Tuning t, double progress, double durationMs, int i, int n, bool lastInLine = false)
    {
        if (progress <= FarAhead) return 0;
        if (progress >= LongSung) return 1;
        n = Math.Max(1, n);
        var tail = t.Tail;
        var floor = t.LetterRise;
        if (tail <= 0 && floor <= 0) return Light(progress, i, n);
        Band(i, n, tail, out var start, out var width);
        var latest = lastInLine ? 1 : 1 + RunOn;
        width = Math.Max(1e-6, Math.Min(start + width, latest) - start);
        var want = Curves.Clamp01((progress - start) / width);
        // Never faster than the eye can follow: the letter's own ramp from the moment it starts,
        // still done by the latest end.
        if (floor > 0)
        {
            var seconds = Seconds(durationMs);
            floor = Math.Min(floor, (latest - start) * seconds);
            want = Math.Min(want, Curves.Clamp01((progress - start) * seconds / floor));
        }
        return Curves.SmoothStep(0, 1, want);
    }

    /// <summary>How far a word in one piece has come up (0..1).</summary>
    public static double WholeRise(Tuning t, double progress, double durationMs, bool lastInLine = false)
    {
        if (progress <= 0) return 0;
        if (progress >= LongSung) return 1;
        var seconds = Seconds(durationMs);
        var latest = (lastInLine ? 1 : 1 + RunOn) * seconds;
        return Curves.SmootherStep(progress * seconds / Math.Min(t.RiseSec(seconds), latest));
    }

    /// <summary>1 while the line is sung; Noctis's float as it comes down once the line is over.</summary>
    private static double LineKeep(double progress, double lift) =>
        progress >= LongSung ? (double.IsFinite(lift) ? Curves.Clamp01(lift) : 0) : 1;

    public static LyricsLetterPose Pose(Tuning t, in LyricsWord word, int letter)
    {
        var p = word.Progress;
        if (!double.IsFinite(p) || p <= FarAhead) return new LyricsLetterPose(1, 0, 0, 0);

        var whole = Mode(t, word) != LyricsWordMode.Letters;
        var n = whole ? 1 : Math.Max(1, word.LetterCount);
        letter = whole ? 0 : Math.Clamp(letter, 0, n - 1);
        var keep = LineKeep(p, word.Lift);
        var amount = t.Grow(Duration(word.DurationMs), word.IsLastInLine) * (whole ? Tuning.WholeWordShare : 1) * keep;

        // Grown letters make room for themselves: each is pushed by what the ones before it have
        // grown, the word as a whole staying centred where it was.
        double rise, grow, dx = 0;
        if (whole)
        {
            rise = WholeRise(t, p, Duration(word.DurationMs), word.IsLastInLine);
            grow = amount * rise;
        }
        else
        {
            double total = 0, passed = 0;
            rise = grow = 0;
            for (var j = 0; j < n; j++)
            {
                var r = Rise(t, p, Duration(word.DurationMs), j, n, word.IsLastInLine);
                var g = amount * r;
                if (j < letter) passed += g;
                else if (j == letter) { grow = g; rise = r; }
                total += g;
            }
            dx = (passed + grow / 2 - total / 2) * LetterWidthEm;
        }

        // The float: whole pixels, the same for every word, each letter coming up with its own rise
        // as the highlight reaches it (as in Noctis Plus); none when Noctis's own word float is
        // switched off (its lift is then always 0).
        var reach = word.Lift > 0 ? Reach(t, word.FontSize) : 0;
        var dy = -reach * rise * keep - (Tuning.LiftPerGrow + OriginLiftEm) * grow;

        var peak = t.HeldGlow(Duration(word.DurationMs), word.IsLastInLine);
        var glow = peak <= 0 ? 0
            : Curves.Clamp01(peak * keep * (whole ? Curves.SmoothStep(0, 1, rise) : Light(p, letter, n)));
        return new LyricsLetterPose(1 + grow, dx, dy, glow);
    }

    /// <summary>How far a sung word floats, in em: a whole number of pixels at the word's size.</summary>
    public static double Reach(Tuning t, double fontSize)
    {
        if (!(fontSize > 1) || !double.IsFinite(fontSize)) return t.LiftEm;
        return Math.Max(1, Math.Round(t.LiftEm * fontSize)) / fontSize;
    }
}
