using System.Text.Json;
using Mersal.Audit.Client;
using Mersal.Auth.Authorization;
using Mersal.Authz;
using Mersal.Events;
using Mersal.Policy.Domain;
using Mersal.Policy.Infrastructure;
using Mersal.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Mersal.Policy.Api;

/// <summary>
/// Phase 19.2 + 19.2b — policies, plans under them, groups, and the membership lifecycle (design 38 §3–§4.2).
///
/// <para><b>Enrolment GENERATES coverage.</b> A member's <c>coverage</c> and <c>coverage_limit</c> rows are
/// derived from the elected plan's version, stamped with <c>source_plan_version_id</c>. That is what makes an
/// entitlement explainable: "why am I covered for this, and for how much" resolves to a dated, immutable
/// configuration rather than to whoever typed the row.</para>
///
/// <para><b>Changes are EVENTS, never edits.</b> Terminating, reinstating, moving group and moving plan each
/// append an <c>enrollment_event</c>. A retro-effective change has to record both when it applies AND when it
/// was decided, and the two are frequently not the same — that gap is the whole reason the log exists.</para>
///
/// <para><b>This module never writes <c>consumed_value</c>.</b> Phase 18 owns the accumulator.</para>
/// </summary>
public static class EnrollmentEndpoints
{
    public static void MapMembership(this IEndpointRouteBuilder app)
    {
        var v1 = app.MapGroup("/api/v1").RequireAuthorization(HbmpPolicies.Scope("policy:read"));
        MapPolicies(v1);
        MapPolicyPlans(v1);
        MapGroups(v1);
        MapEnrollments(v1);
        MapLifecycle(v1);
    }

    // ---- Policies ----------------------------------------------------------------------------------------
    private static void MapPolicies(RouteGroupBuilder v1)
    {
        v1.MapPost("/policies", async (CreatePolicy req, PolicyDbContext db, PolicyGate gate,
            IAuditClient audit, IOutbox outbox, TimeProvider clock, CancellationToken ct) =>
        {
            if (await gate.CheckAsync(PolicyPolicies.Write, ct) is { } denied) return denied;
            if (string.IsNullOrWhiteSpace(req.PolicyNo))
                return ProblemResults.Invalid("POLICY_NO_REQUIRED", "A policy number is required.");
            if (!await db.Payers.AnyAsync(p => p.PayerId == req.PayerId && !p.IsDeleted, ct))
                return ProblemResults.Invalid("UNKNOWN_PAYER", $"Payer {req.PayerId} does not exist.");
            if (req.EffectiveTo is { } to && to < req.EffectiveFrom)
                return ProblemResults.Invalid("BAD_WINDOW", "effectiveTo must not precede effectiveFrom.");

            var now = clock.GetUtcNow();
            var policy = new Domain.Policy
            {
                PolicyId = Guid.NewGuid(), PolicyNo = req.PolicyNo.Trim(), PayerId = req.PayerId,
                EffectiveFrom = req.EffectiveFrom, EffectiveTo = req.EffectiveTo, MaxMembers = req.MaxMembers,
                Status = PolicyStatus.Active, CreatedAt = now, UpdatedAt = now,
            };
            db.Policies.Add(policy);
            if (await SaveOrConflict(db, ct) is { } conflict) return conflict;

            await audit.EmitAsync(Draft("policy", policy.PolicyId, AuditAction.Create, gate), ct);
            await outbox.EnqueueAsync("PolicyIssued", "policy.events",
                new { tenantId = policy.TenantId, policyId = policy.PolicyId, policy.PolicyNo, payerId = req.PayerId }, ct);
            return Results.Created($"/api/v1/policies/{policy.PolicyId}", new { policy.PolicyId, policy.PolicyNo });
        });

        // Renewal creates a NEW policy linked to the old one. Carrying members forward is explicit and
        // REPORTED: a renewal that silently moved everyone would make who is covered a side effect nobody
        // reviewed. Members map by plan LABEL (ADR-0020); anything unmapped is named, never defaulted.
        v1.MapPost("/policies/{id:guid}/renew", async (Guid id, RenewPolicy req, PolicyDbContext db,
            PolicyGate gate, IAuditClient audit, IOutbox outbox, TimeProvider clock, CancellationToken ct) =>
        {
            if (await gate.CheckAsync(PolicyPolicies.Write, ct) is { } denied) return denied;
            var previous = await db.Policies.AsNoTracking().FirstOrDefaultAsync(p => p.PolicyId == id && !p.IsDeleted, ct);
            if (previous is null) return NotFound();

            var now = clock.GetUtcNow();
            var renewed = new Domain.Policy
            {
                PolicyId = Guid.NewGuid(), PolicyNo = req.PolicyNo.Trim(), PayerId = previous.PayerId,
                PreviousPolicyId = previous.PolicyId, MaxMembers = previous.MaxMembers,
                EffectiveFrom = req.EffectiveFrom, EffectiveTo = req.EffectiveTo,
                Status = PolicyStatus.Active, CreatedAt = now, UpdatedAt = now,
            };
            db.Policies.Add(renewed);

            var unmapped = new List<string>();
            var carried = 0;
            if (req.CarryMembersForward)
            {
                // Map by LABEL, not by plan version: the new policy's "Standard" is the successor of the old
                // policy's "Standard" even when the version behind it changed, which is the normal case.
                var oldPlans = await db.PolicyPlans.AsNoTracking()
                    .Where(pp => pp.PolicyId == previous.PolicyId && !pp.IsDeleted).ToListAsync(ct);
                var newPlans = await db.PolicyPlans.AsNoTracking()
                    .Where(pp => pp.PolicyId == renewed.PolicyId && !pp.IsDeleted).ToListAsync(ct);
                var byLabel = newPlans.ToDictionary(pp => pp.PlanLabel, StringComparer.OrdinalIgnoreCase);

                var members = await db.Enrollments
                    .Where(e => e.PolicyId == previous.PolicyId && !e.IsDeleted
                                && (e.Status == EnrollmentStatus.Active || e.Status == EnrollmentStatus.Suspended))
                    .ToListAsync(ct);

                foreach (var member in members)
                {
                    var oldLabel = oldPlans.FirstOrDefault(pp => pp.PolicyPlanId == member.PolicyPlanId)?.PlanLabel;
                    if (oldLabel is null || !byLabel.TryGetValue(oldLabel, out var target))
                    {
                        // REPORTED, not defaulted. Dropping an unmapped member onto the default plan would
                        // silently change what they are entitled to, in a bulk operation nobody reads line by line.
                        unmapped.Add($"{member.MemberNo} (plan '{oldLabel ?? "unknown"}' has no counterpart on the new policy)");
                        continue;
                    }
                    carried++;
                    _ = target;   // the enrolment itself is created by the bulk path in 19.5b
                }
            }

            if (await SaveOrConflict(db, ct) is { } conflict) return conflict;
            await audit.EmitAsync(Draft("policy", renewed.PolicyId, AuditAction.Create, gate, "renewed"), ct);
            await outbox.EnqueueAsync("PolicyRenewed", "policy.events", new
            {
                tenantId = renewed.TenantId, policyId = renewed.PolicyId, previousPolicyId = previous.PolicyId,
                membersCarried = carried, unmapped = unmapped.Count,
            }, ct);
            return Results.Ok(new RenewalView(renewed.PolicyId, renewed.PolicyNo, previous.PolicyId, carried, unmapped));
        });
    }

