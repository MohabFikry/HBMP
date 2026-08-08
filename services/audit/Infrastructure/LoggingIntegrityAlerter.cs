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
        ArgumentNullException.ThrowIfNull(result);

        // The COUNT and the records-verified total lead, because they are what tells an operator whether this
        // is the known historical damage (docs/audit-chain-integrity-2026-08.md) or something new. A message
        // that names one record cannot answer "has this got worse?", and that was the question nobody could
        // answer while the verifier stopped at the first break.
        logger.LogCritical(
            "integrity.mismatch in audit partition {Partition}: {BreakCount} break(s) across {Verified} record(s) verified. First: index {Index} (record {RecordId}) — {Reason}",
            partitionKey, result.Breaks.Count, result.RecordsVerified,
            result.BrokenAtIndex, result.BrokenRecordId, result.Reason);

        // Every break, not just the first. Capped so a partition-wide corruption cannot flood the log into
        // uselessness — the count above is always exact, whether or not every line is printed.
        const int MaxDetailed = 20;
        foreach (var b in result.Breaks.Skip(1).Take(MaxDetailed - 1))
        {
            logger.LogCritical(
                "integrity.mismatch in audit partition {Partition}: index {Index} (record {RecordId}) — {Reason}",
                partitionKey, b.Index, b.RecordId, b.Reason);
        }

        if (result.Breaks.Count > MaxDetailed)
        {
            logger.LogCritical(
                "integrity.mismatch in audit partition {Partition}: {Suppressed} further break(s) not listed",
                partitionKey, result.Breaks.Count - MaxDetailed);
        }

        return Task.CompletedTask;
    }
}
