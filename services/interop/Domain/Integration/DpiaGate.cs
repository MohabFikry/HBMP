namespace Mersal.Interop.Domain.Integration;

/// <summary>The outcome of a governance gate check — allow, or deny with a machine + human reason.</summary>
public sealed record GateOutcome(bool Allowed, string ReasonCode, string Message)
{
    public static GateOutcome Allow() => new(true, "ok", "DPIA + data-sharing agreement on file.");
    public static GateOutcome Deny(string code, string message) => new(false, code, message);
}

/// <summary>
/// The DPIA / data-sharing gate (13.2 / 13.3 guardrail; 20-compliance §6). No external integration may be
/// enabled in ANY environment unless BOTH artifacts exist for that partner: a DPIA sign-off record AND a
/// data-sharing agreement reference. This is enforced at runtime (registry <c>TryEnable</c>) and in CI
/// (tools/ci/check-integration-dpia.py). Pure + unit-testable — the single authority both call sites use.
///
/// "20 §6 — any new integration (UNHCR/gov/insurer) always requires a DPIA; §5 cross-border PDPL posture."
/// </summary>
public static class DpiaGate
{
    /// <summary>May this partner be enabled? Requires DPIA=SignedOff AND a non-empty data-sharing agreement ref.</summary>
    public static GateOutcome CanEnable(PartnerDescriptor p)
    {
        ArgumentNullException.ThrowIfNull(p);

        var missing = new List<string>();
        if (p.Dpia != DpiaStatus.SignedOff) missing.Add("a signed-off DPIA");
        if (string.IsNullOrWhiteSpace(p.DataSharingAgreementRef)) missing.Add("a data-sharing agreement reference");

        if (missing.Count > 0)
            return GateOutcome.Deny("dpia-gate-blocked",
                $"Partner '{p.PartnerId}' cannot be enabled — missing {string.Join(" and ", missing)}. " +
                "No external integration goes live without a DPIA + data-sharing agreement (20-compliance §6).");

        return GateOutcome.Allow();
    }

    /// <summary>Is a currently-Enabled partner still compliant? (used by CI/pre-flight to catch a partner that was
    /// enabled and later had its DPIA revoked or agreement removed.)</summary>
    public static GateOutcome IsCompliant(PartnerDescriptor p)
    {
        ArgumentNullException.ThrowIfNull(p);
        return p.Status == IntegrationStatus.Enabled ? CanEnable(p) : GateOutcome.Allow();
    }
}
