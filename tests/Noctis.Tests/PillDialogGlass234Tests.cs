using System.Diagnostics;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Controls;
using Noctis.Helpers;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #234: the pop-ups (Fetch Lyrics, Spectrogram, ReplayGain, Convert Audio, Edit Info,
/// Add to Playlist, Update Description, Send to Folder and every other PillDialogHost card)
/// sat on a plain card while the rest of the app frosted. With Liquid Glass on the card is
/// now the Settings sheet's frost (a GlassPanel, BlurRadius 22, tinted with the card colour
/// at Glass Opacity, never below the readable floor) over the blurred app; with it off, or
/// without the blurred snapshot to frost, the card is exactly the solid one it was.
/// </summary>
public class PillDialogGlass234Tests
{
    private static void EnsureAppStyles()
    {
        var app = Application.Current!;
        if (app.Resources.TryGetResource("HeartFillIcon", null, out _)) return;
        app.Resources["InterSemiBold"] = FontFamily.Default;
        app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml") });
        app.Styles.Add(new StyleInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml") });
    }

    private static bool PumpUntil(Func<bool> condition, int budgetMs = 3000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < budgetMs)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            if (condition()) return true;
            var frame = new DispatcherFrame();
            using (DispatcherTimer.RunOnce(() => frame.Continue = false, TimeSpan.FromMilliseconds(5), DispatcherPriority.Send))
                Dispatcher.UIThread.PushFrame(frame);
        }
        return condition();
    }

    /// <summary>An owner with something to blur (the snapshot needs pixels).</summary>
    private static Window Owner()
    {
        var stripes = new StackPanel();
        var colors = new[] { "#FFFFFF", "#000000", "#E74856", "#2D7DD2" };
        for (var i = 0; i < 16; i++)
            stripes.Children.Add(new Border { Height = 50, Background = new SolidColorBrush(Color.Parse(colors[i % colors.Length])) });
        var owner = new Window { Width = 1100, Height = 820, Content = stripes };
        owner.Show();
        // Rendered once before the pop-up snapshots it.
        PumpUntil(() => false, 100);
        return owner;
    }

    private static PillDialogHost OpenSettled(Window dialog, Window owner, bool expectBackdrop = true)
    {
        _ = dialog.ShowDialog(owner);
        PillDialogHost? host = null;
        Assert.True(PumpUntil(() => (host = dialog.GetVisualDescendants().OfType<PillDialogHost>().FirstOrDefault()) is { Card.Opacity: > 0.999 }, 4000),
            $"{dialog.GetType().Name}: the card never opened");
        // The headless test renderer cannot snapshot the owner (real Skia, NOCTIS_TEST_SKIA=1,
        // does): hand the host the bitmap the capture would have, as the capture does.
        if (expectBackdrop && host!.BackdropBitmap is null && !HeadlessTestApp.RealRendering)
            host.ShowBackdrop(new WriteableBitmap(new PixelSize(8, 8), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul));
        if (expectBackdrop)
            Assert.True(PumpUntil(() => host!.BackdropBitmap != null, 3000), $"{dialog.GetType().Name}: no backdrop snapshot");
        return host!;
    }

    private static Window SimpleDialog(Control? content = null) => new()
    {
        Width = 1100,
        Height = 820,
        Background = Brushes.Transparent,
        Content = new PillDialogHost { Content = content ?? new Border { Width = 400, Height = 240 } },
    };

    private static void AssertSolidCard(PillDialogHost host, string what)
    {
        // The card resolves the very brush it always did, on the very Border it always did.
        var expected = host.FindResource(host.ActualThemeVariant, "SystemControlBackgroundAltHighBrush");
        Assert.NotNull(expected);
        Assert.False(host.IsGlassCard, $"{what}: glass card while it should be solid");
        Assert.NotNull(host.CardFill);
        Assert.True(host.CardFill!.IsVisible, $"{what}: the solid fill is hidden");
        Assert.Same(expected, host.CardFill.Background);
        Assert.Same(expected, host.Background);
        Assert.Equal(BoxShadows.Parse("0 24 64 0 #8C000000, 0 2 10 0 #40000000"), host.CardFill.BoxShadow);
        Assert.Same(host.Card, host.CardFill.GetVisualParent());
        Assert.False(host.CardGlass!.IsVisible, $"{what}: the glass panel paints");
        Assert.False(host.CardGlass.EffectiveGlassActive);
    }

    private static void AssertGlassCard(PillDialogHost host, string what, double glassOpacity = 0.4)
    {
        Assert.True(host.IsGlassCard, $"{what}: no glass card with Liquid Glass on");
        var glass = host.CardGlass!;
        Assert.True(glass.EffectiveGlassActive, $"{what}: the glass panel does not frost");
        Assert.False(host.CardFill!.IsVisible, $"{what}: the solid fill still paints over the frost");
        // The Settings sheet's frost, tinted with the card's own colour.
        Assert.Equal(22, glass.BlurRadius);
        Assert.Equal(0, glass.EdgeThickness);
        Assert.Same(host.Background, glass.Background);
        Assert.Equal(PillDialogHost.GlassTintOpacityFor(glassOpacity), glass.GlassTintOpacity!.Value, 3);
        var (tint, _) = glass.ResolveTint();
        var card = Assert.IsAssignableFrom<ISolidColorBrush>(host.Background).Color;
        Assert.Equal(Color.FromRgb(card.R, card.G, card.B), tint);
        // It fades and moves with the card, outside the card's opacity layer.
        Assert.True(PumpUntil(() => glass.Fade > 0.999, 1000), $"{what}: the frost never faded in (Fade {glass.Fade:0.000})");
        Assert.False(host.Card!.IsVisualAncestorOf(glass));
        Assert.Equal(host.Card.Bounds.Size, glass.Bounds.Size);
    }

    [AvaloniaFact]
    public void GlassOff_TheCardIsTheSolidCard_Unchanged()
    {
        EnsureAppStyles();
        AppGlass.Clear();
        var owner = Owner();
        var dialog = SimpleDialog();
        try
        {
            var host = OpenSettled(dialog, owner);
            AssertSolidCard(host, "glass off");
        }
        finally { dialog.Close(); owner.Close(); }
    }

    [AvaloniaFact]
    public void GlassOn_TheCardFrosts_AndFollowsTheToggleLive()
    {
        EnsureAppStyles();
        var owner = Owner();
        var dialog = SimpleDialog();
        try
        {
            AppGlass.Set(true, Colors.Black, Colors.Black);
            var host = OpenSettled(dialog, owner);
            AssertGlassCard(host, "glass on");

            AppGlass.Clear();
            Dispatcher.UIThread.RunJobs();
            AssertSolidCard(host, "glass turned off while open");

            AppGlass.Set(true, Colors.Black, Colors.Black);
            Dispatcher.UIThread.RunJobs();
            AssertGlassCard(host, "glass turned back on");
        }
        finally { AppGlass.Clear(); dialog.Close(); owner.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(0.0)]
    [InlineData(0.4)]
    [InlineData(0.8)]
    [InlineData(1.0)]
    public void GlassOn_TintFollowsGlassOpacity_NeverBelowTheReadableFloor(double glassOpacity)
    {
        EnsureAppStyles();
        var app = Application.Current!;
        var owner = Owner();
        var dialog = SimpleDialog();
        try
        {
            app.Resources["GlassMenuTintOpacity"] = glassOpacity;
            AppGlass.Set(true, Colors.Black, Colors.Black);
            var host = OpenSettled(dialog, owner);
            AssertGlassCard(host, $"Glass Opacity {glassOpacity:P0}", glassOpacity);
            Assert.Equal(Math.Max(glassOpacity, PillDialogHost.GlassMinTintOpacity), host.CardGlass!.GlassTintOpacity!.Value, 3);
        }
        finally
        {
            AppGlass.Clear();
            app.Resources.Remove("GlassMenuTintOpacity");
            dialog.Close();
            owner.Close();
        }
    }

    /// <summary>Background Blur off (or a backend without a snapshot): the transparent pop-up
    /// window has nothing to frost but the live app, so the card stays solid.</summary>
    [AvaloniaFact]
    public void GlassOn_WithoutTheBlurredSnapshot_FallsBackToTheSolidCard()
    {
        EnsureAppStyles();
        var radius = BackdropSnapshot.BlurRadius;
        var owner = Owner();
        var dialog = SimpleDialog();
        try
        {
            BackdropSnapshot.BlurRadius = 0;
            AppGlass.Set(true, Colors.Black, Colors.Black);
            var host = OpenSettled(dialog, owner, expectBackdrop: false);
            PumpUntil(() => false, 200);
            Assert.Null(host.BackdropBitmap);
            AssertSolidCard(host, "no snapshot");
        }
        finally
        {
            AppGlass.Clear();
            BackdropSnapshot.BlurRadius = radius;
            dialog.Close();
            owner.Close();
        }
    }

    [Fact]
    public void GlassCard_NeedsLiquidGlassAndTheSnapshot()
    {
        Assert.True(PillDialogHost.WantsGlassCard(appGlassActive: true, hasBackdrop: true));
        Assert.False(PillDialogHost.WantsGlassCard(appGlassActive: true, hasBackdrop: false));
        Assert.False(PillDialogHost.WantsGlassCard(appGlassActive: false, hasBackdrop: true));
        Assert.False(PillDialogHost.WantsGlassCard(appGlassActive: false, hasBackdrop: false));
        Assert.Equal(PillDialogHost.GlassMinTintOpacity, PillDialogHost.GlassTintOpacityFor(double.NaN));
        Assert.Equal(1, PillDialogHost.GlassTintOpacityFor(3));
    }

    /// <summary>The close plays on the glass too: it fades out with the card, never left at
    /// full frost after the card's content has gone (the Settings sheet's "transparent box").</summary>
    [AvaloniaFact]
    public void GlassOn_TheFrostFadesOutWithTheCard()
    {
        EnsureAppStyles();
        var owner = Owner();
        var dialog = SimpleDialog();
        try
        {
            AppGlass.Set(true, Colors.Black, Colors.Black);
            var host = OpenSettled(dialog, owner);
            var glass = host.CardGlass!;
            var closed = false;
            dialog.Closed += (_, _) => closed = true;
            dialog.Close();
            Assert.True(host.IsClosing);
            Assert.True(PumpUntil(() => closed || glass.Fade < 0.05), "the frost never faded");
            Assert.True(glass.Fade < 0.05 || closed);
            Assert.True(PumpUntil(() => closed), "the dialog never closed");
        }
        finally { AppGlass.Clear(); owner.Close(); }
    }

    // ── every pop-up card ──

    /// <summary>The windows whose card is a PillDialogHost: all frost through the one template.</summary>
    public static IEnumerable<object[]> PillDialogs() => typeof(Noctis.Views.ConfirmationDialog).Assembly.GetTypes()
        .Where(t => t is { IsAbstract: false, Namespace: "Noctis.Views" } && typeof(Window).IsAssignableFrom(t)
                    && t != typeof(Noctis.Views.MainWindow) && UsesPillHost(t.Name))
        .OrderBy(t => t.Name)
        .Select(t => new object[] { t.Name });

    private static string ViewsDir([System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "src", "Noctis", "Views"));

    private static bool UsesPillHost(string view)
    {
        var path = Path.Combine(ViewsDir(), view + ".axaml");
        return File.Exists(path) && File.ReadAllText(path).Contains("PillDialogHost>", StringComparison.Ordinal);
    }

    [AvaloniaTheory]
    [MemberData(nameof(PillDialogs))]
    public void EveryPillDialog_FrostsWithGlassOn_AndIsTheSolidCardWithGlassOff(string view)
    {
        EnsureAppStyles();
        var type = typeof(Noctis.Views.ConfirmationDialog).Assembly.GetType("Noctis.Views." + view)!;
        foreach (var glassOn in new[] { false, true })
        {
            var owner = Owner();
            var dialog = (Window)Activator.CreateInstance(type)!;
            dialog.Width = 1100;
            dialog.Height = 820;
            try
            {
                if (glassOn) AppGlass.Set(true, Colors.Black, Colors.Black);
                else AppGlass.Clear();
                var host = OpenSettled(dialog, owner);
                if (glassOn) AssertGlassCard(host, view);
                else AssertSolidCard(host, view);
            }
            finally
            {
                AppGlass.Clear();
                dialog.Close();
                PumpUntil(() => !dialog.IsVisible, 1000);
                owner.Close();
            }
        }
    }

    /// <summary>The pop-ups' card colour is the host's own (one shared style): a dialog that set
    /// its own Background would skip the shared surface, glass on or off.</summary>
    [Fact]
    public void NoPillDialog_OverridesTheSharedCard()
    {
        var offenders = Directory.GetFiles(ViewsDir(), "*.axaml")
            .Where(f => Regex.IsMatch(File.ReadAllText(f), @"PillDialogHost\b[^>]*\bBackground="))
            .Select(Path.GetFileName)
            .ToList();
        Assert.Empty(offenders);
        Assert.True(PillDialogs().Count() >= 30, $"only {PillDialogs().Count()} pill dialogs found");
    }

    /// <summary>Pop-up windows that are not a PillDialogHost card, each for a reason: a new one
    /// must either use the host (and so the glass) or be listed here.</summary>
    [Fact]
    public void EveryOtherPopupWindow_IsAccountedFor()
    {
        var known = new Dictionary<string, string>
        {
            ["MainWindow"] = "the app itself; its Settings sheet is the GlassPanel the pop-ups copy",
            ["MiniPlayerWindow"] = "its own frosted player (MiniPlayerFrostedBackground)",
            ["ArtworkViewerDialog"] = "a full-window artwork viewer, no card",
            ["BadgeNameDialog"] = "no blurred backdrop to frost (plain dim overlay)",
            ["LrcEditorDialog"] = "no blurred backdrop to frost (plain dim overlay)",
        };
        var others = Directory.GetFiles(ViewsDir(), "*.axaml")
            .Where(f => Regex.IsMatch(File.ReadAllText(f), @"^\s*<Window\b", RegexOptions.Multiline))
            .Select(f => Path.GetFileNameWithoutExtension(f)!)
            .Where(n => !UsesPillHost(n))
            .ToList();
        var unknown = others.Where(n => !known.ContainsKey(n)).ToList();
        Assert.True(unknown.Count == 0, "pop-up windows outside PillDialogHost: " + string.Join(", ", unknown));
    }

    // ── contrast on the glass card ──

    private static ResourceDictionary BaseDictionary(ThemeVariant variant)
    {
        EnsureAppStyles();
        var include = new StyleInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml") };
        var styles = Assert.IsType<Styles>(include.Loaded);
        return Assert.IsType<ResourceDictionary>(styles.Resources.ThemeDictionaries[variant]);
    }

    private static Color? ColorOf(IResourceDictionary dict, string brushKey, string colorKey)
    {
        if (dict.TryGetValue(brushKey, out var b) && b is ISolidColorBrush brush) return brush.Color;
        if (dict.TryGetValue(colorKey, out var c) && c is Color color) return color;
        return null;
    }

    private static Color Over(Color top, double alpha, Color under) => Color.FromRgb(
        (byte)Math.Round(top.R * alpha + under.R * (1 - alpha)),
        (byte)Math.Round(top.G * alpha + under.G * (1 - alpha)),
        (byte)Math.Round(top.B * alpha + under.B * (1 - alpha)));

    /// <summary>
    /// The #179/#180 lesson: text on a see-through surface must be checked against what can
    /// show through it. Under the card is the blurred app behind the pop-ups' dim, anything
    /// from black to white; the card's tint at the floor keeps every built-in theme's text
    /// (Gray/Light base, Dark, Ink, Smoke, Midnight) at WCAG AA over both extremes.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("Gray")]
    [InlineData("Light")]
    [InlineData("Dark")]
    [InlineData("Ink")]
    [InlineData("Smoke")]
    [InlineData("Midnight")]
    public void GlassCardText_ReadsOverAnyBackdrop_AtTheTintFloor(string theme)
    {
        var variant = theme == "Light" ? ThemeVariant.Light : ThemeVariant.Dark;
        var baseDict = BaseDictionary(variant);
        IResourceDictionary dict = theme is "Gray" or "Light" ? baseDict : ThemeOverlayParityTests.LoadOverlay(theme);
        var card = ColorOf(dict, "SystemControlBackgroundAltHighBrush", "SystemAltHighColor")
                   ?? ColorOf(baseDict, "SystemControlBackgroundAltHighBrush", "SystemAltHighColor")!.Value;
        var text = ColorOf(dict, "SystemControlForegroundBaseHighBrush", "SystemBaseHighColor")
                   ?? ColorOf(baseDict, "SystemControlForegroundBaseHighBrush", "SystemBaseHighColor")!.Value;

        var pill = Assert.IsType<Styles>(new StyleInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/PillDialog.axaml") }.Loaded);
        Assert.True(pill.Resources.TryGetResource("PillDialogScrimBrush", null, out var s));
        var scrim = Assert.IsAssignableFrom<ISolidColorBrush>(s).Color;

        foreach (var app in new[] { Colors.White, Colors.Black })
        {
            var backdrop = Over(Color.FromRgb(scrim.R, scrim.G, scrim.B), scrim.A / 255.0, app);
            var surface = Over(card, PillDialogHost.GlassMinTintOpacity, backdrop);
            var ratio = ThemeDerivation.ContrastRatio(text, surface);
            Assert.True(ratio >= 4.5, $"{theme}: text {text} on the glass card over {app} reads at {ratio:0.00}:1");
        }
    }
}
