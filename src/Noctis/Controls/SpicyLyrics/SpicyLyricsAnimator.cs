// Port of LyricsAnimator.kt and LetterSynthesizer.kt from spicylyrics/mobile (AGPL-3.0)

using System;
using System.Collections.Generic;
using System.Globalization;
using Noctis.Models;

namespace Noctis.Controls.SpicyLyrics;

public enum LyricElementState
{
    NotSung,
    Active,
    Sung
}

public readonly record struct SpicyLyricsEvaluatedLetter(
    string Text,
    int Index,
    double Scale,
    double OffsetXEm,
    double OffsetYEm,
    double Glow,
    double GradientPosition);

public readonly record struct SpicyLyricsEvaluatedWord(
    double Scale,
    double OffsetYEm,
    double Glow,
    double GradientPosition,
    LyricElementState State,
    IReadOnlyList<SpicyLyricsEvaluatedLetter>? Letters);

/// <summary>
/// Faithful port of LyricsAnimator.kt and LetterSynthesizer.kt from spicylyrics/mobile.
/// Handles word-level and letter-level analytical springs, cubic splines, letter spread,
/// lift, glow, and state transitions.
/// </summary>
public sealed class SpicyLyricsAnimator
{
    private readonly SpicyLyricsPreset _preset;

    // Splines
    private readonly SpicyLyricsSpline _scaleSpline;
    private readonly SpicyLyricsSpline _glowSpline;
    private readonly SpicyLyricsSpline _yOffsetSpline;
    private readonly SpicyLyricsSpline _letterScaleSpline;
    private readonly SpicyLyricsSpline _letterYOffsetSpline;
    private readonly SpicyLyricsSpline _dotScaleSpline;
    private readonly SpicyLyricsSpline _dotYOffsetSpline;
    private readonly SpicyLyricsSpline _dotGlowSpline;

    private const double SungLetterGlow = 0.2;

    private sealed class WordSprings
    {
        public SpicyLyricsSpring Scale { get; }
        public SpicyLyricsSpring YOffset { get; }
        public SpicyLyricsSpring Glow { get; }

        public WordSprings(SpicyLyricsPreset preset, double initScale, double initY, double initGlow)
        {
            Scale = new SpicyLyricsSpring(initScale, preset.WordSpringFrequency, preset.WordSpringDamping);
            YOffset = new SpicyLyricsSpring(initY, preset.YSpringFrequency, preset.YSpringDamping);
            Glow = new SpicyLyricsSpring(initGlow, preset.GlowSpringFrequency, preset.GlowSpringDamping);
        }
    }

    private sealed class LetterSprings
    {
        public SpicyLyricsSpring Scale { get; }
        public SpicyLyricsSpring YOffset { get; }
        public SpicyLyricsSpring Glow { get; }
        public SpicyLyricsSpring Spread { get; }

        public LetterSprings(SpicyLyricsPreset preset, double initScale, double initY, double initGlow)
        {
            Scale = new SpicyLyricsSpring(initScale, preset.WordSpringFrequency, preset.WordSpringDamping);
            YOffset = new SpicyLyricsSpring(initY, preset.YSpringFrequency, preset.YSpringDamping);
            Glow = new SpicyLyricsSpring(initGlow, preset.GlowSpringFrequency, preset.GlowSpringDamping);
            Spread = new SpicyLyricsSpring(0.0, preset.WordSpringFrequency, preset.WordSpringDamping);
        }
    }

    private readonly Dictionary<int, WordSprings> _wordSprings = new();
    private readonly Dictionary<int, List<LetterSprings>> _letterSprings = new();

