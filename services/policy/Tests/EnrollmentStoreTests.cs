using FluentAssertions;
using Mersal.Policy.Domain;
using Mersal.Policy.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Mersal.Policy.Tests;

/// <summary>
/// Phase 19.2 + 19.2b at the datastore (env-gated <c>POLICY_TEST_DB</c>, migration 0008 applied).
///
/// The endpoints translate these into 409s, but the endpoint is not the guarantee. Each constraint is
/// attempted DIRECTLY through EF with no endpoint in the way:
///
/// <list type="bullet">
/// <item>one beneficiary cannot hold two live memberships of one policy over the same days — otherwise
/// coverage is generated twice and which accumulator a consume decrements depends on query order;</item>
/// <item>a policy has at most one default plan, or enrolling without naming one is a coin toss over
/// entitlement;</item>
/// <item>enrollment_event is append-only, because a log that can be edited is not a log — and it is the only
/// account of why a member's entitlement changed.</item>
/// </list>
/// </summary>
public class EnrollmentStoreTests
{
    private static readonly string? Db = Environment.GetEnvironmentVariable("POLICY_TEST_DB");
    private const string Tenant = "11111111-1111-1111-1111-111111111111";

    private static PolicyDbContext Ctx() => new(new DbContextOptionsBuilder<PolicyDbContext>()
        .UseNpgsql(Db).UseSnakeCaseNamingConvention().Options);

    // ---- The overlap invariant ---------------------------------------------------------------------------

