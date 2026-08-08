using Mersal.Auth;
using Mersal.Authz;

namespace Mersal.Claims.Api;

/// <summary>The claims-access decision. The claims roles hold ONLY the claims actions; there is no rule granting any
/// clinical action, so a diagnosis/EMR read is default-denied (claims ≠ diagnosis, 11-permission-matrix §3.2). The
/// engine audits every deny and every sensitive allow (review, decide, adjust, export, settle). Returns a ready 403
/// when denied, else null. Provider users read through <see cref="CheckClaimReadAsync"/>, which holds them to their
/// own provider by ABAC provider-ownership (RLS is the layer below).</summary>
public sealed class ClaimsGate(IHbmpPrincipalAccessor me, IAuthorizationEngine engine)
{
    /// <summary>Evaluate a claims action against the caller's own scope (tenant + their provider, if any).</summary>
    public Task<IResult?> CheckAsync(string action, CancellationToken ct)
    {
        var p = me.Principal;
        return p is null
            ? Task.FromResult<IResult?>(GateResults.Unauthenticated())
            : EvaluateAsync(p, action, p.ProviderId, ct);
    }

    /// <summary>
    /// The claim READ gate. A provider-affiliated caller is authorized by <c>claims:read:own</c> under
    /// provider-ownership; Mersal staff by the tenant-wide <c>claims:read</c> (11-permission-matrix §3.4).
    ///
    /// <para>Which rule applies is decided by the caller's ROLE, not by whether a provider id happens to be on
    /// the token — a claims officer affiliated with a provider is still staff, and a provider portal user whose
    /// token is missing its provider id is still a provider, and is denied rather than widened. The mapping is
    /// <see cref="BranchScopeModes"/>' (design 37 §3), so "who is an external provider" has one definition on
    /// this platform rather than one per service.</para>
    /// </summary>
    /// <param name="row">The row being read, once it is in hand — see <see cref="ClaimRow"/>. Omitted for a
    /// list, whose resource is the caller's own provider because the query filter is forced to exactly that.</param>
    public Task<IResult?> CheckClaimReadAsync(CancellationToken ct, ClaimRow? row = null)
    {
        var p = me.Principal;
        if (p is null) return Task.FromResult<IResult?>(GateResults.Unauthenticated());

        var action = BranchScopeModes.ModeFor(p) == ScopeMode.ProviderScoped
            ? ClaimsPolicies.ReadOwnClaim
            : ClaimsPolicies.ReadClaim;
        // No row ⇒ the caller's own provider. A row ⇒ ITS provider, verbatim, including null: a claim with no
        // provider (a beneficiary reimbursement) belongs to no provider, so provider-ownership cannot hold and
        // a provider caller is denied. Falling back to the caller's own id there would let a provider read
        // every ownerless claim in the tenant.
        return EvaluateAsync(p, action, row is null ? p.ProviderId : row.ProviderId?.ToString(), ct);
    }

    /// <summary>
    /// The settlement-advice EXPORT gate. A provider downloads its own advice under <c>claims:export:own</c>;
    /// Mersal exports — and releases — under <c>claims:export</c>. The row check is left to
    /// <c>SettlementService.ExportAsync</c>, which already compares the caller to the batch's payee and audits
    /// the refusal as EXPORT_CROSS_PROVIDER at High severity — a stronger record than a generic engine deny.
    /// </summary>
    public Task<IResult?> CheckAdviceExportAsync(CancellationToken ct)
    {
        var p = me.Principal;
        if (p is null) return Task.FromResult<IResult?>(GateResults.Unauthenticated());

        var action = BranchScopeModes.ModeFor(p) == ScopeMode.ProviderScoped
            ? ClaimsPolicies.ExportOwnAdvice
            : ClaimsPolicies.Export;
        return EvaluateAsync(p, action, p.ProviderId, ct);
    }

    private async Task<IResult?> EvaluateAsync(HbmpPrincipal p, string action, string? resourceProviderId, CancellationToken ct)
    {
        var resource = new ResourceRef
        {
            Type = ClaimsPolicies.Resource, TenantId = p.TenantId, ProviderId = resourceProviderId,
        };
        var decision = await engine.EvaluateAsync(new AuthzRequest(p, action, resource), ct);
        if (decision.IsAllowed) return null;

        return GateResults.Forbidden("urn:hbmp:claims-access-denied", detail: "You are not permitted to perform this claims action.", reason: decision.ReasonCode);
    }

    public string? Tenant => me.Principal?.TenantId;
    public string? Subject => me.Principal?.Subject;
    public string? ProviderId => me.Principal?.ProviderId;
    public string? Roles => me.Principal is null ? null : string.Join(',', me.Principal.Roles);
}

/// <summary>The provider a fetched row belongs to. A distinct type rather than a bare <c>Guid?</c> so that
/// "no row supplied" and "a row that belongs to no provider" cannot be passed as the same argument — they are
/// opposite answers, and conflating them is how a provider-ownership check becomes a formality.</summary>
public sealed record ClaimRow(Guid? ProviderId);
