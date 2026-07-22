using Mersal.Audit.Client;

namespace Mersal.Audit.Domain;

/// <summary>
/// Re-computes the hash chain for a partition and raises a critical <c>integrity.mismatch</c>
/// alert on any break (19-audit-strategy.md §4 "periodic anchoring + verifier").
/// </summary>
public sealed class AuditVerifier(IAuditEventStore store, IIntegrityAlerter alerter)
{
    public async Task<ChainVerification> VerifyPartitionAsync(string partitionKey, CancellationToken ct = default)
    {
        var records = await store.ReadPartitionAsync(partitionKey, ct);
        var result = HashChain.Verify(records);
        if (!result.IsIntact)
        {
            await alerter.RaiseAsync(partitionKey, result, ct);
        }
        return result;
    }
}