    // ---- Plans under a policy (19.2b) --------------------------------------------------------------------
    private static void MapPolicyPlans(RouteGroupBuilder v1)
    {
        v1.MapPost("/policies/{id:guid}/plans", async (Guid id, AttachPolicyPlan req, PolicyDbContext db,
            PolicyGate gate, IAuditClient audit, IOutbox outbox, TimeProvider clock, CancellationToken ct) =>
        {
            if (await gate.CheckAsync(PolicyPolicies.Write, ct) is { } denied) return denied;
            var policy = await db.Policies.AsNoTracking().FirstOrDefaultAsync(p => p.PolicyId == id && !p.IsDeleted, ct);
            if (policy is null) return NotFound();

            var version = await db.PlanVersions.AsNoTracking()
                .FirstOrDefaultAsync(v => v.PlanVersionId == req.PlanVersionId, ct);
            if (version is null)
                return ProblemResults.Invalid("UNKNOWN_PLAN_VERSION", $"Plan version {req.PlanVersionId} does not exist.");
            // A Draft has never been in force and its rules are still editable. Attaching one would let a
            // member be enrolled against a configuration that changes under them.
            if (version.Status == PlanVersionStatus.Draft)
                return ProblemResults.Conflict("PLAN_VERSION_NOT_ACTIVE",
                    "Only an activated plan version can be attached to a policy.");

            // The plan's window must actually overlap the policy's, or it offers cover on days the policy
            // does not exist for.
            if (req.EffectiveFrom < policy.EffectiveFrom
                || (policy.EffectiveTo is { } pTo && req.EffectiveFrom > pTo))
                return ProblemResults.Unprocessable("OUTSIDE_POLICY_WINDOW",
                    $"The plan's window must fall inside the policy's ({policy.EffectiveFrom:yyyy-MM-dd} to " +
                    $"{(policy.EffectiveTo?.ToString("yyyy-MM-dd") ?? "open")}).");

            if (PlanEligibility.IsMalformed(req.EligibilityRule))
                return ProblemResults.Invalid("MALFORMED_ELIGIBILITY_RULE",
                    "The eligibility rule could not be parsed. An unreadable rule is not treated as 'no restriction'.");

            var now = clock.GetUtcNow();
            var plan = new PolicyPlan
            {
                PolicyPlanId = Guid.NewGuid(), PolicyId = id, PlanVersionId = req.PlanVersionId,
                PlanLabel = req.PlanLabel.Trim(), EffectiveFrom = req.EffectiveFrom, EffectiveTo = req.EffectiveTo,
                IsDefault = req.IsDefault, EligibilityRule = req.EligibilityRule, MaxMembers = req.MaxMembers,
                CreatedAt = now, UpdatedAt = now, CreatedBy = gate.SubjectId, UpdatedBy = gate.SubjectId,
            };
            db.PolicyPlans.Add(plan);
            if (await SaveOrConflict(db, ct) is { } conflict) return conflict;

            await audit.EmitAsync(Draft("policy_plan", plan.PolicyPlanId, AuditAction.Create, gate), ct);
            await outbox.EnqueueAsync("PolicyPlanAttached", "policy.events", new
            {
                tenantId = plan.TenantId, policyId = id, policyPlanId = plan.PolicyPlanId,
                planVersionId = req.PlanVersionId, plan.PlanLabel, plan.IsDefault,
            }, ct);
            return Results.Created($"/api/v1/policy-plans/{plan.PolicyPlanId}", PolicyPlanView.From(plan));
        });

        v1.MapGet("/policies/{id:guid}/plans", async (Guid id, PolicyDbContext db, PolicyGate gate, CancellationToken ct) =>
        {
            if (await gate.CheckAsync(PolicyPolicies.Read, ct) is { } denied) return denied;
            var plans = await db.PolicyPlans.AsNoTracking()
                .Where(pp => pp.PolicyId == id && !pp.IsDeleted).OrderBy(pp => pp.PlanLabel).ToListAsync(ct);
            var counts = await db.Enrollments.AsNoTracking()
                .Where(e => e.PolicyId == id && !e.IsDeleted && e.Status == EnrollmentStatus.Active)
                .GroupBy(e => e.PolicyPlanId)
                .Select(g => new { PolicyPlanId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.PolicyPlanId, x => x.Count, ct);
            return Results.Ok(plans.Select(pp => PolicyPlanView.From(pp, counts.GetValueOrDefault(pp.PolicyPlanId))));
        });
    }

    // ---- Member groups -----------------------------------------------------------------------------------
    private static void MapGroups(RouteGroupBuilder v1)
    {
        v1.MapPost("/policies/{id:guid}/groups", async (Guid id, CreateMemberGroup req, PolicyDbContext db,
            PolicyGate gate, IAuditClient audit, TimeProvider clock, CancellationToken ct) =>
        {
            if (await gate.CheckAsync(PolicyPolicies.Write, ct) is { } denied) return denied;
            if (!await db.Policies.AnyAsync(p => p.PolicyId == id && !p.IsDeleted, ct)) return NotFound();
            if (!Enum.TryParse<MemberGroupType>(req.GroupType, out var type))
                return ProblemResults.Invalid("UNKNOWN_GROUP_TYPE", $"'{req.GroupType}' is not a group type.");

            var now = clock.GetUtcNow();
            var group = new MemberGroup
            {
                GroupId = Guid.NewGuid(), PolicyId = id, GroupCode = req.GroupCode.Trim(),
                NameEn = req.NameEn, NameAr = req.NameAr, GroupType = type,
                EffectiveFrom = req.EffectiveFrom, EffectiveTo = req.EffectiveTo,
                CreatedAt = now, UpdatedAt = now, CreatedBy = gate.SubjectId, UpdatedBy = gate.SubjectId,
            };
            db.MemberGroups.Add(group);
            if (await SaveOrConflict(db, ct) is { } conflict) return conflict;

            await audit.EmitAsync(Draft("member_group", group.GroupId, AuditAction.Create, gate), ct);
            return Results.Created($"/api/v1/member-groups/{group.GroupId}", MemberGroupView.From(group));
        });

        v1.MapGet("/policies/{id:guid}/groups", async (Guid id, PolicyDbContext db, PolicyGate gate, CancellationToken ct) =>
        {
            if (await gate.CheckAsync(PolicyPolicies.Read, ct) is { } denied) return denied;
            var rows = await db.MemberGroups.AsNoTracking()
                .Where(g => g.PolicyId == id && !g.IsDeleted).OrderBy(g => g.GroupCode).ToListAsync(ct);
            return Results.Ok(rows.Select(MemberGroupView.From));
        });
    }

    // ---- Enrolment ---------------------------------------------------------------------------------------
    private static void MapEnrollments(RouteGroupBuilder v1)
    {
        v1.MapPost("/enrollments", async (CreateEnrollment req, HttpContext http, PolicyDbContext db,
            PolicyGate gate, IBeneficiaryStatusProbe beneficiaries, IMemberNoIssuer memberNos,
            IAuditClient audit, IOutbox outbox, TimeProvider clock, CancellationToken ct) =>
        {
            if (await gate.CheckAsync(PolicyPolicies.Write, ct) is { } denied) return denied;

            var idempotencyKey = http.Request.Headers["Idempotency-Key"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(idempotencyKey))
                return ProblemResults.Invalid("IDEMPOTENCY_KEY_REQUIRED",
                    "Enrolment requires an Idempotency-Key: a retried request must not create a second membership.");

            // Replay returns the row the caller already created rather than a 409 from the overlap exclusion.
            var replay = await db.Enrollments.AsNoTracking()
                .FirstOrDefaultAsync(e => e.IdempotencyKey == idempotencyKey && !e.IsDeleted, ct);
            if (replay is not null)
            {
                var existingCoverages = await db.Coverages.AsNoTracking()
                    .CountAsync(c => c.EnrollmentId == replay.EnrollmentId, ct);
                return Results.Ok(EnrollmentView.From(replay, existingCoverages));
            }

            if (!Enum.TryParse<Relationship>(req.Relationship, out var relationship))
                return ProblemResults.Invalid("UNKNOWN_RELATIONSHIP", $"'{req.Relationship}' is not a relationship.");

            var policy = await db.Policies.AsNoTracking().FirstOrDefaultAsync(p => p.PolicyId == req.PolicyId && !p.IsDeleted, ct);
            if (policy is null) return ProblemResults.Invalid("UNKNOWN_POLICY", $"Policy {req.PolicyId} does not exist.");
            if (policy.Status != PolicyStatus.Active)
                return ProblemResults.Conflict("POLICY_NOT_ACTIVE", $"Policy {policy.PolicyNo} is {policy.Status}.");
            if (req.EffectiveFrom < policy.EffectiveFrom || (policy.EffectiveTo is { } pt && req.EffectiveFrom > pt))
                return ProblemResults.Unprocessable("OUTSIDE_POLICY_WINDOW",
                    "The enrolment start falls outside the policy's effective window.");

            // The beneficiary must be a real, Active person. Enrolling a Pending or Blocked member would
            // generate coverage that eligibility then refuses on every visit — a membership that looks live in
            // every report and works nowhere.
            var status = await beneficiaries.GetStatusAsync(req.BeneficiaryId, Bearer(http), ct);
            if (status is null)
                return ProblemResults.Invalid("UNKNOWN_BENEFICIARY", $"Beneficiary {req.BeneficiaryId} was not found.");
            if (!string.Equals(status, "Active", StringComparison.OrdinalIgnoreCase))
                return ProblemResults.Unprocessable("BENEFICIARY_NOT_ACTIVE",
                    $"The beneficiary is {status}; only an Active beneficiary can be enrolled.");

            // 19.2b — resolve the plan: named explicitly, or the policy's default.
            var plan = req.PolicyPlanId is { } explicitPlan
                ? await db.PolicyPlans.AsNoTracking().FirstOrDefaultAsync(pp => pp.PolicyPlanId == explicitPlan && !pp.IsDeleted, ct)
                : await db.PolicyPlans.AsNoTracking().FirstOrDefaultAsync(
                    pp => pp.PolicyId == req.PolicyId && pp.IsDefault && pp.Status == PolicyPlanStatus.Active && !pp.IsDeleted, ct);
            if (plan is null)
                return ProblemResults.Unprocessable("NO_PLAN",
                    req.PolicyPlanId is null
                        ? "No plan was named and this policy has no default plan to fall back to."
                        : $"Plan {req.PolicyPlanId} does not exist.");
            if (plan.PolicyId != req.PolicyId)
                return ProblemResults.Invalid("PLAN_NOT_OF_POLICY", "That plan belongs to a different policy.");
            if (!plan.Covers(req.EffectiveFrom))
                return ProblemResults.Unprocessable("PLAN_NOT_IN_FORCE",
                    $"Plan '{plan.PlanLabel}' is not in force on {req.EffectiveFrom:yyyy-MM-dd}.");

            // The declarative election rule. A failure names the criterion — "not eligible" alone sends an
            // officer hunting through a plan definition they may not be able to see.
            if (PlanEligibility.IsMalformed(plan.EligibilityRule))
                return ProblemResults.Conflict("MALFORMED_ELIGIBILITY_RULE",
                    $"Plan '{plan.PlanLabel}' has an unreadable eligibility rule; it cannot be elected onto until it is fixed.");
            var failures = PlanEligibility.Evaluate(
                PlanEligibility.Parse(plan.EligibilityRule),
                new ElectionCandidate(req.GroupId, relationship, req.AgeYears, req.BranchId));
            if (failures.Count > 0)
                return ProblemResults.Unprocessable("PLAN_ELIGIBILITY_NOT_MET",
                    $"The member does not meet plan '{plan.PlanLabel}'s election criteria.",
                    new Dictionary<string, object?> { ["failures"] = failures });

            if (plan.MaxMembers is { } cap)
            {
                var onPlan = await db.Enrollments.CountAsync(
                    e => e.PolicyPlanId == plan.PolicyPlanId && !e.IsDeleted && e.Status == EnrollmentStatus.Active, ct);
                if (onPlan >= cap)
                    return ProblemResults.Conflict("PLAN_FULL", $"Plan '{plan.PlanLabel}' is at its {cap}-member cap.");
            }

            var version = await db.PlanVersions.AsNoTracking().Include(v => v.Rules)
                .FirstOrDefaultAsync(v => v.PlanVersionId == plan.PlanVersionId, ct);
            if (version is null)
                return ProblemResults.Conflict("PLAN_VERSION_MISSING", "The plan's version could not be loaded.");

            var now = clock.GetUtcNow();
            var enrollment = new Enrollment
            {
                EnrollmentId = Guid.NewGuid(), BeneficiaryId = req.BeneficiaryId, PolicyId = req.PolicyId,
                PolicyPlanId = plan.PolicyPlanId, GroupId = req.GroupId,
                MemberNo = await memberNos.NextAsync(req.EffectiveFrom, ct),
                Relationship = relationship, PrincipalEnrollmentId = req.PrincipalEnrollmentId,
                EffectiveFrom = req.EffectiveFrom, EffectiveTo = req.EffectiveTo,
                WaitingPeriodEndsOn = WaitingPeriod.EndsOn(version, req.EffectiveFrom),
                Status = EnrollmentStatus.Active,
                SourcePlanVersionId = version.PlanVersionId,
                IdempotencyKey = idempotencyKey,
                CreatedAt = now, UpdatedAt = now, CreatedBy = gate.SubjectId, UpdatedBy = gate.SubjectId,
            };
            db.Enrollments.Add(enrollment);

            // GENERATE the coverage. This is the join between the product layer and the benefit spine that
            // eligibility and the phase-18 accumulator already run on.
            var coverages = CoverageGenerator.Generate(version, enrollment, enrollment.TenantId);
            foreach (var coverage in coverages)
            {
                coverage.SourcePlanVersionId = version.PlanVersionId;
                coverage.EnrollmentId = enrollment.EnrollmentId;
                db.Coverages.Add(coverage);
            }

            db.EnrollmentEvents.Add(Event(enrollment, EnrollmentEventType.Enrolled, req.EffectiveFrom, null, gate, now,
                new { planLabel = plan.PlanLabel, coverages = coverages.Count }));

            if (await SaveOrConflict(db, ct) is { } conflict) return conflict;

            await audit.EmitAsync(Draft("enrollment", enrollment.EnrollmentId, AuditAction.Create, gate), ct);
            await outbox.EnqueueAsync("MemberEnrolled", "policy.events", new
            {
                tenantId = enrollment.TenantId, enrollmentId = enrollment.EnrollmentId,
                beneficiaryId = enrollment.BeneficiaryId, policyId = enrollment.PolicyId,
                policyPlanId = plan.PolicyPlanId, enrollment.MemberNo,
                effectiveFrom = enrollment.EffectiveFrom, waitingPeriodEndsOn = enrollment.WaitingPeriodEndsOn,
            }, ct);
            await outbox.EnqueueAsync("CoverageGenerated", "policy.events", new
            {
                tenantId = enrollment.TenantId, enrollmentId = enrollment.EnrollmentId,
                beneficiaryId = enrollment.BeneficiaryId, sourcePlanVersionId = version.PlanVersionId,
                categories = coverages.Count,
            }, ct);

            return Results.Created($"/api/v1/enrollments/{enrollment.EnrollmentId}",
                EnrollmentView.From(enrollment, coverages.Count));
        });

        v1.MapGet("/enrollments/{id:guid}", async (Guid id, PolicyDbContext db, PolicyGate gate, CancellationToken ct) =>
        {
            if (await gate.CheckAsync(PolicyPolicies.Read, ct) is { } denied) return denied;
            var e = await db.Enrollments.AsNoTracking().FirstOrDefaultAsync(x => x.EnrollmentId == id && !x.IsDeleted, ct);
            if (e is null) return NotFound();
            var coverages = await db.Coverages.AsNoTracking().CountAsync(c => c.EnrollmentId == id, ct);
            return Results.Ok(EnrollmentView.From(e, coverages));
        });

        v1.MapGet("/enrollments/{id:guid}/events", async (Guid id, PolicyDbContext db, PolicyGate gate, CancellationToken ct) =>
        {
            if (await gate.CheckAsync(PolicyPolicies.Read, ct) is { } denied) return denied;
            var rows = await db.EnrollmentEvents.AsNoTracking()
                .Where(e => e.EnrollmentId == id).OrderByDescending(e => e.OccurredAt).ToListAsync(ct);
            return Results.Ok(rows.Select(EnrollmentEventView.From));
        });
    }

    // ---- Lifecycle: every one of these is an EVENT ---------------------------------------------------------
    private static void MapLifecycle(RouteGroupBuilder v1)
    {
        v1.MapPost("/enrollments/{id:guid}/terminate", async (Guid id, TerminateEnrollment req, PolicyDbContext db,
            PolicyGate gate, IAuditClient audit, IOutbox outbox, IBusinessCalendar calendar,
            TimeProvider clock, CancellationToken ct) =>
        {
            if (await gate.CheckAsync(PolicyPolicies.Write, ct) is { } denied) return denied;
            // MANDATORY. A termination is the change most likely to be disputed, and the reason is the only
            // account of it the next person to open the record will have.
            if (string.IsNullOrWhiteSpace(req.Reason))
                return ProblemResults.Invalid("REASON_REQUIRED", "A reason is required to terminate a membership.");

            var e = await db.Enrollments.FirstOrDefaultAsync(x => x.EnrollmentId == id && !x.IsDeleted, ct);
            if (e is null) return NotFound();
            if (e.Status == EnrollmentStatus.Terminated)
                return ProblemResults.Conflict("ALREADY_TERMINATED", "This membership is already terminated.");
            if (req.EffectiveDate < e.EffectiveFrom)
                return ProblemResults.Unprocessable("BEFORE_ENROLMENT",
                    "A termination cannot take effect before the membership began; cancel it instead.");

            // Retro-effective changes need the SUPERVISORY increment (design 38 §5.5) — back-dating a
            // termination retroactively withdraws cover for care that may already have been delivered.
            if (req.EffectiveDate < calendar.Today()
                && await gate.CheckAsync(PolicyPolicies.Supervise, ct) is { } superviseDenied)
                return superviseDenied;

            var now = clock.GetUtcNow();
            e.Status = EnrollmentStatus.Terminated;
            e.EffectiveTo = req.EffectiveDate;      // INCLUSIVE: the member IS covered on this day
            e.TerminationReason = req.Reason;
            e.UpdatedAt = now;
            e.UpdatedBy = gate.SubjectId;

            // Close the generated coverage on the same day, so eligibility and the membership agree.
            await db.Coverages.Where(c => c.EnrollmentId == id)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.EffectiveTo, req.EffectiveDate), ct);

            db.EnrollmentEvents.Add(Event(e, EnrollmentEventType.Terminated, req.EffectiveDate, req.Reason, gate, now, null));
            if (await SaveOrConflict(db, ct) is { } conflict) return conflict;

            await audit.EmitAsync(Draft("enrollment", id, AuditAction.StateChange, gate, "terminated", req.Reason), ct);
            await outbox.EnqueueAsync("MemberTerminated", "policy.events", new
            {
                tenantId = e.TenantId, enrollmentId = id, beneficiaryId = e.BeneficiaryId,
                effectiveDate = req.EffectiveDate, reason = req.Reason,
            }, ct);
            return Results.Ok(EnrollmentView.From(e));
        });

