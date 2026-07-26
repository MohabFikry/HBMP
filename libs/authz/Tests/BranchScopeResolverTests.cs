using FluentAssertions;
using Mersal.Auth;
using Mersal.Authz;

namespace Mersal.Authz.Tests;

/// <summary>Phase 14.4 — per-request active-branch resolution (design 37 §3). Member/provider-scoped callers
/// are branch-unrestricted; a BranchScoped caller is narrowed to a validated active branch (header if
/// permitted, else Home); an out-of-set header is denied (never trusted).</summary>
public class BranchScopeResolverTests
{
    private static readonly Guid Maadi = Guid.NewGuid();
    private static readonly Guid Dokki = Guid.NewGuid();
    private static readonly Guid Aswan = Guid.NewGuid();

    private sealed class FakeDirectory(PermittedBranches pb) : IBranchDirectory
    {
        public Task<PermittedBranches> GetAsync(HbmpPrincipal principal, CancellationToken ct = default) => Task.FromResult(pb);
    }

    private static HbmpPrincipal Principal(string role) =>
        new() { Subject = "u-1", Roles = new HashSet<string> { role }, Scopes = new HashSet<string>(), TenantId = "t0", MfaSatisfied = true };

    private static IBranchDirectory Dir() => new FakeDirectory(new PermittedBranches(Maadi, new HashSet<Guid> { Maadi, Dokki }));

    [Fact]
    public async Task Member_scoped_caller_is_branch_unrestricted()
    {
        var s = await BranchScopeResolver.ResolveAsync(Principal("medical_approval"), Aswan.ToString(), Dir());
        s.Denied.Should().BeFalse();
        s.Context.IsBranchUnrestricted.Should().BeTrue();
        s.Context.ActiveBranchId.Should().BeNull();
    }

    [Fact]
    public async Task Branch_scoped_caller_defaults_to_home_when_no_header()
    {
        var s = await BranchScopeResolver.ResolveAsync(Principal("reception"), activeBranchHeader: null, Dir());
        s.Denied.Should().BeFalse();
        s.Context.ActiveBranchId.Should().Be(Maadi);
        s.Context.PermittedBranchIds.Should().BeEquivalentTo([Maadi, Dokki]);
    }

    [Fact]
    public async Task Branch_scoped_caller_accepts_a_permitted_header()
    {
        var s = await BranchScopeResolver.ResolveAsync(Principal("nurse"), Dokki.ToString(), Dir());
        s.Denied.Should().BeFalse();
        s.Context.ActiveBranchId.Should().Be(Dokki);
    }

    [Fact]
    public async Task Branch_scoped_caller_with_an_out_of_set_header_is_denied()
    {
        var s = await BranchScopeResolver.ResolveAsync(Principal("doctor"), Aswan.ToString(), Dir());
        s.Denied.Should().BeTrue();
    }
}
