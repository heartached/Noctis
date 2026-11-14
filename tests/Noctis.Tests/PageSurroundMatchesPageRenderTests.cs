using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #240, rendered: the window around the page (the rail's left margin) must show the
/// same colour as the page area beside it, with Liquid Glass on and off, in every dark theme.
/// With glass on the page area stacked three translucent AppMainBackground-coloured layers
/// (window, content Grid, page root) but the rail's margin only the window's, so the blur
/// behind showed through it much more: the "lighter gray band behind the sidebar".
/// A white stand-in for the desktop under the acrylic makes the difference measurable.
/// Real Skia only (NOCTIS_TEST_SKIA=1); NOCTIS_240_SHOTS=dir saves before/after PNGs.
/// </summary>
public class PageSurroundMatchesPageRenderTests
{
    private static WriteableBitmap Shot(Window w, string? name)
    {
        for (var i = 0; i < 8; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
        using var frame = w.CaptureRenderedFrame()!;
        var dir = Environment.GetEnvironmentVariable("NOCTIS_240_SHOTS");
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

    private static Color At(WriteableBitmap b, int x, int y)
    {
        using var l = b.Lock();
        var px = new byte[4];
        System.Runtime.InteropServices.Marshal.Copy(l.Address + y * l.RowBytes + x * 4, px, 0, 4);
        return Color.FromRgb(px[2], px[1], px[0]); // BGRA
    }

    private static int Diff(Color a, Color b) =>
        Math.Max(Math.Abs(a.R - b.R), Math.Max(Math.Abs(a.G - b.G), Math.Abs(a.B - b.B)));

    private static void ApplyLiquidGlass(MainWindow w, bool on) =>
        typeof(MainWindow).GetMethod("ApplyLiquidGlass", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(w, new object[] { on });

    [AvaloniaTheory]
    [InlineData("Gray", false)]
    [InlineData("Gray", true)]
    [InlineData("Dark", true)]
    [InlineData("Ink", true)]
    [InlineData("Midnight", true)]
    [InlineData("Smoke", false)]
    [InlineData("Smoke", true)]
    public void RailMargin_MatchesThePage(string theme, bool glass)
    {
        if (!HeadlessTestApp.RealRendering) Assert.Skip("needs real Skia rendering (NOCTIS_TEST_SKIA=1)");
        if (OperatingSystem.IsLinux() && glass) Assert.Skip("Liquid Glass never applies on Linux");
        AddToPlaylistDialogTests.EnsureAppStyles();

        var app = Application.Current!;
        // Gray is the base dark dictionary (the default theme); the others merge their overlay
        // on top of it, as App.SetTheme does.
        var overlay = theme == "Gray" ? null : ThemeOverlayParityTests.LoadOverlay(theme);
        if (overlay != null) app.Resources.MergedDictionaries.Add(overlay);
        var oldVariant = app.RequestedThemeVariant;
        app.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;

        var main = new MainWindow { Width = 1200, Height = 800 };
        main.Show();
        try
        {
            // A page as every built-in page is: its root paints AppMainBackground.
            var page = new UserControl();
            page.Bind(UserControl.BackgroundProperty, page.GetResourceObservable("AppMainBackground"));
            main.FindControl<ContentControl>("PageHost")!.Content = page;
            Dispatcher.UIThread.RunJobs();

            if (glass)
            {
                ApplyLiquidGlass(main, true);
                // The desktop the OS blur would show: white, the worst case for a light band.
                var acrylic = main.FindControl<ExperimentalAcrylicBorder>("LiquidGlassAcrylic")!;
                acrylic.Material = new ExperimentalAcrylicMaterial
                {
                    TintColor = Colors.White, TintOpacity = 1, MaterialOpacity = 1, FallbackColor = Colors.White,
                };
            }

            var tag = $"{theme.ToLowerInvariant()}-{(glass ? "glass" : "noglass")}";
            using var shot = Shot(main, $"after-{tag}");
            var scale = shot.PixelSize.Width / main.Bounds.Width;
            int Px(double v) => (int)(v * scale);
            var pageLeft = page.TranslatePoint(new Point(0, 0), main)!.Value.X;
            Assert.True(pageLeft > 60, $"page starts at {pageLeft}");

            foreach (var y in new[] { 200.0, 600.0 })
            {
                var margin = At(shot, Px(3), Px(y));
                var beside = At(shot, Px(pageLeft + 40), Px(y));
                Assert.True(Diff(margin, beside) <= 4, $"{tag} y={y}: rail margin {margin} vs page {beside}");
            }

            // Without the surround layers (the old layering) glass shows the band: the test
            // can tell the two apart.
            if (glass)
            {
                main.FindControl<Border>("PageSurroundTop")!.IsVisible = false;
                main.FindControl<Border>("PageSurroundLeft")!.IsVisible = false;
                using var before = Shot(main, $"before-{tag}");
                var margin = At(before, Px(3), Px(600));
                var beside = At(before, Px(pageLeft + 40), Px(600));
                Assert.True(Diff(margin, beside) > 20, $"{tag} old layering: {margin} vs {beside}");
            }
        }
        finally
        {
            if (glass) ApplyLiquidGlass(main, false);
            main.Close();
            if (overlay != null) app.Resources.MergedDictionaries.Remove(overlay);
            app.RequestedThemeVariant = oldVariant;
        }
    }
}
