using System.Text.RegularExpressions;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// 10-27, found while live-testing the Settings › Library tools over UI Automation: the Find
/// Duplicates and Find Metadata rows' buttons were both named "Find" (a screen reader could
/// not tell them apart), and Find Metadata's Identify all button had no name at all, so it
/// was read out as "Avalonia.Controls.StackPanel".
/// </summary>
public class ToolButtonNamesTests
{
    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { FindRepoRoot() }.Concat(parts).ToArray()));

    [Fact]
    public void SettingsToolButtons_AreNamedAfterTheirRow()
    {
        var xaml = Read("src", "Noctis", "Views", "SettingsView.axaml");
        Assert.DoesNotContain("AutomationProperties.Name=\"{loc:T Settings.Find}\"", xaml);
        Assert.Matches(new Regex(@"OpenDuplicateFinderCommand\}""\s+AutomationProperties\.Name=""\{loc:T Settings\.FindDuplicates\}"""), xaml);
        Assert.Matches(new Regex(@"OpenMetadataFinderCommand\}""\s+AutomationProperties\.Name=""\{loc:T Settings\.FindMetadata\}"""), xaml);
    }

    [Fact]
    public void IdentifyAll_HasAnAccessibleName()
    {
        var xaml = Read("src", "Noctis", "Views", "MetadataFinderDialog.axaml");
        Assert.Matches(new Regex(@"IdentifyAllCommand\}""\s+AutomationProperties\.Name=""\{loc:T MetadataFinder\.IdentifyAll\}"""), xaml);
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "Noctis.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return dir!;
    }
}
