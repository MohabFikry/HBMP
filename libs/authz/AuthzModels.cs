using Mersal.Auth;

namespace Mersal.Authz;

/// <summary>
/// An authorization question: may this <paramref name="Principal"/> perform <paramref name="Action"/>
/// on <paramref name="Resource"/>, for a stated <paramref name="Purpose"/>? Attributes on the resource
/// drive ABAC (provider-ownership, tenant, status, treating-relationship). See 18-security-model.md §4.
/// </summary>
public sealed record AuthzRequest(
    HbmpPrincipal Principal,
    string Action,
    ResourceRef Resource,
    string? Purpose = null);

/// <summary>The resource being acted on, with the attributes ABAC evaluates.</summary>
public sealed record ResourceRef
{
    public required string Type { get; init; }
    public string? Id { get; init; }

    /// <summary>Owning tenant (ABAC tenant isolation).</summary>
    public string? TenantId { get; init; }

    /// <summary>Owning provider (ABAC provider-ownership).</summary>
    public string? ProviderId { get; init; }

    /// <summary>Resource status (e.g. order/rx status) some policies gate on.</summary>
    public string? Status { get; init; }

    /// <summary>Beneficiary ids the principal has an active treating relationship with (doctor↔patient).</summary>
    public IReadOnlySet<string> TreatingBeneficiaryIds { get; init; } = new HashSet<string>();

    /// <summary>The beneficiary this resource concerns (for treating-relationship checks).</summary>
    public string? BeneficiaryId { get; init; }

    /// <summary>Case ids the principal holds an ACTIVE assignment to (case-assignment ABAC, phase 10). Resolved
    /// from the <c>case_assignment</c> rows before evaluation; unassignment removes the id → access revoked.</summary>
    public IReadOnlySet<string> AssignedCaseIds { get; init; } = new HashSet<string>();

    /// <summary>The branch this resource belongs to (branch-scope ABAC, phase 14). Null ⇒ not branch-bound.</summary>
    public Guid? BranchId { get; init; }

    /// <summary>The caller's permitted branch set, resolved before evaluation (Home ∪ Additional, effective).</summary>
    public IReadOnlySet<Guid> PermittedBranchIds { get; init; } = new HashSet<Guid>();

    /// <summary>The caller's active branch (from X-Active-Branch, validated). Null ⇒ member-scoped / no context.</summary>
    public Guid? ActiveBranchId { get; init; }

    /// <summary>
    /// 25.1 (design 42 §1) — HOW the caller's branch reach is expressed, which decides what
    /// <see cref="ActiveBranchId"/> means to <see cref="AbacConditions.InBranchScope"/>:
    ///
    ///   • <see cref="ScopeMode.BranchScoped"/> (the default) — the active branch RESTRICTS. A coordinator
    ///     acts in the one clinic they are working in.
    ///   • <see cref="ScopeMode.BranchSetScoped"/> — the active branch FILTERS a view and does not restrict an
    ///     ACT. A clinics manager who has narrowed their screen to Maadi is still the supervisor of Dokki;
    ///     refusing their Dokki write because of a UI filter would make the filter a permission boundary,
    ///     which is precisely the collapse of authority into reach this phase exists to prevent.
    ///
    /// Defaulted to <see cref="ScopeMode.BranchScoped"/> so every pre-25.1 call site keeps its exact
    /// behaviour without being touched.
    /// </summary>
    public ScopeMode BranchReach { get; init; } = ScopeMode.BranchScoped;
}

public enum AuthzEffect { Deny, Allow }

/// <summary>
/// Every decision returns allow/deny + a reason code + the satisfied condition codes, and is auditable
/// (18-security-model.md §4: "returns allow/deny + reason_code + satisfied condition codes").
/// </summary>
public sealed record AuthzDecision(
    AuthzEffect Effect,
    string ReasonCode,
    IReadOnlyList<string> SatisfiedConditions,
    bool BreakGlass = false)
{
    public bool IsAllowed => Effect == AuthzEffect.Allow;

    public static AuthzDecision Allow(string reason, IReadOnlyList<string>? conditions = null, bool breakGlass = false)
        => new(AuthzEffect.Allow, reason, conditions ?? [], breakGlass);

    public static AuthzDecision Deny(string reason)
        => new(AuthzEffect.Deny, reason, []);
}
