using System.Security.Cryptography;

namespace Mersal.Audit.Client;

/// <summary>
/// The tamper-evident hash chain over audit records (19-audit-strategy.md §4).
/// Each record carries prev_hash (the previous record's record_hash in its partition) and
/// record_hash = SHA-256(canonicalized record incl. prev_hash). Recomputing the chain and
/// comparing against stored hashes detects any insertion, deletion, reordering, or edit.
/// </summary>
public static class HashChain
{
    /// <summary>The genesis prev_hash for the first record in a partition.</summary>
    public const string Genesis = "0000000000000000000000000000000000000000000000000000000000000000";

    /// <summary>Compute the record_hash for an event whose PrevHash is already set.</summary>
    public static string ComputeRecordHash(AuditEvent eventWithPrevHash)
    {
        var canonical = AuditCanonicalizer.Canonicalize(eventWithPrevHash);
        var hash = SHA256.HashData(canonical);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Chain a new event onto <paramref name="prevHash"/> (or <see cref="Genesis"/> for the first),
    /// returning a copy with PrevHash + RecordHash populated. This is what audit-service does on
    /// ingest as the single writer per partition.
    /// </summary>
    public static AuditEvent Chain(AuditEvent e, string? prevHash)
    {
        var withPrev = e with { PrevHash = prevHash ?? Genesis, RecordHash = null };
        return withPrev with { RecordHash = ComputeRecordHash(withPrev) };
    }

    /// <summary>
    /// Verify an ordered sequence (one partition, ascending). Returns the first integrity
    /// violation found, or <see cref="ChainVerification.Ok"/> if the whole chain is intact.
    /// </summary>
    public static ChainVerification Verify(IReadOnlyList<AuditEvent> orderedRecords)
    {
        ArgumentNullException.ThrowIfNull(orderedRecords);

        string? expectedPrev = Genesis;
        for (var i = 0; i < orderedRecords.Count; i++)
        {
            var r = orderedRecords[i];

            if (!string.Equals(r.PrevHash, expectedPrev, StringComparison.Ordinal))
            {
                return ChainVerification.Broken(i, r.AuditEventId,
                    $"prev_hash mismatch: expected {expectedPrev}, found {r.PrevHash} (insertion/deletion/reorder)");
            }

            var recomputed = ComputeRecordHash(r);
            if (!string.Equals(r.RecordHash, recomputed, StringComparison.Ordinal))
            {
                return ChainVerification.Broken(i, r.AuditEventId,
                    $"record_hash mismatch: stored {r.RecordHash}, recomputed {recomputed} (record was tampered)");
            }

            expectedPrev = r.RecordHash;
        }

        return ChainVerification.Ok;
    }
}

/// <summary>Result of a chain verification pass.</summary>
public sealed record ChainVerification(bool IsIntact, int? BrokenAtIndex, Guid? BrokenRecordId, string? Reason)
{
    public static readonly ChainVerification Ok = new(true, null, null, null);

    public static ChainVerification Broken(int index, Guid id, string reason) => new(false, index, id, reason);
}
