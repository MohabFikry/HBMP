using System.Text.Json;
using Mersal.Audit.Client;
using Mersal.Events;
using System.Globalization;
using Mersal.Policy.Domain;
using Mersal.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Mersal.Policy.Infrastructure;

// Phase 19.5b — the membership write path, extracted from the HTTP handlers so the BULK engine and the SINGLE
// member form execute the same code.
//
// ============================================================================================================
// WHY THIS EXISTS
// ============================================================================================================
// 19.5b's own instruction is "REUSE those patterns, do not invent a second importer". The sharpest version of
// that is not about the loader's shape — it is about the RULES. If the bulk path re-implements "is the plan in
// force", "does the member meet the eligibility rule", "is the beneficiary Active", then the file becomes a way
// to create memberships the form would have refused, and nobody finds out until a claim is denied against
// coverage that should never have been generated.
//
// So the rules live here, once, and both callers ask this class. The HTTP layer keeps what is genuinely its
// own: authorization, idempotency-header handling, and turning a failure into an RFC 7807 body.

public enum MembershipFailureKind { Invalid, Conflict, Unprocessable, NotFound, Forbidden }

public sealed record MembershipError(
    MembershipFailureKind Kind, string Code, string Detail, IReadOnlyList<string>? Failures = null);

public sealed record MembershipResult<T>(T? Value, MembershipError? Error)
{
    public bool Ok => Error is null;
}

/// <summary>Factories for <see cref="MembershipResult{T}"/>. Non-generic so the helpers are not static members
/// of a generic type, which reads as <c>MembershipResults.Fail&lt;T&gt;(...)</c> at every call site anyway.</summary>
public static class MembershipResults
{
    public static MembershipResult<T> Success<T>(T value) => new(value, null);

    public static MembershipResult<T> Fail<T>(
        MembershipFailureKind kind, string code, string detail, IReadOnlyList<string>? failures = null) =>
        new(default, new MembershipError(kind, code, detail, failures));
}

/// <summary>Who is making the change. Passed explicitly rather than read from the request gate, because a
/// bulk row is applied outside the request that submitted it.</summary>
/// <summary>
/// Who performed a membership change.
///
/// <param name="Subject">The token subject — a uuid. Machine-stable and unreadable.</param>
/// <param name="Display">The human name off the token (<c>name</c> / <c>preferred_username</c>), snapshotted
/// at write time like every other signature on this platform. Without it the member's history says a change
/// was made by <c>129d2a05-8c27-43c7-aae2-f2cc4c7fda30</c>, which answers "who did this" only for somebody
/// willing to go and look the id up — so in practice it does not answer it.</param>
/// </summary>
public sealed record ActorRef(Guid? UserId, string? Subject, string? Display = null);

public sealed record EnrollCommand(
    Guid BeneficiaryId, Guid PolicyId, Guid? PolicyPlanId, Guid? GroupId, string Relationship,
    Guid? PrincipalEnrollmentId, DateOnly EffectiveFrom, DateOnly? EffectiveTo, Guid? BranchId, int? AgeYears);

public sealed record EnrollOutcome(Enrollment Enrollment, int CoverageCount, bool WasReplay);
public sealed record GroupChangeOutcome(Enrollment Enrollment, Guid? PreviousGroupId);
/// <summary>The analytic dimensions every membership event carries (19.6b). Flat, and spread into the payload
/// rather than nested, because the projector reads a flat field bag — a nested object would arrive as one
/// unparsed string and the dashboard would aggregate everything into "unknown".</summary>
public sealed record EventDimensions(
    Guid? PayerId, Guid PolicyId, Guid? PolicyPlanId, Guid? GroupId, Guid? BranchId,
    string Relationship, string Status);

public sealed record PlanChangeOutcome(
    Enrollment Enrollment, PolicyPlan Plan, Guid PreviousPolicyPlanId, Guid PlanVersionId,
    string ConsumptionPolicy, IReadOnlyList<CarriedLimit> Carried,
    /// <summary>Categories the member held that the new plan does not cover. Reported on the outcome as well as
    /// the preview so the confirmation and the dry-run answer the same question.</summary>
    IReadOnlyList<Guid> DroppedCategories);

/// <summary>A plan change resolved and validated but not applied — the shared middle step between the dry-run
/// and the change (19.6). Internal to the membership layer; the HTTP layer sees an outcome or a preview.</summary>
/// <param name="Existing">The member's current coverages, tracked when the caller intends to close them.</param>
/// <param name="CurrentLimits">Category → the ceiling in force TODAY, null where unbounded. Carried on the
/// preview only: an officer comparing plans needs the number being moved away from, and the change response
/// has never carried it because after the change there is no "before" left to report.</param>
/// <param name="Consumed">Category → what the member has already used. Read from the phase-18 accumulator and
/// carried onto the preview so a category the new plan DROPS can still report the usage that is about to stop
/// being covered — the arithmetic the carried rows do not contain because they only describe the new plan.</param>
public sealed record PlanChangePlan(
    Enrollment Enrollment, PolicyPlan Plan, PlanVersion Version, IReadOnlyList<Coverage> Existing,
    PlanChangeConsumptionPolicy ConsumptionPolicy, IReadOnlyList<CarriedLimit> Carried,
    IReadOnlyDictionary<Guid, decimal?> CurrentLimits, IReadOnlyDictionary<Guid, decimal> Consumed,
    IReadOnlyList<Guid> DroppedCategories);

/// <summary>What a plan change WOULD do. Nothing here has been written.</summary>
public sealed record PlanChangePreview(
    Guid EnrollmentId, Guid FromPolicyPlanId, PolicyPlan ToPlan, Guid PlanVersionId, DateOnly EffectiveDate,
    string ConsumptionPolicy, IReadOnlyList<CarriedLimit> Carried,
    IReadOnlyDictionary<Guid, decimal?> CurrentLimits, IReadOnlyDictionary<Guid, decimal> Consumed,
    IReadOnlyList<Guid> DroppedCategories);

