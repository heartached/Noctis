using System;

namespace Noctis.Helpers;

/// <summary>
/// The one motion curve every lyric line change uses — the scroll glide that brings
/// the new line to the anchor AND the line's own scale transition (XAML
/// <see cref="LineMotionEase"/>) share it, so size and position arrive together
/// instead of the line popping to full size before the list has started to move.
///
/// Spring step response fitted to both reference players (2026-10-24 captures, 60 fps,
/// every window below the active line tracked by cross-correlation): Apple Music over 521
/// window curves fits ζ 0.83 / ωn 9.4 rad/s, Joy Tunes over 266 fits ζ 0.88 / ωn 9.9; both
/// reach 10/50/90/99% at ~30/150/300/450 ms from the start of the motion with a median
/// overshoot of 0.34% / 0.09%. The 10-18 fit (ζ 0.77, ωn 8.25) came from one song and
/// overshot 2.3% — a visible stop-then-drift-back the new captures don't show. The base
/// curve is ζ <see cref="Zeta"/> = 0.86, ωn <see cref="NaturalFrequency"/> = 9.4
/// (0.5% overshoot). It runs in real time and is normalized to end exactly at 1 at
/// <see cref="DurationMs"/>. The scroll glide itself picks a spring by tempo
/// (<see cref="ForHold"/>) and carries velocity across interruptions (<see cref="LineGlide"/>).
/// Pinned by LyricsMotionTests.
/// </summary>
public static class LineMotion
{
    /// <summary>Length of a line-change motion — long enough for the spring's overshoot
    /// to settle back. The XAML line transitions carry the same value ("0:0:1") — pinned
    /// by LyricsMotionTests.</summary>
    public const int DurationMs = 1000;

    /// <summary>Damping ratio of the base curve. 1 = critically damped, lower = bouncier.</summary>
    public const double Zeta = 0.86;

    /// <summary>Natural frequency of the base curve in rad/s.</summary>
    public const double NaturalFrequency = 9.4;

    /// <summary>The base curve as a spring (an ordinary ~2 s line).</summary>
    public static readonly LineSpring BaseSpring = new(Zeta, NaturalFrequency);

    /// <summary>Lines held this long or longer glide on <see cref="SlowSpring"/>.</summary>
    public const double SlowHoldMs = 4000;

    /// <summary>Lines held this long glide on <see cref="BaseSpring"/>.</summary>
    public const double BaseHoldMs = 2000;

    /// <summary>Lines held this briefly or less glide on <see cref="FastSpring"/>.</summary>
    public const double FastHoldMs = 800;

    /// <summary>Slow parts (ballads, long held lines): a soft glide that lands without a
    /// drift-back. Apple's slow songs measured ζ 0.78-0.80 / ωn 7.5-8.1 with 1-2% overshoot
    /// (BIRDS OF A FEATHER, Die With A Smile); Joy's damping (ζ ~0.9) keeps the softness and
    /// drops the overshoot to 0.3%. 99% at ~590 ms.</summary>
    public static readonly LineSpring SlowSpring = new(0.88, 8.2);

    /// <summary>Fast lines (rap): Apple's Godzilla line followed 0.6 s later by the next
    /// one glided at ωn 14.1 (99% in ~290 ms) against ~10 on its 2-3 s lines. 99% at
    /// ~330 ms, so the list has landed before the next line is due.</summary>
    public static readonly LineSpring FastSpring = new(0.86, 14.0);

    /// <summary>
    /// The spring for a glide to a line that stays the scroll target for
    /// <paramref name="holdMs"/> (its start to the next line's start): <see cref="FastSpring"/>
    /// at or under <see cref="FastHoldMs"/>, <see cref="BaseSpring"/> at <see cref="BaseHoldMs"/>,
    /// <see cref="SlowSpring"/> from <see cref="SlowHoldMs"/>, log-interpolated in between.
    /// Unknown (NaN, last line, plain lyrics) is the base spring.
    /// </summary>
    public static LineSpring ForHold(double holdMs)
    {
        if (double.IsNaN(holdMs) || holdMs <= 0) return BaseSpring;
        if (holdMs <= FastHoldMs) return FastSpring;
        if (holdMs >= SlowHoldMs) return SlowSpring;
        static LineSpring Lerp(LineSpring a, LineSpring b, double f) =>
            new(a.Zeta + (b.Zeta - a.Zeta) * f, a.NaturalFrequency + (b.NaturalFrequency - a.NaturalFrequency) * f);
        return holdMs <= BaseHoldMs
            ? Lerp(FastSpring, BaseSpring, Math.Log(holdMs / FastHoldMs) / Math.Log(BaseHoldMs / FastHoldMs))
            : Lerp(BaseSpring, SlowSpring, Math.Log(holdMs / BaseHoldMs) / Math.Log(SlowHoldMs / BaseHoldMs));
    }

