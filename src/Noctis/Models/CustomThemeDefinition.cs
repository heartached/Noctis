namespace Noctis.Models;

public sealed class CustomThemeDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string BaseMode { get; set; } = "Dark";
    public string MainBackgroundHex { get; set; } = "#121212";
    public string SidebarBackgroundHex { get; set; } = "#1C1C1C";
    public string AccentHex { get; set; } = "#E74856";
}
