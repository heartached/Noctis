namespace Noctis.Models;

/// <summary>
/// Basic audit record for metadata/library mutations.
/// </summary>
public class AuditEvent
{
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public string EventType { get; set; } = string.Empty;
    public string EntityType { get; set; } = string.Empty;
    public string EntityId { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public Dictionary<string, string> Details { get; set; } = new();
}

