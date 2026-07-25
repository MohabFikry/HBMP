using Mersal.Authz;

namespace Mersal.Admin.Domain;

/// <summary>The outcome of evaluating a proposed role grant against the current bindings + SoD matrix.</summary>
public sealed record GrantEvaluation(bool Allowed, string? ReasonCode, IReadOnlyList<SegregationOfDuties.Violation> Violations)
{
    public static GrantEvaluation Ok() => new(true, null, []);
    public static GrantEvaluation Denied(string reason, IReadOnlyList<SegregationOfDuties.Violation> v) => new(false, reason, v);
}

/// <summary>
/// Pure assignment-time policy for role bindings. Given the roles a user ALREADY actively holds and a proposed new
/// role, it (1) rejects an unknown role, (2) rejects a duplicate active grant, and (3) rejects any grant that would
/// breach the Segregation-of-Duties matrix (10-role-matrix §7) — including the no-self-elevation rules (Org Admin →
/// Super Admin, Provider Admin → clinical). The service layer consults this before writing a binding; the same SoD
/// matrix is re-checked at decision time by the deciding services.
/// </summary>
public static class RoleAssignment
{
    public static GrantEvaluation Evaluate(IEnumerable<string> heldRoles, string proposedRole)
    {
        ArgumentNullException.ThrowIfNull(heldRoles);
        ArgumentNullException.ThrowIfNull(proposedRole);

        var role = proposedRole.Trim().ToLowerInvariant();
        if (!RoleCatalog.IsKnown(role))
            return GrantEvaluation.Denied("unknown-role", []);

        var held = heldRoles.Select(r => r.Trim().ToLowerInvariant()).Where(r => r.Length > 0).ToHashSet(StringComparer.Ordinal);
        if (held.Contains(role))
            return GrantEvaluation.Denied("already-granted", []);

        var violations = SegregationOfDuties.Evaluate(held, [role]);
        return violations.Count > 0
            ? GrantEvaluation.Denied("sod-conflict", violations)
            : GrantEvaluation.Ok();
    }

    /// <summary>The review deadline for a new grant: T3/T4 roles are recertified one quarter out; lower tiers never.</summary>
    public static DateTimeOffset? ReviewDueAt(SensitivityTier tier, DateTimeOffset grantedAt) =>
        RoleCatalog.RequiresReview(tier) ? grantedAt.AddDays(90) : null;
}
