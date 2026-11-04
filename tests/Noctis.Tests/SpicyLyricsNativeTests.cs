using System;
using System.Collections.Generic;
using Avalonia.Media;
using Noctis.Controls.SpicyLyrics;
using Noctis.Models;
using Xunit;

namespace Noctis.Tests;

public sealed class SpicyLyricsNativeTests
{
    [Fact]
    public void Spline_InterpolatesEndpointsAndInteriorExact()
    {
        var spline = SpicyLyricsSpline.FromPoints((0.0, 0.95), (0.7, 1.0505), (1.0, 1.0));

        Assert.Equal(0.95, spline.At(0.0), precision: 4);
        Assert.Equal(1.0505, spline.At(0.7), precision: 4);
        Assert.Equal(1.0, spline.At(1.0), precision: 4);

        // Clamping check
        Assert.Equal(0.95, spline.At(-0.5), precision: 4);
        Assert.Equal(1.0, spline.At(1.5), precision: 4);
    }

    [Fact]
    public void Spring_AdvancesTowardGoalAnalytically()
    {
        var spring = new SpicyLyricsSpring(0.0, 1.45, 0.40);
        spring.SetGoal(1.0);

        for (var i = 0; i < 30; i++)
            spring.Step(0.016);

        // Underdamped spring should move toward and overshoot goal
        Assert.True(spring.Position > 0.5);
    }

    [Fact]
    public void Presets_ProvideFullMinimalAndCinematic()
    {
        Assert.Equal(4, SpicyLyricsPreset.All.Count);

        var full = SpicyLyricsPreset.FromMode("SpicyLyrics Full");
        Assert.False(full.IsMinimal);
        Assert.False(full.IsCinematic);

        var simple = SpicyLyricsPreset.FromMode("SpicyLyrics Simple");
        Assert.False(simple.IsMinimal);

        var minimal = SpicyLyricsPreset.FromMode("SpicyLyrics Minimal");
        Assert.True(minimal.IsMinimal);

        var cinematic = SpicyLyricsPreset.FromMode("SpicyLyrics Cinematic");
        Assert.True(cinematic.IsCinematic);
    }

    [Fact]
    public void RefreshedMotion_EvaluatesSweepEdges()
    {
        var x = new double[] { 0, 50, 100 };
        var w = new double[] { 40, 40, 40 };
        var starts = new double[] { 0, 1000, 2000 };
        var ends = new double[] { 1000, 2000, 3000 };

        var edges = SpicyLyricsRefreshedMotion.SweepEdges(x, w, starts, ends, fade: 20, t: 1500);

        Assert.Equal(3, edges.Length);
        Assert.True(edges[0] > 0);
        // Upstream invariant: past word tracks wherever the light has reached
        Assert.Equal(edges[1], edges[0]);
        // Current word is at 70px (midway between 50 and 90)
        Assert.Equal(70.0, edges[1], precision: 2);
    }

    [Fact]
    public void Animator_HeldWord_SynthesizesIndividualPoppingAndSpreadingLetters()
    {
        var animator = new SpicyLyricsAnimator(SpicyLyricsPreset.Full);
        var word = new WordTiming
        {
            Text = "forever ",
            Start = TimeSpan.FromSeconds(10),
            End = TimeSpan.FromSeconds(12), // 2000ms duration -> held word
            HeldDurationMs = 2000,
            IsCurrent = true,
            Progress = 0.35 // Active mid-word
        };

        var evaluated = animator.AnimateWord(word, wordIndex: 0, isLineActive: true, dt: 0.016);
        for (var i = 0; i < 15; i++)
            evaluated = animator.AnimateWord(word, wordIndex: 0, isLineActive: true, dt: 0.016);

        // Word should be broken into 7 letters ("forever")
        Assert.NotNull(evaluated.Letters);
        Assert.Equal(7, evaluated.Letters!.Count);

        // Check outward spreading: left letters shift left (< 0), right letters shift right (> 0)
        var leftLetter = evaluated.Letters[0]; // 'f'
        var rightLetter = evaluated.Letters[6]; // 'r'
        Assert.True(leftLetter.OffsetXEm < 0, $"Expected left letter to shift left, got {leftLetter.OffsetXEm}");
        Assert.True(rightLetter.OffsetXEm > 0, $"Expected right letter to shift right, got {rightLetter.OffsetXEm}");

        // Each active letter should lift (negative Y) and glow
        var middleLetter = evaluated.Letters[2]; // 'r'
        Assert.True(middleLetter.OffsetYEm < 0, $"Expected letter to lift, got {middleLetter.OffsetYEm}");
        Assert.True(middleLetter.Glow > 0, $"Expected letter to glow, got {middleLetter.Glow}");
    }

