namespace Noctis.Plugins.LyricMotion.Motion;

/// <summary>
/// The motion itself: a letter's pose as a pure function of where Noctis's highlight is
/// (<see cref="LyricsWord.Progress"/>), how long the word is sung and Noctis's float
/// (<see cref="LyricsWord.Lift"/>). No state and no allocation, so it is exactly in time with the
/// sweep Noctis draws, whatever the frame rate, and a seek simply lands on the right pose.
///
/// Progress runs 0..1 across the word, a little past both ends on its neighbours; Noctis
/// reports -2 for words far ahead and 3 for words long sung. Everything here is over by 2:
/// a letter comes up while the highlight crosses it (a band <see cref="Tuning.Tail"/> wider
/// than the letter in Smooth and Flowing), and once the word is over the letters relax again,
/// first letter first, so the release runs along the word the way the singing did.
/// </summary>
public static class LetterMotion
{
    /// <summary>Where a word's letters start to relax, past its end (in word lengths).</summary>
    public const double SettleStart = 1.1;
    /// <summary>How much later the last letter relaxes than the first.</summary>
    public const double SettleSpread = 0.3;
    /// <summary>How long a letter takes to relax.</summary>
    public const double SettleLength = 0.55;

    public static LyricsWordMode Mode(Tuning t, in LyricsWord word)
    {
        if (!t.Moves) return LyricsWordMode.Native;
        if (word.Surface != LyricsSurface.Page && !t.OtherViews) return LyricsWordMode.Native;
        if (t.Letters && !word.IsRightToLeft && word.LetterCount > 1 && word.LetterCount <= Tuning.MaxLetters
            && (t.Animation == Handover.Flowing || t.Grow(word.DurationMs, word.IsLastInLine) >= Tuning.LetterGrowFloor))
            return LyricsWordMode.Letters;
        return LyricsWordMode.Whole;
    }

    /// <summary>How far the highlight has brought letter <paramref name="i"/> of <paramref name="n"/> up (0..1).</summary>
    public static double Arrival(Tuning t, double progress, int i, int n)
    {
        if (progress <= -1.5) return 0;
        if (progress >= 2.5) return 1;
        n = Math.Max(1, n);
        var start = (double)i / n;
        var width = 1.0 / n + t.Tail;
        // Up before its relaxing starts, however wide the band.
        width = Math.Min(width, SettleStart + SettleSpread * i / n - start);
        return Curves.SmootherStep((progress - start) / width);
    }

    /// <summary>How far letter <paramref name="i"/> has relaxed after its word (0..1).</summary>
    public static double Settle(double progress, int i, int n)
    {
        if (progress <= -1.5) return 0;
        if (progress >= 2.5) return 1;
        n = Math.Max(1, n);
        var from = SettleStart + SettleSpread * i / n;
        return Curves.SmootherStep((progress - from) / SettleLength);
    }

    public static LyricsLetterPose Pose(Tuning t, in LyricsWord word, int letter)
    {
        var p = word.Progress;
        var held = word.DurationMs >= t.HeldMs;
        var lift = double.IsFinite(word.Lift) ? Curves.Clamp01(word.Lift) : 0;
        var floatEm = -t.LiftEm * lift;
        var glowPeak = t.HeldGlow(word.DurationMs, word.IsLastInLine);
        if (!double.IsFinite(p)) return new LyricsLetterPose(1, 0, floatEm, glowPeak);

        double grow;
        int n;
        if (Mode(t, word) == LyricsWordMode.Letters)
        {
            n = Math.Max(1, word.LetterCount);
            letter = Math.Clamp(letter, 0, n - 1);
            var amount = t.Grow(word.DurationMs, word.IsLastInLine);
            grow = amount * Arrival(t, p, letter, n) * (1 - Settle(p, letter, n));
        }
        else
        {
            n = 1;
            letter = 0;
            if (held)
            {
                // A long note in one piece grows as a whole, a little less, and relaxes after.
                grow = t.HeldGrow(word.DurationMs, word.IsLastInLine) * Tuning.WholeWordShare
                       * Arrival(t, p, 0, 1) * (1 - Settle(p, 0, 1));
            }
            else
            {
                // An ordinary word swells once while it is sung (the band's width in Smooth/Flowing).
                var span = 1 + t.Tail;
                grow = t.WordGrow(word.DurationMs) * (p is > -1.5 and < 2.5 ? Curves.Hill(p / span) : 0);
            }
        }

        // Grown letters push away from the word's middle a little so they do not crowd, and rise
        // a touch on top of the word's float.
        var centre = (n - 1) / 2.0;
        var dx = (letter - centre) * grow * 0.28;
        var dy = floatEm - Tuning.LiftPerGrow * grow;
        // A long note's glow follows its letters up and settles with them.
        var glow = glowPeak <= 0 ? 0 : glowPeak * (held ? Math.Max(0.35, 1 - Settle(p, letter, n)) : 0);
        return new LyricsLetterPose(1 + grow, dx, dy, glow);
    }
}
