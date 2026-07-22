using Mersal.Audit.Client;
using Mersal.Audit.Domain;
using Microsoft.Extensions.Logging;

namespace Mersal.Audit.Infrastructure;

/// <summary>
/// Raises the critical <c>integrity.mismatch</c> alert on a broken chain. Logs at Critical so the
/// LGTM/Alertmanager rules (phase 11) page on-call. In later phases this also publishes an event to
/// notification-service for Security/DPO.
/// </summary>
public sealed class LoggingIntegrityAlerter(ILogger<LoggingIntegrityAlerter> logger) : IIntegrityAlerter
{
    public Task RaiseAsync(string partitionKey, ChainVerification result, CancellationToken ct = default)
    {
        logger.LogCritical(
            "integrity.mismatch in audit partition {Partition}: broken at index {Index} (record {RecordId}) — {Reason}",
            partitionKey, result.BrokenAtIndex, result.BrokenRecordId, result.Reason);
        return Task.CompletedTask;
    }
}
