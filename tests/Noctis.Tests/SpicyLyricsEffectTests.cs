using Noctis.Controls;
using Xunit;

namespace Noctis.Tests;

public sealed class SpicyLyricsEffectTests
{
    [Fact]
    public void RendererSelection_NormalizesUnknownValuesToDefault()
    {
        LyricsRenderer.SetMode(LyricsRenderer.SpicyLyrics);
        Assert.True(LyricsRenderer.IsSpicyLyrics);

        LyricsRenderer.SetMode("not-a-renderer");
        Assert.False(LyricsRenderer.IsSpicyLyrics);
        Assert.Equal(LyricsRenderer.NoctisDefault, LyricsRenderer.Mode);
    }

    [Fact]
    public void Mode_UsesNativeForDistantWords_AndLettersForHeldWords()
    {
        var effect = SpicyLyricsEffect.Instance;

        var distant = new WordPoseInput(
            -2, 1600, 0, false, false, false, 5, 40, WordSurface.Page);

        var active = new WordPoseInput(
            0.45, 1600, 0, false, false, false, 5, 40, WordSurface.Page);

        var held = new WordPoseInput(
            0.45, 2400, 0, true, false, false, 5, 40, WordSurface.Page)
        {
            EmphasisMs = 900
        };

        Assert.Equal(WordDrawMode.Native, effect.Mode(distant));
        Assert.Equal(WordDrawMode.Whole, effect.Mode(active));
        Assert.Equal(WordDrawMode.Letters, effect.Mode(held));
    }

    [Fact]
    public void JoiningAndRtlWords_AreNeverSplitIntoLetters()
    {
        var effect = SpicyLyricsEffect.Instance;

        var arabic = new WordPoseInput(
            0.4, 2200, 0, true, false, true, 4, 40, WordSurface.Page)
        {
            EmphasisMs = 700
        };

        var joined = new WordPoseInput(
            0.4, 2200, 0, true, false, false, 4, 40, WordSurface.Page)
        {
            EmphasisMs = 700,
            IsJoinedScript = true
        };

        Assert.Equal(WordDrawMode.Whole, effect.Mode(arabic));
        Assert.Equal(WordDrawMode.Whole, effect.Mode(joined));
    }

    [Fact]
    public void LetterPoses_RemainFiniteAcrossTheHeldWord()
    {
        var effect = SpicyLyricsEffect.Instance;

        for (int frame = 0; frame <= 240; frame++)
        {
            var input = new WordPoseInput(
                Math.Clamp(frame / 240.0, 0, 1),
                2600,
                0,
                true,
                false,
                false,
                8,
                42,
                WordSurface.Page)
            {
                EmphasisMs = frame * (2600.0 / 240)
            };

            for (int letter = 0; letter < input.LetterCount; letter++)
            {
                var pose = effect.Pose(input, letter);
                Assert.True(double.IsFinite(pose.Scale));
                Assert.True(double.IsFinite(pose.OffsetXEm));
                Assert.True(double.IsFinite(pose.OffsetYEm));
                Assert.True(double.IsFinite(pose.Glow));
                Assert.InRange(pose.Scale, 0.05, 3.0);
                Assert.InRange(pose.Glow, 0.0, 1.0);
            }
        }
    }

    [Fact]
    public void WarmedUpPoseLoop_DoesNotAllocatePerFrame()
    {
        var effect = SpicyLyricsEffect.Instance;
        var input = new WordPoseInput(
            0.55, 3000, 0, true, false, false, 8, 40, WordSurface.Page)
        {
            EmphasisMs = 1500
        };

        for (int i = 0; i < 1000; i++)
            _ = effect.Pose(input, i % input.LetterCount);

        long before = GC.GetAllocatedBytesForCurrentThread();

        for (int i = 0; i < 10000; i++)
            _ = effect.Pose(input, i % input.LetterCount);

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
    }
}
