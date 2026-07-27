using FluentAssertions;
using Mersal.Audit.Client;
using Mersal.Events;
using Mersal.Policy.Domain;
using Mersal.Policy.Infrastructure;
using Mersal.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Mersal.Policy.Tests;

/// <summary>
/// Phase 19.6 — the plan-change DRY RUN against real Postgres (env-gated <c>POLICY_TEST_DB</c>).
///
/// <para>The portal's change-plan dialog must show an officer how remaining limits carry forward before they
/// confirm. A preview that is merely close is worse than none: it would be trusted, and it would be wrong at
/// the moment somebody is deciding whether to move a patient mid-treatment. So the claim under test is not
/// "the preview looks reasonable" but "the preview and the change are the same computation" — asserted by
/// running both against the same member and comparing them field by field.</para>
///
/// <para>The second claim is that the preview WRITES NOTHING. It resolves and validates a change, touching the
/// same tracked entities the real path mutates; a dry run that leaves a modified enrolment behind for some
/// later SaveChanges to flush would be a plan change nobody asked for.</para>
/// </summary>
[Collection("policy-db")]
public class PlanChangePreviewTests
{
    private static readonly string? Db = Environment.GetEnvironmentVariable("POLICY_TEST_DB");
    private const string Tenant = "11111111-1111-1111-1111-111111111111";

    private static PolicyDbContext Ctx() => new(new DbContextOptionsBuilder<PolicyDbContext>()
        .UseNpgsql(Db).UseSnakeCaseNamingConvention().Options);

    // ---- The preview agrees with the change ---------------------------------------------------------------