    public SpicyLyricsAnimator(SpicyLyricsPreset preset)
    {
        _preset = preset;

        var wordScaleTarget = 1.0 + preset.WordScaleAmount;
        var letterScaleTarget = 1.0 + preset.LetterScaleAmount;
        var lift = preset.LiftEm;

        _scaleSpline = SpicyLyricsSpline.FromPoints((0.0, 0.95), (0.7, wordScaleTarget), (1.0, 1.0));
        _glowSpline = SpicyLyricsSpline.FromPoints((0.0, 0.0), (0.15, 1.0), (0.6, 1.0), (1.0, 0.0));
        _yOffsetSpline = SpicyLyricsSpline.FromPoints((0.0, 0.01), (0.9, -lift), (1.0, 0.0));
        _letterScaleSpline = SpicyLyricsSpline.FromPoints((0.0, 0.95), (0.7, letterScaleTarget), (1.0, 1.0));
        _letterYOffsetSpline = SpicyLyricsSpline.FromPoints((0.0, 0.01), (0.9, -lift * 1.25), (1.0, 0.0));

        _dotScaleSpline = SpicyLyricsSpline.FromPoints((0.0, 0.75), (0.7, 1.05), (1.0, 1.0));
        _dotYOffsetSpline = SpicyLyricsSpline.FromPoints((0.0, 0.0), (0.9, -0.12), (1.0, 0.0));
        _dotGlowSpline = SpicyLyricsSpline.FromPoints((0.0, 0.0), (0.6, 1.0), (1.0, 1.0));
    }

    public void Reset()
    {
        _wordSprings.Clear();
        _letterSprings.Clear();
    }

    private static readonly char[] PunctuationTrimChars = { ',', '.', '!', '?', ';', ':', '—', '-', '\"', '\'', '’', '”' };

    private static bool IsCjk(string text)
    {
        foreach (var c in text)
            if ((c >= '\u2E80' && c <= '\u9FFF') || (c >= '\uAC00' && c <= '\uD7AF')) return true;
        return false;
    }

    private WordSprings GetOrCreateWordSprings(int wordIndex)
    {
        if (!_wordSprings.TryGetValue(wordIndex, out var springs))
        {
            springs = new WordSprings(
                _preset,
                _scaleSpline.At(0),
                _yOffsetSpline.At(0),
                _glowSpline.At(0));
            _wordSprings[wordIndex] = springs;
        }
        return springs;
    }

    private List<LetterSprings> GetOrCreateLetterSprings(int wordIndex, int letterCount)
    {
        if (!_letterSprings.TryGetValue(wordIndex, out var list) || list.Count != letterCount)
        {
            list = new List<LetterSprings>(letterCount);
            for (var i = 0; i < letterCount; i++)
            {
                list.Add(new LetterSprings(
                    _preset,
                    _letterScaleSpline.At(0),
                    _letterYOffsetSpline.At(0),
                    _glowSpline.At(0)));
            }
            _letterSprings[wordIndex] = list;
        }
        return list;
    }

