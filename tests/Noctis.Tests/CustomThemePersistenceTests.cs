using System.Text.Json;
using Noctis.Models;
using Xunit;

namespace Noctis.Tests;

public class CustomThemePersistenceTests
{
    [Fact]
    public void RoundTrip_PreservesAllFields()
    {
        var settings = new AppSettings
        {
            Theme = "Custom:abc123",
            CustomThemes = new List<CustomThemeDefinition>
            {
                new()
                {
                    Id = "abc123",
                    Name = "My Sunset",
                    BaseMode = "Dark",
                    MainBackgroundHex = "#1A0E14",
                    SidebarBackgroundHex = "#221319",
                    AccentHex = "#FF8547"
                }
            }
        };

        var json = JsonSerializer.Serialize(settings);
        var restored = JsonSerializer.Deserialize<AppSettings>(json)!;

        Assert.Equal("Custom:abc123", restored.Theme);
        Assert.Single(restored.CustomThemes);
        var t = restored.CustomThemes[0];
        Assert.Equal("abc123", t.Id);
        Assert.Equal("My Sunset", t.Name);
        Assert.Equal("Dark", t.BaseMode);
        Assert.Equal("#1A0E14", t.MainBackgroundHex);
        Assert.Equal("#221319", t.SidebarBackgroundHex);
        Assert.Equal("#FF8547", t.AccentHex);
    }

    [Fact]
    public void EmptySettings_HasNoCustomThemes()
    {
        var settings = new AppSettings();
        Assert.NotNull(settings.CustomThemes);
        Assert.Empty(settings.CustomThemes);
    }
}
