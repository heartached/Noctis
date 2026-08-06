using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using Noctis.Views;
using Xunit;
using Xunit.Abstractions;

namespace Noctis.Tests;

/// <summary>
/// The Music Server card's four inputs must wear byte-identical outlines. Two
/// by-eye rounds failed because the ComboBox outline was drawn by the Fluent
/// template's own chrome (Border#Background), which never renders quite like the
/// TextBox's PART_BorderElement — first dimmer/thinner (theme values at rest),
/// then brighter (pinned values, different sub-pixel snapping). The durable fix
/// is structural: disarm the combo's template chrome entirely and draw the
/// outline with a plain wrapper Border carrying exactly the field recipe. This
/// test mounts the REAL SettingsView styles (donor view) and pins that contract.
/// </summary>
public class MediaServerOutlineParityTests
{
    private readonly ITestOutputHelper _output;

    public MediaServerOutlineParityTests(ITestOutputHelper output) => _output = output;

    [AvaloniaFact]
    public void ServerTypePicker_OutlineMatchesTheFieldBoxes()
    {
        // The donor view hosts the shipped UserControl.Styles (server-pill,
        // server-pill-shell, metadata-info-field). Styles can't be re-parented onto
        // another host, so the probe controls are mounted INSIDE the donor: its
        // compiled content is swapped for the probe panel, and the real style
        // objects apply to it as descendants.
        var donor = new SettingsView();
        var window = new Window { Width = 700, Height = 300, Content = donor };

        var combo = new ComboBox { ItemsSource = new[] { "Jellyfin" }, SelectedIndex = 0 };
        combo.Classes.Add("pill-list");
        combo.Classes.Add("server-pill");
        var shell = new Border { Child = combo };
        shell.Classes.Add("server-pill-shell");

        var field = new TextBox { Text = "" };
        field.Classes.Add("metadata-info-field");

        donor.Content = new StackPanel { Spacing = 10, Children = { shell, field } };
        window.Show();
        window.UpdateLayout();

        var comboChrome = combo.GetVisualDescendants().OfType<Border>()
            .FirstOrDefault(b => b.Name == "Background");
        var fieldChrome = field.GetVisualDescendants().OfType<Border>()
            .FirstOrDefault(b => b.Name == "PART_BorderElement");
        Assert.NotNull(comboChrome);
        Assert.NotNull(fieldChrome);

        _output.WriteLine($"combo template chrome: thickness={comboChrome!.BorderThickness} " +
                          $"brush={(comboChrome.BorderBrush as ISolidColorBrush)?.Color.ToString() ?? "null"} " +
                          $"bg={(comboChrome.Background as ISolidColorBrush)?.Color.ToString() ?? "null"}");
        _output.WriteLine($"field template chrome: thickness={fieldChrome!.BorderThickness} " +
                          $"brush={(fieldChrome.BorderBrush as ISolidColorBrush)?.Color.ToString() ?? "null"}");
        _output.WriteLine($"shell: thickness={shell.BorderThickness} " +
                          $"brush={(shell.BorderBrush as ISolidColorBrush)?.Color.ToString() ?? "null"} " +
                          $"radius={shell.CornerRadius}");

        // 1. The combo's own template chrome must be fully disarmed — no stroke of its
        //    own, so the theme cannot render a competing (mismatched) outline.
        Assert.Equal(new Thickness(0), comboChrome.BorderThickness);

        // 2. The visible outline comes from the wrapper Border, carrying exactly the
        //    resolved stroke of the field boxes: same thickness, same color, same radius.
        Assert.Equal(fieldChrome.BorderThickness, shell.BorderThickness);
        var fieldColor = Assert.IsAssignableFrom<ISolidColorBrush>(fieldChrome.BorderBrush).Color;
        var shellColor = Assert.IsAssignableFrom<ISolidColorBrush>(shell.BorderBrush).Color;
        Assert.Equal(fieldColor, shellColor);
        Assert.Equal(fieldChrome.CornerRadius, shell.CornerRadius);
    }
}
