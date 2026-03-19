namespace Noctis.Models;

/// <summary>
/// Include/exclude rule for library scanning.
/// </summary>
public class FolderRule
{
    public string Path { get; set; } = string.Empty;
    public bool Include { get; set; } = true;
    public bool Enabled { get; set; } = true;
}

