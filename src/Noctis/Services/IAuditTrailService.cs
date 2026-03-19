using Noctis.Models;

namespace Noctis.Services;

/// <summary>
/// Lightweight audit trail recording for library and metadata changes.
/// </summary>
public interface IAuditTrailService
{
    Task AppendAsync(AuditEvent auditEvent, CancellationToken ct = default);
}