    [SkippableFact]
    public async Task The_preview_reports_exactly_the_balances_the_change_then_applies()
    {
        Skip.If(Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        var f = await Seed();
        try
        {
            // 300 already consumed against a 1,000 ceiling; the target plan's ceiling is 500. The member has
            // 200 left, not 500 — the exact case the dialog's hint describes, and the one an officer gets
            // wrong when the screen only shows them the new plan's number.
            await Consume(f, f.LabCategoryId, 300m);

            await using var db = Ctx();
            var membership = Commands(db);

            var preview = await membership.PreviewPlanChangeAsync(
                f.EnrollmentId, f.LeanPolicyPlanId, new DateOnly(2026, 6, 1));
            preview.Ok.Should().BeTrue();

            var lab = preview.Value!.Carried.Single(c => c.BenefitCategoryId == f.LabCategoryId);
            lab.LimitValue.Should().Be(500m);
            lab.ConsumedValue.Should().Be(300m);
            lab.Remaining.Should().Be(200m);
            lab.Exhausted.Should().BeFalse();
            preview.Value!.CurrentLimits[f.LabCategoryId].Should().Be(1000m);

            db.ChangeTracker.Clear();
            var applied = await membership.ChangePlanAsync(
                f.EnrollmentId, f.LeanPolicyPlanId, new DateOnly(2026, 6, 1), "moved to the lean plan",
                new ActorRef(Guid.NewGuid(), "tester"));
            applied.Ok.Should().BeTrue();

            // Field by field, not "roughly the same shape". This is the whole guarantee.
            applied.Value!.Carried.Should().BeEquivalentTo(preview.Value!.Carried);
            applied.Value!.DroppedCategories.Should().BeEquivalentTo(preview.Value!.DroppedCategories);
            applied.Value!.ConsumptionPolicy.Should().Be(preview.Value!.ConsumptionPolicy);
        }
        finally { await Cleanup(f); }
    }

    [SkippableFact]
    public async Task The_preview_names_the_benefit_the_new_plan_would_withdraw()
    {
        Skip.If(Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        var f = await Seed();
        try
        {
            // The rich plan covers pharmacy; the lean one does not. Nothing in the CARRIED rows can express
            // that — they describe the new plan, and the new plan has no pharmacy row to describe. Without
            // this list the benefit simply vanishes between one screen and the next.
            await Consume(f, f.PharmacyCategoryId, 120m);

            await using var db = Ctx();
            var preview = await Commands(db).PreviewPlanChangeAsync(
                f.EnrollmentId, f.LeanPolicyPlanId, new DateOnly(2026, 6, 1));

            preview.Ok.Should().BeTrue();
            preview.Value!.Carried.Should().NotContain(c => c.BenefitCategoryId == f.PharmacyCategoryId);
            preview.Value!.DroppedCategories.Should().ContainSingle().Which.Should().Be(f.PharmacyCategoryId);
            // Reported with the figures that are about to stop being covered — a bare id would tell the
            // officer something is being withdrawn without telling them how much of it the member is using.
            preview.Value!.CurrentLimits[f.PharmacyCategoryId].Should().Be(400m);
            preview.Value!.Consumed[f.PharmacyCategoryId].Should().Be(120m);
        }
        finally { await Cleanup(f); }
    }

    // ---- A dry run is dry ---------------------------------------------------------------------------------

    [SkippableFact]
    public async Task The_preview_changes_nothing()
    {
        Skip.If(Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        var f = await Seed();
        try
        {
            await Consume(f, f.LabCategoryId, 300m);

            await using (var db = Ctx())
            {
                var preview = await Commands(db).PreviewPlanChangeAsync(
                    f.EnrollmentId, f.LeanPolicyPlanId, new DateOnly(2026, 6, 1));
                preview.Ok.Should().BeTrue();

                // The resolution loads the enrolment and its coverages. If it loaded them TRACKED, an unrelated
                // SaveChanges later in the same unit of work would flush a plan change nobody confirmed — so
                // the dry run tracks NOTHING, and the tracker is empty rather than merely unmodified.
                db.ChangeTracker.Entries().Should().BeEmpty();
                await db.SaveChangesAsync();
            }

            await using var check = Ctx();
            var enrollment = await check.Enrollments.AsNoTracking()
                .FirstAsync(e => e.EnrollmentId == f.EnrollmentId);
            enrollment.PolicyPlanId.Should().Be(f.RichPolicyPlanId, "the preview must not move the member");

            var coverages = await check.Coverages.AsNoTracking().Include(c => c.Limits)
                .Where(c => c.EnrollmentId == f.EnrollmentId).ToListAsync();
            coverages.Should().HaveCount(2, "no coverage was closed and none regenerated");
            coverages.Should().OnlyContain(c => c.EffectiveTo == null);
            coverages.SelectMany(c => c.Limits).Sum(l => l.ConsumedValue).Should().Be(300m,
                "the accumulator is phase 18's to write, and a dry run is not phase 18");

            var events = await check.EnrollmentEvents.AsNoTracking()
                .CountAsync(e => e.EnrollmentId == f.EnrollmentId && e.EventType == EnrollmentEventType.PlanChanged);
            events.Should().Be(0);
        }
        finally { await Cleanup(f); }
    }

    // ---- It fails where the change fails ------------------------------------------------------------------

    [SkippableFact]
    public async Task The_preview_refuses_a_plan_that_is_not_in_force_on_the_chosen_date()
    {
        Skip.If(Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        var f = await Seed();
        try
        {
            await using var db = Ctx();
            var membership = Commands(db);
            var date = new DateOnly(2025, 12, 1); // before the policy plans open

            var preview = await membership.PreviewPlanChangeAsync(f.EnrollmentId, f.LeanPolicyPlanId, date);
            db.ChangeTracker.Clear();
            var change = await membership.ChangePlanAsync(
                f.EnrollmentId, f.LeanPolicyPlanId, date, "a reason", new ActorRef(Guid.NewGuid(), "tester"));

            // Same refusal, same code. The dialog therefore surfaces the problem while it is still a choice,
            // rather than after the officer has written a justification for a change that could never apply.
            preview.Ok.Should().BeFalse();
            preview.Error!.Code.Should().Be("PLAN_NOT_IN_FORCE");
            change.Error!.Code.Should().Be(preview.Error!.Code);
        }
        finally { await Cleanup(f); }
    }

    [SkippableFact]
    public async Task The_preview_needs_no_reason_where_the_change_demands_one()
    {
        Skip.If(Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        var f = await Seed();
        try
        {
            await using var db = Ctx();
            var membership = Commands(db);

            // Deliberate asymmetry: an officer cannot be asked to justify a decision before being shown what
            // it does. The mandatory reason belongs to the act, not to looking.
            var preview = await membership.PreviewPlanChangeAsync(
                f.EnrollmentId, f.LeanPolicyPlanId, new DateOnly(2026, 6, 1));
            preview.Ok.Should().BeTrue();

            db.ChangeTracker.Clear();
            var change = await membership.ChangePlanAsync(
                f.EnrollmentId, f.LeanPolicyPlanId, new DateOnly(2026, 6, 1), "   ",
                new ActorRef(Guid.NewGuid(), "tester"));
            change.Ok.Should().BeFalse();
            change.Error!.Code.Should().Be("REASON_REQUIRED");
        }
        finally { await Cleanup(f); }
    }

    // ---- Fixture ------------------------------------------------------------------------------------------

    private sealed record Fixture(
        Guid PolicyId, Guid RichPlanId, Guid LeanPlanId, Guid RichPolicyPlanId, Guid LeanPolicyPlanId,
        Guid EnrollmentId, Guid LabCategoryId, Guid PharmacyCategoryId);

    private static MembershipCommands Commands(PolicyDbContext db)
    {
        var clock = TimeProvider.System;
        return new MembershipCommands(
            db, new AlwaysActive(), new FixedMemberNos(), new NullAudit(), new NullOutbox(),
            new BusinessCalendar(clock), Options.Create(new MembershipOptions()), clock);
    }

    /// <summary>Move the accumulator the way phase 18 does — by raw update, because nothing in this service is
    /// allowed to write <c>consumed_value</c> and a test that used a policy-service path to do it would be
    /// asserting against a writer that must not exist.</summary>
    private static async Task Consume(Fixture f, Guid categoryId, decimal amount)
    {
        await using var db = Ctx();
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE policy.coverage_limit SET consumed_value = {0} WHERE coverage_id IN " +
            "(SELECT coverage_id FROM policy.coverage WHERE enrollment_id = {1} AND benefit_category_id = {2})",
            amount, f.EnrollmentId, categoryId);
    }

    private static async Task<Fixture> Seed()
    {
        await using var db = Ctx();
        var prefix = $"C{Guid.NewGuid():N}"[..9].ToUpperInvariant();

        var lab = await Category(db, "LAB", "Laboratory");
        var pharmacy = await Category(db, "PHARMACY", "Pharmacy");

        // Rich covers LAB 1,000 and PHARMACY 400; lean covers LAB 500 only. Two plans rather than two versions
        // of one, because 0008 forbids two Active policy_plan rows on the same policy and version.
        var rich = NewPlan(prefix, "R");
        var lean = NewPlan(prefix, "L");
        db.Plans.AddRange(rich, lean);
        await db.SaveChangesAsync();

        var richVersion = NewVersion(rich.PlanId, [(lab, 1000m), (pharmacy, 400m)]);
        var leanVersion = NewVersion(lean.PlanId, [(lab, 500m)]);
        var policy = new Domain.Policy
        {
            PolicyId = Guid.NewGuid(), TenantId = Tenant, PolicyNo = $"{prefix}-POL",
            EffectiveFrom = new DateOnly(2026, 1, 1), Status = PolicyStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.PlanVersions.AddRange(richVersion, leanVersion);
        db.Policies.Add(policy);
        await db.SaveChangesAsync();

        // Rules cannot be inserted under a non-Draft version (0005), so both were seeded Draft and are promoted
        // here with the immutability trigger lifted for the duration.
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE policy.plan_version DISABLE TRIGGER trg_plan_version_immutable");
        try
        {
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE policy.plan_version SET status = 'Active', activated_at = now() WHERE plan_version_id = ANY({0})",
                new[] { richVersion.PlanVersionId, leanVersion.PlanVersionId });
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE policy.plan_version ENABLE TRIGGER trg_plan_version_immutable");
        }

        var richPlan = NewPolicyPlan(policy.PolicyId, richVersion.PlanVersionId, "Rich", isDefault: true);
        var leanPlan = NewPolicyPlan(policy.PolicyId, leanVersion.PlanVersionId, "Lean", isDefault: false);
        db.PolicyPlans.AddRange(richPlan, leanPlan);
        await db.SaveChangesAsync();

        var enrollment = new Enrollment
        {
            EnrollmentId = Guid.NewGuid(), TenantId = Tenant, BeneficiaryId = Guid.NewGuid(),
            PolicyId = policy.PolicyId, PolicyPlanId = richPlan.PolicyPlanId,
            MemberNo = $"MEM-PRV-{Guid.NewGuid():N}"[..24], Relationship = Relationship.Principal,
            EffectiveFrom = new DateOnly(2026, 1, 1), Status = EnrollmentStatus.Active,
            SourcePlanVersionId = richVersion.PlanVersionId,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Enrollments.Add(enrollment);
        await db.SaveChangesAsync();

        // Generated the same way an enrolment generates them, so the preview is reading real coverage.
        var loaded = await db.PlanVersions.AsNoTracking().Include(v => v.Rules)
            .FirstAsync(v => v.PlanVersionId == richVersion.PlanVersionId);
        foreach (var coverage in CoverageGenerator.Generate(loaded, enrollment, Tenant))
        {
            coverage.EnrollmentId = enrollment.EnrollmentId;
            coverage.SourcePlanVersionId = richVersion.PlanVersionId;
            db.Coverages.Add(coverage);
        }
        await db.SaveChangesAsync();

        return new Fixture(policy.PolicyId, rich.PlanId, lean.PlanId, richPlan.PolicyPlanId,
            leanPlan.PolicyPlanId, enrollment.EnrollmentId, lab, pharmacy);
    }

    private static async Task<Guid> Category(PolicyDbContext db, string code, string name)
    {
        var existing = await db.BenefitCategories.FirstOrDefaultAsync(c => c.Code == code);
        if (existing is not null) return existing.BenefitCategoryId;
        var created = new BenefitCategory
        {
            BenefitCategoryId = Guid.NewGuid(), TenantId = Tenant, Code = code, Name = name,
        };
        db.BenefitCategories.Add(created);
        await db.SaveChangesAsync();
        return created.BenefitCategoryId;
    }

    private static Plan NewPlan(string prefix, string suffix) => new()
    {
        PlanId = Guid.NewGuid(), TenantId = Tenant, PlanCode = $"{prefix}{suffix}",
        NameEn = "Preview", NameAr = "Preview", Category = "Primary",
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static PlanVersion NewVersion(Guid planId, IReadOnlyList<(Guid Category, decimal Limit)> rules) => new()
    {
        PlanVersionId = Guid.NewGuid(), TenantId = Tenant, PlanId = planId, VersionNo = 1,
        EffectiveFrom = new DateOnly(2026, 1, 1), Status = PlanVersionStatus.Draft, ActivatedAt = null,
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        Rules =
        [
            .. rules.Select(r => new BenefitRule
            {
                RuleId = Guid.NewGuid(), TenantId = Tenant, BenefitCategoryId = r.Category,
                IsCovered = true, LimitType = LimitType.Annual, LimitValue = r.Limit,
                ResetPeriod = ResetPeriod.Yearly, WaitingPeriodDays = 0, Exclusions = "[]",
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            }),
        ],
    };

    private static PolicyPlan NewPolicyPlan(Guid policyId, Guid versionId, string label, bool isDefault) => new()
    {
        PolicyPlanId = Guid.NewGuid(), TenantId = Tenant, PolicyId = policyId, PlanVersionId = versionId,
        PlanLabel = label, EffectiveFrom = new DateOnly(2026, 1, 1), IsDefault = isDefault,
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
    };

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
            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM policy.coverage_limit WHERE coverage_id IN " +
                "(SELECT coverage_id FROM policy.coverage WHERE policy_id = {0})", f.PolicyId);
            await db.Database.ExecuteSqlRawAsync("DELETE FROM policy.coverage WHERE policy_id = {0}", f.PolicyId);
            await db.Database.ExecuteSqlRawAsync("DELETE FROM policy.enrollment WHERE policy_id = {0}", f.PolicyId);
            await db.Database.ExecuteSqlRawAsync("DELETE FROM policy.policy_plan WHERE policy_id = {0}", f.PolicyId);
            await db.Database.ExecuteSqlRawAsync("DELETE FROM policy.policy WHERE policy_id = {0}", f.PolicyId);
            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM policy.plan_version WHERE plan_id = ANY({0})", new[] { f.RichPlanId, f.LeanPlanId });
            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM policy.plan WHERE plan_id = ANY({0})", new[] { f.RichPlanId, f.LeanPlanId });
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE policy.enrollment_event ENABLE TRIGGER trg_enrollment_event_append_only");
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE policy.plan_version ENABLE TRIGGER trg_plan_version_immutable");
        }
    }

    private sealed class NullAudit : IAuditClient
    {
        public ValueTask EmitAsync(AuditEventDraft draft, CancellationToken ct = default) => ValueTask.CompletedTask;
    }

    private sealed class NullOutbox : IOutbox
    {
        public ValueTask EnqueueAsync<T>(string eventType, string destination, T payload, CancellationToken ct = default)
            => ValueTask.CompletedTask;

        public ValueTask EnqueueRawAsync(OutboxMessage message, CancellationToken ct = default)
            => ValueTask.CompletedTask;
    }

    private sealed class AlwaysActive : IBeneficiaryStatusProbe
    {
        public Task<string?> GetStatusAsync(Guid beneficiaryId, string? bearerToken, CancellationToken ct = default)
            => Task.FromResult<string?>("Active");
    }

    private sealed class FixedMemberNos : IMemberNoIssuer
    {
        public Task<string> NextAsync(DateOnly effectiveFrom, CancellationToken ct = default)
            => Task.FromResult($"PRV-{Guid.NewGuid():N}"[..16]);
    }
}