    public SpicyLyricsEvaluatedWord AnimateWord(
        WordTiming word,
        int wordIndex,
        bool isLineActive,
        double dt)
    {
        var progress = double.IsFinite(word.Progress) ? word.Progress : 0.0;
        LyricElementState state;
        double pct;

        if (!isLineActive)
        {
            state = (progress >= 1.0 || word.IsPast) ? LyricElementState.Sung : LyricElementState.NotSung;
            pct = state == LyricElementState.Sung ? 1.0 : 0.0;
        }
        else
        {
            if (word.IsCurrent)
            {
                state = LyricElementState.Active;
                pct = Math.Clamp(progress, 0.0, 1.0);
            }
            else if (word.IsPast || progress >= 1.0)
            {
                state = LyricElementState.Sung;
                pct = 1.0;
            }
            else
            {
                state = LyricElementState.NotSung;
                pct = 0.0;
            }
        }

        const double gradientBase = -20.0;
        double targetScale;
        double targetYOffset;
        double targetGlow;
        double targetGradientPos;

        switch (state)
        {
            case LyricElementState.Active:
                targetScale = _scaleSpline.At(pct);
                targetYOffset = _yOffsetSpline.At(pct);
                targetGlow = _glowSpline.At(pct) * _preset.GlowMultiplier * LyricsRenderer.GlowExposure;
                targetGradientPos = gradientBase + 120.0 * pct;
                break;
            case LyricElementState.NotSung:
                targetScale = _scaleSpline.At(0);
                targetYOffset = _yOffsetSpline.At(0);
                targetGlow = _glowSpline.At(0);
                targetGradientPos = gradientBase;
                break;
            case LyricElementState.Sung:
            default:
                targetScale = _scaleSpline.At(1);
                targetYOffset = _yOffsetSpline.At(1);
                targetGlow = _glowSpline.At(1);
                targetGradientPos = 100.0;
                break;
        }

        if (!_preset.EnableGlow || LyricsRenderer.GlowExposure <= 0.01)
            targetGlow = 0.0;

        var springs = GetOrCreateWordSprings(wordIndex);
        springs.Scale.SetGoal(targetScale);
        springs.YOffset.SetGoal(targetYOffset);
        springs.Glow.SetGoal(targetGlow);

        var curScale = springs.Scale.Step(dt);
        var curY = springs.YOffset.Step(dt);
        var curGlow = springs.Glow.Step(dt);

        // Held word letter animation:
        // Faithful to spicylyrics/mobile (AppleMusicMotion.kt & LetterSynthesizer.kt):
        // 1) Leading whitespace is preserved separately so letter count reflects actual word characters.
        // 2) Punctuation is trimmed to count clean letters.
        // 3) 2 to 7 letters for Latin scripts (or any CJK word), held for >= 800ms or flagged IsEmphasis.
        // 4) Words longer than 7 letters (such as "spectacular?") do NOT split into letters — they sweep
        //    smoothly as a unified standard word. Words with 2..7 letters (such as "Dracula" or "forever")
        //    split into individually animated letters with outward spread, swell, lift, and soft glow!
        var text = word.SweepText;
        var trimmedStart = text.TrimStart();
        var lead = text.Length - trimmedStart.Length;
        var lettersText = text[lead..];
        var cleanText = lettersText.TrimEnd(PunctuationTrimChars);
        var cleanCount = new StringInfo(cleanText).LengthInTextElements;
        var elementCount = new StringInfo(lettersText).LengthInTextElements;
        var durationMs = word.HeldDurationMs > 0
            ? word.HeldDurationMs
            : (word.End.HasValue ? (word.End.Value - word.Start).TotalMilliseconds : 0.0);

        var isLetterCountOk = IsCjk(cleanText) || (cleanCount >= 2 && cleanCount <= LyricsRenderer.MaxSplitLetters);

        var isHeld = _preset.AnimateLetters &&
                     !word.IsRightToLeft &&
                     isLetterCountOk &&
                     elementCount >= 2 &&
                     (word.IsEmphasis || durationMs >= LyricsRenderer.EmphasisMinMs);

        IReadOnlyList<SpicyLyricsEvaluatedLetter>? letters = null;
        if (isHeld)
        {
            letters = AnimateLetters(lettersText, wordIndex, state, pct, durationMs, gradientBase, dt, word.IsLastInLine);
        }

        return new SpicyLyricsEvaluatedWord(
            Scale: curScale,
            OffsetYEm: curY,
            Glow: curGlow,
            GradientPosition: targetGradientPos,
            State: state,
            Letters: letters);
    }

