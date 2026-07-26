namespace Mersal.Auth;

// Phase 14.2 — active-branch context plumbing (design 37 §2.2–2.3). These primitives are shared by every
// service so branch resolution + validation behave identically everywhere. The AUTHORITATIVE source of a
// user's assignments is admin-service (user_branch_assignment); this library only carries the value types,
// the pure resolution rules, and the per-request context contract. Enforcement (BranchScope ABAC) is layered
// on in 14.3. THE INVARIANT: never trust the X-Active-Branch header — always resolve it against the
// permitted set.

/// <summary>Header the client sends to select the working branch. Absent ⇒ resolve the user's Home branch.</summary>
public static class BranchHeaders
{
    public const string ActiveBranch = "X-Active-Branch";
}

public enum BranchAssignmentType { Home, Additional }

public enum BranchAssignmentStatus { Active, Revoked }

/// <summary>A single staff↔branch assignment, projected from admin-service for the pure resolution rules.</summary>
public sealed record BranchAssignment(
    Guid BranchId, BranchAssignmentType AssignmentType, DateOnly ValidFrom, DateOnly? ValidTo, BranchAssignmentStatus Status);

/// <summary>The resolved per-request branch context. BranchScoped roles are narrowed to <see cref="ActiveBranchId"/>;
/// MemberScoped roles set <see cref="IsBranchUnrestricted"/> (branch is a convenience filter, never a restriction).</summary>
public interface IBranchContext
{
    Guid? ActiveBranchId { get; }
    IReadOnlySet<Guid> PermittedBranchIds { get; }
    bool IsBranchUnrestricted { get; }
}

public sealed record BranchContext(
    Guid? ActiveBranchId, IReadOnlySet<Guid> PermittedBranchIds, bool IsBranchUnrestricted) : IBranchContext
{
    public static readonly BranchContext Unrestricted = new(null, new HashSet<Guid>(), true);
}

/// <summary>Pure resolution rules over a user's assignments (design 37 §2.2–2.3). No I/O — the service loads the
/// rows, these decide the permitted set and validate the requested active branch.</summary>
public static class BranchAssignmentRules
{
    /// <summary>An assignment counts only while Active and within its validity window (inclusive).</summary>
    public static bool IsEffective(BranchAssignment a, DateOnly on) =>
        a.Status == BranchAssignmentStatus.Active && a.ValidFrom <= on && (a.ValidTo is null || a.ValidTo >= on);

    /// <summary>Permitted set = Home ∪ Additional, filtered to effective assignments.</summary>
    public static IReadOnlySet<Guid> PermittedBranches(IEnumerable<BranchAssignment> assignments, DateOnly on) =>
        assignments.Where(a => IsEffective(a, on)).Select(a => a.BranchId).ToHashSet();

    /// <summary>The (single) effective Home branch, if any.</summary>
    public static Guid? HomeBranch(IEnumerable<BranchAssignment> assignments, DateOnly on) =>
        assignments.Where(a => a.AssignmentType == BranchAssignmentType.Home && IsEffective(a, on))
                   .Select(a => (Guid?)a.BranchId).FirstOrDefault();

    public enum ResolveOutcome { ResolvedHome, ResolvedRequested, DeniedNotPermitted, NoHome }

    public sealed record Resolution(ResolveOutcome Outcome, Guid? BranchId, IReadOnlySet<Guid> Permitted)
    {
        public bool Allowed => Outcome is ResolveOutcome.ResolvedHome or ResolveOutcome.ResolvedRequested;
    }

    /// <summary>Resolve the active branch. Requested (from the header) must be in the permitted set — otherwise
    /// DENIED (the caller returns 403 + audits BranchScopeDenied). No header ⇒ the Home branch.</summary>
    public static Resolution ResolveActiveBranch(IEnumerable<BranchAssignment> assignments, Guid? requested, DateOnly on)
    {
        var list = assignments as ICollection<BranchAssignment> ?? assignments.ToList();
        var permitted = PermittedBranches(list, on);
        if (requested is { } r)
            return permitted.Contains(r)
                ? new Resolution(ResolveOutcome.ResolvedRequested, r, permitted)
                : new Resolution(ResolveOutcome.DeniedNotPermitted, null, permitted);

        var home = HomeBranch(list, on);
        return home is null
            ? new Resolution(ResolveOutcome.NoHome, null, permitted)
            : new Resolution(ResolveOutcome.ResolvedHome, home, permitted);
    }
}