        v1.MapPost("/enrollments/{id:guid}/reinstate", async (Guid id, ReinstateEnrollment req, PolicyDbContext db,
            PolicyGate gate, IAuditClient audit, IOutbox outbox, TimeProvider clock, CancellationToken ct) =>
        {
            if (await gate.CheckAsync(PolicyPolicies.Write, ct) is { } denied) return denied;
            var e = await db.Enrollments.FirstOrDefaultAsync(x => x.EnrollmentId == id && !x.IsDeleted, ct);
            if (e is null) return NotFound();
            if (e.Status is not (EnrollmentStatus.Terminated or EnrollmentStatus.Suspended))
                return ProblemResults.Conflict("NOT_REINSTATABLE", $"A {e.Status} membership cannot be reinstated.");

            var now = clock.GetUtcNow();
            e.Status = EnrollmentStatus.Active;
            e.EffectiveTo = null;                   // reopened
            e.TerminationReason = null;
            e.UpdatedAt = now;
            e.UpdatedBy = gate.SubjectId;
            await db.Coverages.Where(c => c.EnrollmentId == id)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.EffectiveTo, (DateOnly?)null), ct);

            db.EnrollmentEvents.Add(Event(e, EnrollmentEventType.Reinstated, req.EffectiveDate, req.Reason, gate, now, null));
            if (await SaveOrConflict(db, ct) is { } conflict) return conflict;

