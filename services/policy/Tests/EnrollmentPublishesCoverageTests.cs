using FluentAssertions;
using Mersal.Audit.Client;
using Mersal.Events;
using Mersal.Data;
using Mersal.Policy.Domain;
using Mersal.Policy.Infrastructure;
using Mersal.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Mersal.Policy.Tests;

/// <summary>
/// Phase 24 Gate 3 — INV-ELIGIBILITY-MATRIX, producer side.
///
/// <para><b>An enrolled member had no coverage in eligibility-service at all.</b> Coverage generated from a
/// plan version (19.2) was persisted here and announced with <c>CoverageGenerated</c>, whose payload carried
/// <c>categories = coverages.Count</c> — a NUMBER. eligibility-service does not handle that event, and its
/// coverage projection is written by exactly one handler, <c>OnCoverageChanged</c>, whose only publisher was
/// the manual <c>POST /policies/{id}/coverages</c> endpoint. So enrolling a member through the membership
/// path — the path the product actually uses — put nothing in front of the eligibility engine, and every
/// check for that member returned <c>Ineligible — "no active coverage for LAB"</c>. Entitlement the plan
/// grants, refused at the counter, with no error anywhere to explain it.</para>
///
/// <para>Nothing failed. <c>CoverageGenerationParityTests</c> covers this exact seam and stayed green: it
/// projects a generated <c>Coverage</c> into the engine's view type with a HAND-WRITTEN helper that does what
/// the real projection would do if it ever received the row. The stand-in it warns about in its own summary
/// is the projection step, and that is precisely where the wire was missing. A test can only prove the seam
/// it actually crosses.</para>
///
/// <para>So this asserts what was published, from a real <c>EnrollAsync</c> against a real database, in the
/// vocabulary the consumer reads — the field names below are the contract with
/// <c>ProjectionUpdater.OnCoverageChanged</c>. Env-gated on <c>POLICY_TEST_DB</c>.</para>
/// </summary>
/// <remarks>
/// [Collection("policy-db")] is load-bearing, not decoration. Teardown lifts the append-only trigger on
/// enrollment_event for the duration of its DELETEs and restores it in a finally — and a sibling class doing
/// the same thing in parallel re-enables it mid-teardown, so the DELETE meets a trigger that was supposed to
/// be off and dies with "enrollment_event is append-only". It passed locally and failed in CI, which is the
/// signature of a scheduling race rather than a logic error.
/// </remarks>
[Collection("policy-db")]
public class EnrollmentPublishesCoverageTests
{
    private static readonly string? Db = Environment.GetEnvironmentVariable("POLICY_TEST_DB");
    private const string Tenant = "11111111-1111-1111-1111-111111111111";

    // 24.x — the same tenant stamper the API composes. EnrollAsync writes enrolment, coverage and an
    // append-only event; without the interceptor they carry the entity default of "" and the
    // ck_*_tenant_not_blank constraints refuse them, which is exactly what production would do to a request
    // with no tenant bound.
    private static PolicyDbContext Ctx() => new(new DbContextOptionsBuilder<PolicyDbContext>()
        .UseNpgsql(Db).UseSnakeCaseNamingConvention()
        .AddInterceptors(new TenantStampingInterceptor(new RlsContext { TenantId = Tenant }))
        .Options);

