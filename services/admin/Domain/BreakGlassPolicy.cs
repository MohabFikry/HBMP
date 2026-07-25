namespace Mersal.Admin.Domain;

/// <summary>
/// Pure break-glass decision rules (18-security-model §11), independent of storage. Enforces dual control
/// (approver ≠ requester — no self-approval), the scope check (an access is widened only for a scoped resource
/// type/id — no field-deny bypass beyond scope), and the activation window (a grant is only live while Active,
/// step-up satisfied, and within [NotBefore, ExpiresAt)).
/// </summary>
public static class BreakGlassPolicy
{
    /// <summary>May <paramref name="approver"/> approve this grant? False if they are the requester (dual control).</summary>
    public static bool CanApprove(BreakGlassGrantRecord grant, string approver) =>
        !string.Equals(grant.RequesterUserId, approver, StringComparison.Ordinal);

    /// <summary>Is a request to access <paramref name="resourceType"/>/<paramref name="resourceId"/> within the
    /// grant's explicit scope? Empty scoped-types ⇒ nothing widened (fail-closed); empty scoped-ids ⇒ any id of a
    /// scoped type.</summary>
    public static bool InScope(IReadOnlyCollection<string> scopedTypes, IReadOnlyCollection<string> scopedIds,
        string resourceType, string? resourceId)
    {
        if (scopedTypes.Count == 0) return false;
        if (!scopedTypes.Contains(resourceType, StringComparer.Ordinal)) return false;
        return scopedIds.Count == 0 || (resourceId is not null && scopedIds.Contains(resourceId, StringComparer.Ordinal));
    }

    /// <summary>The activation window for an approved grant that just passed step-up.</summary>
    public static (DateTimeOffset NotBefore, DateTimeOffset ExpiresAt) Window(DateTimeOffset now, int windowMinutes) =>
        (now, now.AddMinutes(Math.Clamp(windowMinutes, 1, 240)));
}
