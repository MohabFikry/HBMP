using FluentAssertions;
using Mersal.Policy.Domain;
using Mersal.Policy.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Policy.Tests;

/// <summary>
/// Where the Logs tab starts (19.6e).
///
/// <para><b>The defect these exist for.</b> The timeline is newest-first and cursor-paged, so the line every
/// reader wants first — when this membership began, and who began it — was the one guaranteed to be furthest
/// away, behind however many "load older" pages the record had earned. Worse, `MemberEnrolled` is only written
/// by the enrolment command: memberships created by bulk intake, by a migration, or before the projection
/// existed have no such entry at all, and their history simply began mid-sentence with a plan change.</para>
///
/// <para>The fallback is held to the same standard as the rest of the log: it reads facts the record already
/// holds — the append-only enrolment event, or failing that the row's own creation stamp — and invents no
/// actor to sign them with. The endpoint marks the result derived so the reader is told which kind of line
/// they are looking at.</para>
/// </summary>
[Collection("policy-db")]
public class TimelineOriginTests
{
    private static readonly string? Db = Environment.GetEnvironmentVariable("POLICY_TEST_DB");
    private const string Tenant = "11111111-1111-1111-1111-111111111111";

    private static PolicyDbContext Ctx() => new(new DbContextOptionsBuilder<PolicyDbContext>()
        .UseNpgsql(Db).UseSnakeCaseNamingConvention().Options);

