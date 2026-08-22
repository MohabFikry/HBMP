using FluentAssertions;

namespace Mersal.Architecture.Tests;

/// <summary>
/// A service that resolves the branch scope must carry BOTH halves of it onto the request state.
/// </summary>
/// <remarks>
/// <para><b>The defect this exists to prevent, which had already happened in all four services.</b>
/// <c>BranchScopeResolver.ResolveAsync</c> returns a <c>BranchScopeState</c> carrying a
/// <c>Context</c> (which branches, if any) and a <c>Mode</c> (what kind of reach the caller has). Every
/// service's middleware copied the Context onto the scoped state and dropped the Mode.</para>
///
/// <para>Dropping it is not a missing feature; it is a silent grant. <c>BranchScopeState.Mode</c> defaults to
/// <c>ScopeMode.MemberScoped</c> so that it agrees with <c>BranchContext.Unrestricted</c> — and MemberScoped
/// is the one value meaning "the branch dimension does not restrict this caller". So every consumer reading
/// <c>branch.Mode</c> was told the caller was unrestricted regardless of who they were:
/// <c>BranchWriteScope.ResolveTarget</c> fell to its default arm and returned the branch id off the request
/// body without testing it against the permitted set, and <c>RefuseUnlessWritable</c> returned null for every
/// row. That is precisely the hole <c>BranchWriteScope</c> was written to close, reopened one layer above it.
/// A branch-scoped coordinator could read and edit another clinic's weekly pattern.</para>
///
/// <para><b>Why it survived.</b> The endpoints that re-derive the mode from the principal through their own
/// private <c>BranchModeOf</c> helper were unaffected — the copies were right, and the shared field meant to
/// replace them was empty. So the surfaces anyone would think to test behaved correctly, and only the ones
/// that had adopted the newer, better-documented seam were open.</para>
///
/// <para><b>The rule.</b> A file that assigns <c>BranchScopeState</c>'s <c>Context</c> must also assign its
/// <c>Mode</c>. Textual and deliberately crude: the failure is a line that was never written, and a rule that
/// asks whether a line exists is the shape that catches it.</para>
/// </remarks>
public class BranchScopeModeCarriedTests
{
    [Fact]
    public void Every_service_that_resolves_a_branch_scope_carries_its_mode_too()
    {
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(
                     Path.Combine(RepoRoot(), "services"), "Program.cs", SearchOption.AllDirectories))
        {
            var src = File.ReadAllText(file);
            if (!src.Contains("BranchScopeResolver.ResolveAsync", StringComparison.Ordinal)) continue;

            // Both assignments, in whatever form: `state.Context` straight onto the service, or via a local.
            var carriesContext = src.Contains(".Context = state.Context", StringComparison.Ordinal);
            var carriesMode = src.Contains(".Mode = state.Mode", StringComparison.Ordinal);

            if (carriesContext && !carriesMode)
                offenders.Add(Path.GetRelativePath(RepoRoot(), file).Replace('\\', '/'));
        }

        offenders.Should().BeEmpty(
            "a resolved branch scope whose Mode is dropped defaults to MemberScoped, which every branch guard "
            + "reads as 'this caller is not branch-restricted' — assign scope.Mode = state.Mode beside the Context");
    }

    /// <summary>The rule is worth nothing if no service is being read. Pins that the scan finds them.</summary>
    [Fact]
    public void The_scan_actually_reaches_the_services_that_resolve_a_branch_scope()
    {
        var resolving = Directory.EnumerateFiles(
                Path.Combine(RepoRoot(), "services"), "Program.cs", SearchOption.AllDirectories)
            .Count(f => File.ReadAllText(f).Contains("BranchScopeResolver.ResolveAsync", StringComparison.Ordinal));

        resolving.Should().BeGreaterThanOrEqualTo(4,
            "emr, inventory, orders and provider all resolve a branch scope; a scan finding fewer is reading "
            + "the wrong tree and would pass on an empty set");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "HbmpPlatform.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}
