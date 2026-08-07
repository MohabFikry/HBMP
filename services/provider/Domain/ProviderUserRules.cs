namespace Mersal.Provider.Domain;

public enum ProviderUserStatus { Active, Revoked }

/// <summary>A provider-scoped user account. Stamped with exactly one provider_id at creation and may only
/// ever hold provider-scoped roles for THAT provider (FR-NET-003). Suspending/terminating the provider
/// revokes every one of these (FR-IAM-010).</summary>
public sealed class ProviderUser
{
    public Guid UserId { get; set; }
    public Guid ProviderId { get; set; }
    public string TenantId { get; set; } = default!;
    public string SubjectRef { get; set; } = default!;   // identity-service subject (Keycloak sub)
    public string Role { get; set; } = default!;
    public ProviderUserStatus Status { get; set; } = ProviderUserStatus.Active;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

/// <summary>Provisioning authorization + separation-of-duties for provider users (FR-NET-003, §7 SoD).
/// Pure so the rules are unit-tested without identity-service. The invariant: a provider user gets ONLY
/// provider-scoped roles for their own org; a Provider Admin cannot self-grant admin or any clinical role;
/// the Network Team cannot mint a provider financial-release role.</summary>
public static class ProviderUserRules
{
    /// <summary>Every role that may ever be attached to a provider-scoped account.</summary>
    public static readonly IReadOnlySet<string> ProviderScopedRoles =
        new HashSet<string>(StringComparer.Ordinal) { "provider_admin", "lab_tech", "imaging_tech", "radiology_tech", "pharmacist" };

    /// <summary>Clinical roles are never provisioned through the provider onboarding path.</summary>
    public static readonly IReadOnlySet<string> ClinicalRoles =
        new HashSet<string>(StringComparer.Ordinal) { "doctor", "nurse", "medical_approval", "medical_director" };

    public static bool IsProviderScopedRole(string role) => ProviderScopedRoles.Contains(role);

    /// <summary>May an actor holding <paramref name="actorRoles"/> provision <paramref name="requestedRole"/>?
    /// Network Team may create the Provider Admin + tech roles; a Provider Admin may create only tech roles
    /// (no self-elevation to admin, no clinical, no financial-release). Anything else is denied.</summary>
    public static TransitionResult CanProvision(IEnumerable<string> actorRoles, string requestedRole)
    {
        var roles = new HashSet<string>(actorRoles, StringComparer.Ordinal);

        if (ClinicalRoles.Contains(requestedRole))
            return TransitionResult.Blocked($"Clinical role '{requestedRole}' cannot be granted through provider onboarding.");
        if (!IsProviderScopedRole(requestedRole))
            return TransitionResult.Blocked($"Role '{requestedRole}' is not a provider-scoped role.");

        if (roles.Contains("network_team"))
            return TransitionResult.Ok;   // may grant admin + tech, but ClinicalRoles already excluded above

        if (roles.Contains("provider_admin"))
            return requestedRole == "provider_admin"
                ? TransitionResult.Blocked("A Provider Admin cannot grant the Provider Admin role (no self-elevation).")
                : TransitionResult.Ok;

        return TransitionResult.Blocked("Only the Network Team or a Provider Admin may provision provider users.");
    }
}
