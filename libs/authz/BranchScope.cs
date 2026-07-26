using Mersal.Auth;

namespace Mersal.Authz;

/// <summary>How a caller relates to the branch dimension (phase 14, design 37 §3).</summary>
public enum ScopeMode
{
    /// <summary>Operational roles — worklists/queues/branch-originated rows are narrowed to the active branch.</summary>
    BranchScoped,
    /// <summary>Member/beneficiary-centred roles — span all branches; a branch filter is a convenience only.</summary>
    MemberScoped,
    /// <summary>External contracted providers — scoped by provider-ownership; the branch dimension does not apply.</summary>
    ProviderScoped,
}

/// <summary>Classifies a principal into a <see cref="ScopeMode"/> from its roles (design 37 §3 table). This is
/// the reusable primitive the services consult to decide whether to apply a branch predicate: BranchScoped
/// operational roles are narrowed to the active branch; approvals/managers/finance/etc. are member-scoped
/// (all branches); external providers are provider-scoped and untouched by the branch dimension.</summary>
public static class BranchScopeModes
{
    /// <summary>Operational roles whose worklists are narrowed to the active branch (37 §3).</summary>
    public static readonly IReadOnlySet<string> BranchScopedRoles =
        new HashSet<string>(StringComparer.Ordinal) { "reception", "appointment_coordinator", "nurse", "doctor", "branch_manager", "clinic_manager" };

    /// <summary>External contracted providers — scoped by provider-ownership, never by branch (37 §3).</summary>
    public static readonly IReadOnlySet<string> ProviderScopedRoles =
        new HashSet<string>(StringComparer.Ordinal) { "provider_admin", "lab_tech", "imaging_tech", "pharmacist" };

    /// <summary>The caller's effective mode. Provider-scoped wins (external actors are never branch-scoped);
    /// otherwise any operational role ⇒ BranchScoped; everyone else (approvals, medical director, case managers,
    /// finance, claims, network, admins, reporting) ⇒ MemberScoped (all branches).</summary>
    public static ScopeMode ModeFor(HbmpPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (ProviderScopedRoles.Any(principal.IsInRole)) return ScopeMode.ProviderScoped;
        if (BranchScopedRoles.Any(principal.IsInRole)) return ScopeMode.BranchScoped;
        return ScopeMode.MemberScoped;
    }
}