    [Fact]
    public void Animator_ShortWord_DoesNotSynthesizeLetters()
    {
        var animator = new SpicyLyricsAnimator(SpicyLyricsPreset.Full);
        var word = new WordTiming
        {
            Text = "the ",
            Start = TimeSpan.FromSeconds(1),
            End = TimeSpan.FromSeconds(1.3), // 300ms duration -> not held
            HeldDurationMs = 300,
            IsCurrent = true,
            Progress = 0.5
        };

        var evaluated = animator.AnimateWord(word, wordIndex: 0, isLineActive: true, dt: 0.016);
        for (var i = 0; i < 15; i++)
            evaluated = animator.AnimateWord(word, wordIndex: 0, isLineActive: true, dt: 0.016);

        // Normal short words should NOT split into letters
        Assert.Null(evaluated.Letters);
        // But word itself animates with scale > 1.0 and lift < 0
        Assert.True(evaluated.Scale > 1.0);
        Assert.True(evaluated.OffsetYEm < 0);
    }

    [Fact]
    public void Animator_Dracula_SplitsIntoLetters_And_Spectacular_StaysWhole()
    {
        var animator = new SpicyLyricsAnimator(SpicyLyricsPreset.Full);

        // "Dracula" (7 letters, held > 1000ms):
        // Upstream SpicyLyrics Mobile rule: 2..7 letters held -> splits into individual letters (f o r e v e r look)!
        var draculaWord = new WordTiming
        {
            Text = "Dracula",
            Start = TimeSpan.FromSeconds(20),
            End = TimeSpan.FromSeconds(22),
            HeldDurationMs = 2000,
            IsCurrent = true,
            Progress = 0.5
        };

        var draculaEval = animator.AnimateWord(draculaWord, wordIndex: 0, isLineActive: true, dt: 0.016);
        for (var i = 0; i < 15; i++)
            draculaEval = animator.AnimateWord(draculaWord, wordIndex: 0, isLineActive: true, dt: 0.016);

        Assert.NotNull(draculaEval.Letters);
        Assert.Equal(7, draculaEval.Letters!.Count);
        Assert.True(draculaEval.Letters[0].OffsetXEm < 0, "Dracula 'D' should shift left");
        Assert.True(draculaEval.Letters[6].OffsetXEm > 0, "Dracula 'a' should shift right");

        // "spectacular?" (12 characters > 7 letters):
        // Upstream SpicyLyrics Mobile rule: words longer than 7 letters do NOT split into letters,
        // they sweep smoothly as a unified standard word.
        var spectacularWord = new WordTiming
        {
            Text = "spectacular?",
            Start = TimeSpan.FromSeconds(25),
            End = TimeSpan.FromSeconds(27),
            HeldDurationMs = 2000,
            IsCurrent = true,
            Progress = 0.5
        };

        var spectacularEval = animator.AnimateWord(spectacularWord, wordIndex: 1, isLineActive: true, dt: 0.016);
        for (var i = 0; i < 15; i++)
            spectacularEval = animator.AnimateWord(spectacularWord, wordIndex: 1, isLineActive: true, dt: 0.016);

        Assert.Null(spectacularEval.Letters);
        Assert.True(spectacularEval.Scale > 1.0);
    }

    [Fact]
    public void AppleMusicMotion_Emphasizes_Rules_Match_Upstream_Tests()
    {
        // Direct parity with AppleMusicMotionTest.kt in spicylyrics/mobile
        Assert.True(SpicyLyricsAppleMusicMotion.Emphasizes("love", 4, 1200));
        Assert.False(SpicyLyricsAppleMusicMotion.Emphasizes("love", 4, 900));
        Assert.False(SpicyLyricsAppleMusicMotion.Emphasizes("I", 1, 2000));
        Assert.False(SpicyLyricsAppleMusicMotion.Emphasizes("everything", 10, 2000));
        Assert.False(SpicyLyricsAppleMusicMotion.Emphasizes("spectacular?", 12, 2000));
        Assert.True(SpicyLyricsAppleMusicMotion.Emphasizes("Dracula", 7, 1500));
        Assert.True(SpicyLyricsAppleMusicMotion.Emphasizes("forever", 7, 2000));
        Assert.True(SpicyLyricsAppleMusicMotion.Emphasizes("愛してる", 4, 1000));
        Assert.False(SpicyLyricsAppleMusicMotion.Emphasizes("حب", 2, 2000)); // RTL
    }

