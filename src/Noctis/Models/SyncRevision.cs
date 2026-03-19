namespace Noctis.Models;

/// <summary>
/// Tracks sync revision state per source.
/// </summary>
public class SyncRevision
{
    public SourceType SourceType { get; set; } = SourceType.Local;
    public string SourceKey { get; set; } = string.Empty;
    public string RevisionToken { get; set; } = string.Empty;
    public DateTime LastSyncedUtc { get; set; } = DateTime.MinValue;
}

