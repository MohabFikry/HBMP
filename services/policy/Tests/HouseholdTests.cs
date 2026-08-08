using FluentAssertions;
using Mersal.Policy.Domain;
using Mersal.Policy.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Policy.Tests;

/// <summary>
/// Who is covered together (19.6d).
///
/// <para><b>The bug these exist for.</b> The 360's family section walked one hop out from the enrolments the
/// caller already held. From a principal that is right. From a CHILD it reaches the principal and stops — so a
/// dependant's record listed their father and none of their brothers or sisters, and the omission was
/// invisible: a family of five rendered as a family of two with nothing to say anything was missing.</para>
///
/// <para>The traversal is rooted on the principal now, which is what makes it symmetric from any member.</para>
/// </summary>
public class HouseholdRuleTests
{
    private static readonly Guid Father = Guid.NewGuid();
    private static readonly Guid Son = Guid.NewGuid();

    [Fact]
    public void A_principal_is_its_own_root()
        => Household.RootOf(Father, null).Should().Be(Father);

    [Fact]
    public void A_dependant_roots_on_the_principal_it_points_at()
        => Household.RootOf(Son, Father).Should().Be(Father);

    [Fact]
    public void The_father_and_the_son_resolve_to_the_SAME_root()
    {
        // The whole property. Two people asking "who is on this cover" from opposite ends of the same family
        // must be asking about one set of rows, or the answer depends on whose record you opened.
        Household.RootOf(Father, null).Should().Be(Household.RootOf(Son, Father));
    }

    [Fact]
    public void Several_memberships_root_separately_and_are_de_duplicated()
    {
        // One person can hold two enrolments under two policies — two households, and a shared root counted
        // once so the query does not ask for the same family twice.
        var other = Guid.NewGuid();
        Household.RootsOf([(Son, Father), (other, null), (Guid.NewGuid(), Father)])
            .Should().BeEquivalentTo([Father, other]);
    }

    [Fact]
    public void The_principal_reads_first_then_spouse_then_children()
    {
        // A household is read to find the person at the desk, and it reads in the shape people already hold:
        // the principal is who the cover belongs to and every other row is defined against it.
        var order = new[]
        {
            Household.SortKey(false, Relationship.Dependent),
            Household.SortKey(false, Relationship.Child),
            Household.SortKey(false, Relationship.Spouse),
            Household.SortKey(true, Relationship.Principal),
        };
        order.Should().BeInDescendingOrder();
    }
}

/// <summary>The same traversal against real Postgres — where it actually runs.</summary>
[Collection("policy-db")]
public class HouseholdStoreTests
{
    private static readonly string? Db = Environment.GetEnvironmentVariable("POLICY_TEST_DB");
    private const string Tenant = "11111111-1111-1111-1111-111111111111";

    private static PolicyDbContext Ctx() => new(new DbContextOptionsBuilder<PolicyDbContext>()
        .UseNpgsql(Db).UseSnakeCaseNamingConvention().Options);

