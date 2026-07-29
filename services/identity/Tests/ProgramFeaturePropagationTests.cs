using OpenIddict.Abstractions;
using System.Security.Claims;
using FluentAssertions;
using Mersal.Auth;
using Mersal.Authz;
using Mersal.Identity.Api;
using Mersal.Identity.Api.Auth;
using Mersal.Identity.Infrastructure;

namespace Mersal.Identity.Tests;

/// <summary>
/// 21.4 propagation — the switches are administered in admin-service and enforced wherever the module lives, so
/// they travel: TenantFeatureChanged → identity.tenant_feature → the `features` claim (design 40 §4/§5 mode 1).
///
/// These are the DB-less halves: what the consumer accepts, what the issuer emits, and whether the three places
/// that spell out the feature catalogue still agree. The projection's ordering guard needs a real Postgres and
/// lives in <see cref="TenantFeatureProjectionTests"/>.
/// </summary>
public class ProgramFeaturePropagationTests
{
    // ---- What the issuer emits ---------------------------------------------------------------------------

    private static UserTokenFacts Facts(params string[] features) => new(
        Subject: "u-1", Roles: ["doctor"], Scopes: new HashSet<string>(["emr:read"]),
        TenantId: "t-1", ProviderId: null, DisplayName: "Dr Nour",
        MembershipId: Guid.NewGuid(), Features: features);

    /// <summary>
    /// The round trip that matters: the claim the issuer writes must be the claim `libs/auth` reads. Asserting
    /// the raw claim alone would pass even if the two sides disagreed about the shape — which is exactly how a
    /// features claim could exist in every token and still leave HasFeature false everywhere.
    /// </summary>
    [Fact]
    public void Features_survive_the_round_trip_from_issuer_to_HbmpPrincipal()
    {
        var factory = new TokenPrincipalFactory();

        var principal = factory.ForUser(
            Facts(ProgramFeatures.CallCentre, ProgramFeatures.Emr), ["emr:read"], ["pwd"]);

        principal.Should().NotBeNull();
        var read = HbmpPrincipal.FromClaims(new ClaimsPrincipal(principal!.Identity!));

        read.Features.Should().BeEquivalentTo(ProgramFeatures.CallCentre, ProgramFeatures.Emr);
        read.HasFeature(ProgramFeatures.CallCentre).Should().BeTrue();
        read.HasFeature(ProgramFeatures.Claims).Should().BeFalse("a feature absent from the claim is NOT enabled");
    }

    /// <summary>
    /// The claim is OPTIONAL by contract, and its absence must be inert. If a missing claim were ever read as
    /// "everything on", the gate would be defeated by the oldest token in circulation; if it threw, deploying
    /// this change would have logged the whole platform out.
    /// </summary>
    [Fact]
    public void No_features_means_no_claim_and_nothing_enabled()
    {
        var principal = new TokenPrincipalFactory().ForUser(Facts(), ["emr:read"], ["pwd"]);

        principal!.FindAll(TokenPrincipalFactory.FeaturesClaim).Should().BeEmpty();
        HbmpPrincipal.FromClaims(new ClaimsPrincipal(principal.Identity!))
            .HasFeature(ProgramFeatures.Emr).Should().BeFalse();
    }

    /// <summary>
    /// Enablement NEVER GRANTS (design 40 §4). A feature must not leak into the two claims that DO carry
    /// authority — `scope` and `roles`. Asserted on the raw claims rather than through
    /// <see cref="HbmpPrincipal"/>: OpenIddict's <c>SetScopes</c> keeps scopes in its own internal claim and
    /// only writes the space-delimited `scope` claim when the token is serialized, so a pre-serialization
    /// principal legitimately has none — and a HasScope assertion here would pass for that reason rather than
    /// for the one under test.
    /// </summary>
    [Fact]
    public void A_feature_does_not_leak_into_the_claims_that_carry_authority()
    {
        var principal = new TokenPrincipalFactory().ForUser(
            Facts(ProgramFeatures.Claims), ["emr:read"], ["pwd"]);

        principal!.FindAll(TokenPrincipalFactory.FeaturesClaim).Select(c => c.Value)
            .Should().BeEquivalentTo([ProgramFeatures.Claims]);

        principal.FindAll(TokenPrincipalFactory.RolesClaim).Select(c => c.Value)
            .Should().BeEquivalentTo(["doctor"], "a feature is not a role");
        principal.GetScopes().Should().BeEquivalentTo(["emr:read"], "a feature is not a scope");

        // Belt and braces on the shape actually put on the wire: no claim of any type carries the feature key
        // except the features claim itself.
        principal.Claims
            .Where(c => c.Type != TokenPrincipalFactory.FeaturesClaim && c.Value.Contains(ProgramFeatures.Claims, StringComparison.Ordinal))
            .Should().BeEmpty();
    }

