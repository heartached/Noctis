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
/// foreground driven by the THEME — white on dark themes, black on light ones — rather than
/// by the accent's own luminance.
///
/// That constant is a deliberate design choice, made after seeing both on screen: a solid
/// accent band with constant text reads better than text that flips colour per accent. Do not
/// "fix" it into a plain luminance-derived foreground; most accents must keep the theme colour.
///
/// It is not, however, unconditional any more. It originally was, and the accepted trade-off
/// ("contrast is NOT guaranteed") turned out to have a floor below which the row stopped being
/// a design choice and became a bug: on a pale accent — the Dark theme's silver, a white or
/// pastel custom accent — white-on-near-white rendered the row as a blank white bar with no
/// visible title, time or EQ bars. So the theme constant now holds only while it clears 3:1
/// against the band, and below that the row falls back to whichever of black/white contrasts.
/// The accents where the constant reads are unaffected.
/// </summary>
public class NowPlayingRowAccentTests
{
    /// <summary>The floor App.SetAccent applies before it abandons the theme constant.</summary>
    private const double ContrastFloor = 3.0;

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
    /// Dark themes keep WHITE row text for every accent white can actually be read on — which
    /// is the normal case, and includes accents white is merely unexciting on. The constant is
    /// not chasing maximum contrast, only clearing the floor.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("#E74856")] // stock accent — white lands 3.85:1
    [InlineData("#0D47A1")] // deep blue
    [InlineData("#000000")] // black
    public void DarkTheme_RowText_KeepsTheThemeWhite_WhereWhiteReads(string hex)
    {
        AccentTestHarness.WithAccent(hex, ThemeVariant.Dark, () =>
        {
            Assert.Equal(Colors.White, AccentTestHarness.ResourceColor("NowPlayingRowForegroundBrush"));
        });
    }

    /// <summary>
    /// The bug this floor exists for: a pale accent on a dark theme is still a near-white
    /// band, and the theme's white text disappeared into it — the row rendered as a blank
    /// bar. These accents must flip to black instead.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("#FFFFFF")] // white — white-on-white, 1.0:1
    [InlineData("#E8E8E8")] // the Dark theme's silver accent — the reported case
    [InlineData("#FFAFC0")] // pale pink — white here is 1.7:1
    [InlineData("#FFD966")] // yellow
    [InlineData("#4CAF50")] // mid green — 2.5:1, under the floor
    public void DarkTheme_RowText_FlipsToBlack_WhenWhiteWouldVanish(string hex)
    {
        AccentTestHarness.WithAccent(hex, ThemeVariant.Dark, () =>
        {
            Assert.Equal(Colors.Black, AccentTestHarness.ResourceColor("NowPlayingRowForegroundBrush"));
        });
    }

    /// <summary>Light themes keep BLACK row text on everything but a genuinely dark accent.</summary>
    [AvaloniaTheory]
    [InlineData("#FFFFFF")]
    [InlineData("#E8E8E8")]
    [InlineData("#FFAFC0")]
    [InlineData("#E74856")]
    [InlineData("#4CAF50")]
    [InlineData("#FFD966")]
    public void LightTheme_RowText_KeepsTheThemeBlack_WhereBlackReads(string hex)
    {
        AccentTestHarness.WithAccent(hex, ThemeVariant.Light, () =>
        {
            Assert.Equal(Colors.Black, AccentTestHarness.ResourceColor("NowPlayingRowForegroundBrush"));
        });
    }

    /// <summary>The mirror case: black on a dark accent band is the same defect.</summary>
    [AvaloniaTheory]
    [InlineData("#0D47A1")] // deep blue — black lands 2.3:1
    [InlineData("#000000")] // black-on-black, 1.0:1
    public void LightTheme_RowText_FlipsToWhite_WhenBlackWouldVanish(string hex)
    {
        AccentTestHarness.WithAccent(hex, ThemeVariant.Light, () =>
        {
            Assert.Equal(Colors.White, AccentTestHarness.ResourceColor("NowPlayingRowForegroundBrush"));
        });
    }

    /// <summary>
    /// The property that matters, stated once over the whole accent range and both themes:
    /// the row text is always legible on the row fill. Whatever the constant does, no accent
    /// may put the row back under the floor.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("#FFFFFF")]
    [InlineData("#E8E8E8")]
    [InlineData("#FFAFC0")]
    [InlineData("#FFD966")]
    [InlineData("#4CAF50")]
    [InlineData("#12C76F")]
    [InlineData("#E74856")]
    [InlineData("#0D47A1")]
    [InlineData("#000000")]
    public void RowText_ClearsTheContrastFloor_OnEveryAccent(string hex)
    {
        foreach (var variant in new[] { ThemeVariant.Dark, ThemeVariant.Light })
        {
            AccentTestHarness.WithAccent(hex, variant, () =>
            {
                var fill = AccentTestHarness.ResourceColor("NowPlayingRowBrush");
                var text = AccentTestHarness.ResourceColor("NowPlayingRowForegroundBrush");
                Assert.True(Contrast(text, fill) >= ContrastFloor,
                    $"{variant} row text {text} on accent {hex} is {Contrast(text, fill):F2}:1");
            });
        }
    }

    /// <summary>
    /// The row foreground is its own resource, not a reuse of the accent-pill foreground.
    /// AccentForegroundBrush comes from GetReadableForeground, which biases toward white and is
    /// tuned for small glyphs; the row prefers the theme constant and only then the strongest
    /// contrast. Mid green on a light theme separates them: the pill takes white, the row keeps
    /// the theme's black. They must not be collapsed into one key.
    /// </summary>
    [AvaloniaFact]
    public void RowForeground_IsDistinctFromTheAccentPillForeground()
    {
        AccentTestHarness.WithAccent("#4CAF50", ThemeVariant.Light, () =>
        {
            Assert.Equal(Colors.Black, AccentTestHarness.ResourceColor("NowPlayingRowForegroundBrush"));
            Assert.Equal(Colors.White, AccentTestHarness.ResourceColor("AccentForegroundBrush"));
        });
    }

    /// <summary>WCAG contrast ratio, mirroring App's own helper so the tests stay independent of it.</summary>
    private static double Contrast(Color a, Color b)
    {
        static double Relative(Color c)
        {
            static double Linear(byte channel)
            {
                var value = channel / 255d;
                return value <= 0.03928 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
            }

            return 0.2126 * Linear(c.R) + 0.7152 * Linear(c.G) + 0.0722 * Linear(c.B);
        }

        var la = Relative(a);
        var lb = Relative(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
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
        // Which colour it lands on is the theme-constant-plus-floor rule the tests above pin.
        // What this one adds is that the value reaching a real consumer is the overlay's and
        // not a shadowed App.axaml literal, so assert the property that would break if it were:
        // a stale literal cannot stay legible across all three accents.
        Assert.True(
            Contrast(AccentTestHarness.ColorOf(fg as IBrush), Color.Parse(hex)) >= ContrastFloor,
            $"row text on accent {hex} did not come from the accent overlay");
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