    /// <summary>
    /// How far ahead of the next word-timed line's first word the list sets off toward it
    /// (when the lit lines have finished by then — see LyricsTimeline.ScrollIndex). Measured:
    /// Apple Music starts the glide within 0-180 ms of the outgoing line's last word, and
    /// when that line ended long before, 680-750 ms ahead of the next line's first word
    /// (Godzilla, Love Me); Joy Tunes ~750 ms. The line has settled (99% at ~450-590 ms)
    /// before the voice instead of still travelling when it starts — the old lead was the
    /// curve's 90% time (349 ms).
    /// </summary>
    public const double ScrollLeadMs = 700;

    /// <summary>Per-line stagger of the cascade: each line below the active one starts
    /// its glide this much later than the line above it. Measured on Apple Music (iOS,
    /// 120 fps capture, "Die With A Smile"): four consecutive lines below the active one
    /// started at exactly +50, +100, +150, +200 ms, each on the same curve — 35 ms read
    /// as the stack moving nearly as one slab. Shared by the lyrics page and side panel.</summary>
    public const double CascadeDelayPerLineMs = 50;

    // Natural frequency per unit of normalized progress (p = 1 at DurationMs).
    private static readonly double Wn = NaturalFrequency * DurationMs / 1000.0;
    private static readonly double Wd = Wn * Math.Sqrt(1 - Zeta * Zeta);
    private static readonly double Norm = Raw(1);

    private static double Raw(double p)
    {
        var env = Math.Exp(-Zeta * Wn * p);
        return 1 - env * (Math.Cos(Wd * p) + Zeta / Math.Sqrt(1 - Zeta * Zeta) * Math.Sin(Wd * p));
    }

    /// <summary>Normalized progress → eased fraction; 0 at 0, exactly 1 at 1, clamped outside.</summary>
    public static double Ease(double p)
    {
        if (p <= 0) return 0;
        if (p >= 1) return 1;
        return Raw(p) / Norm;
    }

    /// <summary>Time at which the motion has covered half its travel — the moment the
    /// eye reads the line as "arrived". The line-only lyric lead is derived from it.</summary>
    public static readonly double HalfTravelMs = SolveTravel(0.5);

    /// <summary>Time at which the motion has covered 90% of its travel. The scroll leads
    /// a word-timed line by this much, so the list has all but arrived when its first
    /// word starts (see LyricsTimeline.ScrollIndex).</summary>
    public static readonly double NinetyTravelMs = SolveTravel(0.9);

    /// <summary>
    /// Limits one cascade line's lag relative to the line above it. Lines below the
    /// active one lag the base glide by a per-line delay; the raw lag difference
    /// between neighbours peaks at this curve's steepest slope × the per-line delay
    /// (~21% of the travel at 50 ms), which exceeds a 16px hard clamp on an ordinary
    /// line change. A hard clamp is a velocity kink — the line rides the one above for a few
    /// frames, then snaps back to its own curve — right at peak speed.
    ///
    /// A <em>negative</em> step means the lower line is displaced toward the one above
    /// (the cascade is closing: a backward glide). That is the only direction lines can
    /// cross, so it keeps the hard bound. A positive step means the lines are spreading
    /// (forward advance): identity up to <paramref name="hardStepPx"/>, then a smooth
    /// saturation that never exceeds twice it — no kink, no fly-apart on multi-line skips.
    /// </summary>
    public static double CascadeStep(double rawLag, double prevLag, double hardStepPx)
    {
        var step = rawLag - prevLag;
        if (step <= hardStepPx)
            return prevLag + Math.Max(step, -hardStepPx);
        var excess = step - hardStepPx;
        return prevLag + hardStepPx + hardStepPx * Math.Tanh(excess / hardStepPx);
    }

    private static double SolveTravel(double fraction)
    {
        double lo = 0, hi = 1;
        for (var i = 0; i < 60; i++)
        {
            var mid = (lo + hi) / 2;
            if (Ease(mid) < fraction) lo = mid; else hi = mid;
        }
        return (lo + hi) / 2 * DurationMs;
    }
}

/// <summary>XAML-usable easing over <see cref="LineMotion.Ease"/>.</summary>
public sealed class LineMotionEase : Avalonia.Animation.Easings.Easing
{
    public override double Ease(double progress) => LineMotion.Ease(progress);
}
