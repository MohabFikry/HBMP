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

        if (BranchScopeModes.ModeFor(principal) != ScopeMode.BranchScoped)
            return new BranchScopeState { Context = BranchContext.Unrestricted };

        var pb = await directory.GetAsync(principal, ct);
        Guid? requested = Guid.TryParse(activeBranchHeader, out var h) ? h : null;
        var active = requested ?? pb.Home;

        // A requested (or defaulted) branch outside the permitted set is denied — never silently widened.
        if (active is { } a && !pb.Permitted.Contains(a))
            return new BranchScopeState { Denied = true };

        return new BranchScopeState { Context = new BranchContext(active, pb.Permitted, IsBranchUnrestricted: false) };
    }
}
