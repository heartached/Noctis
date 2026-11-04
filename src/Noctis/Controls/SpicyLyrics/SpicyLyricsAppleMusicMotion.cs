// Port of AppleMusicMotion.kt from spicylyrics/mobile (AGPL-3.0)
// High-fidelity animation engine matching Apple Music and spicylyrics/mobile

using System;

namespace Noctis.Controls.SpicyLyrics;

/// <summary>
/// The Apple Music lyrics motion engine from spicylyrics/mobile:
/// Pure functions of time providing deterministic, seek-safe animation poses for:
/// - Word rise and soft wipe (Apple Music style)
/// - Held-word letter-by-letter popping, swelling, spreading, and glowing (2..7 letters)
/// - Interlude breathing and progressive dot illumination
/// - Distance blur, scale, and line opacity
/// </summary>
public static class SpicyLyricsAppleMusicMotion
{
    // ── Words ───────────────────────────────────────────────────────────────────────

    /// <summary>How far a word rises as it is sung (2.3px on 41px text); background vocals rise twice as far.</summary>
    public const double WordLiftEm = 0.055;

    /// <summary>The rise's spring response in seconds: critically damped, settling over about 1.4s.</summary>
    public const double RiseResponseS = 1.4;

    /// <summary>
    /// How far up a word is (0 -> 1, times WordLiftEm): springs up from start, stays up while line is sung,
    /// and once line ends springs back down.
    /// </summary>
    public static double WordRise(double t, double wordStartMs, double wordEndMs, double? lineEndMs)
    {
        static double Up(double ms) => SpringProgress(ms / 1000.0, RiseResponseS);
        if (!lineEndMs.HasValue || t < lineEndMs.Value) return Up(t - wordStartMs);
        return Up(lineEndMs.Value - wordStartMs) * (1.0 - Up(t - lineEndMs.Value));
    }

    /// <summary>A critically damped spring's step, 0 -> 1, settling over response seconds.</summary>
    public static double SpringProgress(double t, double response)
    {
        if (t <= 0.0) return 0.0;
        if (double.IsPositiveInfinity(t)) return 1.0;
        var r = Math.Max(0.001, response);
        var phase = 2.0 * Math.PI * t / r;
        return 1.0 - (1.0 + phase) * Math.Exp(-phase);
    }

    public const double DimAlpha = 0.55;
    public const double BrightAlpha = 1.0;
    public const double BackgroundDimAlpha = 0.40;
    public const double BackgroundBrightAlpha = 0.75;

    /// <summary>
    /// Soft edge behind the wipe: 0.75em, up to 0.45em more for long words, 0.3em for CJK.
    /// </summary>
    public static double FeatherEm(int charCount, bool cjk)
    {
        if (cjk) return 0.30;
        var extra = Math.Clamp((charCount - 6) / 10.0, 0.0, 1.0) * 0.45;
        return 0.75 + extra;
    }

    // ── Held words ──────────────────────────────────────────────────────────────────

    public static double EmphasisMinMs => LyricsRenderer.EmphasisMinMs;
    public static int EmphasisMaxLetters => LyricsRenderer.MaxSplitLetters;
    public const double EmphasisStrength = 0.70;
    public const double BackgroundEmphasis = 0.35;

    /// <summary>
    /// Upstream SpicyLyrics Mobile rule:
    /// Held words >= 1000ms get letter emphasis: 2 to 7 letters, or any CJK word.
    /// Words longer than 7 letters (such as "spectacular?") do NOT get letter emphasis.
    /// </summary>
    public static bool Emphasizes(string text, int letterCount, double durationMs)
    {
        if (durationMs < EmphasisMinMs || IsRtl(text)) return false;
        if (IsCjk(text)) return true;
        return letterCount >= 2 && letterCount <= EmphasisMaxLetters;
    }

    public readonly record struct LetterPose(
        double OffsetXEm,
        double OffsetYEm,
        double Scale,
        double Glow,
        double GlowRadiusEm);

    private static readonly CubicBezier EmphasisIn = new(0.2, 0.4, 0.58, 1.0);
    private static readonly CubicBezier EmphasisOut = new(0.3, 0.0, 0.58, 1.0);

    private static double EmphasisEasing(double x)
    {
        var clamped = Math.Clamp(x, 0.0, 1.0);
        return clamped < 0.5
            ? EmphasisIn.Transform(clamped / 0.5)
            : 1.0 - EmphasisOut.Transform((clamped - 0.5) / 0.5);
    }

    /// <summary>
    /// Evaluates one letter of a held word at time t (ms):
    /// The letters swell, push apart from the middle, and glow one after another with an organic bob.
    /// </summary>
    public static LetterPose EmphasisLetter(
        double t,
        double wordStartMs,
        double wordEndMs,
        int index,
        int count,
        bool lastWord,
        bool background)
    {
        var n = Math.Max(1, count);
        var du = Math.Max(EmphasisMinMs, wordEndMs - wordStartMs);

        static double Grow(double x) => x > 1.0 ? Math.Sqrt(x) : x * x * x;

        var amount = Grow(du / 2000.0) * 0.6;
        var ratio = Math.Clamp(du / 1500.0, 0.4, 2.5);
        var growBlur = ratio >= 1.0 ? Math.Sqrt(ratio) : (0.5 + 0.5 * ratio);
        var blur = Math.Min(1.0, growBlur * 0.85);

        if (lastWord) du *= 1.2;
        amount = Math.Min(1.2, amount) * EmphasisStrength * (background ? BackgroundEmphasis : 1.0);

        // Organic stagger: each letter sets off 1/2.5 of the word length over letter count
        var start = wordStartMs + du / 2.5 / n * index;
        var grow = EmphasisEasing((t - start) / du);

        // Organic bob starting 400ms ahead
        var bobX = Math.Clamp((t - (start - 400.0)) / (du * 1.4), 0.0, 1.0);
        var bob = Math.Sin(bobX * Math.PI) * WordLiftEm;

        var a = amount;
        var boost = LyricsRenderer.AnimationScale;
        var glowExposure = LyricsRenderer.GlowExposure;
        return new LetterPose(
            OffsetXEm: -grow * 0.038 * a * (n / 2.0 - index) * boost,
            OffsetYEm: -grow * 0.107 * a * boost - bob * boost,
            Scale: 1.0 + grow * 0.12 * a * boost,
            Glow: grow * blur * glowExposure,
            GlowRadiusEm: Math.Min(0.35, blur * 0.30 * glowExposure));
    }

