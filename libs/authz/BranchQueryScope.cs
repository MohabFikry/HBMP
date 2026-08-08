using System.Linq.Expressions;
using Mersal.Auth;

namespace Mersal.Authz;

/// <summary>
/// 25.1 — the branch predicate for a QUERY, in one place, for all three reach modes.
///
/// <para><b>Why this exists.</b> Every branch-scoped read on the platform was written as:</para>
/// <code>if (branch.Context.ActiveBranchId is { } active) q = q.Where(x =&gt; x.BranchId == active);</code>
/// <para>which is correct for the two modes that existed when it was written and quietly WRONG for
/// <see cref="ScopeMode.BranchSetScoped"/>. A clinics manager has no single active branch when they have not
/// filtered, so that condition is false, so no predicate is applied at all — and a manager holding grants to
/// three clinics would read all six. The bug is invisible in exactly the way that matters: the supervisor
/// sees MORE, every screen works, and nothing errors.</para>
///
/// <para>The fix is not to add a third branch to nine call sites. It is to stop asking about
/// <c>ActiveBranchId</c> and start asking THIS, which knows all three modes and fails closed in each.</para>
///
/// <para><b>Fail-closed, the same rule as <see cref="RowScope.NoBranchSentinel"/>.</b> An empty branch
/// predicate does not mean "no branches", it means "every branch in the tenant". A caller whose reach did not
/// resolve is narrowed to the sentinel and matches nothing.</para>
/// </summary>
public static class BranchQueryScope
{
    /// <summary>
    /// The branch ids this caller may read, or null when the branch dimension does not restrict them at all
    /// (member-scoped and provider-scoped callers, for whom a branch filter is a convenience).
    ///
    /// <paramref name="requestedFilter"/> is the caller's OPTIONAL narrowing — a `?branchId=` on a
    /// member-scoped board, or a set-scoped manager's chosen clinic. It can only narrow; a filter outside the
    /// permitted set resolves to the sentinel rather than being ignored, because ignoring it would serve a
    /// wider dataset than the one that was asked for.
    /// </summary>
    public static IReadOnlySet<Guid>? PermittedFor(
        ScopeMode mode, IBranchContext branch, Guid? requestedFilter = null)
    {
        ArgumentNullException.ThrowIfNull(branch);

        switch (mode)
        {
            case ScopeMode.BranchScoped:
            {
                // One branch: the validated active one. Unresolved ⇒ sentinel ⇒ zero rows.
                var active = branch.ActiveBranchId ?? RowScope.NoBranchSentinel;
                return new HashSet<Guid> { active };
            }

            case ScopeMode.BranchSetScoped:
            {
                var permitted = branch.PermittedBranchIds;
                if (permitted.Count == 0) return new HashSet<Guid> { RowScope.NoBranchSentinel };

                // The manager's own filter (header) takes precedence over a query parameter — the header is
                // what the switcher sets and what the server already validated.
                var filter = branch.ActiveBranchId ?? requestedFilter;
                if (filter is { } f)
                    return new HashSet<Guid> { permitted.Contains(f) ? f : RowScope.NoBranchSentinel };

                return new HashSet<Guid>(permitted);
            }

            default:
                // Member-scoped / provider-scoped: branch is a convenience filter, never a restriction.
                return requestedFilter is { } r ? new HashSet<Guid> { r } : null;
        }
    }

    /// <summary>Compose the predicate onto a query. Null return from <see cref="PermittedFor"/> ⇒ the query is
    /// returned untouched, which is the only case in which no branch predicate is correct.</summary>
    public static IQueryable<T> ApplyBranchScope<T>(
        this IQueryable<T> query,
        Expression<Func<T, Guid?>> branchIdOf,
        ScopeMode mode,
        IBranchContext branch,
        Guid? requestedFilter = null)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(branchIdOf);

        var permitted = PermittedFor(mode, branch, requestedFilter);
        if (permitted is null) return query;

        // Built as `permitted.Contains(x.BranchId.Value)` rather than an OR-chain so it translates to a single
        // `branch_id = ANY(...)` and stays one index scan at six branches or sixty.
        var ids = permitted.ToList();
        var param = branchIdOf.Parameters[0];
        var body = Expression.AndAlso(
            Expression.NotEqual(branchIdOf.Body, Expression.Constant(null, typeof(Guid?))),
            Expression.Call(
                Expression.Constant(ids),
                typeof(List<Guid>).GetMethod(nameof(List<Guid>.Contains))!,
                Expression.Property(branchIdOf.Body, nameof(Nullable<Guid>.Value))));

        return query.Where(Expression.Lambda<Func<T, bool>>(body, param));
    }
}
