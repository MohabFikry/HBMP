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

    /// <summary>
    /// 25.1 (design 42 §1) — reach over a SET of branches simultaneously: the predicate is
    /// <c>branch_id ∈ PermittedBranchIds</c> rather than <c>= ActiveBranchId</c>. The clinics manager
    /// supervises all six clinics at once, and neither existing mode expresses that:
    ///
    ///   • <see cref="BranchScoped"/> makes them switch branches one at a time. A licence-alert worklist
    ///     showing one sixth of the alerts is not a supervisory tool.
    ///   • <see cref="MemberScoped"/> is unrestricted — an ungoverned "everything" with no grant behind it.
    ///     Reach that no assignment produced cannot be reviewed, revoked, or explained.
    ///
    /// The set still comes from real <c>user_branch_assignment</c> rows: reach stays GRANT-derived, never
    /// role-derived. An unresolvable set fails closed on the sentinel exactly as BranchScoped does — it
    /// matches zero rows, never all of them. Here the active branch is an optional FILTER (narrow to one
    /// clinic) rather than a restriction, which is what lets ONE branch control serve both roles: it
    /// switches for a coordinator and filters for a manager.
    /// </summary>
    BranchSetScoped,
}

/// <summary>Classifies a principal into a <see cref="ScopeMode"/> from its roles (design 37 §3 table). This is
/// the reusable primitive the services consult to decide whether to apply a branch predicate: BranchScoped
/// operational roles are narrowed to the active branch; approvals/managers/finance/etc. are member-scoped
/// (all branches); external providers are provider-scoped and untouched by the branch dimension.</summary>
public static class BranchScopeModes
{
    /// <summary>
    /// Operational roles whose worklists are narrowed to the active branch (37 §3).
    ///
    /// 25.1 — <c>branch_manager</c> and <c>clinic_manager</c> used to sit here. Both were PHANTOMS: named in
    /// this set and in the SPA's mirror of it, never seeded as identity roles, never held by any principal.
    /// Two spellings of one idea that was never built, which is how the next reader concludes both exist.
    /// They are replaced by the one seeded spelling — <c>branch_coordinator</c>, who runs a single clinic —
    /// while the supervisor of all six is <c>clinics_manager</c> and belongs in
    /// <see cref="BranchSetScopedRoles"/>, not here.
    /// </summary>
    public static readonly IReadOnlySet<string> BranchScopedRoles =
        new HashSet<string>(StringComparer.Ordinal) { "reception", "appointment_coordinator", "nurse", "doctor", "branch_coordinator" };

    /// <summary>
    /// 25.1 (design 42 §1) — roles that reach a SET of branches at once. The clinics manager supervises all
    /// six clinics, and their permitted set comes from real branch assignments like everyone else's.
    ///
    /// This role must NOT be allowed to fall through to <see cref="ScopeMode.MemberScoped"/>. That is the
    /// failure this set exists to prevent: MemberScoped is unrestricted, so omitting the role here would not
    /// break anything visibly — it would silently hand a clinic supervisor tenant-wide reach that no grant
    /// authorised and no review would ever surface.
    /// </summary>
    public static readonly IReadOnlySet<string> BranchSetScopedRoles =
        new HashSet<string>(StringComparer.Ordinal) { "clinics_manager" };

    /// <summary>External contracted providers — scoped by provider-ownership, never by branch (37 §3).</summary>
    public static readonly IReadOnlySet<string> ProviderScopedRoles =
        new HashSet<string>(StringComparer.Ordinal) { "provider_admin", "lab_tech", "imaging_tech", "radiology_tech", "pharmacist" };

    /// <summary>The caller's effective mode. Provider-scoped wins (external actors are never branch-scoped);
    /// then BranchSetScoped, then BranchScoped; everyone else (approvals, medical director, case managers,
    /// finance, claims, network, admins, reporting) ⇒ MemberScoped (all branches).
    ///
    /// SET BEFORE SINGLE, deliberately: someone holding both <c>branch_coordinator</c> and
    /// <c>clinics_manager</c> supervises the network, and narrowing them to one branch would make the wider,
    /// explicitly-granted authority the weaker one. Both modes are governed by the same assignment rows, so
    /// preferring the set widens reach only as far as the grants already allow.
    ///
    /// NOTE (25.1): mode is still derived from ROLE NAMES. Phase 21 moves reach to grants — this method is
    /// the seam that change lands on, and it is kept deliberately small for that reason.</summary>
    public static ScopeMode ModeFor(HbmpPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (ProviderScopedRoles.Any(principal.IsInRole)) return ScopeMode.ProviderScoped;
        if (BranchSetScopedRoles.Any(principal.IsInRole)) return ScopeMode.BranchSetScoped;
        if (BranchScopedRoles.Any(principal.IsInRole)) return ScopeMode.BranchScoped;
        return ScopeMode.MemberScoped;
    }

    /// <summary>True when the mode carries a branch predicate at all (single OR set). The call sites that ask
    /// "is this caller branch-restricted?" must treat both as yes; asking <c>== BranchScoped</c> is the bug
    /// that would leave a set-scoped caller unrestricted.</summary>
    public static bool IsBranchRestricted(ScopeMode mode) =>
        mode is ScopeMode.BranchScoped or ScopeMode.BranchSetScoped;
}
