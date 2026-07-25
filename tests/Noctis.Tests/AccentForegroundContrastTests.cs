using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// App.SetAccent re-points every accent *fill* resource at the chosen accent, but Fluent
/// paints the foreground of its accent-filled control states (checked ToggleButton label,
/// CheckBox tick, RadioButton dot) with a hardcoded white. With a white / very light accent
/// that rendered white-on-white — the "Raw" pill in the metadata window lost its label and
/// ticks/radio dots disappeared. The overlay now carries the readable on-accent foreground
/// for those states too.
/// </summary>
public class AccentForegroundContrastTests
{
    private const string WhiteAccent = "#FFFFFF";

    /// <summary>
    /// Runs <paramref name="probe"/> with the real accent overlay App.SetAccent builds
    /// merged onto the headless application, so Fluent templates resolve exactly what
    /// they resolve in the running app.
    /// </summary>
    private static void WithAccent(string hex, Action probe)
    {
        var noctisApp = new Noctis.App();
        noctisApp.SetAccent(hex);
        var overlay = noctisApp.Resources.MergedDictionaries[^1];
        // A dictionary can only have one owner; hand it over to the headless app.
        noctisApp.Resources.MergedDictionaries.Remove(overlay);

        var app = Application.Current!;
        app.Resources.MergedDictionaries.Add(overlay);
        try { probe(); }
        finally { app.Resources.MergedDictionaries.Remove(overlay); }
    }

    private static Color ColorOf(IBrush? b) =>
        Assert.IsAssignableFrom<ISolidColorBrush>(b).Color;

    [AvaloniaFact]
    public void WhiteAccent_KeepsCheckedControlsReadable()
    {
        WithAccent(WhiteAccent, () =>
        {
            var toggle = new ToggleButton { Content = "Raw" };
            var check = new CheckBox { Content = "Check", IsChecked = true };
            var radio = new RadioButton { Content = "Radio", IsChecked = true };
            var panel = new StackPanel();
            panel.Children.Add(toggle);
            panel.Children.Add(check);
            panel.Children.Add(radio);

            var win = new Window { Width = 400, Height = 300, Content = panel };
            try
            {
                win.Show();
                Dispatcher.UIThread.RunJobs();
                toggle.IsChecked = true;
                Dispatcher.UIThread.RunJobs();

                var presenter = toggle.GetVisualDescendants().OfType<ContentPresenter>()
                                      .First(c => c.Name == "PART_ContentPresenter");
                AssertReadable(presenter.Background, presenter.Foreground, "checked ToggleButton label");

                var box = check.GetVisualDescendants().OfType<Border>().First(b => b.Name == "NormalRectangle");
                var tick = check.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>()
                                .First(p => p.Name == "CheckGlyph");
                AssertReadable(box.Background, tick.Fill, "checked CheckBox tick");

                var outer = radio.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Ellipse>()
                                 .First(e => e.Name == "CheckOuterEllipse");
                var dot = radio.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Ellipse>()
                               .First(e => e.Name == "CheckGlyph");
                AssertReadable(outer.Fill, dot.Fill, "checked RadioButton dot");
            }
            finally
            {
                win.Close();
            }
        });
    }

    private static void AssertReadable(IBrush? fill, IBrush? foreground, string what)
    {
        var ratio = ThemeDerivation.ContrastRatio(ColorOf(fill), ColorOf(foreground));
        Assert.True(ratio >= 4.5, $"{what}: contrast {ratio:F2}:1 against the accent fill");
    }
}
