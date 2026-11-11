using System.Text.Json.Serialization;

namespace Noctis.Models;

public sealed class CustomThemeDefinition : IJsonExtensible
{
    /// <summary>Members a newer Noctis saved that this build does not know, written back
    /// unchanged so an older build sharing the data folder does not erase them (GitHub #230).</summary>
    [JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? ExtensionData { get; set; }

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string BaseMode { get; set; } = "Dark";
    public string MainBackgroundHex { get; set; } = "#121212";
    public string SidebarBackgroundHex { get; set; } = "#1C1C1C";
    public string AccentHex { get; set; } = "#E74856";
}
