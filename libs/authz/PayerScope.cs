using Mersal.Auth;

namespace Mersal.Authz;

/// <summary>
/// Phase 19.5 — the payer dimension of access (design 38 §6: "a payer-scoped user sees only their payer's
/// policies").
///
/// <para>MODELLED AS A RESTRICTION, NOT AS AN ENTITLEMENT. A user with no payer assignment is payer-UNRESTRICTED;
/// a user with assignments sees only those payers. The opposite reading — "you see nothing until somebody grants
/// you a payer" — would have required assigning every existing Beneficiary-Management officer to every payer on
/// the day this shipped, and an entitlement that has to be granted to everyone stops being read as an
/// entitlement. Restricting a user is therefore a deliberate, audited act, and the absence of one is a
/// deliberate configuration too.</para>
///
/// <para>THE FAILURE CASE IS WHERE THAT MODEL EARNS ITS KEEP. "No rows came back" and "the directory could not
/// be reached" are the same shape on the wire and opposite in meaning: read the first as unrestricted and an
/// admin-service outage silently WIDENS everyone's access to every payer. So the three states are distinct —
/// <see cref="Unrestricted"/>, a restricted set, and <see cref="DenyAll"/> for "could not ask" — and the seam
/// returns DenyAll on failure. A payer-restricted user seeing nothing during an outage is a bad afternoon; a
/// donor's caseload leaking into another donor's screen is a breach.</para>
///
/// <para>The token contract is FROZEN (phase 17, <c>docs/security/token-contract.md</c>) and carries no payer
/// claim, so the assignment is resolved per request from admin-service — exactly the shape branch scope already
/// uses (<see cref="IBranchDirectory"/>), and for the same reason: a revocation must take effect without
/// waiting for every outstanding token to expire.</para>
/// </summary>
public sealed record PermittedPayers(bool IsUnrestricted, IReadOnlySet<Guid> PayerIds)
{
    /// <summary>No assignment exists — the caller is not payer-restricted. The common case.</summary>
    public static readonly PermittedPayers Unrestricted = new(true, new HashSet<Guid>());

    /// <summary>The fail-closed value: restricted to the empty set. Used when the directory could not be
    /// reached, so an outage narrows access instead of widening it.</summary>
    public static readonly PermittedPayers DenyAll = new(false, new HashSet<Guid>());

    public static PermittedPayers RestrictedTo(IEnumerable<Guid> payerIds) =>
        new(false, new HashSet<Guid>(payerIds ?? []));

    /// <summary>May this caller see data belonging to <paramref name="payerId"/>?</summary>
    public bool Allows(Guid payerId) => IsUnrestricted || PayerIds.Contains(payerId);

    /// <summary>May this caller see data whose payer is unknown (a policy with no <c>payer_id</c> yet — the
    /// pre-19.2 rows the 19.7 backfill retires)? Only an unrestricted caller may: a payer-restricted user
    /// asked for one payer's book of business, and a row that might belong to any payer is not it.</summary>
    public bool AllowsUnattributed => IsUnrestricted;
}

/// <summary>The seam a service implements to read a caller's payer restrictions. Admin-service owns the
/// assignment (it owns role bindings and branch assignments already — this is the same kind of fact); each
/// service resolves it per request and caches briefly. Tests supply a fake.</summary>
public interface IPayerDirectory
{
    Task<PermittedPayers> GetAsync(HbmpPrincipal principal, CancellationToken ct = default);
}

/// <summary>Per-request holder for the resolved payer scope, populated by middleware and read by handlers.
/// Defaults to <see cref="PermittedPayers.Unrestricted"/> so a service that never wires the middleware behaves
/// exactly as it did before payer scope existed.</summary>
public sealed class PayerScopeState
{
    public PermittedPayers Permitted { get; set; } = PermittedPayers.Unrestricted;

    /// <summary>The scope narrowed the result set. Reported on list responses so a short page is legible as
    /// "your scope" rather than mistaken for "there is no more data".</summary>
    public bool IsRestricted => !Permitted.IsUnrestricted;
}

/// <summary>
/// How a targeted read of one payer-owned entity resolves against the caller's scope.
///
/// The distinction between <see cref="Denied"/> and an empty list is the acceptance criterion of 19.5, and it is
/// a deliberate inversion of the usual advice. Returning 404/empty for an unauthorized id avoids confirming the
/// id exists — good hygiene when the resource is a PERSON. A payer is an ORGANISATION Mersal contracts with, and
/// its existence is not the secret; the secret is its members. Answering "no such policy" to an administrator
/// who is looking straight at the policy number sends them to raise a data-loss incident, so this says 403.
/// </summary>
public enum PayerScopeOutcome
{
    /// <summary>Allowed — the entity's payer is in scope, or the caller is unrestricted.</summary>
    Allowed,
    /// <summary>The entity belongs to a payer the caller may not see → 403.</summary>
    Denied,
}

public static class PayerScopeRules
{
    /// <summary>Resolve a targeted read of an entity owned by <paramref name="payerId"/> (null = the payer is
    /// not recorded on the row).</summary>
    public static PayerScopeOutcome Check(PermittedPayers permitted, Guid? payerId)
    {
        ArgumentNullException.ThrowIfNull(permitted);
        return payerId switch
        {
            null => permitted.AllowsUnattributed ? PayerScopeOutcome.Allowed : PayerScopeOutcome.Denied,
            { } id => permitted.Allows(id) ? PayerScopeOutcome.Allowed : PayerScopeOutcome.Denied,
        };
    }
}
