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
