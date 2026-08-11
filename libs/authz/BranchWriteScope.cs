using Mersal.Auth;
using Microsoft.AspNetCore.Http;

namespace Mersal.Authz;

/// <summary>
/// The branch predicate for a WRITE, in one place, for all three reach modes — the counterpart to
/// <see cref="BranchQueryScope"/>.
///
/// <para><b>Why this exists.</b> <see cref="BranchQueryScope"/> was written because every branch-scoped READ
/// on the platform asked <c>ActiveBranchId ==</c>, which is correct for the two modes that existed at the time
/// and quietly wrong for <see cref="ScopeMode.BranchSetScoped"/>. The WRITE path was never migrated. It still
/// read:</para>
/// <code>if (branch.Context.ActiveBranchId is not { } active) return (requested, null);</code>
/// <para>A set-scoped caller who has not filtered has no active branch — that is the whole point of the mode,
/// and <see cref="BranchScopeResolver"/> sets it deliberately so a supervisory worklist spans every clinic.
/// So the guard fell through and returned the branch id off the caller's own request body, never tested
/// against <see cref="IBranchContext.PermittedBranchIds"/>. A clinics manager granted two clinics could close,
/// materialize slots into, book, check in and cancel at all six.</para>
///
/// <para>The failure was invisible in exactly the way that matters: nothing errors, every screen works, and
/// the supervisor sees and does MORE. That is the same shape as the read-path bug, and it has the same fix —
/// stop asking about <c>ActiveBranchId</c> and ask this, which knows all three modes and fails closed in
/// each.</para>
///
/// <para><b>Fail-closed, the same rule as <see cref="RowScope.NoBranchSentinel"/>.</b> An unresolved reach is
/// "no branches", never "every branch": an empty permitted set writes nowhere.</para>
/// </summary>
public static class BranchWriteScope
{
    /// <summary>The caller holds the authority and pointed it at a clinic they do not run. Shared with the
    /// read path so a client sees one problem type for one kind of refusal.</summary>
    public const string ProblemType = "urn:hbmp:branch-scope-denied";

    /// <summary>A set-scoped caller must name the clinic they are writing to. Distinct from a refusal: the
    /// request is not forbidden, it is incomplete.</summary>
    public const string TargetRequiredProblemType = "urn:hbmp:branch-target-required";

    /// <summary>
    /// The branch a NEW record is being written into: the caller's request, validated against their reach.
    /// Returns the branch to persist, or a problem result to return unchanged.
    /// </summary>
    /// <param name="requested">The branch named in the request body, if any. Never trusted.</param>
    public static (Guid? Branch, IResult? Denied) ResolveTarget(
        ScopeMode mode, IBranchContext branch, Guid? requested)
    {
        ArgumentNullException.ThrowIfNull(branch);

        switch (mode)
        {
            case ScopeMode.BranchScoped:
            {
                // One branch: the validated active one. A request naming another is refused rather than
                // silently rewritten — silently moving a write to a different branch is the surprise design
                // 37 §3 forbids, and it is worse than an error because nobody learns it happened.
                if (branch.ActiveBranchId is not { } active)
                    return (null, Denied("your active branch could not be resolved, so this write has no target"));
                if (requested is { } r && r != active)
                    return (null, Denied("you can only write to your active branch"));
                return (active, null);
            }

            case ScopeMode.BranchSetScoped:
            {
                // A filter, when set, narrows a write exactly as an active branch does: what is on screen and
                // what is written must be the same clinic.
                if (branch.ActiveBranchId is { } filter)
                {
                    if (requested is { } r && r != filter)
                        return (null, Denied("you can only write to the clinic you have filtered to"));
                    return branch.PermittedBranchIds.Contains(filter)
                        ? (filter, null)
                        : (null, Denied("that clinic is not one you run"));
                }

                // Unfiltered. The target must be named and must be in reach.
                //
                // Refusing an unnamed target is the fail-closed reading, and not merely symmetry with the
                // coordinator: a supervisor's write with no branch could plausibly mean "all six clinics", and
                // a request that would close six clinics has to say so.
                if (requested is not { } target)
                    return (null, Results.Problem(
                        statusCode: 400, title: "branch-target-required", type: TargetRequiredProblemType,
                        detail: "You supervise several clinics, so this change must name the one it applies to."));

                return branch.PermittedBranchIds.Contains(target)
                    ? (target, null)
                    : (null, Denied("that clinic is not one you run"));
            }

            default:
                // Member-scoped / provider-scoped: the branch dimension does not restrict them, and never did.
                return (requested, null);
        }
    }

    /// <summary>
    /// Refuse a write against an EXISTING row owned by <paramref name="owning"/>. Returns null when allowed.
    ///
    /// <para>A branchless row returns null: pre-branch and external-provider records carry no branch, and
    /// turning "this record predates branch scoping" into a permission error is a different and misleading
    /// answer. The endpoint's own 404/409 handles it.</para>
    /// </summary>
    public static IResult? RefuseUnlessWritable(ScopeMode mode, IBranchContext branch, Guid? owning)
    {
        ArgumentNullException.ThrowIfNull(branch);
        if (owning is not { } rowBranch) return null;

        return mode switch
        {
            ScopeMode.BranchScoped =>
                branch.ActiveBranchId == rowBranch ? null : Denied("this record is not in your active branch"),

            // The filter narrows, the grants bound. Both apply, in that order.
            ScopeMode.BranchSetScoped =>
                branch.PermittedBranchIds.Contains(rowBranch)
                && (branch.ActiveBranchId is not { } f || f == rowBranch)
                    ? null
                    : Denied("this record is not at a clinic you run"),

            _ => null,
        };
    }

    private static IResult Denied(string detail) =>
        Results.Problem(statusCode: 403, title: "branch-scope-denied", type: ProblemType, detail: detail);
}
