using Microsoft.AspNetCore.Http;

namespace Mersal.Authz;

/// <summary>The outcome of a requested membership / tenant switch.</summary>
/// <param name="Allowed">Whether the switch may proceed.</param>
/// <param name="AuditEvent">The audit event to emit — ALWAYS set, including on success (A2: nothing silent).</param>
/// <param name="AdministrativeOnly">True when the switch is permitted only over platform-administration
/// keys, i.e. a platform admin reaching into a tenant they hold no membership in.</param>
/// <param name="Reason">Machine-readable refusal reason, null when allowed.</param>
public sealed record SwitchDecision(
    bool Allowed, string AuditEvent, bool AdministrativeOnly = false, string? Reason = null);

/// <summary>
/// 21.5 — guards on changing which membership (and therefore which organisation) a session acts under
/// (design 40 §6, adaptations A1 and A2).
///
/// A2 — NOTHING SILENT. Every outcome here produces an audit event, refusals included. A cross-tenant
/// attempt that is simply ignored leaves no trace of the attempt, and "nobody tried" and "somebody tried
/// and we dropped it" are indistinguishable afterwards — which is precisely the signal an investigation
/// needs.
///
/// A1 — a platform administrator may reach a tenant they hold no membership in, but ONLY over
/// administration keys. The decision carries that as a flag rather than a boolean allow, so the caller
/// cannot mistake "may administer this tenant" for "may read this tenant's patients".
/// </summary>
public static class MembershipSwitch
{
    public const string Switched = "MembershipSwitched";
    public const string TenantSwitchDenied = "TenantSwitchDenied";
    public const string DeniedType = "https://mersal.foundation/problems/tenant-switch-denied";

    /// <summary>
    /// Decide a switch from <paramref name="fromMembershipId"/> to a target.
    /// </summary>
    /// <param name="targetIsOwnMembership">Whether the target is a membership this identity actually holds
    /// and may select (Active, non-deleted — resolved by the caller against the store, never trusted from
    /// the request).</param>
    /// <param name="isPlatformAdmin">Whether the identity carries the platform-administration flag.</param>
    /// <param name="crossTenant">Whether the target is in a different tenant than the current session.</param>
    public static SwitchDecision Decide(
        string? fromMembershipId, bool targetIsOwnMembership, bool isPlatformAdmin, bool crossTenant)
    {
        // The ordinary case: switching to another membership this person genuinely holds. Re-resolution
        // gives them new claims; nothing is inherited from the previous membership.
        if (targetIsOwnMembership) return new SwitchDecision(true, Switched);

        // Not their membership. Only a platform administrator may go further, and only administratively.
        if (isPlatformAdmin)
            return new SwitchDecision(true, Switched, AdministrativeOnly: true);

        // Everyone else is refused — and the refusal is recorded. Reaching for another organisation's data
        // is exactly the event that must not vanish.
        return new SwitchDecision(
            false, TenantSwitchDenied,
            Reason: crossTenant ? "cross-tenant-without-membership" : "not-a-held-membership");
    }

    /// <summary>403 for a refused switch. Carries the reason so the audit trail and the client agree on
    /// why, rather than the client inventing its own explanation.</summary>
    public static IResult Denied(string reason) => Results.Problem(
        statusCode: StatusCodes.Status403Forbidden,
        title: "tenant-switch-denied",
        type: DeniedType,
        detail: "You do not hold a membership in that organization.",
        extensions: new Dictionary<string, object?> { ["code"] = TenantSwitchDenied, ["reason"] = reason });
}
