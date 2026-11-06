using Noctis.Controls;
using Noctis.Models;
using SkiaSharp;

namespace Noctis.Services;

/// <summary>
/// Paints one word-timed token of a share render the way the lyrics page draws a word:
/// the held-note glow beneath the text, then the sung part lit through the page's soft band
/// (<see cref="KaraokeBand"/>) with the rest at the unsung level. Shared by the karaoke card
/// and the Lyrics Page video so both sweep alike. Painted as foreground gradients, never
/// OpacityMask (see ProgressToSweepForegroundConverter: mask layers clipped/boxed the glow
/// on the page).
/// </summary>
internal static class KaraokeTokenPainter
{
    /// <summary>
    /// Draws <paramref name="token"/> at (<paramref name="x"/>, <paramref name="baseline"/>):
    /// glow when <paramref name="word"/> is a held note (<see cref="EmphasisGlow.Opacity"/> ×
    /// <paramref name="litOpacity"/>, blurred by <paramref name="glowBlur"/>), then the band
    /// gradient from lit (<paramref name="litOpacity"/>) to dim (<paramref name="dimOpacity"/>)
    /// across the token's <paramref name="glyphWidth"/>. Leaves <paramref name="paint"/> with
    /// no shader or mask filter; its color is left at the last one drawn.
    /// </summary>
    internal static void DrawToken(SKCanvas canvas, string token, float x, float baseline, float glyphWidth,
        WordTiming word, float fontSize, SKColor fg, float litOpacity, float dimOpacity,
        SKPaint paint, SKMaskFilter glowBlur)
    {
        var litColor = fg.WithAlpha(ToAlpha(fg, litOpacity));
        var dimColor = fg.WithAlpha(ToAlpha(fg, dimOpacity));
        float w = glyphWidth;
        float y = baseline;

        // Held-note glow under the text (page: word-cell.emphasis word-glow).
        if (word.IsEmphasis)
        {
            var glow = EmphasisGlow.Opacity(word.EmphasisMs, word.HeldDurationMs, word.IsLastInLine,
                word.EmphasisCharCount) * litOpacity;
            if (glow > 0.004)
            {
                paint.Shader = null;
                paint.MaskFilter = glowBlur;
                paint.Color = fg.WithAlpha(ToAlpha(fg, (float)glow));
                ShareCardRenderer.DrawTextFallback(canvas, token, x, y, paint);
                paint.MaskFilter = null;
            }
        }

        // The soft band, in this word's glyph space (KaraokeWordText.Band).
        var feather = KaraokeBand.Feather(w, fontSize);
        var center = KaraokeBand.Center(word.Progress, feather, word.SweepLeadIn, word.SweepTrailOut);
        var lo = (float)((center - feather) * w);
        var hi = (float)((center + feather) * w);
        if (hi <= 0 || dimColor == litColor)
        {
            paint.Shader = null;
            paint.Color = hi <= 0 ? dimColor : litColor;
        }
        else if (lo >= w)
        {
            paint.Shader = null;
            paint.Color = litColor;
        }
        else
        {
            paint.Color = litColor;
            paint.Shader = SKShader.CreateLinearGradient(
                new SKPoint(x + lo, 0), new SKPoint(x + Math.Max(hi, lo + 0.5f), 0),
                new[] { litColor, dimColor }, null, SKShaderTileMode.Clamp);
        }
        ShareCardRenderer.DrawTextFallback(canvas, token, x, y, paint);
        if (paint.Shader is { } shader)
        {
            paint.Shader = null;
            shader.Dispose();
        }
    }

    /// <summary><paramref name="c"/>'s alpha scaled by <paramref name="opacity"/> (clamped to 0..1).</summary>
    internal static byte ToAlpha(SKColor c, float opacity) =>
        (byte)Math.Clamp(Math.Round(c.Alpha * Math.Clamp(opacity, 0f, 1f)), 0, 255);
}
