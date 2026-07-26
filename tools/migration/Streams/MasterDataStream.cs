using Mersal.Migration.Core;

namespace Mersal.Migration.Streams;

/// <summary>A master-data dataset's expected vs actual state (count + version) for reconciliation.</summary>
public sealed record DatasetCheck(string Dataset, int ExpectedCount, int ActualCount, string ExpectedVersion, string ActualVersion);

/// <summary>
/// STREAM A — master data (phase 12.1). ICD-10/CPT/LOINC-ready + Drug/ATC + allergens/interactions
/// were already ingested in phase 0b, so this stream VALIDATES rather than re-loads: it asserts
/// counts and versions match and lists any drift as exceptions. A version bump is the only reason to
/// re-load, and that is an explicit, separate action.
/// </summary>
public static class MasterDataStream
{
    public const string StreamName = "master-data";

    public static ReconciliationReport Reconcile(Guid batchId, IReadOnlyList<DatasetCheck> checks)
    {
        ArgumentNullException.ThrowIfNull(checks);
        var recon = new ReconciliationReport(StreamName, batchId) { SourceCount = checks.Count };

        foreach (var c in checks)
        {
            if (c.ExpectedCount != c.ActualCount)
                recon.Reject(c.Dataset, $"count drift: expected {c.ExpectedCount}, found {c.ActualCount}");
            else if (!string.Equals(c.ExpectedVersion, c.ActualVersion, StringComparison.Ordinal))
                recon.Reject(c.Dataset, $"version drift: expected {c.ExpectedVersion}, found {c.ActualVersion}");
            else
            {
                recon.Updated++; // validated in place (no rows written).
                recon.CountField(c.Dataset);
            }
        }
        return recon;
    }
}