    [Fact]
    public void AppleMusicMotion_EmphasisLetter_PushesApartAndLifts()
    {
        // 7-letter word (Dracula / forever) at full hold (t = 2200ms, start = 1000ms, end = 3000ms)
        var left = SpicyLyricsAppleMusicMotion.EmphasisLetter(
            t: 2200, wordStartMs: 1000, wordEndMs: 3000, index: 0, count: 7, lastWord: false, background: false);
        var right = SpicyLyricsAppleMusicMotion.EmphasisLetter(
            t: 2200, wordStartMs: 1000, wordEndMs: 3000, index: 6, count: 7, lastWord: false, background: false);

        Assert.True(left.OffsetXEm < 0, "Leftmost letter should spread left");
        Assert.True(right.OffsetXEm > 0, "Rightmost letter should spread right");
        Assert.True(left.Scale > 1.0, "Held letter should swell");
        Assert.True(left.Glow > 0, "Held letter should glow");
        Assert.True(left.GlowRadiusEm <= 0.30, "Halo radius should be bounded");
    }

    [Fact]
    public void Animator_HeldWord_WithLeadingWhitespace_PreservesWhitespaceAndSplitsLetters()
    {
        var animator = new SpicyLyricsAnimator(SpicyLyricsPreset.Full);
        var word = new WordTiming
        {
            Text = " forever ", // Note leading space!
            Start = TimeSpan.FromSeconds(10),
            End = TimeSpan.FromSeconds(12),
            HeldDurationMs = 2000,
            IsCurrent = true,
            Progress = 0.5
        };

        var evaluated = animator.AnimateWord(word, wordIndex: 0, isLineActive: true, dt: 0.016);
        for (var i = 0; i < 15; i++)
            evaluated = animator.AnimateWord(word, wordIndex: 0, isLineActive: true, dt: 0.016);

        // Even with leading whitespace, clean letters are 7 ("forever"), so it MUST split into 7 letters!
        Assert.NotNull(evaluated.Letters);
        Assert.Equal(7, evaluated.Letters!.Count);
        Assert.Equal("f", evaluated.Letters[0].Text);
        Assert.Equal("r", evaluated.Letters[6].Text);
    }

    [Fact]
    public void AppleMusicMotion_Dots_ProgressiveIllumination()
    {
        var dots = new SpicyLyricsAppleMusicMotion.Dots(durationMs: 6000, count: 3);

        // After enter phase (t = 2000ms), dot 0 has lit up before dot 2
        var d0 = dots.DotAlpha(0, 2000);
        var d2 = dots.DotAlpha(2, 2000);
        Assert.True(d0 > d2, "Dot 0 should light before dot 2");

        // Towards end, all dots are fully lit
        var d2_end = dots.DotAlpha(2, 5800);
        Assert.Equal(1.0, d2_end, precision: 2);
    }

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void SpicyLyricsRenderControl_MeasuresAndWraps_CorrectHeight()
    {
        var engine = new SpicyLyricsLayoutEngine();
        var tf = new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Bold);
        var line2Words = new[]
        {
            new WordTiming { Text = "That " },
            new WordTiming { Text = "I " },
            new WordTiming { Text = "haven't " },
            new WordTiming { Text = "seen " },
            new WordTiming { Text = "your " },
            new WordTiming { Text = "face" }
        };

        // When constrained to 200px width, "That I haven't seen your face" wraps into multiple rows
        var rows = engine.BuildRows(line2Words, tf, 46, Brushes.White, 200);
        try
        {
            Assert.True(rows.Count >= 2, $"Expected at least 2 rows when constrained to 200px, but got {rows.Count}");
            
            var totalHeight = 0.0;
            for (var i = 0; i < rows.Count; i++)
            {
                if (i > 0) totalHeight += 6.0; // LayerSpacing
                totalHeight += rows[i].Height;
            }
            Assert.True(totalHeight > 46.0 * rows.Count * 0.8, "Total height must scale with row count to prevent clipping");
        }
        finally
        {
            foreach (var r in rows) r.Dispose();
        }
    }
}
