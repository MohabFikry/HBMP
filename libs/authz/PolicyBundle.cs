namespace Mersal.Authz;

/// <summary>
/// The versioned, source-controlled policy bundle: which role+scope may perform an action, and which
/// ABAC conditions that action requires. In Tier 1 / tests this native bundle IS the policy engine;
/// it is shaped to be swapped for a Cerbos/OPA sidecar later (ADR-0005) without changing callers.
/// Deploying a bundle emits admin.policy.deploy audit (phase 8b).
/// </summary>
public sealed record PolicyBundle(string Version, IReadOnlyList<PolicyRule> Rules)
{
    /// <summary>Find the first rule matching an action + resource type. Order = priority.</summary>
    public PolicyRule? Match(string action, string resourceType) =>
        Rules.FirstOrDefault(r =>
            r.Action == action &&
            (r.ResourceType == resourceType || r.ResourceType == "*"));
}

/// <summary>
/// A single rule: an action on a resource type is permitted for any of <see cref="Roles"/> holding
/// one of <see cref="Scopes"/> (if specified), provided every ABAC condition in
/// <see cref="RequiredConditions"/> holds. Default-deny: no matching rule ⇒ deny.
/// </summary>
public sealed record PolicyRule
{
    public required string Action { get; init; }
    public required string ResourceType { get; init; }

    /// <summary>Roles this rule grants to (empty = any authenticated role).</summary>
    public IReadOnlySet<string> Roles { get; init; } = new HashSet<string>();

    /// <summary>OAuth2 scopes required (empty = none beyond role).</summary>
    public IReadOnlySet<string> Scopes { get; init; } = new HashSet<string>();

    /// <summary>ABAC condition codes that must all hold (see <see cref="AbacConditions"/>).</summary>
    public IReadOnlyList<string> RequiredConditions { get; init; } = [];

    /// <summary>Whether this action reads/writes sensitive resources → audit even on allow.</summary>
    public bool Sensitive { get; init; }
}