    // ── Lines & Distance ────────────────────────────────────────────────────────────

    public const double InactiveScale = 0.98;
    public const double BlurStrength = 0.60;
    public const double BackgroundBlurShare = 0.50;

    public static double LineOpacity(int distance, bool userScrolling) =>
        distance == 0 ? 1.0 : (userScrolling ? 0.80 : 0.64);

    public static double BlurEm(int signedDistance)
    {
        var raw = signedDistance switch
        {
            0 => 0.0,
            -1 => 0.08,
            < 0 => 0.12,
            1 => 0.07,
            2 => 0.11,
            3 => 0.13,
            _ => 0.15
        };
        return raw * BlurStrength;
    }

    public static double BackgroundAlpha(double presence)
    {
        var x = Math.Clamp((presence - 0.3) / 0.7, 0.0, 1.0);
        return x * x * (3.0 - 2.0 * x);
    }

    public const double BackgroundFoldedScale = 0.96;

    // ── Interlude Dots ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The breathing dots over an instrumental break: fade in, breathe smoothly, dip and rise, then exit.
    /// </summary>
    public sealed class Dots
    {
        private const double Enter = 800.0;
        private const double Dip = 3000.0;
        private const double Still = 200.0;
        private const double Exit = 200.0;
        private const double Small = 0.92;

        private readonly double _durationMs;
        private readonly int _count;
        private readonly double _enterEnd;
        private readonly double _exitStart;
        private readonly double _stillStart;
        private readonly double _dipStart;
        private readonly bool _breathes;
        private readonly double _period;
        private readonly double _dotSpan;

        public Dots(double durationMs, int count = 3)
        {
            _durationMs = durationMs;
            _count = count;

            var factor = Math.Min(1.0, durationMs / (Enter + Dip + Still + Exit));
            _enterEnd = Enter * factor;
            _exitStart = durationMs - Exit * factor;
            _stillStart = _exitStart - Still * factor;
            _dipStart = _stillStart - Dip * factor;

            var breathing = Math.Max(0.0, _dipStart - _enterEnd);
            _breathes = breathing > 16.0;
            var halfCycles = Math.Max(1, (long)Math.Round(breathing / 1500.0));
            if (halfCycles % 2 == 0) halfCycles++;
            _period = 2.0 * breathing / halfCycles;
            _dotSpan = Math.Max(1.0, _exitStart - _enterEnd) / _count;
        }

        public double Scale(double t)
        {
            if (_durationMs <= 0 || t < 0) return 0.0;
            if (t < _enterEnd) return _breathes ? Small : 1.0;
            if (_breathes && t < _dipStart)
                return (1.0 + Small) / 2.0 - (1.0 - Small) / 2.0 * Math.Cos((t - _enterEnd) / _period * 2.0 * Math.PI);
            if (t < _dipStart) return 1.0;
            if (t < _stillStart)
            {
                var span = Math.Max(1e-6, _stillStart - _dipStart);
                return Small + (1.0 - Small) * Math.Cos(Math.Clamp((t - _dipStart) / span, 0.0, 1.0) * 2.0 * Math.PI);
            }
            if (t < _exitStart) return 1.0;
            var exitSpan = Math.Max(1e-6, _durationMs - _exitStart);
            return Smooth((_durationMs - t) / exitSpan);
        }

        public double Alpha(double t)
        {
            if (_durationMs <= 0) return 0.0;
            if (t < _enterEnd) return Smooth(t / _enterEnd);
            if (t < _exitStart) return 1.0;
            var exitSpan = Math.Max(1e-6, _durationMs - _exitStart);
            return Smooth((_durationMs - t) / exitSpan);
        }

        public double DotAlpha(int index, double t)
        {
            var p = Math.Clamp((t - _enterEnd - _dotSpan * index) / _dotSpan, 0.0, 1.0);
            return 0.20 + 0.80 * p;
        }

        private static double Smooth(double v)
        {
            var x = Math.Clamp(v, 0.0, 1.0);
            return x * x * (3.0 - 2.0 * x);
        }
    }

    // ── Script helpers ──────────────────────────────────────────────────────────────

    public static bool IsCjk(string text)
    {
        foreach (var c in text)
            if ((c >= '\u2E80' && c <= '\u9FFF') || (c >= '\uAC00' && c <= '\uD7AF') || (c >= '\u3040' && c <= '\u30FF')) return true;
        return false;
    }

    public static bool IsRtl(string text)
    {
        foreach (var c in text)
            if (c >= '\u0590' && c <= '\u08FF') return true;
        return false;
    }
}
