using FluentAssertions;
using Mersal.Authz;

namespace Mersal.Authz.Tests;

/// <summary>
/// 21.2 — the authority algebra (design 40 §2, invariant 5).
///
///     effective = (role grants ∪ membership allows) − membership denies
///
/// These pin the rules that decide who can do what, so they are written as claims about outcomes rather
/// than about implementation: deny beats allow no matter the order, expiry is judged at resolution time,
/// deprecation never revokes, and the platform-admin flag reaches administration keys and NOTHING else.
/// </summary>
public class EffectiveSetEvaluatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 28, 12, 0, 0, TimeSpan.Zero);

    private static IReadOnlyDictionary<string, CatalogKey> Catalog(params CatalogKey[] keys) =>
        keys.ToDictionary(k => k.Key, StringComparer.Ordinal);

    private static readonly IReadOnlyDictionary<string, CatalogKey> Standard = Catalog(
        new CatalogKey("orders:read"),
        new CatalogKey("orders:consume"),
        new CatalogKey("emr:read"),
        new CatalogKey("admin:read", IsPlatformAdminKey: true),
        new CatalogKey("admin:write", IsPlatformAdminKey: true),
        new CatalogKey("legacy:all", Deprecated: true, ReplacedBy: "orders:read"));

    private static EffectiveSet Compute(
        IEnumerable<string> roles, IEnumerable<OverrideEntry>? overrides = null,
        bool platformAdmin = false, IReadOnlyDictionary<string, CatalogKey>? catalog = null) =>
        EffectiveSetEvaluator.Compute(
            new MembershipSnapshot([.. roles], [.. overrides ?? []], platformAdmin), catalog ?? Standard, Now);

    // ---- The algebra ------------------------------------------------------------------------------------

    [Fact]
    public void Role_grants_alone_are_the_effective_set()
    {
        Compute(["orders:read", "emr:read"]).Keys
            .Should().BeEquivalentTo("orders:read", "emr:read");
    }

    [Fact]
    public void An_allow_override_adds_a_key_the_roles_do_not_carry()
    {
        Compute(["orders:read"], [new OverrideEntry("emr:read", Deny: false)]).Keys
            .Should().BeEquivalentTo("orders:read", "emr:read");
    }

    [Fact]
    public void A_deny_override_removes_a_key_the_roles_do_carry()
    {
        Compute(["orders:read", "emr:read"], [new OverrideEntry("emr:read", Deny: true)]).Keys
            .Should().BeEquivalentTo("orders:read");
    }

    [Fact]
    public void Deny_beats_allow_on_the_same_key_regardless_of_order()
    {
        // Both orderings, because "deny wins" implemented as a sequential apply is order-sensitive and
        // would pass a test that only ever presented the denies last.
        var denyFirst = Compute(["orders:read"],
            [new OverrideEntry("emr:read", Deny: true), new OverrideEntry("emr:read", Deny: false)]);
        var allowFirst = Compute(["orders:read"],
            [new OverrideEntry("emr:read", Deny: false), new OverrideEntry("emr:read", Deny: true)]);

        denyFirst.Keys.Should().NotContain("emr:read");
        allowFirst.Keys.Should().NotContain("emr:read");
    }

    [Fact]
    public void A_deny_beats_a_role_grant_which_is_the_acceptance_case()
    {
        // 21.2 acceptance: a role granting orders:read plus a Deny override on orders:read ⇒ denied.
        Compute(["orders:read"], [new OverrideEntry("orders:read", Deny: true)]).Keys
            .Should().NotContain("orders:read");
    }

    // ---- Expiry -----------------------------------------------------------------------------------------

    [Fact]
    public void An_allow_override_that_has_expired_is_absent()
    {
        // 21.2 acceptance. Expiry is judged at RESOLUTION time — there is no sweeper whose failure could
        // leave a temporary grant switched on forever.
        Compute(["orders:read"], [new OverrideEntry("emr:read", Deny: false, Now.AddSeconds(-1))]).Keys
            .Should().NotContain("emr:read");
    }

    [Fact]
    public void An_expired_deny_stops_withholding()
    {
        // A time-boxed restriction is meant to END. Leaving an expired Deny in force would make
        // "suspend this person's ordering until the 30th" a permanent revocation nobody could see.
        Compute(["orders:read"], [new OverrideEntry("orders:read", Deny: true, Now.AddSeconds(-1))]).Keys
            .Should().Contain("orders:read");
    }

    [Fact]
    public void An_override_expiring_exactly_now_has_already_stopped_applying()
    {
        // The boundary is closed against the grant: at the instant it expires it no longer applies. Fail
        // closed on the Allow side rather than granting one more request's worth of access.
        Compute([], [new OverrideEntry("emr:read", Deny: false, Now)]).Keys.Should().BeEmpty();
    }

    [Fact]
    public void An_override_with_no_expiry_applies_indefinitely()
    {
        Compute([], [new OverrideEntry("emr:read", Deny: false, ValidUntil: null)]).Keys
            .Should().BeEquivalentTo("emr:read");
    }

    // ---- Deprecation ------------------------------------------------------------------------------------

    [Fact]
    public void A_deprecated_key_still_resolves_and_is_reported()
    {
        // 21.2 acceptance: it works, and it warns. Deprecation is a migration signal — revoking access
        // because someone renamed a key is an outage, not a cleanup.
        var result = Compute(["legacy:all", "orders:read"]);

        result.Keys.Should().Contain("legacy:all");
        result.DeprecatedInUse.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new DeprecationUse("legacy:all", "orders:read"));
    }

    [Fact]
    public void A_key_absent_from_the_catalog_still_resolves()
    {
        // The catalog carries METADATA, not membership. If a catalog row failed to seed, treating its key
        // as unknown-therefore-absent would silently revoke live access across the platform.
        Compute(["orders:read"], catalog: Catalog()).Keys.Should().BeEquivalentTo("orders:read");
    }

    [Fact]
    public void A_deprecated_key_that_was_denied_is_not_reported_as_in_use()
    {
        // It is not in the effective set, so nobody has to be migrated off it.
        Compute(["legacy:all"], [new OverrideEntry("legacy:all", Deny: true)])
            .DeprecatedInUse.Should().BeEmpty();
    }

    // ---- A1: the platform-admin flag --------------------------------------------------------------------

    [Fact]
    public void Platform_admin_reaches_administration_keys()
    {
        Compute([], platformAdmin: true).Keys.Should().Contain(["admin:read", "admin:write"]);
    }

    /// <summary>
    /// THE A1 TEST (design 40 §0 A1, ADR-0021). Do not delete, skip, or weaken this.
    ///
    /// The platform-admin flag exists so Mersal staff can administer tenants, the catalog and identities. It
    /// is NOT a wildcard, and the single most damaging way to get this model wrong is to implement it as
    /// one — a refugee's diagnoses are not an administrative object. A platform administrator holding no
    /// membership at all must therefore be able to reach administration keys and NOTHING else: no patient
    /// read, no projected clinical field, no sensitive result, no branch-scoped order list.
    ///
    /// Break-glass (libs/authz/BreakGlass.cs) remains the only elevation into clinical data, and it is
    /// deliberately loud, time-boxed and audited in a way this flag is not.
    /// </summary>
    [Fact]
    public void A1_platform_admin_with_no_membership_reaches_no_clinical_or_benefit_key()
    {
        var catalog = Catalog(
            new CatalogKey("admin:read", IsPlatformAdminKey: true),
            new CatalogKey("admin:write", IsPlatformAdminKey: true),
            // (a) a patient read, (b) a projected clinical field, (c) a sensitive result,
            // (d) a branch-scoped order list — the four the phase prompt names, plus the money.
            new CatalogKey("patient:read"),
            new CatalogKey("emr:read"),
            new CatalogKey("emr:sensitive"),
            new CatalogKey("orders:read"),
            new CatalogKey("finance:read"),
            new CatalogKey("pharmacy:dispense"));

        var result = EffectiveSetEvaluator.Compute(
            new MembershipSnapshot(RoleGrants: [], Overrides: [], IsPlatformAdmin: true), catalog, Now);

        result.Keys.Should().BeEquivalentTo(["admin:read", "admin:write"],
            "the platform-admin flag short-circuits ONLY keys marked is_platform_admin_key (A1)");

        foreach (var forbidden in new[]
                 { "patient:read", "emr:read", "emr:sensitive", "orders:read", "finance:read", "pharmacy:dispense" })
            result.Has(forbidden).Should().BeFalse(
                "{0} is not an administration key, so no amount of platform administration may confer it", forbidden);
    }

    [Fact]
    public void Platform_admin_does_not_overturn_an_explicit_deny_on_an_administration_key()
    {
        // An override is a deliberate act by another administrator. Silently reinstating what it withheld
        // would make the whole override surface untrustworthy — and it is exactly how a compromised or
        // off-boarded platform account keeps its reach after someone tries to fence it off.
        Compute([], [new OverrideEntry("admin:write", Deny: true)], platformAdmin: true).Keys
            .Should().Contain("admin:read").And.NotContain("admin:write");
    }

    [Fact]
    public void Without_the_flag_administration_keys_are_not_conferred()
    {
        Compute([]).Keys.Should().BeEmpty();
    }

    // ---- Shape ------------------------------------------------------------------------------------------

    [Fact]
    public void Duplicate_grants_collapse_and_the_result_is_a_set()
    {
        Compute(["orders:read", "orders:read"], [new OverrideEntry("orders:read", Deny: false)]).Keys
            .Should().BeEquivalentTo("orders:read");
    }

    [Fact]
    public void Key_comparison_is_ordinal_and_case_sensitive()
    {
        // Scope keys are exact strings the services match verbatim; a case-insensitive set here would make
        // "EMR:read" silently equal "emr:read" and let a mis-cased catalog row grant real access.
        Compute(["emr:read"], [new OverrideEntry("EMR:READ", Deny: true)]).Keys
            .Should().Contain("emr:read");
    }
}
