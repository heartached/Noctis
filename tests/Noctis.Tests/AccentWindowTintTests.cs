using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Services;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #240 part 2: Settings › Appearance › "Tint window with accent colour" shades the
/// window and page background toward the accent (AccentWindowTint, added by App.SetAccent).
/// Text must stay readable on the shade in every built-in theme with every accent preset,
/// and with the option off the window renders exactly as before.
/// Render tests need real Skia (NOCTIS_TEST_SKIA=1); NOCTIS_240_TINT_SHOTS=dir saves PNGs.
/// </summary>
public class AccentWindowTintTests
{
    public static readonly string[] Themes = { "Gray", "Dark", "Ink", "Midnight", "Smoke", "Light" };

    [Fact]
    public void Tint_KeepsAlpha_AndMovesASmallStepTowardTheAccent()
    {
        var accent = Color.Parse("#E74856");
        var tinted = AccentWindowTint.Tint(Color.Parse("#992A2D38"), accent, AccentWindowTint.DarkStrength);
        Assert.Equal(0x99, tinted.A);
        Assert.True(tinted.R > 0x2A && tinted.G < 0x2D + 3 && tinted.B < 0x38 + 3, tinted.ToString());

        var gradient = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Colors.Black, 0), new GradientStop(Colors.Gray, 1) },
        };
        var shaded = Assert.IsType<LinearGradientBrush>(AccentWindowTint.Tint(gradient, accent, 0.1));
        Assert.Equal(2, shaded.GradientStops.Count);
        Assert.Equal(1, shaded.GradientStops[1].Offset);
        Assert.Equal(gradient.EndPoint, shaded.EndPoint);
        Assert.True(shaded.GradientStops[0].Color.R > 0);

        Assert.Null(AccentWindowTint.Tint(new object(), accent, 0.1));
    }

    /// <summary>App.SetAccent's overlay: off (the default) carries no background keys, so the
    /// theme's own surfaces show exactly as before; on, it carries the shaded ones, and
    /// switching back off (or changing the accent) re-applies at once.</summary>
    [AvaloniaFact]
    public void AppAccentOverlay_AddsTheShadeOnlyWhileOn()
    {
        var app = new App { RequestedThemeVariant = ThemeVariant.Dark };
        // The theme, merged as App.SetTheme merges an overlay (the accent overlay goes on top).
        var theme = new ResourceDictionary
        {
            ["AppMainBackground"] = new SolidColorBrush(Color.Parse("#252525")),
            ["AppMainBackgroundColor"] = Color.Parse("#252525"),
            ["AppWindowBackgroundBrush"] = new SolidColorBrush(Color.Parse("#252525")),
        };
        app.Resources.MergedDictionaries.Add(theme);
        Color Main() => app.TryGetResource("AppMainBackgroundColor", ThemeVariant.Dark, out var v) ? (Color)v! : default;
        IResourceDictionary Overlay() => app.ActiveAccentOverlay!;

        Assert.False(app.TintWindowWithAccent);
        app.SetAccent("#E74856");
        Assert.All(AccentWindowTint.Keys, k => Assert.False(Overlay().ContainsKey(k), k));
        Assert.Equal(Color.Parse("#252525"), Main());

        app.SetTintWindowWithAccent(true);
        Assert.All(AccentWindowTint.Keys, k => Assert.True(Overlay().ContainsKey(k), k));
        var crimson = Main();
        Assert.True(crimson.R > 0x25 && crimson.R - crimson.G > 8, crimson.ToString());
        Assert.Equal(2, app.Resources.MergedDictionaries.Count); // theme + one accent overlay, not stacked

        app.SetAccent("#2A7FCF"); // a new accent (picker, or "Accent follows album art")
        var blue = Main();
        Assert.True(blue.B > blue.R + 8, blue.ToString());

        app.SetTintWindowWithAccent(false);
        Assert.All(AccentWindowTint.Keys, k => Assert.False(Overlay().ContainsKey(k), k));
        Assert.Equal(Color.Parse("#252525"), Main());
    }

    /// <summary>Loads a built-in theme the way App.SetTheme does: Gray / Light are the base
    /// dictionary's Dark / Light variants, the others merge their overlay on top of Dark.</summary>
    internal static IDisposable UseTheme(string theme)
    {
        AddToPlaylistDialogTests.EnsureAppStyles();
        var app = Application.Current!;
        var overlay = theme is "Gray" or "Light" ? null : ThemeOverlayParityTests.LoadOverlay(theme);
        if (overlay != null) app.Resources.MergedDictionaries.Add(overlay);
        var oldVariant = app.RequestedThemeVariant;
        app.RequestedThemeVariant = theme == "Light" ? ThemeVariant.Light : ThemeVariant.Dark;
        return new Restore(() =>
        {
            if (overlay != null) app.Resources.MergedDictionaries.Remove(overlay);
            app.RequestedThemeVariant = oldVariant;
        });
    }

    /// <summary>The accent overlay App.SetAccent merges: the palette, plus the shaded
    /// backgrounds while the option is on (read before the overlay is merged).</summary>
    internal static IDisposable UseAccent(Color accent, bool tint)
    {
        var app = Application.Current!;
        var isLight = app.RequestedThemeVariant == ThemeVariant.Light;
        var rd = Helpers.AccentPalette.Build(accent, isLight);
        if (tint) AccentWindowTint.AddTo(rd, app, app.ActualThemeVariant, accent, isLight);
        app.Resources.MergedDictionaries.Add(rd);
        return new Restore(() => app.Resources.MergedDictionaries.Remove(rd));
    }

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }

    private static Color ResolveColor(string key)
    {
        var app = Application.Current!;
        Assert.True(app.TryGetResource(key, app.ActualThemeVariant, out var v), key);
        return v switch
        {
            Color c => c,
            ISolidColorBrush b => b.Color,
            _ => throw new Xunit.Sdk.XunitException($"{key} is {v?.GetType().Name}"),
        };
    }

    private static Color Over(Color top, Color bottom)
    {
        var a = top.A / 255.0;
        byte Ch(byte t, byte b) => (byte)Math.Round(t * a + b * (1 - a));
        return Color.FromRgb(Ch(top.R, bottom.R), Ch(top.G, bottom.G), Ch(top.B, bottom.B));
    }

    /// <summary>What a page shows, opaque: the window background (each stop of a gradient)
    /// under the content Grid's and the page root's AppMainBackground.</summary>
    private static IEnumerable<Color> PageColors()
    {
        var app = Application.Current!;
        Assert.True(app.TryGetResource("AppWindowBackgroundBrush", app.ActualThemeVariant, out var win));
        var main = ResolveColor("AppMainBackground");
        var windowColors = win switch
        {
            ISolidColorBrush s => new[] { s.Color },
            IGradientBrush g => g.GradientStops.Select(s => s.Color).ToArray(),
            _ => throw new Xunit.Sdk.XunitException("window brush " + win?.GetType().Name),
        };
        return windowColors.Select(w => Over(main, Over(main, Over(w, Colors.Black))));
    }

    [AvaloniaTheory]
    [InlineData("Gray")]
    [InlineData("Dark")]
    [InlineData("Ink")]
    [InlineData("Midnight")]
    [InlineData("Smoke")]
    [InlineData("Light")]
    public void TintedBackground_KeepsTextReadable_WithEveryAccentPreset(string theme)
    {
        using var _ = UseTheme(theme);
        var text = ResolveColor("SystemControlForegroundBaseHighBrush");
        var failures = new List<string>();
        foreach (var preset in App.AccentPresets)
        {
            var accent = Color.Parse(preset.Hex);
            double[] plain, tinted;
            using (UseAccent(accent, tint: false))
                plain = PageColors().Select(bg => ThemeDerivation.ContrastRatio(text, bg)).ToArray();
            using (UseAccent(accent, tint: true))
            {
                tinted = PageColors().Select(bg => ThemeDerivation.ContrastRatio(text, bg)).ToArray();
                foreach (var bg in PageColors())
                {
                    // Hints are the text colour at 50% opacity (Settings rows, page subtitles).
                    var hint = Over(Color.FromArgb(128, text.R, text.G, text.B), bg);
                    var body = ThemeDerivation.ContrastRatio(text, bg);
                    var muted = ThemeDerivation.ContrastRatio(hint, bg);
                    if (body < 7 || muted < 3)
                        failures.Add($"{preset.Name}: text {body:F2}:1, hint {muted:F2}:1 on {bg}");
                }
            }
            // Subtle: the shade costs body text at most ~10% of its contrast (MaxContrastLoss,
            // plus byte rounding and Smoke's gradient stops).
            for (var i = 0; i < plain.Length; i++)
                if (tinted[i] < plain[i] * 0.88)
                    failures.Add($"{preset.Name}: contrast {plain[i]:F2} -> {tinted[i]:F2}");
        }
        Assert.True(failures.Count == 0, theme + ": " + string.Join("; ", failures));
    }

    // ── Rendered ──

    internal static WriteableBitmap Shot(Window w, string? name)
    {
        for (var i = 0; i < 8; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
        using var frame = w.CaptureRenderedFrame()!;
        var dir = Environment.GetEnvironmentVariable("NOCTIS_240_TINT_SHOTS");
        if (!string.IsNullOrEmpty(dir) && name != null)
        {
            Directory.CreateDirectory(dir);
            frame.Save(Path.Combine(dir, name + ".png"));
        }
        using var ms = new MemoryStream();
        frame.Save(ms);
        ms.Position = 0;
        return WriteableBitmap.Decode(ms);
    }

    private static byte[] Pixels(WriteableBitmap b)
    {
        using var l = b.Lock();
        var bytes = new byte[l.RowBytes * l.Size.Height];
        System.Runtime.InteropServices.Marshal.Copy(l.Address, bytes, 0, bytes.Length);
        return bytes;
    }

    private static Color At(WriteableBitmap b, int x, int y)
    {
        using var l = b.Lock();
        var px = new byte[4];
        System.Runtime.InteropServices.Marshal.Copy(l.Address + y * l.RowBytes + x * 4, px, 0, 4);
        return Color.FromRgb(px[2], px[1], px[0]); // BGRA
    }

    private static int Diff(Color a, Color b) =>
        Math.Max(Math.Abs(a.R - b.R), Math.Max(Math.Abs(a.G - b.G), Math.Abs(a.B - b.B)));

    /// <summary>Pixels differing by more than <paramref name="tolerance"/> levels.</summary>
    private static int CountDiff(WriteableBitmap a, WriteableBitmap b, int tolerance)
    {
        var pa = Pixels(a);
        var pb = Pixels(b);
        var n = 0;
        for (var i = 0; i < pa.Length; i += 4)
            if (Math.Abs(pa[i] - pb[i]) > tolerance || Math.Abs(pa[i + 1] - pb[i + 1]) > tolerance
                || Math.Abs(pa[i + 2] - pb[i + 2]) > tolerance)
                n++;
        return n;
    }

    private static void ApplyLiquidGlass(MainWindow w, bool on) =>
        typeof(MainWindow).GetMethod("ApplyLiquidGlass", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(w, new object[] { on });

    /// <summary>A page as every built-in page is (its root paints AppMainBackground), with a
    /// title, an accent link, body text and a hint, so the shots show text on the shade.</summary>
    private static UserControl SamplePage()
    {
        var page = new UserControl();
        page.Bind(UserControl.BackgroundProperty, page.GetResourceObservable("AppMainBackground"));
        var stack = new StackPanel { Margin = new Thickness(40, 30), Spacing = 8, HorizontalAlignment = HorizontalAlignment.Left };
        TextBlock T(string s, double size, FontWeight w, double opacity = 1, string brush = "SystemControlForegroundBaseHighBrush")
        {
            var t = new TextBlock { Text = s, FontSize = size, FontWeight = w, Opacity = opacity };
            t.Bind(TextBlock.ForegroundProperty, t.GetResourceObservable(brush));
            return t;
        }
        stack.Children.Add(T("Midnight Sun", 34, FontWeight.Bold));
        stack.Children.Add(T("Zara Larsson", 20, FontWeight.SemiBold, brush: "AccentColorBrush"));
        stack.Children.Add(T("1 track · 3m 09s · MP3", 13, FontWeight.SemiBold, 0.6));
        stack.Children.Add(T("The window and page background take a subtle shade of the accent colour.", 14, FontWeight.Normal));
        stack.Children.Add(T("Hint text at half opacity, like a Settings row description.", 12, FontWeight.Normal, 0.5));
        page.Content = stack;
        return page;
    }

    /// <summary>A fresh main window showing <see cref="SamplePage"/>, with Liquid Glass over a
    /// stand-in for the desktop the OS blur would show when glass is on.</summary>
    private sealed class TestWindow : IDisposable
    {
        public MainWindow Main { get; } = new() { Width = 1200, Height = 800 };
        public UserControl Page { get; } = SamplePage();
        private readonly bool _glass;
        private readonly Color _desk;

        public TestWindow(bool glass, bool light)
        {
            _glass = glass;
            _desk = light ? Color.Parse("#C8CCD4") : Color.Parse("#1C2433");
            Main.Show();
            Main.FindControl<ContentControl>("PageHost")!.Content = Page;
            Dispatcher.UIThread.RunJobs();
            // The drop-import pill's spinner turns forever (no view model here to hide it), so
            // two shots of the same window would differ; nothing else animates.
            foreach (var spinner in Main.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Ellipse>()
                         .Where(e => e.Classes.Contains("loading-spinner")))
                spinner.IsVisible = false;
            Glass();
        }

        /// <summary>(Re)applies Liquid Glass, which re-reads the theme surfaces, as MainWindow
        /// does after an accent change with the tint on (RefreshLiquidGlassForAccentTint).</summary>
        public void Glass()
        {
            if (!_glass) return;
            ApplyLiquidGlass(Main, true);
            var acrylic = Main.FindControl<ExperimentalAcrylicBorder>("LiquidGlassAcrylic")!;
            acrylic.Material = new ExperimentalAcrylicMaterial
            {
                TintColor = _desk, TintOpacity = 1, MaterialOpacity = 1, FallbackColor = _desk,
            };
        }

        public void Dispose()
        {
            if (_glass) ApplyLiquidGlass(Main, false);
            Main.Close();
        }
    }

    /// <summary>
    /// Off: renders pixel for pixel as before. On: the page and the area around it
    /// (PageSurround, part 1) take the same subtle shade, and turning it on and off live in an
    /// open window follows. Off again: a window renders exactly as before, so nothing lingers.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("Gray", "#E74856", false)]
    [InlineData("Gray", "#E74856", true)]
    [InlineData("Gray", "#2A7FCF", false)]
    [InlineData("Gray", "#2A7FCF", true)]
    [InlineData("Dark", "#E74856", true)]
    [InlineData("Ink", "#2A7FCF", false)]
    [InlineData("Midnight", "#E74856", true)]
    [InlineData("Smoke", "#E74856", false)]
    [InlineData("Smoke", "#E74856", true)]
    [InlineData("Light", "#E74856", false)]
    [InlineData("Light", "#2A7FCF", true)]
    public void Rendered_OffIsUnchanged_OnShadesPageAndSurroundAlike(string theme, string accentHex, bool glass)
    {
        if (!HeadlessTestApp.RealRendering) Assert.Skip("needs real Skia rendering (NOCTIS_TEST_SKIA=1)");
        if (OperatingSystem.IsLinux() && glass) Assert.Skip("Liquid Glass never applies on Linux");
        using var _ = UseTheme(theme);
        var accent = Color.Parse(accentHex);
        var light = theme == "Light";
        var tag = $"{theme.ToLowerInvariant()}-{accentHex.TrimStart('#').ToLowerInvariant()}-{(glass ? "glass" : "noglass")}";

        // Before: the accent overlay as App.SetAccent merges it with the option off.
        WriteableBitmap before;
        double pageLeft, scale;
        using (UseAccent(accent, tint: false))
        using (var w = new TestWindow(glass, light))
        {
            before = Shot(w.Main, $"before-{tag}");
            pageLeft = w.Page.TranslatePoint(new Point(0, 0), w.Main)!.Value.X;
            scale = before.PixelSize.Width / w.Main.Bounds.Width;
        }
        int Px(double v) => (int)(v * scale);
        var probes = new[] { (x: pageLeft + 700, y: 500.0), (x: 3.0, y: 200.0), (x: 3.0, y: 600.0) };

        try
        {
            // On, live in an open window: the shade lands on the page and its surround alike,
            // and going back off restores it (bar a level or two of anti-aliasing on edges that
            // were repainted over the shaded frame).
            using (var w = new TestWindow(glass, light))
            {
                var overlay = UseAccent(accent, tint: true);
                w.Glass();
                using var after = Shot(w.Main, $"after-{tag}");
                var pageColor = At(after, Px(probes[0].x), Px(probes[0].y));
                var shift = Diff(pageColor, At(before, Px(probes[0].x), Px(probes[0].y)));
                Assert.True(shift is >= 2 and <= 24, $"{tag}: page shade moved {shift} (want subtle but visible)");
                foreach (var (x, y) in probes.Skip(1))
                {
                    var c = At(after, Px(x), Px(y));
                    Assert.True(Diff(c, pageColor) <= 4, $"{tag}: ({x},{y}) {c} vs page {pageColor}");
                }

                overlay.Dispose();
                using (UseAccent(accent, tint: false))
                {
                    w.Glass();
                    using var liveOff = Shot(w.Main, null);
                    var off = CountDiff(before, liveOff, tolerance: 8);
                    Assert.True(off == 0, $"{tag}: live off left {off} px more than 8 levels from before");
                }
            }

            // Off again, a fresh window: identical to before.
            using (UseAccent(accent, tint: false))
            using (var w = new TestWindow(glass, light))
            {
                using var again = Shot(w.Main, null);
                Assert.True(Pixels(before).AsSpan().SequenceEqual(Pixels(again)),
                    $"{tag}: off differs from before in {CountDiff(before, again, 0)} px");
            }
        }
        finally
        {
            before.Dispose();
        }
    }
}