    [SkippableFact]
    public async Task A_child_sees_the_principal_AND_their_siblings()
    {
        Skip.If(Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        var f = await Seed();
        try
        {
            await using var db = Ctx();
            var query = new AdministrativeQuery(db);

            var (exists, _, root, _) = await query.EnrollmentHouseholdRootAsync(f.Daughter);
            exists.Should().BeTrue();
            root.Should().Be(f.Principal, "a dependant's household is rooted on the enrolment it points at");

            var household = await query.HouseholdAsync([root]);

            household.Select(e => e.EnrollmentId).Should().BeEquivalentTo(
                [f.Principal, f.Spouse, f.Son, f.Daughter],
                "asked from the daughter, the answer is the whole family — not just her father");
            household.Select(e => e.EnrollmentId).Should().NotContain(f.Stranger);
        }
        finally { await Cleanup(f); }
    }

    [SkippableFact]
    public async Task The_principal_sees_exactly_the_same_household()
    {
        Skip.If(Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        var f = await Seed();
        try
        {
            await using var db = Ctx();
            var query = new AdministrativeQuery(db);

            var (_, _, fromChild, _) = await query.EnrollmentHouseholdRootAsync(f.Son);
            var (_, _, fromPrincipal, _) = await query.EnrollmentHouseholdRootAsync(f.Principal);

            var a = await query.HouseholdAsync([fromChild]);
            var b = await query.HouseholdAsync([fromPrincipal]);
            a.Select(e => e.EnrollmentId).Should().Equal(b.Select(e => e.EnrollmentId));
        }
        finally { await Cleanup(f); }
    }

    [SkippableFact]
    public async Task The_principal_is_listed_first()
    {
        Skip.If(Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        var f = await Seed();
        try
        {
            await using var db = Ctx();
            var household = await new AdministrativeQuery(db).HouseholdAsync([f.Principal]);
            household[0].EnrollmentId.Should().Be(f.Principal);
            household[1].Relationship.Should().Be(Relationship.Spouse);
        }
        finally { await Cleanup(f); }
    }

    [SkippableFact]
    public async Task A_person_with_no_family_is_a_household_of_one_not_an_empty_answer()
    {
        Skip.If(Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        var f = await Seed();
        try
        {
            await using var db = Ctx();
            var query = new AdministrativeQuery(db);
            var (_, _, root, _) = await query.EnrollmentHouseholdRootAsync(f.Stranger);

            var household = await query.HouseholdAsync([root]);

            // The subject is always in the result, so the UI can say "nobody else is on this cover" rather
            // than rendering an empty table that reads as a failed lookup.
            household.Should().ContainSingle().Which.EnrollmentId.Should().Be(f.Stranger);
        }
        finally { await Cleanup(f); }
    }

    [SkippableFact]
    public async Task A_terminated_dependant_stays_in_the_household()
    {
        Skip.If(Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        var f = await Seed();
        try
        {
            await using var db = Ctx();
            var household = await new AdministrativeQuery(db).HouseholdAsync([f.Principal]);

            // "Who else is on this cover" is asked about a family's history as often as its present, and a
            // child whose cover ended last month is frequently the answer to why a claim was rejected.
            household.Should().Contain(e => e.EnrollmentId == f.Son && e.Status == EnrollmentStatus.Terminated);
        }
        finally { await Cleanup(f); }
    }

    [SkippableFact]
    public async Task An_unknown_enrolment_is_reported_as_missing_rather_than_as_an_empty_family()
    {
        Skip.If(Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        await using var db = Ctx();
        var (exists, _, _, _) = await new AdministrativeQuery(db).EnrollmentHouseholdRootAsync(Guid.NewGuid());
        // The endpoint turns this into 404. An empty household would say "this person has no family", which
        // is a different and wrong answer to "there is no such membership".
        exists.Should().BeFalse();
    }

    // ---- Fixture -----------------------------------------------------------------------------------------

    private sealed record Fixture(
        string Prefix, Guid PayerId, Guid PolicyId, Guid PolicyPlanId, Guid PlanId, Guid PlanVersionId,
        Guid Principal, Guid Spouse, Guid Son, Guid Daughter, Guid Stranger);

    /// <summary>One family of four — father (principal), spouse, a terminated son, a daughter — plus an
    /// unrelated principal on the same policy, which is what proves the traversal is not just "everybody".</summary>
    private static async Task<Fixture> Seed()
    {
        await using var db = Ctx();
        var prefix = $"H{Guid.NewGuid():N}"[..9].ToUpperInvariant();

        var payer = new Payer
        {
            PayerId = Guid.NewGuid(), TenantId = Tenant, PayerCode = prefix[..8],
            NameEn = "Household Payer", NameAr = "Household Payer", PayerType = PayerType.Donor,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        var plan = new Plan
        {
            PlanId = Guid.NewGuid(), TenantId = Tenant, PlanCode = $"{prefix}PL",
            NameEn = "Household", NameAr = "Household", Category = "Primary",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Payers.Add(payer);
        db.Plans.Add(plan);
        await db.SaveChangesAsync();

        var version = new PlanVersion
        {
            PlanVersionId = Guid.NewGuid(), TenantId = Tenant, PlanId = plan.PlanId, VersionNo = 1,
            EffectiveFrom = new DateOnly(2026, 1, 1), Status = PlanVersionStatus.Draft,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        var policy = new Domain.Policy
        {
            PolicyId = Guid.NewGuid(), TenantId = Tenant, PolicyNo = $"{prefix}-POL",
            PayerId = payer.PayerId, EffectiveFrom = new DateOnly(2026, 1, 1), Status = PolicyStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.PlanVersions.Add(version);
        db.Policies.Add(policy);
        await db.SaveChangesAsync();

        var policyPlan = new PolicyPlan
        {
            PolicyPlanId = Guid.NewGuid(), TenantId = Tenant, PolicyId = policy.PolicyId,
            PlanVersionId = version.PlanVersionId, PlanLabel = "Standard",
            EffectiveFrom = new DateOnly(2026, 1, 1), IsDefault = true,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.PolicyPlans.Add(policyPlan);
        await db.SaveChangesAsync();

        var principal = Member(prefix, policy.PolicyId, policyPlan.PolicyPlanId, "001",
            Relationship.Principal, null, EnrollmentStatus.Active);
        var spouse = Member(prefix, policy.PolicyId, policyPlan.PolicyPlanId, "002",
            Relationship.Spouse, principal.EnrollmentId, EnrollmentStatus.Active);
        var son = Member(prefix, policy.PolicyId, policyPlan.PolicyPlanId, "003",
            Relationship.Child, principal.EnrollmentId, EnrollmentStatus.Terminated);
        var daughter = Member(prefix, policy.PolicyId, policyPlan.PolicyPlanId, "004",
            Relationship.Child, principal.EnrollmentId, EnrollmentStatus.Active);
        var stranger = Member(prefix, policy.PolicyId, policyPlan.PolicyPlanId, "009",
            Relationship.Principal, null, EnrollmentStatus.Active);

        // The principal is saved FIRST and on its own: `principal_enrollment_id` is a self-referencing foreign
        // key, and a single batch does not promise the parent row is inserted before its children.
        db.Enrollments.Add(principal);
        await db.SaveChangesAsync();
        db.Enrollments.AddRange(spouse, son, daughter, stranger);
        await db.SaveChangesAsync();

        return new Fixture(prefix, payer.PayerId, policy.PolicyId, policyPlan.PolicyPlanId,
            plan.PlanId, version.PlanVersionId,
            principal.EnrollmentId, spouse.EnrollmentId, son.EnrollmentId, daughter.EnrollmentId,
            stranger.EnrollmentId);
    }

    private static Enrollment Member(
        string prefix, Guid policyId, Guid policyPlanId, string suffix,
        Relationship relationship, Guid? principalEnrollmentId, EnrollmentStatus status) => new()
        {
            EnrollmentId = Guid.NewGuid(), TenantId = Tenant, BeneficiaryId = Guid.NewGuid(),
            PolicyId = policyId, PolicyPlanId = policyPlanId, MemberNo = $"{prefix}-{suffix}",
            Relationship = relationship, PrincipalEnrollmentId = principalEnrollmentId,
            EffectiveFrom = new DateOnly(2026, 1, 1),
            EffectiveTo = status == EnrollmentStatus.Terminated ? new DateOnly(2026, 6, 30) : null,
            Status = status,
            // ck_enrollment_termination_reason: a membership cannot END for no recorded reason. The database
            // holds that rule, which is why the fixture has to satisfy it rather than work around it.
            TerminationReason = status == EnrollmentStatus.Terminated ? "left the programme" : null,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };

    private static async Task Cleanup(Fixture f)
    {
        await using var db = Ctx();
        await db.Database.ExecuteSqlRawAsync(
            "DELETE FROM policy.enrollment WHERE member_no LIKE {0}", f.Prefix + "%");
        await db.Database.ExecuteSqlRawAsync(
            "DELETE FROM policy.policy_plan WHERE policy_plan_id = {0}", f.PolicyPlanId);
        await db.Database.ExecuteSqlRawAsync("DELETE FROM policy.policy WHERE policy_id = {0}", f.PolicyId);
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE policy.plan_version DISABLE TRIGGER trg_plan_version_immutable");
        try
        {
            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM policy.plan_version WHERE plan_version_id = {0}", f.PlanVersionId);
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE policy.plan_version ENABLE TRIGGER trg_plan_version_immutable");
        }
        await db.Database.ExecuteSqlRawAsync("DELETE FROM policy.plan WHERE plan_id = {0}", f.PlanId);
        await db.Database.ExecuteSqlRawAsync("DELETE FROM policy.payer WHERE payer_id = {0}", f.PayerId);
    }
}
