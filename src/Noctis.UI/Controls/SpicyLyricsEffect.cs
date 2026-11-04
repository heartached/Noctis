using System;
using System.Collections.Generic;
using Noctis.Models;

namespace Noctis.Controls;

/// <summary>
/// Selects the visual word renderer independently of plugin effects.
/// The parser, timeline and lyric models are shared by both renderers.
/// </summary>
public static class LyricsRenderer
{
    public const string NoctisDefault = "Noctis Default";
    public const string SpicyLyricsSimple = "SpicyLyrics Simple";
    public const string SpicyLyricsFull = "SpicyLyrics Full";
    public const string SpicyLyricsMinimal = "SpicyLyrics Minimal";
    public const string SpicyLyricsCinematic = "SpicyLyrics Cinematic";

    public const string SpicyLyrics = "SpicyLyrics";

    private static string _mode = SpicyLyrics;

    static LyricsRenderer()
    {
        if (IsSpicyLyrics)
        {
            LyricsWordEffect.Set(SpicyLyricsEffect.Instance);
        }
    }

    public static string Mode => _mode;
    public static bool IsSpicyLyrics => !string.Equals(_mode, NoctisDefault, StringComparison.Ordinal);

    public static int MaxSplitLetters { get; private set; } = 7;
    public static int EmphasisMinMs { get; private set; } = 1000;
    public static double AnimationScale { get; private set; } = 1.0;
    public static double GlowExposure { get; private set; } = 1.0;
    public static bool IgnoreBackgroundVocalsParentheses
    {
        get => WordTiming.IgnoreBackgroundParentheses;
        set => WordTiming.IgnoreBackgroundParentheses = value;
    }

    public static void UpdateConfig(int maxSplitLetters, int emphasisMinMs, int animationScalePercent, int glowExposurePercent, bool ignoreBackgroundVocalsParentheses = false)
    {
        MaxSplitLetters = Math.Clamp(maxSplitLetters, 2, 20);
        EmphasisMinMs = Math.Max(100, emphasisMinMs);
        AnimationScale = Math.Clamp(animationScalePercent / 100.0, 0.0, 2.0);
        GlowExposure = Math.Clamp(glowExposurePercent / 100.0, 0.0, 2.0);
        IgnoreBackgroundVocalsParentheses = ignoreBackgroundVocalsParentheses;
        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static void UpdateConfig(int maxSplitLetters, int emphasisMinMs, int animationScalePercent, int glowExposurePercent) =>
        UpdateConfig(maxSplitLetters, emphasisMinMs, animationScalePercent, glowExposurePercent, false);

    public static void UpdateConfig(int maxSplitLetters, int emphasisMinMs, int animationScalePercent) =>
        UpdateConfig(maxSplitLetters, emphasisMinMs, animationScalePercent, 100, false);

    public static event EventHandler? Changed;

    public static void SetMode(string? mode)
    {
        var next = mode switch
        {
            SpicyLyrics => SpicyLyrics,
            SpicyLyricsSimple => SpicyLyrics,
            SpicyLyricsFull => SpicyLyrics,
            SpicyLyricsMinimal => SpicyLyrics,
            SpicyLyricsCinematic => SpicyLyrics,
            _ => NoctisDefault
        };

        if (string.Equals(_mode, next, StringComparison.Ordinal))
            return;

        _mode = next;
        if (IsSpicyLyrics)
        {
            LyricsWordEffect.Set(SpicyLyricsEffect.Instance);
        }
        else
        {
            LyricsWordEffect.Set(null);
        }
        Changed?.Invoke(null, EventArgs.Empty);
    }
}

/// <summary>
/// Native SpicyLyrics-inspired implementation of Noctis's existing IWordEffect contract.
/// It uses deterministic time-based poses rather than a second timer or playback clock.
/// </summary>
public sealed class SpicyLyricsEffect : IWordEffect
{
    public static SpicyLyricsEffect Instance { get; } = new();

    private SpicyLyricsEffect() { }

    public WordDrawMode Mode(in WordPoseInput word)
    {
        // KaraokeSweep uses -2 / 3 as inert sentinels. Leave distant words to the
        // existing renderer so only words participating in the current sweep are animated.
        if (word.Progress <= -2 || word.Progress >= 3)
            return WordDrawMode.Native;

        // Upstream AppleMusicMotion.kt: held words (duration >= 1000ms) with 2 to 7 letters
        // (or CJK) split into individually animated letters (f o r e v e r blossom).
        // Words longer than 7 letters (such as "spectacular?") stay as Whole words.
        if (word.IsHeld && word.LetterCount >= 2 && word.LetterCount <= LyricsRenderer.MaxSplitLetters && !word.IsRightToLeft && !word.IsJoinedScript)
            return WordDrawMode.Letters;

        return WordDrawMode.Whole;
    }

    public LetterPose Pose(in WordPoseInput word, int letter)
    {
        if (!double.IsFinite(word.Progress) || word.Progress <= -2 || word.Progress >= 3)
            return LetterPose.Rest;

        if (word.IsHeld && word.LetterCount >= 2 && word.LetterCount <= LyricsRenderer.MaxSplitLetters && !word.IsRightToLeft &&
            !word.IsJoinedScript && double.IsFinite(word.EmphasisMs))
        {
            return LetterPoseAt(word, letter);
        }

        return WordPose(word);
    }

