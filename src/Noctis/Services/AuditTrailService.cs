using System.Text.Json;
using Noctis.Models;

namespace Noctis.Services;

/// <summary>
/// Persists append-only audit events as JSONL.
/// </summary>
public sealed class AuditTrailService : IAuditTrailService
{
    private readonly string _auditPath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public AuditTrailService(IPersistenceService persistence)
    {
        var auditDir = Path.Combine(persistence.DataDirectory, "audit");
        Directory.CreateDirectory(auditDir);
        _auditPath = Path.Combine(auditDir, "events.jsonl");
    }

    public async Task AppendAsync(AuditEvent auditEvent, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var line = JsonSerializer.Serialize(auditEvent) + Environment.NewLine;
            await File.AppendAllTextAsync(_auditPath, line, ct);
        }
        catch
        {
            // Audit trail is best-effort and must not break playback/scanning.
        }
        finally
        {
            _gate.Release();
        }
    }
}