    /// <summary>Enrolling must announce EVERY generated coverage as a row-level event, not a count.</summary>
    [SkippableFact]
    public async Task Enrolling_publishes_one_coverage_event_per_generated_coverage()
    {
        Skip.If(Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        var f = await SeedAsync(waitingDays: 0);
        try
        {
            var outbox = new RecordingOutbox();
            var result = await EnrollAsync(f, outbox);
            result.Ok.Should().BeTrue("the fixture is a valid enrolment; error was {0}", result.Error?.Code);
            result.Value!.CoverageCount.Should().Be(2, "the plan version covers two categories");

            var events = outbox.Of("CoverageChanged");
            events.Should().HaveCount(2,
                "eligibility-service builds its coverage projection from CoverageChanged and from nothing " +
                "else; announcing only a COUNT of generated coverages leaves an enrolled member with no " +
                "coverage rows and a hard Ineligible at every check");
            events.Select(e => Str(e, "category")).Should().BeEquivalentTo(["LAB", "PHARMACY"]);
        }
        finally { await CleanupAsync(f); }
    }

    /// <summary>Every field the consumer reads must be present and correct — a published event that omits
    /// one is a projection row that is silently wrong rather than absent, which is harder to notice.</summary>
    [SkippableFact]
    public async Task The_published_coverage_carries_every_field_the_projection_reads()
    {
        Skip.If(Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        var f = await SeedAsync(waitingDays: 0);
        try
        {
            var outbox = new RecordingOutbox();
            var result = await EnrollAsync(f, outbox);
            var lab = outbox.Of("CoverageChanged").Single(e => Str(e, "category") == "LAB");

            Guid.Parse(Str(lab, "coverageId")!).Should().NotBeEmpty();
            Str(lab, "beneficiaryId").Should().Be(result.Value!.Enrollment.BeneficiaryId.ToString());
            Str(lab, "status").Should().Be("Active");
            // policyNo is what PolicyChanged cascades on: a coverage published without it is invisible to
            // suspend/reactivate, so the member would keep their benefit through a suspended policy.
            Str(lab, "policyNo").Should().Be(f.PolicyNo);
            Str(lab, "effectiveFrom").Should().Be("2026-01-01");
            lab.Should().ContainKey("limits", "the accumulator the engine reads for remaining balance");
        }
        finally { await CleanupAsync(f); }
    }

    /// <summary>19.2 — the boundary travels with the coverage. policy-service owns it (it is a function of
    /// the plan's benefit rule and the enrolment date, neither of which eligibility-service holds), so if it
    /// is not on this event the consumer cannot enforce the waiting period at all.</summary>
    [SkippableFact]
    public async Task The_published_coverage_carries_the_waiting_period_boundary()
    {
        Skip.If(Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        var f = await SeedAsync(waitingDays: 30);
        try
        {
            var outbox = new RecordingOutbox();
            await EnrollAsync(f, outbox);
            var lab = outbox.Of("CoverageChanged").Single(e => Str(e, "category") == "LAB");

            // Enrolled 1 Jan with a 30-day wait: the last day INSIDE the period is 30 Jan.
            Str(lab, "waitingPeriodEndsOn").Should().Be("2026-01-30",
                "without the boundary on the event the member is payable from day one and the waiting " +
                "period the plan sells is unenforceable");

            // Per CATEGORY, not the enrolment-wide maximum: PHARMACY has no waiting period here, and
            // publishing the longest wait across categories would delay a benefit that starts immediately.
            var pharmacy = outbox.Of("CoverageChanged").Single(e => Str(e, "category") == "PHARMACY");
            Str(pharmacy, "waitingPeriodEndsOn").Should().BeNull(
                "this category imposes no waiting period; the enrolment-level summary date is a different " +
                "value for a different purpose (what the member is told), not this one");
        }
        finally { await CleanupAsync(f); }
    }

    /// <summary>The other half of the same invariant, and the one that was still missing after the enrolment
    /// fix above: a TERMINATED member must stop being covered. Termination announced only
    /// <c>MemberTerminated</c>, which the consumer's switch has no case for and drops through its default —
    /// so every coverage row stayed exactly as the enrolment published it, open-ended, and the engine went on
    /// answering Eligible at the counter for a membership that had ended.</summary>
    [SkippableFact]
    public async Task Terminating_publishes_a_coverage_event_closing_every_window()
    {
        Skip.If(Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        var f = await SeedAsync(waitingDays: 0);
        try
        {
            var enrolled = await EnrollAsync(f, new RecordingOutbox());
            var outbox = new RecordingOutbox();
            var result = await TerminateAsync(enrolled.Value!.Enrollment.EnrollmentId, outbox);
            result.Ok.Should().BeTrue("the membership is active; error was {0}", result.Error?.Code);

            var events = outbox.Of("CoverageChanged");
            events.Should().HaveCount(2,
                "eligibility-service reads CoverageChanged and nothing else; MemberTerminated alone leaves " +
                "every coverage row open-ended and the member Eligible after their membership ended");
            events.Select(e => Str(e, "category")).Should().BeEquivalentTo(["LAB", "PHARMACY"]);
            events.Select(e => Str(e, "effectiveTo")).Should().AllBe("2026-06-30",
                "the end date IS the fact that closes the cover — the engine reads effectiveTo");
        }
        finally { await CleanupAsync(f); }
    }

    /// <summary>And reinstatement must reopen it. The end date is cleared with an explicit null, which
    /// <c>OnCoverageChanged</c> reads as "clear" under the same absent-vs-null rule it applies to the waiting
    /// period — otherwise a reinstated member keeps the date their termination wrote and stays refused.</summary>
    [SkippableFact]
    public async Task Reinstating_publishes_a_coverage_event_reopening_every_window()
    {
        Skip.If(Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        var f = await SeedAsync(waitingDays: 0);
        try
        {
            var enrolled = await EnrollAsync(f, new RecordingOutbox());
            var enrollmentId = enrolled.Value!.Enrollment.EnrollmentId;
            await TerminateAsync(enrollmentId, new RecordingOutbox());

            var outbox = new RecordingOutbox();
            var result = await ReinstateAsync(enrollmentId, outbox);
            result.Ok.Should().BeTrue("a terminated membership is reinstatable; error was {0}", result.Error?.Code);

            var events = outbox.Of("CoverageChanged");
            events.Should().HaveCount(2);
            events.Should().OnlyContain(e => e.ContainsKey("effectiveTo"),
                "an ABSENT property means 'unchanged' to the consumer, so the clear has to be an explicit null");
            events.Select(e => Str(e, "effectiveTo")).Should().AllSatisfy(v => v.Should().BeNull(
                "reinstatement lifts the end date; leaving it set keeps the member refused at the counter"));
        }
        finally { await CleanupAsync(f); }
    }

    // ---- harness -----------------------------------------------------------------------------------------

    private sealed record Fixture(
        Guid PolicyId, Guid PlanId, Guid PlanVersionId, Guid PolicyPlanId, string PolicyNo,
        Guid LabCategoryId, Guid PharmacyCategoryId, Guid BeneficiaryId);

    private static async Task<MembershipResult<EnrollOutcome>> EnrollAsync(Fixture f, IOutbox outbox)
    {
        await using var db = Ctx();
        var commands = new MembershipCommands(
            db,
            new StubProbe("Active"),
            new StubMemberNos(),
            new StubAudit(),
            outbox,
            new FixedCalendar(new DateOnly(2026, 1, 1)),
            Options.Create(new MembershipOptions()),
            TimeProvider.System);

        return await commands.EnrollAsync(
            new EnrollCommand(f.BeneficiaryId, f.PolicyId, f.PolicyPlanId, null, "Principal", null,
                new DateOnly(2026, 1, 1), null, null, null),
            idempotencyKey: Guid.NewGuid().ToString("N"),
            bearerToken: null,
            actor: new ActorRef(Guid.NewGuid(), "gate-3"));
    }

    private static async Task<MembershipResult<Enrollment>> TerminateAsync(Guid enrollmentId, IOutbox outbox)
    {
        await using var db = Ctx();
        return await Commands(db, outbox).TerminateAsync(
            enrollmentId, new DateOnly(2026, 6, 30), "gate-3 termination", maySupervise: false,
            new ActorRef(Guid.NewGuid(), "gate-3"));
    }

    private static async Task<MembershipResult<Enrollment>> ReinstateAsync(Guid enrollmentId, IOutbox outbox)
    {
        await using var db = Ctx();
        return await Commands(db, outbox).ReinstateAsync(
            enrollmentId, new DateOnly(2026, 7, 1), "gate-3 reinstatement",
            new ActorRef(Guid.NewGuid(), "gate-3"));
    }

    private static MembershipCommands Commands(PolicyDbContext db, IOutbox outbox) => new(
        db,
        new StubProbe("Active"),
        new StubMemberNos(),
        new StubAudit(),
        outbox,
        new FixedCalendar(new DateOnly(2026, 1, 1)),
        Options.Create(new MembershipOptions()),
        TimeProvider.System);

    private static async Task<Fixture> SeedAsync(int waitingDays)
    {
        await using var db = Ctx();
        // benefit_category.code is unique and LAB/PHARMACY are seeded master data on any migrated database.
        // Reuse them rather than inserting: a test that owns a shared reference row would either collide
        // here or delete a category other rows point at during cleanup.
        var lab = await GetOrAddCategoryAsync(db, "LAB", "Lab");
        var pharmacy = await GetOrAddCategoryAsync(db, "PHARMACY", "Pharmacy");
        var plan = new Plan
        {
            PlanId = Guid.NewGuid(), TenantId = Tenant, PlanCode = $"P{Guid.NewGuid():N}"[..12],
            NameEn = "Gate3", NameAr = "Gate3", Category = "Primary",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        var version = new PlanVersion
        {
            PlanVersionId = Guid.NewGuid(), TenantId = Tenant, PlanId = plan.PlanId, VersionNo = 1,
            EffectiveFrom = new DateOnly(2026, 1, 1),
            // Rules go in while the version is a DRAFT: a database trigger makes an Active version's rules
            // immutable ("amend the plan to create a new version"), which is the product rule that stops a
            // member's entitlement changing under them. The fixture activates it below, as an amendment would.
            Status = PlanVersionStatus.Draft,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            Rules =
            [
                Rule(lab.BenefitCategoryId, waitingDays),
                Rule(pharmacy.BenefitCategoryId, 0),
            ],
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
            EffectiveFrom = new DateOnly(2026, 1, 1), IsDefault = true,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Plans.Add(plan);
        db.PlanVersions.Add(version);
        db.Policies.Add(policy);
        db.PolicyPlans.Add(policyPlan);
        await db.SaveChangesAsync();

        version.Status = PlanVersionStatus.Active;
        version.ActivatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        return new Fixture(policy.PolicyId, plan.PlanId, version.PlanVersionId, policyPlan.PolicyPlanId,
            policy.PolicyNo, lab.BenefitCategoryId, pharmacy.BenefitCategoryId, Guid.NewGuid());
    }

    private static async Task<BenefitCategory> GetOrAddCategoryAsync(PolicyDbContext db, string code, string name)
    {
        var existing = await db.BenefitCategories.FirstOrDefaultAsync(c => c.Code == code);
        if (existing is not null) return existing;
        var created = new BenefitCategory
        {
            BenefitCategoryId = Guid.NewGuid(), TenantId = Tenant, Code = code, Name = name,
        };
        db.BenefitCategories.Add(created);
        await db.SaveChangesAsync();
        return created;
    }

    private static BenefitRule Rule(Guid categoryId, int waitingDays) => new()
    {
        RuleId = Guid.NewGuid(), TenantId = Tenant, BenefitCategoryId = categoryId, IsCovered = true,
        LimitType = LimitType.Annual, LimitValue = 5000m, ResetPeriod = ResetPeriod.Yearly,
        WaitingPeriodDays = waitingDays,
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
    };

    /// <summary>Same shape as EnrollmentStoreTests.Cleanup: enrollment_event is append-only and plan_version
    /// is immutable by trigger, and a fixture must not be able to erase either guard for real code — so the
    /// triggers are lifted only for the duration of the teardown and restored in a finally.
    /// benefit_rule cascades from plan_version, whose own guard stands down once the parent row is gone.
    /// Benefit categories are shared reference data this fixture borrows and never deletes.</summary>
    private static async Task CleanupAsync(Fixture f)
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

    private static string? Str(IReadOnlyDictionary<string, object?> e, string key) =>
        e.TryGetValue(key, out var v) ? v?.ToString() : null;

    /// <summary>Captures what was enqueued as a flat field bag, so the assertions read in the consumer's
    /// vocabulary rather than against a serializer's idea of the shape.</summary>
    private sealed class RecordingOutbox : IOutbox
    {
        private readonly List<(string Type, Dictionary<string, object?> Fields)> sent = [];

        public ValueTask EnqueueAsync<T>(string eventType, string destination, T payload, CancellationToken ct = default)
        {
            var fields = typeof(T).GetProperties()
                .ToDictionary(p => p.Name, p => Normalize(p.GetValue(payload)));
            sent.Add((eventType, fields));
            return ValueTask.CompletedTask;
        }

        public ValueTask EnqueueRawAsync(OutboxMessage message, CancellationToken ct = default) => ValueTask.CompletedTask;

        public IReadOnlyList<IReadOnlyDictionary<string, object?>> Of(string eventType) =>
            [.. sent.Where(s => s.Type == eventType).Select(s => (IReadOnlyDictionary<string, object?>)s.Fields)];

        // Dates are compared as the wire renders them (ISO yyyy-MM-dd), not as .NET's default ToString,
        // so a test cannot pass on a format the consumer would fail to parse.
        private static object? Normalize(object? v) => v switch
        {
            DateOnly d => d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            _ => v,
        };
    }

    private sealed class StubProbe(string status) : IBeneficiaryStatusProbe
    {
        public Task<string?> GetStatusAsync(Guid beneficiaryId, string? bearerToken, CancellationToken ct = default) =>
            Task.FromResult<string?>(status);
    }

    private sealed class StubMemberNos : IMemberNoIssuer
    {
        public Task<string> NextAsync(DateOnly effectiveFrom, CancellationToken ct = default) =>
            Task.FromResult($"MEM-{Guid.NewGuid():N}"[..16]);
    }

    private sealed class StubAudit : IAuditClient
    {
        public ValueTask EmitAsync(AuditEventDraft draft, CancellationToken ct = default) => ValueTask.CompletedTask;
    }

    private sealed class FixedCalendar(DateOnly today) : IBusinessCalendar
    {
        public DateOnly Today() => today;
        public DateOnly DateOf(DateTimeOffset instant) => DateOnly.FromDateTime(instant.UtcDateTime);
        public TimeZoneInfo Zone => TimeZoneInfo.Utc;
    }
}
