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

        var breaks = new List<ChainBreak>();
        string? expectedPrev = Genesis;

        for (var i = 0; i < orderedRecords.Count; i++)
        {
            var r = orderedRecords[i];

            if (!string.Equals(r.PrevHash, expectedPrev, StringComparison.Ordinal))
            {
                breaks.Add(new ChainBreak(i, r.AuditEventId,
                    $"prev_hash mismatch: expected {expectedPrev}, found {r.PrevHash} (insertion/deletion/reorder)"));
            }
            else if (!string.Equals(r.RecordHash, ComputeRecordHash(r), StringComparison.Ordinal))
            {
                breaks.Add(new ChainBreak(i, r.AuditEventId,
                    $"record_hash mismatch: stored {r.RecordHash}, recomputed {ComputeRecordHash(r)} (record was tampered)"));
            }

            /*
             * CONTINUE PAST THE BREAK, resuming from the record's STORED hash.
             *
             * This used to `return` on the first break, and that was the more dangerous half of the audit
             * defect found in 2026-08: one record damaged by the jsonb pre-image bug left 33,404 of 33,407
             * records NEVER REACHED, including everything written afterwards. The verifier is the only
             * mechanism that reports real tampering, and a single known-bad row switched it off for the rest
             * of the partition — silently, because the alert it did raise looked like it was doing its job.
             *
             * Resuming from the STORED hash rather than the recomputed one matters just as much: the next
             * record was chained onto what was actually written, so resuming from a recomputed value would
             * turn one real break into a mismatch on every record after it. One break rendered as thousands
             * is exactly as unreadable as none.
             */
            expectedPrev = r.RecordHash;
        }

        return breaks.Count == 0
            ? ChainVerification.Intact(orderedRecords.Count)
            : ChainVerification.WithBreaks(breaks, orderedRecords.Count);
    }
}

/// <summary>One break found in a chain.</summary>
public sealed record ChainBreak(int Index, Guid RecordId, string Reason);

/// <summary>
/// Result of a chain verification pass.
///
/// <para><see cref="Breaks"/> lists EVERY break found; <see cref="BrokenAtIndex"/> and friends expose the
/// first for callers that only need one. <see cref="RecordsVerified"/> is what makes a pass auditable in its
/// own right — "no breaks" means nothing without knowing how many records were actually looked at.</para>
/// </summary>
public sealed record ChainVerification(
    bool IsIntact, int? BrokenAtIndex, Guid? BrokenRecordId, string? Reason,
    IReadOnlyList<ChainBreak> Breaks, int RecordsVerified)
{
    public static readonly ChainVerification Ok = new(true, null, null, null, [], 0);

    public static ChainVerification Intact(int recordsVerified) =>
        new(true, null, null, null, [], recordsVerified);

    public static ChainVerification WithBreaks(IReadOnlyList<ChainBreak> breaks, int recordsVerified) =>
        new(false, breaks[0].Index, breaks[0].RecordId, breaks[0].Reason, breaks, recordsVerified);

    /// <summary>Kept for existing callers/tests that construct a single-break result directly.</summary>
    public static ChainVerification Broken(int index, Guid id, string reason) =>
        WithBreaks([new ChainBreak(index, id, reason)], index + 1);
}

