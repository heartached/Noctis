using Xunit;

namespace Noctis.Tests;

/// <summary>Settings › Appearance › Accent Color: the preset grid.</summary>
public class AccentPaletteTests
{
    [Fact]
    public void Presets_LeaveExactlyTheLastCellForTheCustomPicker()
    {
        // The swatches sit in a 12-column grid and the custom picker is laid over the 12th
        // cell of the last row (SettingsView.axaml). Any other count puts it on top of a
        // swatch or leaves a gap.
        Assert.Equal(11, App.AccentPresets.Count % 12);
    }

    [Fact]
    public void Presets_HaveUniqueNamesAndColours()
    {
        Assert.Equal(App.AccentPresets.Count, App.AccentPresets.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(App.AccentPresets.Count, App.AccentPresets.Select(p => p.Hex).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}
