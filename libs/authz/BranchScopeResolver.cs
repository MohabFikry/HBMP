using Mersal.Auth;

namespace Mersal.Authz;

/// <summary>A user's branch entitlements, resolved from the authoritative source (admin-service). Home is the
/// default active branch when no X-Active-Branch header is sent.</summary>
public sealed record PermittedBranches(Guid? Home, IReadOnlySet<Guid> Permitted)
{
    public static readonly PermittedBranches None = new(null, new HashSet<Guid>());
}

/// <summary>Seam a service implements to fetch a caller's permitted branch set (design 37 §2.3 — the
/// permitted set lives in admin-service; each service resolves it per request, cached briefly). Tests supply
/// a fake; production wires an HTTP-backed implementation forwarding the caller's bearer token.</summary>
public interface IBranchDirectory
{
    Task<PermittedBranches> GetAsync(HbmpPrincipal principal, CancellationToken ct = default);
}

/// <summary>Per-request holder for the resolved branch context, populated by middleware and read by handlers.</summary>
public sealed class BranchScopeState
{
    public IBranchContext Context { get; set; } = BranchContext.Unrestricted;
    /// <summary>True when a BranchScoped caller supplied an X-Active-Branch outside their permitted set →
    /// the request must be rejected 403 + audited BranchScopeDenied (THE INVARIANT: never trust the header).</summary>
    public bool Denied { get; set; }

    /// <summary>
    /// The caller's reach mode, carried rather than re-derived.
    ///
    /// <para><see cref="BranchScopeResolver.ResolveAsync"/> computes this to decide what to put in
    /// <see cref="Context"/>, and used to discard it — so every consumer re-derived it from the principal
    /// through its own private <c>BranchModeOf</c> helper (there were three copies in emr alone). That is
    /// tolerable for a read, which passes the mode explicitly to <see cref="BranchQueryScope"/>. It is not
    /// tolerable for a write: <see cref="BranchWriteScope"/> has to know the mode, and a guard whose
    /// correctness depends on each of eleven call sites remembering to look it up is a guard that will be
    /// wrong at one of them.</para>
    ///
    /// <para>Defaults to <see cref="ScopeMode.MemberScoped"/> to match <see cref="BranchContext.Unrestricted"/>
    /// — the two halves of the default state agree.</para>
    /// </summary>
    public ScopeMode Mode { get; set; } = ScopeMode.MemberScoped;
}

/// <summary>Resolves the active-branch context for a request (design 37 §3). MemberScoped/ProviderScoped
/// callers are branch-unrestricted; a BranchScoped caller is narrowed to a validated active branch (the
/// header if permitted, else Home). An out-of-set header is denied — never trusted.</summary>
public static class BranchScopeResolver
{
    public static async Task<BranchScopeState> ResolveAsync(
        HbmpPrincipal principal, string? activeBranchHeader, IBranchDirectory directory, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(directory);

        var mode = BranchScopeModes.ModeFor(principal);
        if (!BranchScopeModes.IsBranchRestricted(mode))
            return new BranchScopeState { Context = BranchContext.Unrestricted, Mode = mode };

        var pb = await directory.GetAsync(principal, ct);
        Guid? requested = Guid.TryParse(activeBranchHeader, out var h) ? h : null;

        // 25.1 — SET reach (design 42 §1). The context carries the whole permitted set and, if the caller sent
        // one, a filter. Falling back to Home here would be wrong: a manager who sends no header is asking for
        // all six clinics, not for their home one, and defaulting them to a single branch is how a supervisory
        // worklist silently shows a sixth of its rows.
        //
        // The header is still an ASSERTION even though it only filters, so an out-of-reach value is DENIED
        // rather than ignored (doc 40 §0 A2: nothing security-relevant is silent). A caller asking for a
        // branch they cannot reach has a bug or is probing; serving them a different dataset hides both.
        if (mode == ScopeMode.BranchSetScoped)
        {
            if (requested is { } filter && !pb.Permitted.Contains(filter))
                return new BranchScopeState { Denied = true, Mode = mode };
            return new BranchScopeState
            {
                Context = new BranchContext(requested, pb.Permitted, IsBranchUnrestricted: false), Mode = mode,
            };
        }

        var active = requested ?? pb.Home;

        // A requested (or defaulted) branch outside the permitted set is denied — never silently widened.
        if (active is { } a && !pb.Permitted.Contains(a))
            return new BranchScopeState { Denied = true, Mode = mode };

        return new BranchScopeState
        {
            Context = new BranchContext(active, pb.Permitted, IsBranchUnrestricted: false), Mode = mode,
        };
    }
}