    private List<SpicyLyricsEvaluatedLetter> AnimateLetters(
        string lettersText,
        int wordIndex,
        LyricElementState wordState,
        double wordProgress,
        double durationMs,
        double gradientBase,
        double dt,
        bool isLastInLine)
    {
        var elementCount = new StringInfo(lettersText).LengthInTextElements;
        if (elementCount <= 0)
            return new List<SpicyLyricsEvaluatedLetter>();

        var letterSprings = GetOrCreateLetterSprings(wordIndex, elementCount);
        var result = new List<SpicyLyricsEvaluatedLetter>(elementCount);

        if (wordState != LyricElementState.Active)
        {
            var t = wordState == LyricElementState.NotSung ? 0.0 : 1.0;
            var gradient = wordState == LyricElementState.NotSung ? gradientBase : 100.0;

            var si = StringInfo.GetTextElementEnumerator(lettersText);
            var li = 0;
            while (si.MoveNext() && li < letterSprings.Count)
            {
                var letterText = si.GetTextElement();
                var s = letterSprings[li];
                s.Scale.SetGoal(_letterScaleSpline.At(t));
                s.YOffset.SetGoal(_letterYOffsetSpline.At(t));
                s.Glow.SetGoal(_glowSpline.At(t));
                s.Spread.SetGoal(0.0);

                result.Add(new SpicyLyricsEvaluatedLetter(
                    Text: letterText,
                    Index: li,
                    Scale: s.Scale.Step(dt),
                    OffsetXEm: s.Spread.Step(dt),
                    OffsetYEm: s.YOffset.Step(dt),
                    Glow: s.Glow.Step(dt),
                    GradientPosition: gradient));
                li++;
            }
            return result;
        }

        // Active held word:
        // Letters swell, lift, spread outward from the word center, and glow across the hold.
        var du = Math.Max(LyricsRenderer.EmphasisMinMs, durationMs);
        var strength = SpicyLyricsRefreshedMotion.WaveStrength((long)du, isLastInLine);
        var centerIndex = (elementCount - 1) / 2.0;
        var siActive = StringInfo.GetTextElementEnumerator(lettersText);
        var idx = 0;

        while (siActive.MoveNext() && idx < letterSprings.Count)
        {
            var letterText = siActive.GetTextElement();

            // Organic staggering: each letter sets off across the hold (stagger = 1.0 / 2.5)
            var letterStartShare = (1.0 / 2.5) * idx / Math.Max(1, elementCount);
            var letterAt = wordProgress - letterStartShare;
            var swell = SpicyLyricsRefreshedMotion.Emphasis(letterAt);

            // Upstream bob / float
            var floatAt = (letterAt * du + 400.0) / (du * 1.4);
            var floatVal = floatAt > 0 && floatAt < 1
                ? Math.Sin(floatAt * Math.PI) * 0.05
                : 0.0;

            // Outward horizontal spread (f  o  r  e  v  e  r)
            // Left letters (idx < centerIndex) shift left (< 0), right letters shift right (> 0)
            var targetSpread = -swell * _preset.SpreadEm * strength.Amount * (centerIndex - idx);
            var targetScale = 1.0 + swell * _preset.LetterScaleAmount * strength.Amount;
            var targetLift = -swell * _preset.LiftEm * strength.Amount - floatVal;
            var targetGlow = swell * strength.Blur * _preset.GlowMultiplier * LyricsRenderer.GlowExposure;

            // Letter gradient: each letter sweeps as progress advances through its slot
            var slotStart = (double)idx / elementCount;
            var slotEnd = (double)(idx + 1) / elementCount;
            double targetGradient;
            if (wordProgress >= slotEnd)
            {
                targetGradient = 100.0;
            }
            else if (wordProgress <= slotStart)
            {
                targetGradient = gradientBase;
            }
            else
            {
                var frac = (wordProgress - slotStart) / (slotEnd - slotStart);
                targetGradient = gradientBase + 120.0 * Math.Sin(frac * Math.PI * 0.5);
            }

            if (!_preset.EnableGlow || LyricsRenderer.GlowExposure <= 0.01)
                targetGlow = 0.0;

            var s = letterSprings[idx];
            s.Scale.SetGoal(targetScale);
            s.YOffset.SetGoal(targetLift);
            s.Glow.SetGoal(targetGlow);
            s.Spread.SetGoal(targetSpread);

            result.Add(new SpicyLyricsEvaluatedLetter(
                Text: letterText,
                Index: idx,
                Scale: s.Scale.Step(dt),
                OffsetXEm: s.Spread.Step(dt),
                OffsetYEm: s.YOffset.Step(dt),
                Glow: s.Glow.Step(dt),
                GradientPosition: targetGradient));

            idx++;
        }

        return result;
    }
}
