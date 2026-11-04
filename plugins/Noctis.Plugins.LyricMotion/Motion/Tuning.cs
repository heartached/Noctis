namespace Noctis.Plugins.LyricMotion.Motion;

/// <summary>How the motion passes from one word to the next.</summary>
public enum Handover
{
    /// <summary>Noctis's own motion (right-to-left lyrics are still laid out from the right).</summary>
    Off,
    /// <summary>Every word is a step of its own, finished exactly on its note.</summary>
    Crisp,
    /// <summary>Every word is a step of its own; its last letters finish a moment into the next word.</summary>
    Smooth,
    /// <summary>The motion passes along the line in one piece.</summary>
    Flowing,
}

/// <summary>
/// Every number that shapes the motion, read once from the settings. The values are the ones
/// Lyric Motion uses where it is built into Noctis Plus (its Tuning.cs), so the plugin moves the
/// same way. Lengths are in em of the lyric font size, times in seconds.
/// Immutable: a settings change makes a new one, the frame path only reads it.
/// </summary>
public sealed class Tuning
{
    public Handover Animation { get; init; } = Handover.Smooth;

    /// <summary>Overall strength: 0.7 subtle, 1 balanced, 1.4 expressive.</summary>
    public double Intensity { get; init; } = 1.0;

    public bool Letters { get; init; } = true;
    public bool Glow { get; init; } = true;
    public bool RightToLeft { get; init; } = true;
    public bool OtherViews { get; init; } = true;

    /// <summary>A word sung at least this long is a long note.</summary>
    public double HeldMs { get; init; } = 600;

    public bool Moves => Animation != Handover.Off;

    public bool Flow => Animation == Handover.Flowing;

    /// <summary>
    /// How much of the band a letter comes up in is left standing past the end of its word:
    /// 0 = the last letter is up exactly when the word is over, 1 = it finishes at the pace of
    /// the letters in the middle of the word.
    /// </summary>
    public double Tail => Animation switch { Handover.Flowing => 1, Handover.Smooth => SmoothTail, _ => 0 };

    /// <summary>The shortest time a letter takes to come up; 0 = exactly with the fill.</summary>
    public double LetterRise => Animation switch { Handover.Flowing => LetterRiseSec, Handover.Smooth => SmoothRiseSec, _ => 0 };

    /// <summary>The two numbers of the smooth word-by-word look.</summary>
    public const double SmoothTail = 0.5, SmoothRiseSec = 0.12;

    /// <summary>The shortest time a letter takes to come up in the flowing look.</summary>
    public const double LetterRiseSec = 0.2;

    /// <summary>The shortest time a word takes to float up.</summary>
    public const double MinRiseSec = 0.16;

    /// <summary>How long a word in one piece takes to float up.</summary>
    public double RiseSec(double durationSec) => Animation == Handover.Smooth
        ? Math.Max(durationSec + Math.Min(0.2 * durationSec, 0.06), MinRiseSec + 0.02)
        : Math.Max(durationSec, MinRiseSec);

    /// <summary>How far a sung word floats up (AMLL: 0.05 em). It stays up until its line ends.</summary>
    public double LiftEm => 0.05 * Intensity;

    /// <summary>How much an ordinary word's letters grow (none on quick words: a rap verse would flicker).</summary>
    public double WordGrow(double ms) => 0.018 * Intensity * Curves.SmoothStep(160, 420, ms);

    /// <summary>How much a long note grows: more the longer it is held, a little more at a line's end.</summary>
    public double HeldGrow(double ms, bool lastWordOfLine)
    {
        var grow = Curves.Lerp(0.03, 0.06, Curves.SmoothStep(HeldMs, HeldMs + 1800, ms)) * Intensity;
        if (lastWordOfLine) grow *= 1.1;
        return Math.Min(grow, 0.10);
    }

    public double Grow(double ms, bool lastWordOfLine) => ms >= HeldMs ? HeldGrow(ms, lastWordOfLine) : WordGrow(ms);

    /// <summary>
    /// A long note's glow at its fullest, as a share of Noctis's own glow. Noctis Plus draws its own
    /// halo at 0.2 to 0.5 opacity; through the plugin API the glow can only be a share of Noctis's,
    /// so the same curve is given relative to its top (0.5).
    /// </summary>
    public double HeldGlow(double ms, bool lastWordOfLine)
    {
        if (!Glow || ms < HeldMs) return 0;
        var glow = Curves.Lerp(0.2, 0.5, Curves.SmoothStep(HeldMs, HeldMs + 2000, ms)) * Intensity;
        if (lastWordOfLine) glow *= 1.1;
        return Math.Min(1, glow / 0.5);
    }

    /// <summary>Growth below this cannot be seen: such a word moves in one piece.</summary>
    public const double LetterGrowFloor = 0.004;

    /// <summary>A word in one piece grows a little less.</summary>
    public const double WholeWordShare = 0.7;

    /// <summary>How far a letter rises on top of the word's float, in em per unit of growth.</summary>
    public const double LiftPerGrow = 0.5;

    /// <summary>Longest word (in letters) that is split; longer ones stay whole.</summary>
    public const int MaxLetters = 14;

    public static Tuning From(IPluginSettings? settings)
    {
        if (settings is null) return new Tuning();
        var animation = (settings.GetString("animation") ?? "").Trim().ToLowerInvariant();
        var strength = (settings.GetString("strength") ?? "").Trim().ToLowerInvariant();
        return new Tuning
        {
            Animation = animation.StartsWith("off", StringComparison.Ordinal) ? Handover.Off
                : animation.StartsWith("flow", StringComparison.Ordinal) ? Handover.Flowing
                : animation.Contains("crisp", StringComparison.Ordinal) ? Handover.Crisp
                : Handover.Smooth,
            Intensity = strength switch { "subtle" => 0.7, "expressive" => 1.4, _ => 1.0 },
            Letters = settings.GetBool("letters", true),
            Glow = settings.GetBool("glow", true),
            RightToLeft = settings.GetBool("rtl", true),
            OtherViews = settings.GetBool("otherViews", true),
            HeldMs = Math.Clamp(settings.GetNumber("heldMs", 600) is var ms && double.IsFinite(ms) ? ms : 600, 300, 2000),
        };
    }
}

/// <summary>Easing curves shared by the word and letter motion. No allocation.</summary>
public static class Curves
{
    public static double Clamp01(double x) => x < 0 ? 0 : (x > 1 ? 1 : x);

    public static double Lerp(double a, double b, double t) => a + (b - a) * t;

    /// <summary>0 below <paramref name="lo"/>, 1 above <paramref name="hi"/>, smooth in between.</summary>
    public static double SmoothStep(double lo, double hi, double x)
    {
        var t = Clamp01((x - lo) / (hi - lo));
        return t * t * (3 - 2 * t);
    }

    /// <summary>0 to 1 with zero speed and zero acceleration at both ends: nothing pops, nothing overshoots.</summary>
    public static double SmootherStep(double x)
    {
        var t = Clamp01(x);
        return t * t * t * (t * (t * 6 - 15) + 10);
    }
}