public sealed class MembershipCommands(
    PolicyDbContext db,
    IBeneficiaryStatusProbe beneficiaries,
    IMemberNoIssuer memberNos,
    IAuditClient audit,
    IOutbox outbox,
    IBusinessCalendar calendar,
    IOptions<MembershipOptions> options,
    TimeProvider clock)
{
    // ---- Enrol -------------------------------------------------------------------------------------------

    /// <param name="establishedStatus">The beneficiary's status when the caller already holds it from an
    /// action it just performed, so the probe would be asking a question it has the answer to. Only the
    /// registration-approval consumer supplies it; everything else passes null and is probed.</param>
    public async Task<MembershipResult<EnrollOutcome>> EnrollAsync(
        EnrollCommand cmd, string idempotencyKey, string? bearerToken, ActorRef actor,
        string? establishedStatus = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(cmd);
        ArgumentNullException.ThrowIfNull(actor);

        // Replay returns the row the caller already created rather than a 409 from the overlap exclusion. For
        // a bulk job this is the whole no-double-apply guarantee: the key is (job, row), so a re-commit of a
        // half-finished job walks straight past every row it already wrote.
        var replay = await db.Enrollments.AsNoTracking()
            .FirstOrDefaultAsync(e => e.IdempotencyKey == idempotencyKey && !e.IsDeleted, ct);
        if (replay is not null)
        {
            var existing = await db.Coverages.AsNoTracking().CountAsync(c => c.EnrollmentId == replay.EnrollmentId, ct);
            return MembershipResults.Success(new EnrollOutcome(replay, existing, WasReplay: true));
        }

        if (!Enum.TryParse<Relationship>(cmd.Relationship, ignoreCase: true, out var relationship))
            return MembershipResults.Fail<EnrollOutcome>(MembershipFailureKind.Invalid,
                "UNKNOWN_RELATIONSHIP", $"'{cmd.Relationship}' is not a relationship.");

        var policy = await db.Policies.AsNoTracking()
            .FirstOrDefaultAsync(p => p.PolicyId == cmd.PolicyId && !p.IsDeleted, ct);
        if (policy is null)
            return MembershipResults.Fail<EnrollOutcome>(MembershipFailureKind.Invalid,
                "UNKNOWN_POLICY", $"Policy {cmd.PolicyId} does not exist.");
        if (policy.Status != PolicyStatus.Active)
            return MembershipResults.Fail<EnrollOutcome>(MembershipFailureKind.Conflict,
                "POLICY_NOT_ACTIVE", $"Policy {policy.PolicyNo} is {policy.Status}.");
        if (cmd.EffectiveFrom < policy.EffectiveFrom || (policy.EffectiveTo is { } pt && cmd.EffectiveFrom > pt))
            return MembershipResults.Fail<EnrollOutcome>(MembershipFailureKind.Unprocessable,
                "OUTSIDE_POLICY_WINDOW", "The enrolment start falls outside the policy's effective window.");

        // The beneficiary must be a real, Active person. Enrolling a Pending or Blocked member would generate
        // coverage that eligibility then refuses on every visit — a membership that looks live in every report
        // and works nowhere.
        //
        // `establishedStatus` is the ONE case where the caller already holds the answer: the registration
        // approval writes the activation and the enrolment event in a single transaction, so the status is
        // not merely known, it was just SET. A background consumer has no user token to probe with, and the
        // alternative — giving policy-service a credential of its own — is the pattern this platform
        // deliberately forbids (see profile-service's NoServiceAccountArchitectureTests). Narrow on purpose:
        // it substitutes for the probe, it does not skip the Active check below.
        var status = establishedStatus
                     ?? await beneficiaries.GetStatusAsync(cmd.BeneficiaryId, bearerToken, ct);
        if (status is null)
            return MembershipResults.Fail<EnrollOutcome>(MembershipFailureKind.Invalid,
                "UNKNOWN_BENEFICIARY", $"Beneficiary {cmd.BeneficiaryId} was not found.");
        if (!string.Equals(status, "Active", StringComparison.OrdinalIgnoreCase))
            return MembershipResults.Fail<EnrollOutcome>(MembershipFailureKind.Unprocessable,
                "BENEFICIARY_NOT_ACTIVE", $"The beneficiary is {status}; only an Active beneficiary can be enrolled.");

        var plan = cmd.PolicyPlanId is { } explicitPlan
            ? await db.PolicyPlans.AsNoTracking().FirstOrDefaultAsync(pp => pp.PolicyPlanId == explicitPlan && !pp.IsDeleted, ct)
            : await db.PolicyPlans.AsNoTracking().FirstOrDefaultAsync(
                pp => pp.PolicyId == cmd.PolicyId && pp.IsDefault && pp.Status == PolicyPlanStatus.Active && !pp.IsDeleted, ct);
        if (plan is null)
            return MembershipResults.Fail<EnrollOutcome>(MembershipFailureKind.Unprocessable, "NO_PLAN",
                cmd.PolicyPlanId is null
                    ? "No plan was named and this policy has no default plan to fall back to."
                    : $"Plan {cmd.PolicyPlanId} does not exist.");
        if (plan.PolicyId != cmd.PolicyId)
            return MembershipResults.Fail<EnrollOutcome>(MembershipFailureKind.Invalid,
                "PLAN_NOT_OF_POLICY", "That plan belongs to a different policy.");
        if (!plan.Covers(cmd.EffectiveFrom))
            return MembershipResults.Fail<EnrollOutcome>(MembershipFailureKind.Unprocessable,
                "PLAN_NOT_IN_FORCE", $"Plan '{plan.PlanLabel}' is not in force on {cmd.EffectiveFrom:yyyy-MM-dd}.");

        if (PlanEligibility.IsMalformed(plan.EligibilityRule))
            return MembershipResults.Fail<EnrollOutcome>(MembershipFailureKind.Conflict, "MALFORMED_ELIGIBILITY_RULE",
                $"Plan '{plan.PlanLabel}' has an unreadable eligibility rule; it cannot be elected onto until it is fixed.");
        var failures = PlanEligibility.Evaluate(
            PlanEligibility.Parse(plan.EligibilityRule),
            new ElectionCandidate(cmd.GroupId, relationship, cmd.AgeYears, cmd.BranchId));
        if (failures.Count > 0)
            return MembershipResults.Fail<EnrollOutcome>(MembershipFailureKind.Unprocessable, "PLAN_ELIGIBILITY_NOT_MET",
                $"The member does not meet plan '{plan.PlanLabel}'s election criteria.",
                // The CRITERION is named, not just "not eligible" — an officer told only that a member failed
                // goes hunting through a plan definition they may not even be able to read.
                [.. failures.Select(f => $"{f.Criterion}: {f.Detail}")]);

        if (plan.MaxMembers is { } cap)
        {
            var onPlan = await db.Enrollments.CountAsync(
                e => e.PolicyPlanId == plan.PolicyPlanId && !e.IsDeleted && e.Status == EnrollmentStatus.Active, ct);
            if (onPlan >= cap)
                return MembershipResults.Fail<EnrollOutcome>(MembershipFailureKind.Conflict,
                    "PLAN_FULL", $"Plan '{plan.PlanLabel}' is at its {cap}-member cap.");
        }

        var version = await db.PlanVersions.AsNoTracking().Include(v => v.Rules)
            .FirstOrDefaultAsync(v => v.PlanVersionId == plan.PlanVersionId, ct);
        if (version is null)
            return MembershipResults.Fail<EnrollOutcome>(MembershipFailureKind.Conflict,
                "PLAN_VERSION_MISSING", "The plan's version could not be loaded.");

        // 24.3 — everything from here to the commit is ONE transaction: the enrolment row, the generated
        // coverages, the append-only enrollment_event, and every domain event announcing them. EfOutbox
        // commits each enqueue on its own SaveChanges, so without this a process kill after the business
        // save leaves a member enrolled whose MemberEnrolled and CoverageChanged events are gone — and
        // nothing records that they were owed, which is precisely how an enrolled member ends up invisible
        // to eligibility-service. Any early return below rolls the whole thing back.
        // Join the caller's transaction when there is one. The bulk engine already wraps each row in
        // one (BulkJobEngine), and opening a second inside it throws — while the invariant is
        // already satisfied there, because that outer transaction covers this write and its events
        // together. Owning one only when nobody else does keeps both callers atomic.
        await using var tx = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(ct)
            : null;
        var now = clock.GetUtcNow();
        var enrollment = new Enrollment
        {
            EnrollmentId = Guid.NewGuid(), BeneficiaryId = cmd.BeneficiaryId, PolicyId = cmd.PolicyId,
            PolicyPlanId = plan.PolicyPlanId, GroupId = cmd.GroupId,
            MemberNo = await memberNos.NextAsync(cmd.EffectiveFrom, ct),
            Relationship = relationship, PrincipalEnrollmentId = cmd.PrincipalEnrollmentId,
            EffectiveFrom = cmd.EffectiveFrom, EffectiveTo = cmd.EffectiveTo,
            WaitingPeriodEndsOn = WaitingPeriod.EndsOn(version, cmd.EffectiveFrom),
            Status = EnrollmentStatus.Active,
            SourcePlanVersionId = version.PlanVersionId,
            BranchId = cmd.BranchId,
            IdempotencyKey = idempotencyKey,
            CreatedAt = now, UpdatedAt = now, CreatedBy = actor.UserId, UpdatedBy = actor.UserId,
        };
        db.Enrollments.Add(enrollment);

        var coverages = CoverageGenerator.Generate(version, enrollment, enrollment.TenantId);
        foreach (var coverage in coverages)
        {
            coverage.SourcePlanVersionId = version.PlanVersionId;
            coverage.EnrollmentId = enrollment.EnrollmentId;
            db.Coverages.Add(coverage);
        }

        // policyPlanId and groupId are on the payload because 19.5b's AS-OF extraction reconstructs the plan a
        // member was on from these events. A payload that records only the plan LABEL cannot survive the label
        // being reused on a renewed policy.
        db.EnrollmentEvents.Add(Event(enrollment, EnrollmentEventType.Enrolled, cmd.EffectiveFrom, null, actor, now,
            new { planLabel = plan.PlanLabel, policyPlanId = plan.PolicyPlanId, groupId = cmd.GroupId, coverages = coverages.Count }));

        if (await SaveOrConflict(ct) is { } conflict) return new MembershipResult<EnrollOutcome>(default, conflict);

        await audit.EmitAsync(Draft("enrollment", enrollment.EnrollmentId, AuditAction.Create, actor), ct);
        await ProjectMemberHistoryAsync(enrollment, "MemberEnrolled", actor, Changed(
            // A creation, so every "before" is genuinely null — which the reader renders as "set to", not as
            // a value that vanished.
            ("status", null, enrollment.Status.ToString()),
            ("plan", null, plan.PlanLabel),
            ("relationship", null, enrollment.Relationship.ToString()),
            ("effectiveFrom", null, Day(enrollment.EffectiveFrom))), ct);
        // 19.6b widened the payload with the ANALYTIC DIMENSIONS (payer, group, branch, relationship). The
        // dashboard aggregates by them, and an event that says "a member was enrolled" without saying under
        // which payer forces every consumer to go back and ask — which for reporting-service means querying
        // the transactional benefit spine, the one thing a read model exists to avoid.
        await outbox.EnqueueAsync("MemberEnrolled", "policy.events", new
        {
            tenantId = enrollment.TenantId, enrollmentId = enrollment.EnrollmentId,
            beneficiaryId = enrollment.BeneficiaryId, policyId = enrollment.PolicyId,
            policyPlanId = plan.PolicyPlanId, enrollment.MemberNo,
            effectiveFrom = enrollment.EffectiveFrom, waitingPeriodEndsOn = enrollment.WaitingPeriodEndsOn,
            payerId = policy.PayerId, groupId = enrollment.GroupId, branchId = enrollment.BranchId,
            relationship = enrollment.Relationship.ToString(), status = enrollment.Status.ToString(),
        }, ct);
        await outbox.EnqueueAsync("CoverageGenerated", "policy.events", new
        {
            tenantId = enrollment.TenantId, enrollmentId = enrollment.EnrollmentId,
            beneficiaryId = enrollment.BeneficiaryId, sourcePlanVersionId = version.PlanVersionId,
            categories = coverages.Count,
        }, ct);

        // ...and one ROW-LEVEL event per generated coverage. CoverageGenerated above announces that
        // generation happened and carries a COUNT; eligibility-service builds its coverage projection from
        // CoverageChanged and from nothing else, so a count told it nothing it could store. Enrolling through
        // this path — the path the product uses — therefore left the member with no coverage rows in front of
        // the eligibility engine, and every check came back Ineligible "no active coverage for <category>":
        // an entitlement the plan grants, refused at the counter, with no error anywhere to explain it.
        //
        // Reusing CoverageChanged rather than teaching consumers a new event is deliberate: the manual
        // POST /policies/{id}/coverages endpoint already publishes exactly this shape, the consumer is
        // already idempotent on it, and one event per coverage means the enrolment path and the manual path
        // cannot drift into two different projections of the same fact.
        //
        // INLINE, not a helper. These enqueues have to be provably inside the transaction opened at the top of
        // this method, and OutboxAtomicityTests proves that by reading the code, not by following calls: a
        // helper would put them in a method body with no transaction in it, indistinguishable from the
        // enqueue-after-commit shape this rule exists to forbid. The check cannot be taught to trust a call
        // graph without also being taught to trust the ones that are genuinely wrong.
        //
        // The consumer keys on the category CODE ("LAB"), not the id — its projection has no category table to
        // resolve a Guid against.
        if (coverages.Count > 0)
        {
            var categoryIds = coverages.Select(c => c.BenefitCategoryId).Distinct().ToList();
            var codes = await db.BenefitCategories.AsNoTracking()
                .Where(c => categoryIds.Contains(c.BenefitCategoryId))
                .ToDictionaryAsync(c => c.BenefitCategoryId, c => c.Code, ct);

            foreach (var coverage in coverages)
            {
                // waitingPeriodEndsOn is PER CATEGORY, from this category's own benefit rule, not the
                // enrolment-level summary date. The enrolment stores the LONGEST wait across categories because
                // that is the single date the member is told; publishing it here would delay every benefit to
                // the slowest one. policy-service owns this boundary because it is a function of the plan rule
                // and the enrolment date, neither of which eligibility-service holds.
                //
                // policyNo is the key PolicyChanged cascades on. A coverage published without it is invisible
                // to suspend/reactivate, so the member would keep their benefit through a suspended policy.
                var rule = version.Rules.FirstOrDefault(r => r.BenefitCategoryId == coverage.BenefitCategoryId);
                await outbox.EnqueueAsync("CoverageChanged", "policy.events", new
                {
                    tenantId = coverage.TenantId,
                    coverageId = coverage.CoverageId,
                    beneficiaryId = coverage.BeneficiaryId,
                    category = codes.GetValueOrDefault(coverage.BenefitCategoryId),
                    status = coverage.Status.ToString(),
                    policyNo = policy.PolicyNo,
                    effectiveFrom = coverage.EffectiveFrom,
                    effectiveTo = coverage.EffectiveTo,
                    // 19.2b — the plan the coverage belongs to, and the version it was written under.
                    //
                    // BOTH, because they answer different questions. The VERSION is provenance: what the
                    // member's cover was projected from, and the fallback when nothing better can be
                    // resolved. The PLAN is what lets a downstream quote resolve the version in force ON THE
                    // SERVICE DATE — which is the rule the whole effective-dated layer exists for, and which
                    // `CoverageDetailEndpoints` already applies. Publishing only the version pinned every
                    // future quote to the terms in force the day the member enrolled, so an amendment could
                    // never reach anybody already on the plan.
                    planId = version.PlanId,
                    planVersionId = coverage.SourcePlanVersionId,
                    waitingPeriodEndsOn = rule is null ? null : WaitingPeriod.EndsOnFor(rule, enrollment.EffectiveFrom),
                    limits = coverage.Limits.Select(l => new
                    {
                        limitType = l.LimitType.ToString(), l.LimitValue, l.ConsumedValue,
                    }),
                }, ct);
            }
        }

        if (tx is not null) await tx.CommitAsync(ct);
        return MembershipResults.Success(new EnrollOutcome(enrollment, coverages.Count, WasReplay: false));
    }

    // ---- Terminate ---------------------------------------------------------------------------------------

    public async Task<MembershipResult<Enrollment>> TerminateAsync(
        Guid enrollmentId, DateOnly effectiveDate, string? reason, bool maySupervise, ActorRef actor,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);

        // MANDATORY. A termination is the change most likely to be disputed, and the reason is the only account
        // of it the next person to open the record will have.
        if (string.IsNullOrWhiteSpace(reason))
            return MembershipResults.Fail<Enrollment>(MembershipFailureKind.Invalid,
                "REASON_REQUIRED", "A reason is required to terminate a membership.");

        var e = await db.Enrollments.FirstOrDefaultAsync(x => x.EnrollmentId == enrollmentId && !x.IsDeleted, ct);
        if (e is null) return MembershipResults.Fail<Enrollment>(MembershipFailureKind.NotFound, "NOT_FOUND", "No such membership.");
        if (e.Status == EnrollmentStatus.Terminated)
            return MembershipResults.Fail<Enrollment>(MembershipFailureKind.Conflict,
                "ALREADY_TERMINATED", "This membership is already terminated.");
        if (effectiveDate < e.EffectiveFrom)
            return MembershipResults.Fail<Enrollment>(MembershipFailureKind.Unprocessable,
                "BEFORE_ENROLMENT", "A termination cannot take effect before the membership began; cancel it instead.");

        // Retro-effective changes need the SUPERVISORY increment (design 38 §5.5) — back-dating a termination
        // retroactively withdraws cover for care that may already have been delivered. A BULK file gets no
        // relief from this: a thousand back-dated terminations is the case it matters most for.
        if (effectiveDate < calendar.Today() && !maySupervise)
            return MembershipResults.Fail<Enrollment>(MembershipFailureKind.Forbidden, "SUPERVISION_REQUIRED",
                "Back-dating a termination requires supervisory scope.");

        // Join the caller's transaction when there is one. The bulk engine already wraps each row in
        // one (BulkJobEngine), and opening a second inside it throws — while the invariant is
        // already satisfied there, because that outer transaction covers this write and its events
        // together. Owning one only when nobody else does keeps both callers atomic.
        await using var tx = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(ct)
            : null;
        var now = clock.GetUtcNow();
        // Captured BEFORE the mutation: the history's whole job is to say what the value used to be, and by
        // the time the projection runs the row no longer holds it.
        var wasStatus = e.Status;
        var wasCoveredUntil = e.EffectiveTo;
        e.Status = EnrollmentStatus.Terminated;
        e.EffectiveTo = effectiveDate;      // INCLUSIVE: the member IS covered on this day
        e.TerminationReason = reason;
        e.UpdatedAt = now;
        e.UpdatedBy = actor.UserId;

        await db.Coverages.Where(c => c.EnrollmentId == enrollmentId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.EffectiveTo, effectiveDate), ct);

        db.EnrollmentEvents.Add(Event(e, EnrollmentEventType.Terminated, effectiveDate, reason, actor, now, null));
        if (await SaveOrConflict(ct) is { } conflict) return new MembershipResult<Enrollment>(default, conflict);

        await audit.EmitAsync(Draft("enrollment", enrollmentId, AuditAction.StateChange, actor, "terminated", reason), ct);
        await ProjectMemberHistoryAsync(e, "MemberTerminated", actor, Changed(
            ("status", wasStatus.ToString(), e.Status.ToString()),
            ("coveredUntil", Day(wasCoveredUntil), Day(e.EffectiveTo))), ct);
        var termDims = await DimensionsAsync(e, ct);
        await outbox.EnqueueAsync("MemberTerminated", "policy.events", new
        {
            tenantId = e.TenantId, enrollmentId, beneficiaryId = e.BeneficiaryId,
            effectiveDate, reason,
            termDims.PayerId, termDims.PolicyId, termDims.PolicyPlanId, termDims.GroupId, termDims.BranchId,
            termDims.Relationship, termDims.Status,
        }, ct);
        if (tx is not null) await tx.CommitAsync(ct);
        return MembershipResults.Success(e);
    }

    // ---- Reinstate ---------------------------------------------------------------------------------------

    public async Task<MembershipResult<Enrollment>> ReinstateAsync(
        Guid enrollmentId, DateOnly effectiveDate, string? reason, ActorRef actor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var e = await db.Enrollments.FirstOrDefaultAsync(x => x.EnrollmentId == enrollmentId && !x.IsDeleted, ct);
        if (e is null) return MembershipResults.Fail<Enrollment>(MembershipFailureKind.NotFound, "NOT_FOUND", "No such membership.");
        if (e.Status is not (EnrollmentStatus.Terminated or EnrollmentStatus.Suspended))
            return MembershipResults.Fail<Enrollment>(MembershipFailureKind.Conflict,
                "NOT_REINSTATABLE", $"A {e.Status} membership cannot be reinstated.");

        // Join the caller's transaction when there is one. The bulk engine already wraps each row in
        // one (BulkJobEngine), and opening a second inside it throws — while the invariant is
        // already satisfied there, because that outer transaction covers this write and its events
        // together. Owning one only when nobody else does keeps both callers atomic.
        await using var tx = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(ct)
            : null;
        var now = clock.GetUtcNow();
        var wasStatus = e.Status;
        var wasCoveredUntil = e.EffectiveTo;
        e.Status = EnrollmentStatus.Active;
        e.EffectiveTo = null;
        e.TerminationReason = null;
        e.UpdatedAt = now;
        e.UpdatedBy = actor.UserId;
        await db.Coverages.Where(c => c.EnrollmentId == enrollmentId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.EffectiveTo, (DateOnly?)null), ct);

        db.EnrollmentEvents.Add(Event(e, EnrollmentEventType.Reinstated, effectiveDate, reason, actor, now, null));
        if (await SaveOrConflict(ct) is { } conflict) return new MembershipResult<Enrollment>(default, conflict);

        await audit.EmitAsync(Draft("enrollment", enrollmentId, AuditAction.StateChange, actor, "reinstated", reason), ct);
        await ProjectMemberHistoryAsync(e, "MemberReinstated", actor, Changed(
            ("status", wasStatus.ToString(), e.Status.ToString()),
            // Cleared, not set — "12 Mar 2026 → —" is how the reader sees an end date being lifted.
            ("coveredUntil", Day(wasCoveredUntil), null)), ct);
        var reinDims = await DimensionsAsync(e, ct);
        await outbox.EnqueueAsync("MemberReinstated", "policy.events", new
        {
            tenantId = e.TenantId, enrollmentId, beneficiaryId = e.BeneficiaryId, effectiveDate,
            reinDims.PayerId, reinDims.PolicyId, reinDims.PolicyPlanId, reinDims.GroupId, reinDims.BranchId,
            reinDims.Relationship, reinDims.Status,
        }, ct);
        if (tx is not null) await tx.CommitAsync(ct);
        return MembershipResults.Success(e);
    }

    // ---- Change group ------------------------------------------------------------------------------------

    public async Task<MembershipResult<GroupChangeOutcome>> ChangeGroupAsync(
        Guid enrollmentId, Guid? groupId, DateOnly effectiveDate, string? reason, ActorRef actor,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var e = await db.Enrollments.FirstOrDefaultAsync(x => x.EnrollmentId == enrollmentId && !x.IsDeleted, ct);
        if (e is null) return MembershipResults.Fail<GroupChangeOutcome>(MembershipFailureKind.NotFound, "NOT_FOUND", "No such membership.");
        if (groupId is { } g && !await db.MemberGroups.AnyAsync(
                x => x.GroupId == g && x.PolicyId == e.PolicyId && !x.IsDeleted, ct))
            return MembershipResults.Fail<GroupChangeOutcome>(MembershipFailureKind.Invalid,
                "UNKNOWN_GROUP", "That group does not belong to this policy.");

        // INV-OUTBOX-SURVIVES-CRASH — the same shape every sibling movement uses, and the one this method
        // was missing. `MemberGroupChanged` was the last movement to gain an outbox publish, and it gained
        // it without the transaction its five siblings already had: the write and the event were two
        // separate commits, so a kill between them left the member in the new group with no event saying so
        // — which for a group change means the cohort reports silently disagree with the membership book.
        //
        // Joins the caller's transaction when there is one. The bulk engine already wraps each row, and
        // opening a second inside it throws, while the invariant is already satisfied there.
        await using var tx = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(ct)
            : null;
        var now = clock.GetUtcNow();
        var from = e.GroupId;
        e.GroupId = groupId;
        e.UpdatedAt = now;
        e.UpdatedBy = actor.UserId;
        db.EnrollmentEvents.Add(Event(e, EnrollmentEventType.GroupChanged, effectiveDate, reason, actor, now,
            new { from, to = groupId }));
        if (await SaveOrConflict(ct) is { } conflict) return new MembershipResult<GroupChangeOutcome>(default, conflict);

        await audit.EmitAsync(Draft("enrollment", enrollmentId, AuditAction.Update, actor, "group-changed", reason), ct);
        await ProjectMemberHistoryAsync(e, "MemberGroupChanged", actor, Changed(
            ("group", await GroupCodeAsync(from, ct), await GroupCodeAsync(groupId, ct)),
            ("effectiveDate", null, Day(effectiveDate))), ct);
        /*
         * The only membership movement that never left this service.
         *
         * Terminate, reinstate, plan-change and cancel all publish to `policy.events` with the same dimension
         * bag; a group change wrote its enrollment_event row and its timeline entry and stopped there. So the
         * enrolment curve counted five of the six movements, and a member moving between groups — which is
         * how a cohort is re-cut, and therefore exactly what a group-level report is asked about — was
         * invisible to every consumer outside policy-service.
         */
        var groupDims = await DimensionsAsync(e, ct);
        await outbox.EnqueueAsync("MemberGroupChanged", "policy.events", new
        {
            tenantId = e.TenantId, enrollmentId, beneficiaryId = e.BeneficiaryId, effectiveDate,
            fromGroupId = from,
            groupDims.PayerId, groupDims.PolicyId, groupDims.PolicyPlanId, groupDims.GroupId,
            groupDims.BranchId, groupDims.Relationship, groupDims.Status,
        }, ct);
        if (tx is not null) await tx.CommitAsync(ct);
        return MembershipResults.Success(new GroupChangeOutcome(e, from));
    }


    // ============================================================================================================
    // THE MEMBER'S HISTORY — written HERE, in the transaction that made the change.
    // ============================================================================================================
    //
    // The Logs tab reads `policy.entity_timeline`. Every membership event this class performs was published to
    // `policy.events` and NOTHING projected it, so the only rows in that table were the ones the demo seed
    // wrote: a plan change made in the app left no trace, which is exactly what an operator reported.
    //
    // `TimelineProjector` documents an intent that the timeline should be projected from events "that already
    // exist", so it cannot drift from the audit trail. That intent is honoured — this runs immediately after
    // the audit event is emitted, from the same values, inside the same transaction — and the guarantee is
    // stronger than a consumer's would be: a change and its history entry commit together or neither does.
    // A consumer on `policy.events` was the alternative and it buys eventual consistency for a projection the
    // operator expects to see the moment the dialog closes.
    //
    // The event id is DERIVED from what happened rather than random, so a retry of the same change projects
    // the same row: `ProjectAsync` dedupes on the source event id, and a random one would make every retry a
    // duplicate line in somebody's history.
    private async Task ProjectMemberHistoryAsync(
        Enrollment e, string eventType, ActorRef actor,
        IReadOnlyDictionary<string, (string? Before, string? After)>? changes,
        CancellationToken ct)
    {
        var occurredAt = clock.GetUtcNow();
        await new TimelineProjector(db, clock).ProjectAsync(
            [new TimelineSource(
                EventId: DerivedEventId(e.EnrollmentId, eventType, occurredAt),
                EventType: eventType,
                Scope: NoteScope.Member,
                ScopeRef: e.EnrollmentId,
                OccurredAt: occurredAt,
                SourceService: "policy-service",
                ActorUserId: actor.UserId,
                ActorUsername: actor.Subject,
                // The NAME, snapshotted. The reader of a member's history is asking "who changed this", and a
                // subject uuid is an answer only to somebody who can resolve it.
                ActorDisplay: actor.Display,
                Changes: changes)],
            e.TenantId, ct);
    }

    /// <summary>
    /// The before/after bag for one change, in the vocabulary the READER has.
    ///
    /// <para>Labels, not identifiers. "Plan: Standard → Enhanced" is the sentence somebody opens the Logs tab
    /// to read; <c>policyPlanId: 4f2c… → 91ab…</c> is the same fact written so that answering the question
    /// requires two more lookups. The identifiers are not lost — <c>policy.enrollment_event</c> keeps them, and
    /// it is what 19.5b's as-of extraction reconstructs history from, precisely because a label can be reused
    /// on a renewed policy and an id cannot.</para>
    ///
    /// <para><b>The termination reason is deliberately absent</b> from every bag below. It can say "deceased"
    /// or "suspected misuse", <c>AdministrativeProjection.MayReadCase</c> withholds it from roles that read
    /// this history, and a diff carries ONE visibility class — so putting it here would route it around the
    /// projection that exists to hold it back. It stays on the enrolment event and in the audit trail.</para>
    /// </summary>
    private static Dictionary<string, (string? Before, string? After)> Changed(
        params (string Field, string? Before, string? After)[] fields)
    {
        var bag = new Dictionary<string, (string?, string?)>(StringComparer.Ordinal);
        foreach (var (field, before, after) in fields) bag[field] = (before, after);
        return bag;
    }

    /// <summary>Dates in the diff are ISO, and formatted invariantly: the entry is rendered in whichever
    /// language the reader has chosen, so a value serialized in the server's culture would be a second
    /// formatting decision made in the wrong place.</summary>
    private static string? Day(DateOnly? date) =>
        date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>The group's CODE for the history. Null id → null, which the reader shows as "no group" rather
    /// than as a missing value.</summary>
    private async Task<string?> GroupCodeAsync(Guid? groupId, CancellationToken ct) =>
        groupId is not { } id ? null
            : await db.MemberGroups.AsNoTracking()
                .Where(g => g.GroupId == id).Select(g => g.GroupCode).FirstOrDefaultAsync(ct);

    /// <summary>The plan's LABEL for the history, by id — the previous plan is only an id by the time the
    /// change has been applied.</summary>
    private async Task<string?> PlanLabelAsync(Guid policyPlanId, CancellationToken ct) =>
        await db.PolicyPlans.AsNoTracking()
            .Where(pp => pp.PolicyPlanId == policyPlanId).Select(pp => pp.PlanLabel).FirstOrDefaultAsync(ct);

    /// <summary>A stable id for one change: same enrollment, same event type, same instant → same id.</summary>
    private static Guid DerivedEventId(Guid enrollmentId, string eventType, DateTimeOffset occurredAt)
    {
        var seed = $"{enrollmentId:N}|{eventType}|{occurredAt.UtcTicks}";
        return new Guid(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(seed))[..16]);
    }

    // ---- Change plan -------------------------------------------------------------------------------------

    /// <summary>
    /// Resolve and validate a plan change WITHOUT applying it, and compute the arithmetic it would produce.
    ///
    /// <para>19.6 closed a gap the portal had papered over. The change dialog is required to show the officer
    /// how remaining limits carry forward BEFORE they confirm, and with no server dry-run the only honest thing
    /// the client could render was what the member had already consumed plus a sentence describing the rule.
    /// That is not a preview: the rule is a SETTING (ADR-0020, still unsigned), the new plan's limits are the
    /// other half of the sum, and a category the new plan does not cover at all disappears silently. Any of the
    /// three would have made a client-side estimate disagree with the outcome — and it would disagree exactly
    /// when it matters, at the moment somebody is deciding whether to move a patient mid-treatment.</para>
    ///
    /// <para>So the preview and the change run the SAME resolution. Not a second implementation that happens to
    /// agree today.</para>
    /// </summary>
    /// <param name="forUpdate">Track the entities the caller is about to mutate. A dry-run reads no-tracking so
    /// it cannot leave a half-modified enrolment in the change tracker for some later SaveChanges to pick up.</param>
    private async Task<MembershipResult<PlanChangePlan>> ResolvePlanChangeAsync(
        Guid enrollmentId, Guid policyPlanId, DateOnly effectiveDate, bool forUpdate, CancellationToken ct)
    {
        var enrollments = forUpdate ? db.Enrollments : db.Enrollments.AsNoTracking();
        var e = await enrollments.FirstOrDefaultAsync(x => x.EnrollmentId == enrollmentId && !x.IsDeleted, ct);
        if (e is null) return MembershipResults.Fail<PlanChangePlan>(MembershipFailureKind.NotFound, "NOT_FOUND", "No such membership.");
        if (e.PolicyPlanId == policyPlanId)
            return MembershipResults.Fail<PlanChangePlan>(MembershipFailureKind.Conflict,
                "ALREADY_ON_PLAN", "The member is already on that plan.");

        var plan = await db.PolicyPlans.AsNoTracking()
            .FirstOrDefaultAsync(pp => pp.PolicyPlanId == policyPlanId && !pp.IsDeleted, ct);
        if (plan is null)
            return MembershipResults.Fail<PlanChangePlan>(MembershipFailureKind.Invalid, "UNKNOWN_PLAN", "That plan does not exist.");
        if (plan.PolicyId != e.PolicyId)
            return MembershipResults.Fail<PlanChangePlan>(MembershipFailureKind.Invalid,
                "PLAN_NOT_OF_POLICY", "That plan belongs to a different policy.");
        if (!plan.Covers(effectiveDate))
            return MembershipResults.Fail<PlanChangePlan>(MembershipFailureKind.Unprocessable,
                "PLAN_NOT_IN_FORCE", $"Plan '{plan.PlanLabel}' is not in force on {effectiveDate:yyyy-MM-dd}.");

        var version = await db.PlanVersions.AsNoTracking().Include(v => v.Rules)
            .FirstOrDefaultAsync(v => v.PlanVersionId == plan.PlanVersionId, ct);
        if (version is null)
            return MembershipResults.Fail<PlanChangePlan>(MembershipFailureKind.Conflict,
                "PLAN_VERSION_MISSING", "The plan's version could not be loaded.");

        // READ ONLY — phase 18 owns consumed_value and remains its only writer; carrying forward changes the
        // LIMIT, never the accumulator.
        var coverages = forUpdate ? db.Coverages : db.Coverages.AsNoTracking();
        var existing = await coverages.Include(c => c.Limits)
            .Where(c => c.EnrollmentId == enrollmentId && !c.IsDeleted).ToListAsync(ct);
        // Summed across a category's limits, which is the aggregation the change itself has always applied; the
        // preview reports the same number rather than a per-limit breakdown the outcome would not match.
        var consumedByCategory = existing.ToDictionary(
            c => c.BenefitCategoryId, c => c.Limits.Sum(l => l.ConsumedValue));
        // The ceiling in force today, computed the way CoverageDetail computes the one the member's coverage
        // screen displays — accumulating limits only, and null (unbounded) when there are none. The preview is
        // read beside that screen, so the two must not report different "current" limits for the same category.
        //
        // Consumption above deliberately does NOT apply that filter: it mirrors what the change itself carries.
        // A preview whose limits match the coverage screen but whose consumption does not match the outcome
        // would be the wrong trade — the number being previewed is the one the change is about to write.
        var currentLimits = existing.ToDictionary(
            c => c.BenefitCategoryId,
            c =>
            {
                var accumulating = c.Limits.Where(l => BenefitAccumulation.Accumulates(l.LimitType)).ToList();
                return accumulating.Count == 0 ? (decimal?)null : accumulating.Sum(l => l.LimitValue);
            });

        var covered = version.Rules.Where(r => r.IsCovered).ToList();
        var policyChoice = options.Value.PlanChangeConsumption;
        var carried = ConsumptionCarryForward.Apply(
            covered.Select(r => new CategoryCarryForward(
                r.BenefitCategoryId,
                consumedByCategory.GetValueOrDefault(r.BenefitCategoryId),
                r.LimitValue)),
            policyChoice);

        // The categories the member holds today that the new plan does not cover. These are the rows that would
        // otherwise vanish without a line in the outcome — the change response only ever listed what the NEW
        // plan grants, so a benefit being withdrawn was the one consequence the officer could not see.
        var droppedCategories = existing
            .Select(c => c.BenefitCategoryId)
            .Where(id => !covered.Exists(r => r.BenefitCategoryId == id))
            .Distinct()
            .ToList();

        return MembershipResults.Success(new PlanChangePlan(
            e, plan, version, existing, policyChoice, carried, currentLimits, consumedByCategory, droppedCategories));
    }

    /// <summary>The dry-run. Same resolution, same arithmetic, nothing written.</summary>
    /// <remarks>Takes no reason: a preview is not a change, and demanding the justification before showing the
    /// consequence would force an officer to defend a decision they have not been given the means to evaluate.
    /// The reason stays mandatory on the change itself.</remarks>
    public async Task<MembershipResult<PlanChangePreview>> PreviewPlanChangeAsync(
        Guid enrollmentId, Guid policyPlanId, DateOnly effectiveDate, CancellationToken ct = default)
    {
        var resolved = await ResolvePlanChangeAsync(enrollmentId, policyPlanId, effectiveDate, forUpdate: false, ct);
        if (!resolved.Ok) return new MembershipResult<PlanChangePreview>(default, resolved.Error);

        var p = resolved.Value!;
        return MembershipResults.Success(new PlanChangePreview(
            p.Enrollment.EnrollmentId, p.Enrollment.PolicyPlanId, p.Plan, p.Version.PlanVersionId, effectiveDate,
            p.ConsumptionPolicy.ToString(), p.Carried, p.CurrentLimits, p.Consumed, p.DroppedCategories));
    }

    public async Task<MembershipResult<PlanChangeOutcome>> ChangePlanAsync(
        Guid enrollmentId, Guid policyPlanId, DateOnly effectiveDate, string? reason, ActorRef actor,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (string.IsNullOrWhiteSpace(reason))
            return MembershipResults.Fail<PlanChangeOutcome>(MembershipFailureKind.Invalid,
                "REASON_REQUIRED", "A reason is required to change a member's plan.");

        var resolved = await ResolvePlanChangeAsync(enrollmentId, policyPlanId, effectiveDate, forUpdate: true, ct);
        if (!resolved.Ok) return new MembershipResult<PlanChangeOutcome>(default, resolved.Error);

        var (e, plan, version, existing, policyChoice, carried, _, _, droppedCategories) = resolved.Value!;

        // Join the caller's transaction when there is one. The bulk engine already wraps each row in
        // one (BulkJobEngine), and opening a second inside it throws — while the invariant is
        // already satisfied there, because that outer transaction covers this write and its events
        // together. Owning one only when nobody else does keeps both callers atomic.
        await using var tx = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(ct)
            : null;
        var now = clock.GetUtcNow();
        var previousPlanId = e.PolicyPlanId;
        foreach (var coverage in existing)
            coverage.EffectiveTo = effectiveDate.AddDays(-1);

        e.PolicyPlanId = plan.PolicyPlanId;
        e.SourcePlanVersionId = version.PlanVersionId;
        e.UpdatedAt = now;
        e.UpdatedBy = actor.UserId;

        var regenerated = CoverageGenerator.Generate(version, e, e.TenantId);
        foreach (var coverage in regenerated)
        {
            coverage.EffectiveFrom = effectiveDate;
            coverage.SourcePlanVersionId = version.PlanVersionId;
            coverage.EnrollmentId = e.EnrollmentId;
            var carriedForCategory = carried.FirstOrDefault(c => c.BenefitCategoryId == coverage.BenefitCategoryId);
            foreach (var limit in coverage.Limits)
                limit.ConsumedValue = carriedForCategory.ConsumedValue;
            db.Coverages.Add(coverage);
        }

        db.EnrollmentEvents.Add(Event(e, EnrollmentEventType.PlanChanged, effectiveDate, reason, actor, now,
            new
            {
                toPlan = plan.PlanLabel, policyPlanId = plan.PolicyPlanId, fromPolicyPlanId = previousPlanId,
                planVersionId = version.PlanVersionId, consumptionPolicy = policyChoice.ToString(),
                // Recorded on the event, not merely returned: a benefit the member stopped holding is the part
                // of a plan change somebody asks about months later, and the timeline is where they will look.
                droppedCategories,
            }));
        if (await SaveOrConflict(ct) is { } conflict) return new MembershipResult<PlanChangeOutcome>(default, conflict);

        await audit.EmitAsync(Draft("enrollment", enrollmentId, AuditAction.StateChange, actor, "plan-changed", reason), ct);
        await ProjectMemberHistoryAsync(e, "MemberPlanChanged", actor, Changed(
            ("plan", await PlanLabelAsync(previousPlanId, ct), plan.PlanLabel),
            ("effectiveDate", null, Day(effectiveDate))), ct);
        var planDims = await DimensionsAsync(e, ct);
        await outbox.EnqueueAsync("MemberPlanChanged", "policy.events", new
        {
            tenantId = e.TenantId, enrollmentId, beneficiaryId = e.BeneficiaryId,
            policyPlanId = plan.PolicyPlanId, planVersionId = version.PlanVersionId,
            effectiveDate, consumptionPolicy = policyChoice.ToString(),
            planDims.PayerId, planDims.PolicyId, planDims.GroupId, planDims.BranchId,
            planDims.Relationship, planDims.Status,
        }, ct);

        if (tx is not null) await tx.CommitAsync(ct);
        return MembershipResults.Success(new PlanChangeOutcome(
            e, plan, previousPlanId, version.PlanVersionId, policyChoice.ToString(), carried, droppedCategories));
    }

    // ---- Cancel (the rollback verb for a bulk-created enrolment) -----------------------------------------

    /// <summary>
    /// Reverse a membership this system created in error.
    ///
    /// <para>CANCELLED, not deleted, and distinct from Terminated: a cancellation says the membership never
    /// should have existed, which is exactly what a mis-uploaded file produces. A termination would leave the
    /// member covered for the days between the upload and the rollback — days on which they may have been told
    /// they had no cover.</para>
    /// </summary>
    public async Task<MembershipResult<Enrollment>> CancelAsync(
        Guid enrollmentId, string reason, ActorRef actor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var e = await db.Enrollments.FirstOrDefaultAsync(x => x.EnrollmentId == enrollmentId && !x.IsDeleted, ct);
        if (e is null) return MembershipResults.Fail<Enrollment>(MembershipFailureKind.NotFound, "NOT_FOUND", "No such membership.");

        // Join the caller's transaction when there is one. The bulk engine already wraps each row in
        // one (BulkJobEngine), and opening a second inside it throws — while the invariant is
        // already satisfied there, because that outer transaction covers this write and its events
        // together. Owning one only when nobody else does keeps both callers atomic.
        await using var tx = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(ct)
            : null;
        var now = clock.GetUtcNow();
        var wasStatus = e.Status;
        e.Status = EnrollmentStatus.Cancelled;
        e.TerminationReason = reason;
        e.UpdatedAt = now;
        e.UpdatedBy = actor.UserId;
        await db.Coverages.Where(c => c.EnrollmentId == enrollmentId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.IsDeleted, true), ct);

        db.EnrollmentEvents.Add(Event(e, EnrollmentEventType.Corrected, e.EffectiveFrom, reason, actor, now,
            new { cancelled = true }));
        if (await SaveOrConflict(ct) is { } conflict) return new MembershipResult<Enrollment>(default, conflict);

        await audit.EmitAsync(Draft("enrollment", enrollmentId, AuditAction.StateChange, actor, "cancelled", reason), ct);
        await ProjectMemberHistoryAsync(e, "MemberEnrolmentCancelled", actor, Changed(
            ("status", wasStatus.ToString(), e.Status.ToString())), ct);
        var cancelDims = await DimensionsAsync(e, ct);
        await outbox.EnqueueAsync("MemberEnrolmentCancelled", "policy.events", new
        {
            tenantId = e.TenantId, enrollmentId, beneficiaryId = e.BeneficiaryId, reason,
            cancelDims.PayerId, cancelDims.PolicyId, cancelDims.PolicyPlanId, cancelDims.GroupId,
            cancelDims.BranchId, cancelDims.Relationship, cancelDims.Status,
        }, ct);
        if (tx is not null) await tx.CommitAsync(ct);
        return MembershipResults.Success(e);
    }

    // ---- helpers -----------------------------------------------------------------------------------------

    /// <summary>
    /// The analytic dimensions a membership event carries for 19.6b: the payer the policy is with, plus the
    /// member's group, branch, relationship and status.
    ///
    /// <para>Resolved here rather than left to the consumer. reporting-service aggregates by payer, and a
    /// consumer that has to look the payer up is a consumer querying the transactional benefit spine —
    /// precisely what a read model exists to stop. One indexed lookup on the write path buys that.</para>
    /// </summary>
    private async Task<EventDimensions> DimensionsAsync(Enrollment e, CancellationToken ct)
    {
        var payerId = await db.Policies.AsNoTracking()
            .Where(p => p.PolicyId == e.PolicyId).Select(p => p.PayerId).FirstOrDefaultAsync(ct);
        return new EventDimensions(payerId, e.PolicyId, e.PolicyPlanId, e.GroupId, e.BranchId,
            e.Relationship.ToString(), e.Status.ToString());
    }

    private static EnrollmentEvent Event(
        Enrollment e, EnrollmentEventType type, DateOnly effectiveDate, string? reason,
        ActorRef actor, DateTimeOffset now, object? payload) => new()
    {
        EventId = Guid.NewGuid(), TenantId = e.TenantId, EnrollmentId = e.EnrollmentId,
        EventType = type, EffectiveDate = effectiveDate, Reason = reason,
        Payload = payload is null ? "{}" : JsonSerializer.Serialize(payload),
        ActorUserId = actor.UserId, OccurredAt = now,
    };

    private static AuditEventDraft Draft(
        string entityType, Guid id, AuditAction action, ActorRef actor, string? outcome = null, string? reason = null) => new()
    {
        EntityType = entityType, EntityId = id.ToString(), Action = action,
        ActorUserId = actor.Subject, DecisionOutcome = outcome, DecisionReasonCode = reason,
    };

    /// <summary>Database invariants translated into the same failure vocabulary as the application checks, so a
    /// bulk row that trips the overlap exclusion reports the same reason as a form that trips it.</summary>
    private async Task<MembershipError?> SaveOrConflict(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return null;
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException pg
                                           && pg.SqlState is "23505" or "23P01" or "23514" or "P0001")
        {
            var pgEx = (Npgsql.PostgresException)ex.InnerException!;
            return pgEx.SqlState switch
            {
                "23P01" when pgEx.ConstraintName == "ex_enrollment_no_overlap" => new MembershipError(
                    MembershipFailureKind.Conflict, "OVERLAPPING_ENROLMENT",
                    "This beneficiary already holds a live membership of this policy over part of that period."),
                "23P01" => new MembershipError(MembershipFailureKind.Conflict, "OVERLAPPING_WINDOW",
                    "Another record already covers part of that effective range."),
                "23505" when pgEx.ConstraintName == "uq_policy_plan_single_default" => new MembershipError(
                    MembershipFailureKind.Conflict, "DEFAULT_PLAN_EXISTS", "This policy already has a default plan."),
                "23505" => new MembershipError(MembershipFailureKind.Conflict, "DUPLICATE_KEY",
                    "A record with this code or number already exists."),
                "23514" => new MembershipError(MembershipFailureKind.Unprocessable, "CHECK_VIOLATION", pgEx.MessageText),
                _ => new MembershipError(MembershipFailureKind.Conflict, "APPEND_ONLY", pgEx.MessageText),
            };
        }
    }
}
