namespace Mersal.Authz;

/// <summary>The single disclosure rule for sensitive clinical content (design 37 §6, audit H4). A non-Standard
/// item (Sensitive / HighlySensitive — e.g. mental-health) is content-restricted to EXISTENCE METADATA ONLY
/// (category, date, status, branch, a RESTRICTED marker — never values or fetchable refs) for everyone EXCEPT the
/// authoring/ordering clinician or the holder of an active, single-result report-access grant. This deliberately
/// overrides the medical-approval team's standing EMR oversight. orders-service enforces it on direct result
/// reads (SensitiveResultGate); the approvals review reuses THIS rule so the oversight aggregation cannot become a
/// side channel around it.</summary>
public static class SensitiveDisclosure
{
    public const string Standard = "Standard";

    /// <summary>True ⇒ show existence metadata only (drop values + refs). <paramref name="callerHasAccess"/> is the
    /// author-or-active-grant fact the data owner (orders/emr) computed for this caller.</summary>
    public static bool IsRestricted(string? sensitivityLevel, bool callerHasAccess) =>
        !string.IsNullOrEmpty(sensitivityLevel)
        && !string.Equals(sensitivityLevel, Standard, StringComparison.OrdinalIgnoreCase)
        && !callerHasAccess;
}
