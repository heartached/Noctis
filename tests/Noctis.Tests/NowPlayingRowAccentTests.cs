using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Noctis.Controls;
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

    /// <summary>
    /// Reproduces the resource graph the shipped app actually has, which
    /// <see cref="AccentTestHarness"/> cannot: App.axaml's OWN Application.Resources entries
    /// underneath the accent overlay App.SetAccent merges on top.
    ///
    /// A ResourceDictionary resolves its own entries BEFORE its merged dictionaries, so any
    /// literal declared directly in Application.Resources permanently shadows the overlay
    /// entry for the same key — no DynamicResource consumer ever sees the accent. The
    /// fallback therefore has to live BELOW Application.Resources, in the per-theme
    /// Styles.Resources blocks of Assets/Styles.axaml, exactly like AccentColorBrush.
    ///
    /// The harness lifts the overlay onto the headless application, which has no App.axaml
    /// own entries at all, so it cannot see this class of defect.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("#0D47A1")] // deep blue
    [InlineData("#FFAFC0")] // pale pink
    [InlineData("#12C76F")] // emerald
    public void RowBrushes_BeatAppAxamlOwnResources(string hex)
    {
        var app = LoadRealApp();
        app.SetAccent(hex);

        Assert.True(
            app.Resources.TryGetResource("NowPlayingRowBrush", ThemeVariant.Dark, out var fill),
            "NowPlayingRowBrush missing from Application.Resources after SetAccent");
        Assert.Equal(Color.Parse(hex), AccentTestHarness.ColorOf(fill as IBrush));

        Assert.True(
            app.Resources.TryGetResource("NowPlayingRowForegroundBrush", ThemeVariant.Dark, out var fg),
            "NowPlayingRowForegroundBrush missing from Application.Resources after SetAccent");
        var foreground = AccentTestHarness.ColorOf(fg as IBrush);
        var ratio = ThemeDerivation.ContrastRatio(Color.Parse(hex), foreground);
        Assert.True(ratio >= 3.0,
            $"accent {hex}: row text contrast {ratio:F2}:1 against the row fill");
    }

    /// <summary>
    /// The pre-accent fallback pair must still resolve — it just has to sit BELOW
    /// Application.Resources so the overlay can win. Assets/Styles.axaml's per-theme
    /// Styles.Resources is that layer, reached through Application.Styles, which is exactly
    /// where AccentColorBrush / AccentForegroundBrush already keep theirs. Probing Styles
    /// directly keeps this independent of whether an accent has been applied.
    /// </summary>
    [AvaloniaFact]
    public void RowBrushes_HaveAThemeFallback_BelowApplicationResources()
    {
        var app = LoadRealApp();

        foreach (var key in new[] { "NowPlayingRowBrush", "NowPlayingRowForegroundBrush" })
        {
            Assert.False(app.Resources.ContainsKey(key),
                $"'{key}' is an Application.Resources OWN entry — it would shadow the accent overlay");

            foreach (var variant in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            {
                Assert.True(((Application)app).Styles.TryGetResource(key, variant, out var res),
                    $"'{key}' has no {variant} fallback in Assets/Styles.axaml");
                Assert.IsAssignableFrom<ISolidColorBrush>(res);
            }
        }
    }

    /// <summary>
    /// Pins the 0.30 crossover itself. #949494 sits at Y 0.2961 and #959595 at Y 0.3005 —
    /// the two adjacent 8-bit greys either side of the threshold — so this fails the moment
    /// the crossover moves in either direction. Every other hex in this file sits far from it.
    /// </summary>
    [AvaloniaFact]
    public void RowForeground_FlipsToBlackExactlyAtLuminance030()
    {
        const string justBelow = "#949494";
        const string justAbove = "#959595";

        Assert.True(ThemeDerivation.RelativeLuminance(Color.Parse(justBelow)) < 0.30,
            $"{justBelow} was expected to sit just below the 0.30 crossover");
        Assert.True(ThemeDerivation.RelativeLuminance(Color.Parse(justAbove)) >= 0.30,
            $"{justAbove} was expected to sit just above the 0.30 crossover");

        AccentTestHarness.WithAccent(justBelow, () =>
            Assert.Equal(Colors.White, AccentTestHarness.ResourceColor("NowPlayingRowForegroundBrush")));
        AccentTestHarness.WithAccent(justAbove, () =>
            Assert.Equal(Colors.Black, AccentTestHarness.ResourceColor("NowPlayingRowForegroundBrush")));
    }

    /// <summary>
    /// The row's EQ bars used to carry Foreground="White" as a LOCAL value, which no style
    /// setter can reach — they would have gone invisible the moment a pale accent turned the
    /// row white too. The replacement is a `Border.now-playing controls|EqVisualizer` setter
    /// in Assets/Styles.axaml, and it rests on a precedence rule that is easy to get wrong:
    /// Controls/EqVisualizer.axaml is included AFTER Assets/Styles.axaml and sets Foreground
    /// from a plain type selector, so declaration order alone would hand it the win. It does
    /// not, because a selector carrying an activator (here the .now-playing class) binds at
    /// StyleTrigger priority, which outranks a plain style setter whenever it is active.
    /// This pins that rule; the real selectors are gated by XAML-IL at build time.
    /// </summary>
    [AvaloniaFact]
    public void ActivatedRowForegroundSetter_BeatsTheLaterUnconditionalOne()
    {
        var rowForeground = new Style(x => x.OfType<Border>().Class("now-playing")
                                            .Descendant().OfType<EqVisualizer>());
        rowForeground.Setters.Add(new Setter(TemplatedControl.ForegroundProperty, Brushes.Black));

        // Declared last, exactly like the EqVisualizer.axaml include in App.axaml.
        var controlDefault = new Style(x => x.OfType<EqVisualizer>());
        controlDefault.Setters.Add(new Setter(TemplatedControl.ForegroundProperty, Brushes.Red));

        var eq = new EqVisualizer();
        var row = new Border { Child = eq };
        row.Classes.Add("now-playing");

        var win = new Window { Width = 200, Height = 100, Content = row };
        win.Styles.Add(rowForeground);
        win.Styles.Add(controlDefault);
        try
        {
            win.Show();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(Colors.Black, AccentTestHarness.ColorOf(eq.Foreground));
        }
        finally
        {
            win.Close();
        }
    }

    private static Noctis.App? s_realApp;

    /// <summary>
    /// A real App with App.axaml loaded through the production path, so its
    /// Application.Resources holds exactly the OWN entries the shipped app has.
    /// (AvaloniaXamlLoader.Load from outside the declaring assembly does not populate an
    /// x:Class root — it hands back a bare instance — so Initialize is the only faithful way.)
    /// Built once per run because Initialize also registers a global TopLevel class handler.
    /// Application.Current stays the headless test app; this instance is only probed for
    /// resources, never shown.
    /// </summary>
    private static Noctis.App LoadRealApp()
    {
        if (s_realApp == null)
        {
            var app = new Noctis.App();
            app.Initialize();
            s_realApp = app;
        }

        return s_realApp;
    }
}