    [SkippableFact]
    public async Task One_beneficiary_cannot_hold_two_live_memberships_of_one_policy()
    {
        Skip.If(Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        var fixture = await Seed();
        try
        {
            var beneficiary = Guid.NewGuid();
            await Insert(Enrol(fixture, beneficiary, new(2026, 1, 1)));

            await using var db = Ctx();
            db.Enrollments.Add(Enrol(fixture, beneficiary, new(2026, 6, 1)));

            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            ex.InnerException.Should().BeOfType<PostgresException>()
                .Which.ConstraintName.Should().Be("ex_enrollment_no_overlap");
        }
        finally { await Cleanup(fixture); }
    }

    [SkippableFact]
    public async Task A_suspended_membership_still_blocks_a_second_one()
    {
        // A suspension pauses the benefit; it does not vacate the membership. If Suspended were exempt, a
        // second enrolment could slide in underneath and generate a parallel set of accumulators.
        Skip.If(Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        var fixture = await Seed();
        try
        {
            var beneficiary = Guid.NewGuid();
            var first = Enrol(fixture, beneficiary, new(2026, 1, 1));
            first.Status = EnrollmentStatus.Suspended;
            await Insert(first);

            await using var db = Ctx();
            db.Enrollments.Add(Enrol(fixture, beneficiary, new(2026, 6, 1)));

            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            ex.InnerException.Should().BeOfType<PostgresException>().Which.SqlState.Should().Be("23P01");
        }
        finally { await Cleanup(fixture); }
    }

    [SkippableFact]
    public async Task A_terminated_membership_frees_the_slot_for_a_re_enrolment()
    {
        // The window is INCLUSIVE, so a membership terminated on 31 May and a new one starting 1 June abut
        // without overlapping. Getting this wrong would make re-enrolling a returning beneficiary impossible.
        Skip.If(Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        var fixture = await Seed();
        try
        {
            var beneficiary = Guid.NewGuid();
            var first = Enrol(fixture, beneficiary, new(2026, 1, 1));
            first.EffectiveTo = new DateOnly(2026, 5, 31);
            first.Status = EnrollmentStatus.Terminated;
            first.TerminationReason = "left the programme";
            await Insert(first);

            await Insert(Enrol(fixture, beneficiary, new(2026, 6, 1)));

            await using var db = Ctx();
            (await db.Enrollments.AsNoTracking().CountAsync(e => e.BeneficiaryId == beneficiary)).Should().Be(2);
        }
        finally { await Cleanup(fixture); }
    }

    [SkippableFact]
    public async Task Two_beneficiaries_on_one_policy_do_not_collide()
    {
        Skip.If(Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        var fixture = await Seed();
        try
        {
            await Insert(Enrol(fixture, Guid.NewGuid(), new(2026, 1, 1)));
            await Insert(Enrol(fixture, Guid.NewGuid(), new(2026, 1, 1)));

            await using var db = Ctx();
            (await db.Enrollments.AsNoTracking().CountAsync(e => e.PolicyId == fixture.PolicyId)).Should().Be(2);
        }
        finally { await Cleanup(fixture); }
    }

    // ---- One default plan (19.2b) ------------------------------------------------------------------------

    [SkippableFact]
    public async Task A_policy_may_have_only_one_default_plan()
    {
        Skip.If(Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        var fixture = await Seed(withDefaultPlan: true);
        try
        {
            await using var db = Ctx();
            db.PolicyPlans.Add(new PolicyPlan
            {
                PolicyPlanId = Guid.NewGuid(), TenantId = Tenant, PolicyId = fixture.PolicyId,
                PlanVersionId = fixture.PlanVersionId, PlanLabel = "Oncology",
                EffectiveFrom = new DateOnly(2026, 1, 1), IsDefault = true,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });

            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            ex.InnerException.Should().BeOfType<PostgresException>()
                .Which.ConstraintName.Should().Be("uq_policy_plan_single_default");
        }
        finally { await Cleanup(fixture); }
    }

    // ---- Append-only history -----------------------------------------------------------------------------

    [SkippableFact]
    public async Task An_enrollment_event_cannot_be_edited()
    {
        Skip.If(Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        var fixture = await Seed();
        try
        {
            var enrollment = Enrol(fixture, Guid.NewGuid(), new(2026, 1, 1));
            await Insert(enrollment);

            Guid eventId;
            await using (var db = Ctx())
            {
                var ev = new EnrollmentEvent
                {
                    EventId = Guid.NewGuid(), TenantId = Tenant, EnrollmentId = enrollment.EnrollmentId,
                    EventType = EnrollmentEventType.Terminated, EffectiveDate = new DateOnly(2026, 5, 31),
                    Reason = "left the programme", OccurredAt = DateTimeOffset.UtcNow,
                };
                db.EnrollmentEvents.Add(ev);
                await db.SaveChangesAsync();
                eventId = ev.EventId;
            }

            await using (var db = Ctx())
            {
                var ev = await db.EnrollmentEvents.FirstAsync(e => e.EventId == eventId);
                ev.Reason = "actually, a clerical error";   // rewriting why someone lost their cover

                var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
                ex.InnerException.Should().BeOfType<PostgresException>()
                    .Which.MessageText.Should().Contain("append-only");
            }
        }
        finally { await Cleanup(fixture); }
    }

    [SkippableFact]
    public async Task A_termination_without_a_reason_is_rejected_by_the_database()
    {
        Skip.If(Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        var fixture = await Seed();
        try
        {
            var enrollment = Enrol(fixture, Guid.NewGuid(), new(2026, 1, 1));
            enrollment.Status = EnrollmentStatus.Terminated;   // …but no reason

            await using var db = Ctx();
            db.Enrollments.Add(enrollment);

            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            ex.InnerException.Should().BeOfType<PostgresException>()
                .Which.ConstraintName.Should().Be("ck_enrollment_termination_reason");
        }
        finally { await Cleanup(fixture); }
    }

    [SkippableFact]
    public async Task A_dependent_must_hang_off_a_principal()
    {
        Skip.If(Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        var fixture = await Seed();
        try
        {
            var orphan = Enrol(fixture, Guid.NewGuid(), new(2026, 1, 1));
            orphan.Relationship = Relationship.Child;   // …with no principal_enrollment_id

            await using var db = Ctx();
            db.Enrollments.Add(orphan);

            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            ex.InnerException.Should().BeOfType<PostgresException>()
                .Which.ConstraintName.Should().Be("ck_enrollment_principal_link");
        }
        finally { await Cleanup(fixture); }
    }

    // ---- fixtures ----------------------------------------------------------------------------------------

    private sealed record Fixture(Guid PolicyId, Guid PlanId, Guid PlanVersionId, Guid PolicyPlanId);

    private static Enrollment Enrol(Fixture f, Guid beneficiary, DateOnly from) => new()
    {
        EnrollmentId = Guid.NewGuid(), TenantId = Tenant, BeneficiaryId = beneficiary,
        PolicyId = f.PolicyId, PolicyPlanId = f.PolicyPlanId,
        MemberNo = $"MEM-TEST-{Guid.NewGuid():N}"[..24],
        Relationship = Relationship.Principal, EffectiveFrom = from, Status = EnrollmentStatus.Active,
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static async Task Insert(Enrollment e)
    {
        await using var db = Ctx();
        db.Enrollments.Add(e);
        await db.SaveChangesAsync();
    }

    private static async Task<Fixture> Seed(bool withDefaultPlan = true)
    {
        await using var db = Ctx();
        var plan = new Plan
        {
            PlanId = Guid.NewGuid(), TenantId = Tenant, PlanCode = $"P{Guid.NewGuid():N}"[..12],
            NameEn = "Test", NameAr = "Test", Category = "Primary",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        var version = new PlanVersion
        {
            PlanVersionId = Guid.NewGuid(), TenantId = Tenant, PlanId = plan.PlanId, VersionNo = 1,
            EffectiveFrom = new DateOnly(2026, 1, 1), Status = PlanVersionStatus.Active,
            ActivatedAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        var policy = new Domain.Policy
        {
            PolicyId = Guid.NewGuid(), TenantId = Tenant, PolicyNo = $"POL-{Guid.NewGuid():N}"[..20],
            EffectiveFrom = new DateOnly(2026, 1, 1), Status = PolicyStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        var policyPlan = new PolicyPlan
        {
            PolicyPlanId = Guid.NewGuid(), TenantId = Tenant, PolicyId = policy.PolicyId,
            PlanVersionId = version.PlanVersionId, PlanLabel = "Standard",
            EffectiveFrom = new DateOnly(2026, 1, 1), IsDefault = withDefaultPlan,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Plans.Add(plan);
        db.PlanVersions.Add(version);
        db.Policies.Add(policy);
        db.PolicyPlans.Add(policyPlan);
        await db.SaveChangesAsync();
        return new Fixture(policy.PolicyId, plan.PlanId, version.PlanVersionId, policyPlan.PolicyPlanId);
    }

    /// <summary>Ordered raw SQL with the append-only guard lifted for the duration — the trigger protects
    /// exactly the rows a teardown has to remove, and there is no EF navigation to order these deletes by.</summary>
    private static async Task Cleanup(Fixture f)
    {
        await using var db = Ctx();
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE policy.enrollment_event DISABLE TRIGGER trg_enrollment_event_append_only");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE policy.plan_version DISABLE TRIGGER trg_plan_version_immutable");
        try
        {
            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM policy.enrollment_event WHERE enrollment_id IN " +
                "(SELECT enrollment_id FROM policy.enrollment WHERE policy_id = {0})", f.PolicyId);
            await db.Database.ExecuteSqlRawAsync("DELETE FROM policy.coverage WHERE policy_id = {0}", f.PolicyId);
            await db.Database.ExecuteSqlRawAsync("DELETE FROM policy.enrollment WHERE policy_id = {0}", f.PolicyId);
            await db.Database.ExecuteSqlRawAsync("DELETE FROM policy.member_group WHERE policy_id = {0}", f.PolicyId);
            await db.Database.ExecuteSqlRawAsync("DELETE FROM policy.policy_plan WHERE policy_id = {0}", f.PolicyId);
            await db.Database.ExecuteSqlRawAsync("DELETE FROM policy.policy WHERE policy_id = {0}", f.PolicyId);
            await db.Database.ExecuteSqlRawAsync("DELETE FROM policy.plan_version WHERE plan_id = {0}", f.PlanId);
            await db.Database.ExecuteSqlRawAsync("DELETE FROM policy.plan WHERE plan_id = {0}", f.PlanId);
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE policy.enrollment_event ENABLE TRIGGER trg_enrollment_event_append_only");
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE policy.plan_version ENABLE TRIGGER trg_plan_version_immutable");
        }
    }
}
