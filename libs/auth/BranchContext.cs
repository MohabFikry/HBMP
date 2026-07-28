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

    public enum ResolveOutcome
    {
        ResolvedHome,
        ResolvedRequested,
        DeniedNotPermitted,
        NoHome,

        /// <summary>21.3 — a stale PERSISTED PREFERENCE was skipped and the caller fell through to a branch
        /// they can actually reach. Not a failure: the request proceeds, and the caller is told which branch
        /// it ran under so the UI can correct its switcher silently.</summary>
        ResolvedAfterStalePreference,

        /// <summary>21.3 — no home branch, but the caller has reach somewhere; step ④ picked the first
        /// accessible branch in a stable order.</summary>
        ResolvedFirstAccessible,
    }

    public sealed record Resolution(ResolveOutcome Outcome, Guid? BranchId, IReadOnlySet<Guid> Permitted)
    {
        /// <summary>Whether the request may proceed. 21.3 added the two fall-through outcomes: a stale
        /// preference and a home-less caller both still resolve to a branch they can actually reach, so
        /// leaving them out here would turn "your cookie is out of date" into a failed request.</summary>
        public bool Allowed => Outcome is ResolveOutcome.ResolvedHome or ResolveOutcome.ResolvedRequested
            or ResolveOutcome.ResolvedAfterStalePreference or ResolveOutcome.ResolvedFirstAccessible;

        /// <summary>21.3 — the resolved branch differs from what the client believed. The caller surfaces
        /// this to the SPA (a response header) so the switcher corrects itself silently.</summary>
        public bool PreferenceWasStale => Outcome is ResolveOutcome.ResolvedAfterStalePreference;
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

    /// <summary>
    /// 21.3 — the full active-branch precedence chain (design 40 §3):
    ///
    ///   ① explicit <c>X-Active-Branch</c> header  ② persisted user preference
    ///   ③ the membership's home branch           ④ first accessible, in a stable order
    ///
    /// DUAL FAILURE SEMANTICS, and the distinction is the whole point. An explicit HEADER outside the grant
    /// set is DENIED (403 + audit): a programmatic caller asked for a specific dataset, and silently serving
    /// it a different one is how a batch job writes to the wrong branch. A stale PREFERENCE is SKIPPED and
    /// the chain falls through: a remembered UI selection is a convenience, and expiring someone's October
    /// cover should not lock them out of their own session on the 1st of November.
    /// </summary>
    /// <param name="assignments">The caller's grants (expired ones simply stop matching).</param>
    /// <param name="requested">The header value, if the client sent one.</param>
    /// <param name="preference">The persisted soft preference, if any.</param>
    /// <param name="on">The date reach is judged on.</param>
    public static Resolution ResolveActiveBranch(
        IEnumerable<BranchAssignment> assignments, Guid? requested, Guid? preference, DateOnly on)
    {
        var list = assignments as ICollection<BranchAssignment> ?? assignments.ToList();
        var permitted = PermittedBranches(list, on);

        // ① The header is an assertion, not a hint. Out of scope ⇒ refuse.
        if (requested is { } r)
            return permitted.Contains(r)
                ? new Resolution(ResolveOutcome.ResolvedRequested, r, permitted)
                : new Resolution(ResolveOutcome.DeniedNotPermitted, null, permitted);

        // ② The preference is a hint. Out of scope ⇒ skip it and keep going, but REMEMBER that we did, so
        // the caller can tell the UI to update its switcher instead of leaving a dead selection on screen.
        var stale = preference is { } p && !permitted.Contains(p);
        if (preference is { } pref && permitted.Contains(pref))
            return new Resolution(ResolveOutcome.ResolvedRequested, pref, permitted);

        // ③ Home.
        if (HomeBranch(list, on) is { } home)
            return new Resolution(
                stale ? ResolveOutcome.ResolvedAfterStalePreference : ResolveOutcome.ResolvedHome, home, permitted);

        // ④ First accessible, ordered so the same person lands on the same branch every time — an unstable
        // order here would silently move someone between branches between requests.
        var first = permitted.OrderBy(b => b).Cast<Guid?>().FirstOrDefault();
        if (first is { } f)
            return new Resolution(
                stale ? ResolveOutcome.ResolvedAfterStalePreference : ResolveOutcome.ResolvedFirstAccessible, f, permitted);

        // Nothing is reachable. The caller injects the sentinel — never an empty predicate.
        return new Resolution(ResolveOutcome.NoHome, null, permitted);
    }
}