    [SkippableFact]
    public async Task The_projected_enrolment_entry_is_the_origin_when_one_exists()
    {
        Skip.If(Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        var f = await Seed();
        try
        {
            await using var db = Ctx();
            await Project(db, f.Member, "MemberEnrolled", At(3, 1), "Mona Adel");
            await Project(db, f.Member, "MemberPlanChanged", At(5, 1), "Layla Mansour");

            var origin = await TimelineOriginQuery.ForMemberAsync(db, f.Member);

            origin.Should().NotBeNull();
            origin!.IsDerived.Should().BeFalse();
            // The real entry, with the actor as it was snapshotted at write time — not a reconstruction of it.
            origin.Projected!.EventType.Should().Be("MemberEnrolled");
            origin.Projected.ActorDisplay.Should().Be("Mona Adel");
        }
        finally { await Cleanup(f); }
    }

    [SkippableFact]
    public async Task The_EARLIEST_enrolment_entry_wins_when_a_record_carries_more_than_one()
    {
        Skip.If(Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        var f = await Seed();
        try
        {
            await using var db = Ctx();
            await Project(db, f.Member, "MemberEnrolled", At(3, 1), "Mona Adel");
            // A cancelled membership re-enrolled months later has two. The history starts at the first one.
            await Project(db, f.Member, "MemberEnrolled", At(9, 14), "Amal Nabil");

            var origin = await TimelineOriginQuery.ForMemberAsync(db, f.Member);

            origin!.Projected!.OccurredAt.Should().Be(At(3, 1));
        }
        finally { await Cleanup(f); }
    }

    [SkippableFact]
    public async Task A_membership_with_no_projected_entry_is_anchored_on_its_enrolment_event()
    {
        Skip.If(Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        var f = await Seed();
        try
        {
            await using var db = Ctx();
            db.EnrollmentEvents.Add(new EnrollmentEvent
            {
                EventId = Guid.NewGuid(), TenantId = Tenant, EnrollmentId = f.Member,
                EventType = EnrollmentEventType.Enrolled, EffectiveDate = new DateOnly(2026, 1, 1),
                Payload = "{}", ActorUserId = Guid.NewGuid(), OccurredAt = At(2, 20),
            });
            await db.SaveChangesAsync();

            var origin = await TimelineOriginQuery.ForMemberAsync(db, f.Member);

            origin!.IsDerived.Should().BeTrue();
            // When the enrolment was DECIDED, which on a back-dated or imported membership is not when the
            // row happened to be written.
            origin.DerivedAt.Should().Be(At(2, 20));
        }
        finally { await Cleanup(f); }
    }

    [SkippableFact]
    public async Task A_membership_with_neither_falls_back_to_when_the_record_was_created()
    {
        Skip.If(Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        var f = await Seed();
        try
        {
            await using var db = Ctx();
            var origin = await TimelineOriginQuery.ForMemberAsync(db, f.Member);

            origin!.IsDerived.Should().BeTrue();
            origin.DerivedAt.Should().BeCloseTo(f.CreatedAt, TimeSpan.FromSeconds(1));
        }
        finally { await Cleanup(f); }
    }

    [SkippableFact]
    public async Task An_unknown_membership_has_no_origin_rather_than_one_stamped_now()
    {
        Skip.If(Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        await using var db = Ctx();

        // A log that anchors an id it has never heard of on the current clock answers a question nobody asked
        // with a value nobody can check.
        (await TimelineOriginQuery.ForMemberAsync(db, Guid.NewGuid())).Should().BeNull();
    }

    // ---- Fixture -----------------------------------------------------------------------------------------

    private static DateTimeOffset At(int month, int day) => new(2026, month, day, 9, 0, 0, TimeSpan.Zero);

    private static async Task Project(
        PolicyDbContext db, Guid member, string eventType, DateTimeOffset at, string actor)
    {
        // Through the projection itself, so the fixture cannot drift into writing rows the projector never
        // would — the entry id is derived from the source event, not chosen here.
        var source = new TimelineSource(
            Guid.NewGuid(), eventType, NoteScope.Member, member, at, "policy-service",
            Guid.NewGuid(), "officer.mona", actor);
        db.TimelineEntries.Add(TimelineProjection.Project(source, Tenant, DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();
    }

    private sealed record Fixture(
        string Prefix, Guid PayerId, Guid PolicyId, Guid PolicyPlanId, Guid PlanId, Guid PlanVersionId,
        Guid Member, DateTimeOffset CreatedAt);

    /// <summary>One membership, with nothing else recorded against it — which is exactly the state the
    /// derived cases are about.</summary>
    private static async Task<Fixture> Seed()
    {
        await using var db = Ctx();
        var prefix = $"O{Guid.NewGuid():N}"[..9].ToUpperInvariant();

        var payer = new Payer
        {
            PayerId = Guid.NewGuid(), TenantId = Tenant, PayerCode = prefix[..8],
            NameEn = "Origin Payer", NameAr = "Origin Payer", PayerType = PayerType.Donor,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        var plan = new Plan
        {
            PlanId = Guid.NewGuid(), TenantId = Tenant, PlanCode = $"{prefix}PL",
            NameEn = "Origin", NameAr = "Origin", Category = "Primary",
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

        var created = At(1, 15);
        var member = new Enrollment
        {
            EnrollmentId = Guid.NewGuid(), TenantId = Tenant, BeneficiaryId = Guid.NewGuid(),
            PolicyId = policy.PolicyId, PolicyPlanId = policyPlan.PolicyPlanId, MemberNo = $"{prefix}-001",
            Relationship = Relationship.Principal, EffectiveFrom = new DateOnly(2026, 1, 1),
            Status = EnrollmentStatus.Active, CreatedAt = created, UpdatedAt = created,
        };
        db.Enrollments.Add(member);
        await db.SaveChangesAsync();

        return new Fixture(prefix, payer.PayerId, policy.PolicyId, policyPlan.PolicyPlanId,
            plan.PlanId, version.PlanVersionId, member.EnrollmentId, created);
    }

    private static async Task Cleanup(Fixture f)
    {
        await using var db = Ctx();
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            // DELETE on the timeline is permitted only inside a declared rebuild — SET LOCAL outside a
            // transaction is a no-op, which is why this one is scoped.
            await db.Database.ExecuteSqlRawAsync("SET LOCAL app.timeline_rebuild = 'on'");
            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM policy.entity_timeline WHERE scope_ref = {0}", f.Member);
            await tx.CommitAsync();
        }

        // The enrolment log is append-only by trigger — the same rule the fixture is testing around, so the
        // teardown suspends it explicitly rather than pretending the rows can be removed.
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE policy.enrollment_event DISABLE TRIGGER trg_enrollment_event_append_only");
        try
        {
            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM policy.enrollment_event WHERE enrollment_id = {0}", f.Member);
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE policy.enrollment_event ENABLE TRIGGER trg_enrollment_event_append_only");
        }

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
