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

    // Append-only with no pruning grew without bound; roll the file aside once
    // it passes the cap (previous generation kept as .1 for inspection).
    private const long MaxBytes = 5 * 1024 * 1024;

    public async Task AppendAsync(AuditEvent auditEvent, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var info = new FileInfo(_auditPath);
            if (info.Exists && info.Length > MaxBytes)
                File.Move(_auditPath, _auditPath + ".1", overwrite: true);

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

