using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The now-playing track row used to be tinted from the current artwork's vibrant colour,
/// which ignored the accent the user picked in Settings. It now follows the accent, with a
/// foreground chosen at the true white/black contrast crossover so pale accents stay legible.
/// </summary>
public class NowPlayingRowAccentTests
{
    [AvaloniaTheory]
    [InlineData("#FFFFFF")] // white
    [InlineData("#FFAFC0")] // pale pink — the case that motivated this
    [InlineData("#E74856")] // stock accent
    [InlineData("#0D47A1")] // deep blue
    [InlineData("#000000")] // black
    public void RowFill_MatchesAccentExactly(string hex)
    {
        AccentTestHarness.WithAccent(hex, () =>
        {
            Assert.Equal(Color.Parse(hex), AccentTestHarness.ResourceColor("NowPlayingRowBrush"));
        });
    }

    /// <summary>
    /// The committed floor is 3:1, NOT 4.5:1. See the spec section "Why 0.30 and not the
    /// strict-AA crossover": white-on-colour only clears 4.5:1 up to Y ~ 0.183, so a strict
    /// rule would put black text on the stock red and most of the picker. Mid-tone accents
    /// deliberately land at 3-4:1, the same range Apple Music ships.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("#FFFFFF")]
    [InlineData("#FFAFC0")]
    [InlineData("#E74856")]
    [InlineData("#4CAF50")]
    [InlineData("#FFD966")]
    [InlineData("#0D47A1")]
    [InlineData("#000000")]
    public void RowForeground_NeverFallsBelow3To1(string hex)
    {
        AccentTestHarness.WithAccent(hex, () =>
        {
            var fill = AccentTestHarness.ResourceColor("NowPlayingRowBrush");
            var fg = AccentTestHarness.ResourceColor("NowPlayingRowForegroundBrush");

            Assert.True(fg == Colors.Black || fg == Colors.White,
                $"row foreground should be pure black or white, was {fg}");

            var ratio = ThemeDerivation.ContrastRatio(fill, fg);
            Assert.True(ratio >= 3.0,
                $"accent {hex}: row text contrast {ratio:F2}:1 against the row fill");
        });
    }

    /// <summary>
    /// Pale accents were the actual defect — white on them ran to roughly 1.7:1. Those must
    /// clear full AA, unlike the mid-tones.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("#FFFFFF")]
    [InlineData("#FFAFC0")]
    [InlineData("#FFD966")]
    public void PaleAccents_GetDarkRowText_AndClearAa(string hex)
    {
        AccentTestHarness.WithAccent(hex, () =>
        {
            var fill = AccentTestHarness.ResourceColor("NowPlayingRowBrush");
            var fg = AccentTestHarness.ResourceColor("NowPlayingRowForegroundBrush");

            Assert.Equal(Colors.Black, fg);
            Assert.True(ThemeDerivation.ContrastRatio(fill, fg) >= 4.5,
                $"pale accent {hex} must clear 4.5:1");
        });
    }

    /// <summary>
    /// Regression guard both ways. AccentForegroundBrush comes from GetReadableForeground,
    /// which biases toward white and only flips at luminance >= 0.6 — tuned for small glyphs
    /// like checkbox ticks. Pale pink sits near 0.56, so reusing that brush would keep white
    /// text at roughly 1.7:1. The row needs its own crossover.
    /// </summary>
    [AvaloniaFact]
    public void PalePinkAccent_UsesDarkRowText_NotTheAccentPillForeground()
    {
        AccentTestHarness.WithAccent("#FFAFC0", () =>
        {
            Assert.Equal(Colors.Black, AccentTestHarness.ResourceColor("NowPlayingRowForegroundBrush"));
            Assert.Equal(Colors.White, AccentTestHarness.ResourceColor("AccentForegroundBrush"));
        });
    }

    /// <summary>
    /// Guards the other direction: a saturated mid-tone accent keeps WHITE row text. If a
    /// future change moves the threshold to the strict-AA crossover this fails, which is the
    /// point — black text on the stock red is a deliberate non-goal.
    /// </summary>
    [AvaloniaFact]
    public void StockRedAccent_KeepsWhiteRowText()
    {
        AccentTestHarness.WithAccent("#E74856", () =>
        {
            Assert.Equal(Colors.White, AccentTestHarness.ResourceColor("NowPlayingRowForegroundBrush"));
        });
    }
}
