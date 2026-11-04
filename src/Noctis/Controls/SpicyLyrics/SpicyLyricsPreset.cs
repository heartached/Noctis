using System;
using System.Collections.Generic;

namespace Noctis.Controls.SpicyLyrics;

/// <summary>
/// Preset configuration for the native SpicyLyrics renderer in Noctis.
/// Faithful to upstream RenderConfig.kt and LyricsAnimator.kt.
/// </summary>
public sealed record SpicyLyricsPreset(
    string Name,
    bool IsMinimal,
    bool IsCinematic,
    double WordSpringFrequency,
    double WordSpringDamping,
    double YSpringFrequency,
    double YSpringDamping,
    double GlowSpringFrequency,
    double GlowSpringDamping,
    double WordScaleAmount,
    double LetterScaleAmount,
    double LiftEm,
    double SpreadEm,
    double GlowMultiplier,
    double NotSungOpacity,
    double SungOpacity,
    bool AnimateLetters,
    bool EnableGlow,
    bool EnableDistanceBlur)
{
    /// <summary>
    /// Upstream SpicyLyrics Full mode: classic analytical springs, natural cubic splines,
    /// full word and letter expansion, and line-wide glow.
    /// </summary>
    public static readonly SpicyLyricsPreset Full = new(
        Name: "SpicyLyrics Full",
        IsMinimal: false,
        IsCinematic: false,
        WordSpringFrequency: 0.88,
        WordSpringDamping: 0.64,
        YSpringFrequency: 1.45,
        YSpringDamping: 0.40,
        GlowSpringFrequency: 1.18,
        GlowSpringDamping: 0.56,
        WordScaleAmount: 0.0505,
        LetterScaleAmount: 0.175,
        LiftEm: 1.0 / 60.0,
        SpreadEm: 0.055,
        GlowMultiplier: 1.0,
        NotSungOpacity: 0.51,
        SungOpacity: 0.497,
        AnimateLetters: true,
        EnableGlow: true,
        EnableDistanceBlur: true);

    /// <summary>
    /// Upstream SpicyLyrics Simple mode: slightly reduced opacity tiers.
    /// </summary>
    public static readonly SpicyLyricsPreset Simple = new(
        Name: "SpicyLyrics Simple",
        IsMinimal: false,
        IsCinematic: false,
        WordSpringFrequency: 0.88,
        WordSpringDamping: 0.64,
        YSpringFrequency: 1.45,
        YSpringDamping: 0.40,
        GlowSpringFrequency: 1.18,
        GlowSpringDamping: 0.56,
        WordScaleAmount: 0.0505,
        LetterScaleAmount: 0.175,
        LiftEm: 1.0 / 60.0,
        SpreadEm: 0.055,
        GlowMultiplier: 0.9,
        NotSungOpacity: 0.45,
        SungOpacity: 0.35,
        AnimateLetters: true,
        EnableGlow: true,
        EnableDistanceBlur: true);

    /// <summary>
    /// Upstream SpicyLyrics Minimal mode: preserves word/letter motion, while applying
    /// upstream minimal line opacity (0.50/0.45) and scale tiers (0.965/0.950).
    /// </summary>
    public static readonly SpicyLyricsPreset Minimal = new(
        Name: "SpicyLyrics Minimal",
        IsMinimal: true,
        IsCinematic: false,
        WordSpringFrequency: 0.88,
        WordSpringDamping: 0.64,
        YSpringFrequency: 1.45,
        YSpringDamping: 0.40,
        GlowSpringFrequency: 1.18,
        GlowSpringDamping: 0.56,
        WordScaleAmount: 0.0505,
        LetterScaleAmount: 0.175,
        LiftEm: 1.0 / 60.0,
        SpreadEm: 0.045,
        GlowMultiplier: 0.85,
        NotSungOpacity: 0.50,
        SungOpacity: 0.45,
        AnimateLetters: true,
        EnableGlow: true,
        EnableDistanceBlur: false);

    /// <summary>
    /// Custom Noctis Cinematic mode: built on the SpicyLyrics spring and spline engine,
    /// with widened letter swell, deeper lift, and richer glow envelope.
    /// </summary>
    public static readonly SpicyLyricsPreset Cinematic = new(
        Name: "SpicyLyrics Cinematic",
        IsMinimal: false,
        IsCinematic: true,
        WordSpringFrequency: 0.88,
        WordSpringDamping: 0.60,
        YSpringFrequency: 1.45,
        YSpringDamping: 0.38,
        GlowSpringFrequency: 1.18,
        GlowSpringDamping: 0.52,
        WordScaleAmount: 0.075,
        LetterScaleAmount: 0.225,
        LiftEm: 0.025,
        SpreadEm: 0.075,
        GlowMultiplier: 1.15,
        NotSungOpacity: 0.40,
        SungOpacity: 0.40,
        AnimateLetters: true,
        EnableGlow: true,
        EnableDistanceBlur: true);

    public static IReadOnlyList<SpicyLyricsPreset> All { get; } =
        [Full, Simple, Minimal, Cinematic];

    public static SpicyLyricsPreset FromMode(string? mode)
        => mode switch
        {
            "SpicyLyrics Simple" => Simple,
            "SpicyLyrics Minimal" => Minimal,
            "SpicyLyrics Cinematic" => Cinematic,
            _ => Full // "SpicyLyrics Full" and legacy "SpicyLyrics"
        };
}
