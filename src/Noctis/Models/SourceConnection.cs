namespace Noctis.Models;

/// <summary>
/// Connection settings for external media sources.
/// </summary>
public class SourceConnection
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public SourceType Type { get; set; } = SourceType.Local;
    public string BaseUriOrPath { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string TokenOrPassword { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
}

