using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Threading;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Owner 11-26: Artists page portraits "blink" on click. Button:pressed shrank the tile to 96%
/// for the press and grew it back on the release. Real pointer events on a Button.artist-tile
/// with the app styles: it grows on hover (Button:pointerover, kept), and from the press to the
/// release it never leaves that hover size; a press dragged off the disc rests at full size,
/// never 96%.
/// </summary>
public class ArtistTilePressScaleTests
{
    private readonly ITestOutputHelper _o;
    public ArtistTilePressScaleTests(ITestOutputHelper o) => _o = o;

    private static void EnsureAppStyles()
    {
        var app = Application.Current!;
        if (app.Resources.TryGetResource("HeartFillIcon", null, out _)) return;
        app.Resources["InterSemiBold"] = FontFamily.Default;
        app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml") });
        app.Styles.Add(new StyleInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml") });
    }

    private static void Settle(int ms = 150)
    {
        var until = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < until)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(5);
        }
    }

    private static double Scale(Visual v) => v.RenderTransform?.Value.M11 ?? 1;

    [AvaloniaFact]
    public void ArtistTile_Press_KeepsTheHoverSize_NoShrink()
    {
        EnsureAppStyles();
        var tile = new Button
        {
            Classes = { "artist-tile" },
            Width = 190, Height = 190, Padding = new Thickness(0),
            Content = new Border { Background = Brushes.OrangeRed },
        };
        var plain = new Button { Width = 60, Height = 30, Content = "x" };
        var panel = new Canvas { Width = 600, Height = 400 };
        Canvas.SetLeft(tile, 100); Canvas.SetTop(tile, 100);
        Canvas.SetLeft(plain, 400); Canvas.SetTop(plain, 100);
        panel.Children.Add(tile); panel.Children.Add(plain);
        var win = new Window { Width = 600, Height = 400, Content = panel };
        win.Show();
        try
        {
            Settle(50);
            Assert.Equal(1, Scale(tile), 3);

            var centre = new Point(195, 195);
            win.MouseMove(centre);
            Settle();
            var hover = Scale(tile);
            _o.WriteLine($"hover {hover}");
            Assert.True(hover > 1.04, $"the hover grow is gone ({hover})");

            var seen = new List<double>();
            tile.PropertyChanged += (_, e) =>
            {
                if (e.Property == Visual.RenderTransformProperty) seen.Add(Scale(tile));
            };
            win.MouseDown(centre, MouseButton.Left);
            Settle();
            _o.WriteLine($"pressed {Scale(tile)}");
            Assert.Equal(hover, Scale(tile), 3);
            win.MouseUp(centre, MouseButton.Left);
            Settle();
            _o.WriteLine($"released {Scale(tile)} changes [{string.Join(", ", seen)}]");
            Assert.Equal(hover, Scale(tile), 3);
            Assert.All(seen, s => Assert.Equal(hover, s, 3));

            // Pressed, then dragged off the disc: full size, not 96%.
            win.MouseDown(centre, MouseButton.Left);
            win.MouseMove(new Point(20, 20), RawInputModifiers.LeftMouseButton);
            Settle();
            _o.WriteLine($"pressed + off {Scale(tile)}");
            Assert.Equal(1, Scale(tile), 3);
            win.MouseUp(new Point(20, 20), MouseButton.Left);
            Settle();

            // Other buttons keep the app's press feedback.
            var p = new Point(430, 115);
            win.MouseMove(p);
            win.MouseDown(p, MouseButton.Left);
            Settle();
            Assert.Equal(0.96, Scale(plain), 3);
            win.MouseUp(p, MouseButton.Left);
        }
        finally { win.Close(); }
    }
}
