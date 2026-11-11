using System.Text.Json.Serialization;

namespace Noctis.Models;

/// <summary>
/// Include/exclude rule for library scanning.
/// </summary>
public class FolderRule : IJsonExtensible
{
    /// <summary>Members a newer Noctis saved that this build does not know, written back
    /// unchanged so an older build sharing the data folder does not erase them (GitHub #230).</summary>
    [JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? ExtensionData { get; set; }

    public string Path { get; set; } = string.Empty;
    public bool Include { get; set; } = true;
    public bool Enabled { get; set; } = true;
}