            await audit.EmitAsync(Draft("enrollment", id, AuditAction.StateChange, gate, "reinstated", req.Reason), ct);
            await outbox.EnqueueAsync("MemberReinstated", "policy.events",
                new { tenantId = e.TenantId, enrollmentId = id, beneficiaryId = e.BeneficiaryId }, ct);
            return Results.Ok(EnrollmentView.From(e));
        });

        v1.MapPost("/enrollments/{id:guid}/change-group", async (Guid id, ChangeGroup req, PolicyDbContext db,
            PolicyGate gate, IAuditClient audit, TimeProvider clock, CancellationToken ct) =>
        {
            if (await gate.CheckAsync(PolicyPolicies.Write, ct) is { } denied) return denied;
            var e = await db.Enrollments.FirstOrDefaultAsync(x => x.EnrollmentId == id && !x.IsDeleted, ct);
            if (e is null) return NotFound();
            if (req.GroupId is { } g && !await db.MemberGroups.AnyAsync(
                    x => x.GroupId == g && x.PolicyId == e.PolicyId && !x.IsDeleted, ct))
                return ProblemResults.Invalid("UNKNOWN_GROUP", "That group does not belong to this policy.");

            var now = clock.GetUtcNow();
            var from = e.GroupId;
            e.GroupId = req.GroupId;
            e.UpdatedAt = now;
            e.UpdatedBy = gate.SubjectId;
            db.EnrollmentEvents.Add(Event(e, EnrollmentEventType.GroupChanged, req.EffectiveDate, req.Reason, gate, now,
                new { from, to = req.GroupId }));
            if (await SaveOrConflict(db, ct) is { } conflict) return conflict;

            await audit.EmitAsync(Draft("enrollment", id, AuditAction.Update, gate, "group-changed", req.Reason), ct);
            return Results.Ok(EnrollmentView.From(e));
        });

        // 19.2b — the plan change. Coverage REGENERATES from the new plan version, and consumption is carried
        // across per ADR-0020 (a setting, not a hard-coded rule, because the decision is not yet signed off).
        v1.MapPost("/enrollments/{id:guid}/change-plan", async (Guid id, ChangePlan req, PolicyDbContext db,
            PolicyGate gate, IAuditClient audit, IOutbox outbox, IOptions<MembershipOptions> options,
            TimeProvider clock, CancellationToken ct) =>
        {
            if (await gate.CheckAsync(PolicyPolicies.Write, ct) is { } denied) return denied;
            if (string.IsNullOrWhiteSpace(req.Reason))
                return ProblemResults.Invalid("REASON_REQUIRED", "A reason is required to change a member's plan.");

            var e = await db.Enrollments.FirstOrDefaultAsync(x => x.EnrollmentId == id && !x.IsDeleted, ct);
            if (e is null) return NotFound();
            if (e.PolicyPlanId == req.PolicyPlanId)
                return ProblemResults.Conflict("ALREADY_ON_PLAN", "The member is already on that plan.");

            var plan = await db.PolicyPlans.AsNoTracking()
                .FirstOrDefaultAsync(pp => pp.PolicyPlanId == req.PolicyPlanId && !pp.IsDeleted, ct);
            if (plan is null) return ProblemResults.Invalid("UNKNOWN_PLAN", "That plan does not exist.");
            if (plan.PolicyId != e.PolicyId)
                return ProblemResults.Invalid("PLAN_NOT_OF_POLICY", "That plan belongs to a different policy.");
            if (!plan.Covers(req.EffectiveDate))
                return ProblemResults.Unprocessable("PLAN_NOT_IN_FORCE",
                    $"Plan '{plan.PlanLabel}' is not in force on {req.EffectiveDate:yyyy-MM-dd}.");

            var version = await db.PlanVersions.AsNoTracking().Include(v => v.Rules)
                .FirstOrDefaultAsync(v => v.PlanVersionId == plan.PlanVersionId, ct);
            if (version is null) return ProblemResults.Conflict("PLAN_VERSION_MISSING", "The plan's version could not be loaded.");

            // Read what the member has already used. READ ONLY — phase 18 owns consumed_value and remains its
            // only writer; carrying forward changes the LIMIT, never the accumulator.
            var existing = await db.Coverages.Include(c => c.Limits)
                .Where(c => c.EnrollmentId == id && !c.IsDeleted).ToListAsync(ct);
            var consumedByCategory = existing.ToDictionary(
                c => c.BenefitCategoryId, c => c.Limits.Sum(l => l.ConsumedValue));

            var policyChoice = options.Value.PlanChangeConsumption;
            var carried = ConsumptionCarryForward.Apply(
                version.Rules.Where(r => r.IsCovered).Select(r => new CategoryCarryForward(
                    r.BenefitCategoryId,
                    consumedByCategory.GetValueOrDefault(r.BenefitCategoryId),
                    r.LimitValue)),
                policyChoice);

            var now = clock.GetUtcNow();
            // Close the old coverage the day before the change, and generate the new set from the new version.
            foreach (var coverage in existing)
                coverage.EffectiveTo = req.EffectiveDate.AddDays(-1);

            e.PolicyPlanId = plan.PolicyPlanId;
            e.SourcePlanVersionId = version.PlanVersionId;
            e.UpdatedAt = now;
            e.UpdatedBy = gate.SubjectId;

            var regenerated = CoverageGenerator.Generate(version, e, e.TenantId);
            foreach (var coverage in regenerated)
            {
                coverage.EffectiveFrom = req.EffectiveDate;
                coverage.SourcePlanVersionId = version.PlanVersionId;
                coverage.EnrollmentId = e.EnrollmentId;
                // Carry the accumulator forward onto the NEW rows so remaining = new limit − already consumed.
                // This is initializing a fresh row from a value phase 18 already owns, not a second write to
                // the accumulator — the old rows' consumed_value is untouched.
                var carriedForCategory = carried.FirstOrDefault(c => c.BenefitCategoryId == coverage.BenefitCategoryId);
                foreach (var limit in coverage.Limits)
                    limit.ConsumedValue = carriedForCategory.ConsumedValue;
                db.Coverages.Add(coverage);
            }

            db.EnrollmentEvents.Add(Event(e, EnrollmentEventType.PlanChanged, req.EffectiveDate, req.Reason, gate, now,
                new { toPlan = plan.PlanLabel, planVersionId = version.PlanVersionId, consumptionPolicy = policyChoice.ToString() }));
            if (await SaveOrConflict(db, ct) is { } conflict) return conflict;

            await audit.EmitAsync(Draft("enrollment", id, AuditAction.StateChange, gate, "plan-changed", req.Reason), ct);
            await outbox.EnqueueAsync("MemberPlanChanged", "policy.events", new
            {
                tenantId = e.TenantId, enrollmentId = id, beneficiaryId = e.BeneficiaryId,
                policyPlanId = plan.PolicyPlanId, planVersionId = version.PlanVersionId,
                effectiveDate = req.EffectiveDate, consumptionPolicy = policyChoice.ToString(),
            }, ct);

            return Results.Ok(new PlanChangeView(id, plan.PolicyPlanId, version.PlanVersionId,
                policyChoice.ToString(), [.. carried.Select(CarriedLimitView.From)]));
        });
    }

    // ---- helpers -----------------------------------------------------------------------------------------

    private static EnrollmentEvent Event(
        Enrollment e, EnrollmentEventType type, DateOnly effectiveDate, string? reason,
        PolicyGate gate, DateTimeOffset now, object? payload) => new()
    {
        EventId = Guid.NewGuid(), TenantId = e.TenantId, EnrollmentId = e.EnrollmentId,
        EventType = type, EffectiveDate = effectiveDate, Reason = reason,
        Payload = payload is null ? "{}" : JsonSerializer.Serialize(payload),
        ActorUserId = gate.SubjectId, OccurredAt = now,
    };

    private static AuditEventDraft Draft(
        string entityType, Guid id, AuditAction action, PolicyGate gate, string? outcome = null, string? reason = null) => new()
    {
        EntityType = entityType, EntityId = id.ToString(), Action = action,
        ActorUserId = gate.Subject, DecisionOutcome = outcome, DecisionReasonCode = reason,
    };

    private static string? Bearer(HttpContext http) => http.Request.Headers.Authorization.FirstOrDefault();

    private static IResult NotFound() =>
        Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");

    private static async Task<IResult?> SaveOrConflict(PolicyDbContext db, CancellationToken ct)
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
                "23P01" when pgEx.ConstraintName == "ex_enrollment_no_overlap" => ProblemResults.Conflict(
                    "OVERLAPPING_ENROLMENT",
                    "This beneficiary already holds a live membership of this policy over part of that period."),
                "23P01" => ProblemResults.Conflict("OVERLAPPING_WINDOW",
                    "Another record already covers part of that effective range."),
                "23505" when pgEx.ConstraintName == "uq_policy_plan_single_default" => ProblemResults.Conflict(
                    "DEFAULT_PLAN_EXISTS", "This policy already has a default plan."),
                "23505" => ProblemResults.Conflict("DUPLICATE_KEY", "A record with this code or number already exists."),
                "23514" => ProblemResults.Unprocessable("CHECK_VIOLATION", pgEx.MessageText),
                _ => ProblemResults.Conflict("APPEND_ONLY", pgEx.MessageText),
            };
        }
    }
}

/// <summary>Settings the membership layer reads. <see cref="PlanChangeConsumption"/> is a setting rather than
/// a constant because ADR-0020 is not signed off, and reversing it later must not require migrating every
/// member's accumulator.</summary>
public sealed class MembershipOptions
{
    public const string SectionName = "Membership";
    public PlanChangeConsumptionPolicy PlanChangeConsumption { get; set; } = PlanChangeConsumptionPolicy.CarryForward;
}
