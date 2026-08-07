namespace Mersal.Audit.Domain;

/// <summary>
/// 29.1 — resolves identifiers that were renamed AFTER the audit rows naming them were written, for DISPLAY
/// only (design 45 §1 (c), ADR-0029).
///
/// <para><b>Why historical audit rows are never rewritten.</b> Every <c>audit_event</c> carries a
/// <c>record_hash</c> over its own canonical bytes and a <c>prev_hash</c> linking it to its predecessor
/// (19-audit-strategy.md, <c>HashChain</c>). That chain is the single property that makes the audit trail
/// EVIDENCE rather than a log: it is what lets a verifier state that no row was altered after the fact.
/// An <c>UPDATE ... SET actor_role = 'radiology_tech'</c> would change the canonical bytes of every affected
/// row, every one of their hashes would stop matching, and <c>AuditVerifier</c> would report the partition as
/// tampered — correctly, because it would have been. The cure would be indistinguishable from the disease it
/// is meant to detect.</para>
///
/// <para><b>So the alias is permanent, not a migration step.</b> Rows written before the switch say
/// <c>imaging_tech</c> and will say it in ten years. This type resolves that to today's name when a human
/// reads the trail, and the read model carries BOTH — the stored value stays visible, because an investigator
/// comparing a record against its hash needs the bytes that were actually hashed.</para>
///
/// <para><b>This is the opposite of <c>LegacyRoleAliases</c>.</b> That one is a WINDOW: it makes authority
/// answer to two spellings while tokens drain, and it is emptied at the contract step. This one is FOREVER,
/// and it grants nothing — it is a label on data that has already happened.</para>
/// </summary>
public static class LegacyIdentifierDisplay
{
    /// <summary>Stored (historical) identifier → the name it is known by today. Entries are only ever ADDED.
    /// Removing one does not clean anything up; it makes a decade of audit rows read under a name no part of
    /// the platform still uses.</summary>
    private static readonly IReadOnlyDictionary<string, string> Renamed =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // 29.1 / design 45 §1 — the Imaging → Radiology rename.
            ["imaging_tech"] = "radiology_tech",
            ["Imaging"] = "Radiology",
        };

    /// <summary>Today's name for a stored identifier, or the stored value itself when it was never renamed.
    /// Null in, null out — an absent actor role is absent, not "unknown".</summary>
    public static string? Display(string? stored) =>
        stored is not null && Renamed.TryGetValue(stored, out var current) ? current : stored;

    /// <summary>True when <paramref name="stored"/> is a retired spelling, so a reader can mark it as
    /// historical rather than presenting it as the current name.</summary>
    public static bool IsRetired(string? stored) => stored is not null && Renamed.ContainsKey(stored);
}
