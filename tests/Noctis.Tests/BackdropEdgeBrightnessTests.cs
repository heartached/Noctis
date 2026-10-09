using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Noctis.Controls;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Owner 10-08 live check: sharp white arcs on the pill pop-ups' blurred backdrop, exactly on
/// the anti-aliased rims of round sidebar items (the accent Home pill, the Favorites heart).
/// Nothing in the scene is brighter than its own colours, so no backdrop pixel may come out
/// brighter than the brightest thing drawn. Real Skia only: the stubs draw nothing.
/// </summary>
public class BackdropEdgeBrightnessTests
{
    private readonly ITestOutputHelper _o;
    public BackdropEdgeBrightnessTests(ITestOutputHelper o) => _o = o;

    [AvaloniaFact]
    public async Task AntiAliasedRims_NeverComeOutBrighterThanTheScene()
    {
        if (!HeadlessTestApp.RealRendering)
            Assert.Skip("needs real Skia rendering (NOCTIS_TEST_SKIA=1)");

        // Dark window, a dark sidebar strip, and accent circles with soft edges — the shapes
        // the arcs sat on. The brightest colour drawn is the accent's red (#8E1B3A → R=142).
        var accent = Color.Parse("#8E1B3A");
        var circles = new StackPanel { Spacing = 24, Margin = new Thickness(16), HorizontalAlignment = HorizontalAlignment.Left };
        for (var i = 0; i < 6; i++)
            circles.Children.Add(new Ellipse { Width = 46, Height = 46, Fill = new SolidColorBrush(accent) });
        var sidebar = new Border { Width = 78, Background = new SolidColorBrush(Color.Parse("#141414")), Child = circles, HorizontalAlignment = HorizontalAlignment.Left };
        var win = new Window
        {
            Width = 400, Height = 500, RequestedThemeVariant = ThemeVariant.Dark,
            Background = new SolidColorBrush(Color.Parse("#0F0F0F")),
            Content = new Panel { Children = { sidebar } },
        };
        win.Show();
        try
        {
            for (var i = 0; i < 5; i++) { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }

            var source = await BackdropSnapshot.CaptureSourceAsync(win, win, "test");
            Assert.NotNull(source);
            var px = new byte[source!.Width * source.Height * 4];
            source.BlurInto(px, 0); // radius 0: the flattened snapshot itself, unblurred

            int worst = 0, bright = 0;
            for (var i = 0; i + 3 < px.Length; i += 4)
            {
                var m = Math.Max(px[i], Math.Max(px[i + 1], px[i + 2]));
                if (m > worst) worst = m;
                if (m > accent.R + 8) bright++;
            }
            _o.WriteLine($"snapshot {source.Width}x{source.Height}: brightest channel {worst}, pixels brighter than the accent: {bright}");
            Assert.True(bright == 0, $"{bright} snapshot pixels brighter than anything drawn (max channel {worst}, accent R {accent.R})");
        }
        finally { win.Close(); }
    }
}