    private static LetterPose WordPose(in WordPoseInput word)
    {
        var p = Math.Clamp(word.Progress, 0, 1);

        // SpicyLyrics word scale curve: 0 -> .95, .7 -> 1.0505, 1 -> 1.
        double scale = p <= 0.7
            ? Lerp(0.95, 1.0505, EaseInOut(p / 0.7))
            : Lerp(1.0505, 1.0, EaseInOut((p - 0.7) / 0.3));

        // Its refreshed/simple word float is layered over Noctis's shared Lift clock.
        // Lift is already computed from the active word's absolute playback timing.
        double y = -0.05 * Clamp01(word.Lift);

        // Standard words in spicylyrics/mobile do not glow; only held words (like "spectacular?") glow
        double glow = word.IsHeld
            ? (p <= 0.15 ? EaseInOut(p / 0.15) : p <= 0.6 ? 1 : 1 - EaseInOut((p - 0.6) / 0.4)) * LyricsRenderer.GlowExposure
            : 0;

        return new LetterPose(scale, 0, y, glow);
    }

    private static LetterPose LetterPoseAt(in WordPoseInput word, int letter)
    {
        // Adapted from SpicyLyrics' AppleMusicMotion.emphasisLetter / RefreshedEmphasis.
        double minimumHoldMs = LyricsRenderer.EmphasisMinMs;
        double boost = LyricsRenderer.AnimationScale;
        const double staggerShare = 1.0 / 2.5;
        const double floatLeadMs = 400;
        const double floatStretch = 1.4;
        const double scaleAmount = 0.12;
        const double spreadEm = 0.038;
        const double liftEm = 0.107;
        const double floatEm = 0.055;

        int count = Math.Max(1, word.LetterCount);
        int index = Math.Clamp(letter, 0, count - 1);
        double duration = Math.Max(minimumHoldMs, FiniteOr(word.DurationMs, minimumHoldMs));

        double amount = Grow(duration / 2000.0) * 0.6;
        var ratio = Math.Clamp(duration / 1500.0, 0.4, 2.5);
        var growBlur = ratio >= 1.0 ? Math.Sqrt(ratio) : (0.5 + 0.5 * ratio);
        double blur = Math.Min(1.0, growBlur * 0.85);

        if (word.IsLastInLine)
        {
            amount *= 1.6;
            blur *= 1.3;
            duration *= 1.2;
        }

        amount = Math.Min(1.2, amount);
        blur = Math.Min(1.0, blur);

        // The upstream wave staggers letters over du / 2.5 / n * index
        double staggerMs = duration * staggerShare / count;
        int orderedIndex = word.IsRightToLeft ? count - 1 - index : index;
        double elapsed = word.EmphasisMs - staggerMs * orderedIndex;

        double swell = Emphasis(elapsed / duration);
        double floatProgress = (elapsed + floatLeadMs) / (duration * floatStretch);
        double floatOffset = floatProgress > 0 && floatProgress < 1
            ? Math.Sin(floatProgress * Math.PI) * floatEm
            : 0;

        // Outward horizontal spread (f  o  r  e  v  e  r)
        double x = -swell * spreadEm * amount * (count / 2.0 - index) * boost;
        double y = -swell * liftEm * amount * boost - floatOffset * boost;
        double scale = 1 + (swell * scaleAmount * amount) * boost;
        double glow = Math.Clamp(swell * blur * LyricsRenderer.GlowExposure, 0, 2.0);

        return new LetterPose(scale, x, y, glow);
    }

    public void LineDirections(IReadOnlyList<string> lines, Span<bool> rightToLeft)
    {
        // Direction detection remains Noctis's responsibility. The word control already
        // protects joining scripts from per-letter drawing.
    }

    private static double Emphasis(double x)
    {
        if (x <= 0 || x >= 1)
            return 0;

        return x < 0.5
            ? CubicBezier(0.2, 0.4, 0.58, 1, x / 0.5)
            : 1 - CubicBezier(0.3, 0, 0.58, 1, (x - 0.5) / 0.5);
    }

    private static double CubicBezier(double x1, double y1, double x2, double y2, double x)
    {
        if (x <= 0) return 0;
        if (x >= 1) return 1;

        double cx = 3 * x1;
        double bx = 3 * (x2 - x1) - cx;
        double ax = 1 - cx - bx;
        double cy = 3 * y1;
        double by = 3 * (y2 - y1) - cy;
        double ay = 1 - cy - by;

        double t = x;
        for (int i = 0; i < 8; i++)
        {
            double error = BezierCoordinate(ax, bx, cx, t) - x;
            if (Math.Abs(error) < 1e-6)
                return BezierCoordinate(ay, by, cy, t);

            double slope = BezierSlope(ax, bx, cx, t);
            if (Math.Abs(slope) < 1e-6)
                break;

            t = Math.Clamp(t - error / slope, 0, 1);
        }

        double lo = 0;
        double hi = 1;
        t = x;

        for (int i = 0; i < 18; i++)
        {
            if (BezierCoordinate(ax, bx, cx, t) < x)
                lo = t;
            else
                hi = t;

            t = (lo + hi) * 0.5;
        }

        return BezierCoordinate(ay, by, cy, t);
    }

    private static double BezierCoordinate(double a, double b, double c, double t) =>
        ((a * t + b) * t + c) * t;

    private static double BezierSlope(double a, double b, double c, double t) =>
        (3 * a * t + 2 * b) * t + c;

    private static double Grow(double value) =>
        value > 1 ? Math.Sqrt(value) : value * value * value;

    private static double EaseInOut(double value)
    {
        var t = Clamp01(value);
        return t * t * (3 - 2 * t);
    }

    private static double Lerp(double a, double b, double t) =>
        a + (b - a) * Clamp01(t);

    private static double Clamp01(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;

    private static double FiniteOr(double value, double fallback) =>
        double.IsFinite(value) ? value : fallback;
}