    // ---- What the consumer accepts ------------------------------------------------------------------------

    [Fact]
    public void A_well_formed_change_parses()
    {
        var change = FeatureChange.Parse(
            """{"tenantId":"t-1","featureKey":"claims","enabled":true,"changedAt":"2026-07-29T09:00:00+00:00"}""");

        change.Should().NotBeNull();
        change!.TenantId.Should().Be("t-1");
        change.FeatureKey.Should().Be(ProgramFeatures.Claims);
        change.Enabled.Should().BeTrue();
        change.ChangedAt.Should().Be(DateTimeOffset.Parse("2026-07-29T09:00:00+00:00"));
    }

    /// <summary>
    /// "Absence means disabled" is a rule about a missing ROW, not about a malformed event. Defaulting a
    /// missing `enabled` to false would let a serialization bug switch a live module off for a whole
    /// organisation — so the event is refused (and dead-lettered) instead.
    /// </summary>
    [Fact]
    public void A_change_with_no_enabled_flag_is_refused_not_read_as_off()
    {
        FeatureChange.Parse(
            """{"tenantId":"t-1","featureKey":"claims","changedAt":"2026-07-29T09:00:00+00:00"}""")
            .Should().BeNull();
    }

    /// <summary>Without the admin-stamped instant the ordering guard has nothing to compare, so a stale
    /// redelivery could move a row backwards. Substituting "now" would make the stale event look newest.</summary>
    [Fact]
    public void A_change_with_no_timestamp_is_refused()
    {
        FeatureChange.Parse("""{"tenantId":"t-1","featureKey":"claims","enabled":false}""")
            .Should().BeNull();
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("[]")]
    [InlineData("""{"featureKey":"claims","enabled":true,"changedAt":"2026-07-29T09:00:00+00:00"}""")]
    [InlineData("""{"tenantId":"t-1","enabled":true,"changedAt":"2026-07-29T09:00:00+00:00"}""")]
    [InlineData("""{"tenantId":"","featureKey":"claims","enabled":true,"changedAt":"2026-07-29T09:00:00+00:00"}""")]
    public void An_unusable_payload_is_refused_rather_than_half_applied(string json) =>
        FeatureChange.Parse(json).Should().BeNull();

    // ---- The catalogue, in three places -------------------------------------------------------------------

    /// <summary>
    /// The feature keys are written out in three places: the <see cref="ProgramFeatures"/> constants, admin
    /// 0008's CHECK constraint, and identity 0015's backfill. Nothing at runtime forces them to agree — a key
    /// added to the CHECK but not the backfill would simply never reach a token for existing tenants, and the
    /// module would be dark for them with no error anywhere. This test is that force.
    /// </summary>
    [Fact]
    public void The_feature_catalogue_agrees_across_constants_and_both_migrations()
    {
        var expected = typeof(ProgramFeatures)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToHashSet(StringComparer.Ordinal);

        expected.Should().HaveCount(11, "the catalogue is fixed by design 40 §4; adding one is a deliberate act");

        KeysIn(RepoFile("services/admin/Infrastructure/Migrations/0008_program_enablement.sql"))
            .Should().BeEquivalentTo(expected, "admin 0008's CHECK is the source of truth's allow-list");
        KeysIn(RepoFile("services/admin/Infrastructure/Migrations/0009_program_backfill.sql"))
            .Should().BeEquivalentTo(expected, "a key missing from the backfill is a module dark for every existing tenant");
        KeysIn(RepoFile("services/identity/Infrastructure/Migrations/0015_tenant_feature_projection.sql"))
            .Should().BeEquivalentTo(expected, "the projection backfill must state the same fact as the source's");
    }

    /// <summary>Every quoted lower-case token in the file, filtered to the ones that look like feature keys.
    /// Crude on purpose: a stricter parser would need updating whenever the SQL is reformatted, and the thing
    /// under test is the SET of keys, not the formatting.</summary>
    private static ISet<string> KeysIn(string sql) =>
        System.Text.RegularExpressions.Regex.Matches(sql, @"'([a-z_]{3,32})'")
            .Select(m => m.Groups[1].Value)
            .Where(v => v is "claims" or "callcentre" or "interop" or "reporting_extracts" or "pharmacy"
                        or "orders" or "approvals" or "emr" or "finance" or "documents" or "case_management")
            .ToHashSet(StringComparer.Ordinal);

    private static string RepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "services"))) dir = dir.Parent;
        dir.Should().NotBeNull("the test must be able to find the repo root to read the migrations");
        var path = Path.Combine(dir!.FullName, relative);
        File.Exists(path).Should().BeTrue($"{relative} should exist");
        return File.ReadAllText(path);
    }
}
