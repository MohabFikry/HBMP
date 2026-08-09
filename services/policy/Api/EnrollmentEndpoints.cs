using Mersal.Audit.Client;
using Mersal.Auth.Authorization;
using Mersal.Authz;
using Mersal.Events;
using Mersal.Policy.Domain;
using Mersal.Policy.Infrastructure;
using Mersal.Time;
using Microsoft.EntityFrameworkCore;

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
            // The policy row and the event announcing it commit together, or neither does: a PolicyIssued
            // nobody can look up and a policy nobody downstream heard about are both worse than a failure.
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            db.Policies.Add(policy);
            if (await SaveOrConflict(db, ct) is { } conflict) return conflict;

            await audit.EmitAsync(Draft("policy", policy.PolicyId, AuditAction.Create, gate), ct);
            await outbox.EnqueueAsync("PolicyIssued", "policy.events",
                new { tenantId = policy.TenantId, policyId = policy.PolicyId, policy.PolicyNo, payerId = req.PayerId }, ct);
            await tx.CommitAsync(ct);
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
            // Renewal writes the successor policy and announces it; the carry-forward report below is derived
            // from the same read, so the count in PolicyRenewed and the rows it counts share one commit.
            await using var tx = await db.Database.BeginTransactionAsync(ct);
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
            await tx.CommitAsync(ct);
            return Results.Ok(new RenewalView(renewed.PolicyId, renewed.PolicyNo, previous.PolicyId, carried, unmapped));
        })
        .Produces<RenewalView>();
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
            // Attaching a plan changes what members of this policy are entitled to. Eligibility consumes
            // PolicyPlanAttached to rebuild its view, so the row and the event must not be able to diverge.
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            db.PolicyPlans.Add(plan);
            if (await SaveOrConflict(db, ct) is { } conflict) return conflict;

            await audit.EmitAsync(Draft("policy_plan", plan.PolicyPlanId, AuditAction.Create, gate), ct);
            await outbox.EnqueueAsync("PolicyPlanAttached", "policy.events", new
            {
                tenantId = plan.TenantId, policyId = id, policyPlanId = plan.PolicyPlanId,
                planVersionId = req.PlanVersionId, plan.PlanLabel, plan.IsDefault,
            }, ct);
            await tx.CommitAsync(ct);
            return Results.Created($"/api/v1/policy-plans/{plan.PolicyPlanId}", PolicyPlanView.From(plan));
        })
        .Produces<PolicyPlanView>();

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
        })
        .Produces<IEnumerable<PolicyPlanView>>();
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
        })
        .Produces<MemberGroupView>();

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
        // 19.5b — the RULES now live in MembershipCommands, which the bulk engine also calls. What stays here
        // is what is genuinely the HTTP layer's: authorization, the Idempotency-Key header, and turning a
        // failure into an RFC 7807 body. A second copy of "is this plan in force" would let a file create
        // memberships this form refuses.
        v1.MapPost("/enrollments", async (CreateEnrollment req, HttpContext http, MembershipCommands membership,
            PolicyGate gate, CancellationToken ct) =>
        {
            if (await gate.CheckAsync(PolicyPolicies.Write, ct) is { } denied) return denied;

            var idempotencyKey = http.Request.Headers["Idempotency-Key"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(idempotencyKey))
                return ProblemResults.Invalid("IDEMPOTENCY_KEY_REQUIRED",
                    "Enrolment requires an Idempotency-Key: a retried request must not create a second membership.");

            var command = new EnrollCommand(
                req.BeneficiaryId, req.PolicyId, req.PolicyPlanId, req.GroupId, req.Relationship,
                req.PrincipalEnrollmentId, req.EffectiveFrom, req.EffectiveTo, req.BranchId, req.AgeYears);
            MembershipResult<EnrollOutcome> result;
            try
            {
                result = await membership.EnrollAsync(command, idempotencyKey, Bearer(http), Actor(gate), ct: ct);
            }
            catch (BeneficiaryProbeRefusedException ex)
            {
                // Reported as the permissions problem it is. It used to escape as an unhandled 500 "An error
                // occurred while processing your request", which told the operator nothing actionable.
                return Results.Problem(statusCode: 403, title: "beneficiary-read-required",
                    type: "urn:hbmp:beneficiary-read-required",
                    detail: $"Enrolment must read the beneficiary's status first, and patient-service refused ({ex.Status}). "
                          + "The caller needs permission to read beneficiaries.");
            }
            if (!result.Ok) return Problem(result.Error!);

            var outcome = result.Value!;
            return outcome.WasReplay
                ? Results.Ok(EnrollmentView.From(outcome.Enrollment, outcome.CoverageCount))
                : Results.Created($"/api/v1/enrollments/{outcome.Enrollment.EnrollmentId}",
                    EnrollmentView.From(outcome.Enrollment, outcome.CoverageCount));
        })
        .Produces<EnrollmentView>();

        v1.MapGet("/enrollments/{id:guid}", async (Guid id, PolicyDbContext db, PolicyGate gate, CancellationToken ct) =>
        {
            if (await gate.CheckAsync(PolicyPolicies.Read, ct) is { } denied) return denied;
            var e = await db.Enrollments.AsNoTracking().FirstOrDefaultAsync(x => x.EnrollmentId == id && !x.IsDeleted, ct);
            if (e is null) return NotFound();
            var coverages = await db.Coverages.AsNoTracking().CountAsync(c => c.EnrollmentId == id, ct);
            return Results.Ok(EnrollmentView.From(e, coverages));
        })
        .Produces<EnrollmentView>();

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
        v1.MapPost("/enrollments/{id:guid}/terminate", async (Guid id, TerminateEnrollment req,
            MembershipCommands membership, PolicyGate gate, IBusinessCalendar calendar, CancellationToken ct) =>
        {
            if (await gate.CheckAsync(PolicyPolicies.Write, ct) is { } denied) return denied;

            // The supervisory check is resolved HERE, where the caller's scope is known, and passed in as a
            // capability. MembershipCommands then enforces it identically for a form and for a bulk row — a
            // thousand back-dated terminations is the case that check matters most for.
            var maySupervise = req.EffectiveDate >= calendar.Today()
                               || await gate.CheckAsync(PolicyPolicies.Supervise, ct) is null;

            var result = await membership.TerminateAsync(id, req.EffectiveDate, req.Reason, maySupervise, Actor(gate), ct);
            return result.Ok ? Results.Ok(EnrollmentView.From(result.Value!)) : Problem(result.Error!);
        })
        .Produces<EnrollmentView>();

        v1.MapPost("/enrollments/{id:guid}/reinstate", async (Guid id, ReinstateEnrollment req,
            MembershipCommands membership, PolicyGate gate, CancellationToken ct) =>
        {
            if (await gate.CheckAsync(PolicyPolicies.Write, ct) is { } denied) return denied;
            var result = await membership.ReinstateAsync(id, req.EffectiveDate, req.Reason, Actor(gate), ct);
            return result.Ok ? Results.Ok(EnrollmentView.From(result.Value!)) : Problem(result.Error!);
        })
        .Produces<EnrollmentView>();

        v1.MapPost("/enrollments/{id:guid}/change-group", async (Guid id, ChangeGroup req,
            MembershipCommands membership, PolicyGate gate, CancellationToken ct) =>
        {
            if (await gate.CheckAsync(PolicyPolicies.Write, ct) is { } denied) return denied;
            var result = await membership.ChangeGroupAsync(id, req.GroupId, req.EffectiveDate, req.Reason, Actor(gate), ct);
            return result.Ok ? Results.Ok(EnrollmentView.From(result.Value!.Enrollment)) : Problem(result.Error!);
        })
        .Produces<EnrollmentView>();

        // 19.6 — the DRY RUN behind the change-plan dialog's carry-forward preview.
        //
        // Gated at Write, not Read, and deliberately: a preview is the first half of a change, and the authority
        // to model what moving a member would do to their entitlement is the authority to move them. Read is the
        // broad benefit-configuration permission; it should not double as a way to interrogate one member's
        // consumption against every plan on the policy.
        //
        // POST because it carries a body, not because it writes — nothing here is saved.
        v1.MapPost("/enrollments/{id:guid}/change-plan/preview", async (Guid id, PreviewPlanChange req,
            MembershipCommands membership, PolicyDbContext db, PolicyGate gate, CancellationToken ct) =>
        {
            if (await gate.CheckAsync(PolicyPolicies.Write, ct) is { } denied) return denied;
            ArgumentNullException.ThrowIfNull(req);
            var result = await membership.PreviewPlanChangeAsync(id, req.PolicyPlanId, req.EffectiveDate, ct);
            if (!result.Ok) return Problem(result.Error!);

            var p = result.Value!;
            var codes = await PlanEndpoints.CategoryCodesAsync(db, ct);
            return Results.Ok(new PlanChangePreviewView(
                p.EnrollmentId, p.FromPolicyPlanId, p.ToPlan.PolicyPlanId, p.ToPlan.PlanLabel, p.PlanVersionId,
                p.EffectiveDate, p.ConsumptionPolicy,
                [.. p.Carried.Select(c => new CarryPreviewRow(
                    c.BenefitCategoryId, CarriedLimitView.Code(codes, c.BenefitCategoryId),
                    p.CurrentLimits.ContainsKey(c.BenefitCategoryId),
                    p.CurrentLimits.GetValueOrDefault(c.BenefitCategoryId),
                    c.ConsumedValue, c.LimitValue, c.Remaining, c.Exhausted))],
                [.. p.DroppedCategories.Select(gid => new DroppedCategoryView(
                    gid, CarriedLimitView.Code(codes, gid),
                    p.CurrentLimits.GetValueOrDefault(gid), p.Consumed.GetValueOrDefault(gid)))]));
        })
        .Produces<IEnumerable<CarryPreviewRow>>();

        // 19.2b — the plan change. Coverage REGENERATES from the new plan version, and consumption is carried
        // across per ADR-0020 (a setting, not a hard-coded rule, because the decision is not yet signed off).
        v1.MapPost("/enrollments/{id:guid}/change-plan", async (Guid id, ChangePlan req,
            MembershipCommands membership, PolicyDbContext db, PolicyGate gate, CancellationToken ct) =>
        {
            if (await gate.CheckAsync(PolicyPolicies.Write, ct) is { } denied) return denied;
            var result = await membership.ChangePlanAsync(id, req.PolicyPlanId, req.EffectiveDate, req.Reason, Actor(gate), ct);
            if (!result.Ok) return Problem(result.Error!);

            var outcome = result.Value!;
            var codes = await PlanEndpoints.CategoryCodesAsync(db, ct);
            return Results.Ok(new PlanChangeView(id, outcome.Plan.PolicyPlanId, outcome.PlanVersionId,
                outcome.ConsumptionPolicy, [.. outcome.Carried.Select(c => CarriedLimitView.From(c, codes))],
                // Limits are gone by now — the old coverages were closed in the same transaction — so a dropped
                // category is reported by name and consumption only. The preview is where the before-figures live.
                [.. outcome.DroppedCategories.Select(gid => new DroppedCategoryView(
                    gid, CarriedLimitView.Code(codes, gid), null, 0m))]));
        })
        .Produces<IEnumerable<CarriedLimitView>>();
    }

    /// <summary>Translate a domain failure into the RFC 7807 shape the API already speaks. One place, so the
    /// bulk engine's row errors and the form's problem bodies cannot drift apart in meaning.</summary>
    private static IResult Problem(MembershipError error) => error.Kind switch
    {
        MembershipFailureKind.Invalid => ProblemResults.Invalid(error.Code, Detail(error)),
        MembershipFailureKind.Conflict => ProblemResults.Conflict(error.Code, error.Detail),
        MembershipFailureKind.Unprocessable when error.Failures is { Count: > 0 } => ProblemResults.Unprocessable(
            error.Code, error.Detail, new Dictionary<string, object?> { ["failures"] = error.Failures }),
        MembershipFailureKind.Unprocessable => ProblemResults.Unprocessable(error.Code, error.Detail),
        MembershipFailureKind.NotFound => NotFound(),
        _ => GateResults.Forbidden("urn:hbmp:supervision-required", detail: error.Detail, reason: error.Code),
    };

    /// <summary>Named criteria are folded into the sentence when the 400 shape carries no extensions bag —
    /// "not eligible" without the reason is what sends an officer hunting through a plan they cannot read.</summary>
    private static string Detail(MembershipError error) =>
        error.Failures is { Count: > 0 } f ? $"{error.Detail} ({string.Join("; ", f)})" : error.Detail;

    private static ActorRef Actor(PolicyGate gate) =>
        new(gate.SubjectId, gate.Subject, gate.Principal?.DisplayName);

    // ---- helpers -----------------------------------------------------------------------------------------

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

// 19.5b — MembershipOptions moved to Mersal.Policy.Domain (ConsumptionCarryForward.cs) when the membership
// rules moved into MembershipCommands. The setting belongs beside the rule it governs, not beside the HTTP
// handler that used to hold both.
