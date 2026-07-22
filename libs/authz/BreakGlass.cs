namespace Mersal.Authz;

/// <summary>
/// A scoped, time-boxed, dual-reviewed break-glass grant that widens access and forces high-severity
/// audit on every read under it (18-security-model.md §11). Granting/approval lifecycle lives in
/// phase 8b; this is the runtime evaluation the engine consults.
/// </summary>
public sealed record BreakGlassGrant
{
    public required string GrantId { get; init; }
    public required string SubjectUserId { get; init; }
    public required string ApprovedByUserId { get; init; }   // != subject (dual control, enforced at grant time)
    public required DateTimeOffset NotBefore { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>Resource ids / types this grant is scoped to. Access outside scope is NOT widened.</summary>
    public IReadOnlySet<string> ScopedResourceIds { get; init; } = new HashSet<string>();
    public IReadOnlySet<string> ScopedResourceTypes { get; init; } = new HashSet<string>();

    public bool IsActiveAt(DateTimeOffset now) => now >= NotBefore && now < ExpiresAt;

    /// <summary>True if this grant currently widens access to the requested resource.</summary>
    public bool Covers(ResourceRef resource, DateTimeOffset now) =>
        IsActiveAt(now)
        && (ScopedResourceTypes.Count == 0 || ScopedResourceTypes.Contains(resource.Type))
        && (ScopedResourceIds.Count == 0 || (resource.Id is not null && ScopedResourceIds.Contains(resource.Id)));
}

/// <summary>Supplies active break-glass grants for a subject (backed by phase-8b storage later).</summary>
public interface IBreakGlassProvider
{
    /// <summary>The active grant covering this request, if any.</summary>
    BreakGlassGrant? ActiveGrantFor(HbmpRequestContext ctx);
}

/// <summary>Minimal context passed to the break-glass provider (subject + resource + clock).</summary>
public sealed record HbmpRequestContext(string SubjectUserId, ResourceRef Resource, DateTimeOffset Now);

/// <summary>No grants — the default when break-glass is not configured.</summary>
public sealed class NullBreakGlassProvider : IBreakGlassProvider
{
    public static readonly NullBreakGlassProvider Instance = new();
    public BreakGlassGrant? ActiveGrantFor(HbmpRequestContext ctx) => null;
}
