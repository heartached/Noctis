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
/// foreground driven by the THEME — white on dark themes, black on light ones — not by the
/// accent's own luminance.
///
/// This is a deliberate design choice, made after seeing both on screen: a solid accent band
/// with constant text reads better than text that flips colour per accent. The accepted
/// trade-off is that contrast is NOT guaranteed — a pale accent leaves row text near 1.7:1.
/// Do not "fix" this back to a luminance-derived foreground.
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
    /// Dark themes get WHITE row text, whatever the accent — including the pale accents
    /// where white lands near 1.7:1. That is the accepted trade-off, not an oversight.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("#FFFFFF")] // white
    [InlineData("#FFAFC0")] // pale pink — white here is ~1.7:1, deliberately
    [InlineData("#E74856")] // stock accent
    [InlineData("#4CAF50")] // mid green
    [InlineData("#FFD966")] // yellow
    [InlineData("#0D47A1")] // deep blue
    [InlineData("#000000")] // black
    public void DarkTheme_RowText_IsAlwaysWhite(string hex)
    {
        AccentTestHarness.WithAccent(hex, ThemeVariant.Dark, () =>
        {
            Assert.Equal(Colors.White, AccentTestHarness.ResourceColor("NowPlayingRowForegroundBrush"));
        });
    }

    /// <summary>Light themes get BLACK row text, whatever the accent.</summary>
    [AvaloniaTheory]
    [InlineData("#FFFFFF")]
    [InlineData("#FFAFC0")]
    [InlineData("#E74856")]
    [InlineData("#4CAF50")]
    [InlineData("#FFD966")]
    [InlineData("#0D47A1")]
    [InlineData("#000000")]
    public void LightTheme_RowText_IsAlwaysBlack(string hex)
    {
        AccentTestHarness.WithAccent(hex, ThemeVariant.Light, () =>
        {
            Assert.Equal(Colors.Black, AccentTestHarness.ResourceColor("NowPlayingRowForegroundBrush"));
        });
    }

    /// <summary>
    /// The row foreground must NOT track the accent's luminance. Pale pink and deep blue sit
    /// at opposite ends (Y 0.557 vs 0.064); under the previous luminance rule they resolved to
    /// black and white respectively. On one theme they must now agree. This is the guard
    /// against someone reintroducing a contrast-derived foreground.
    /// </summary>
    [AvaloniaFact]
    public void RowText_DoesNotVaryWithAccentLuminance()
    {
        Color pale = default, deep = default;
        AccentTestHarness.WithAccent("#FFAFC0", ThemeVariant.Dark,
            () => pale = AccentTestHarness.ResourceColor("NowPlayingRowForegroundBrush"));
        AccentTestHarness.WithAccent("#0D47A1", ThemeVariant.Dark,
            () => deep = AccentTestHarness.ResourceColor("NowPlayingRowForegroundBrush"));

        Assert.Equal(pale, deep);
        Assert.Equal(Colors.White, pale);
    }

    /// <summary>
    /// The row foreground is its own resource, not a reuse of the accent-pill foreground.
    /// AccentForegroundBrush comes from GetReadableForeground, which is luminance-derived and
    /// tuned for small glyphs; on a white accent it returns black while the dark-theme row
    /// stays white. They must not be collapsed into one key.
    /// </summary>
    [AvaloniaFact]
    public void RowForeground_IsDistinctFromTheAccentPillForeground()
    {
        AccentTestHarness.WithAccent("#FFFFFF", ThemeVariant.Dark, () =>
        {
            Assert.Equal(Colors.White, AccentTestHarness.ResourceColor("NowPlayingRowForegroundBrush"));
            Assert.Equal(Colors.Black, AccentTestHarness.ResourceColor("AccentForegroundBrush"));
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
        // Probed under the Dark variant above, so the foreground is white for every accent.
        // Deliberately no contrast assertion here: the foreground is theme-driven, and a pale
        // accent like #FFAFC0 lands near 1.7:1 by design.
        Assert.Equal(Colors.White, AccentTestHarness.ColorOf(fg as IBrush));
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
