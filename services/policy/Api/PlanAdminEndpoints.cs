using Mersal.Audit.Client;
using Mersal.Authz;
using Mersal.Events;
using Mersal.Policy.Domain;
using Mersal.Policy.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Policy.Api;

/// <summary>
/// Phase 19.8 — plan administration (design 57).
///
/// <para>The plan had a create, a list, and an amend that authors a new VERSION. The row itself could never be
/// corrected or withdrawn: a typo in a plan's Arabic name was permanent, and <c>status</c> has accepted
/// <c>Inactive</c> since 0005 with no code path able to write it — so a plan withdrawn from sale looked
/// exactly like one still being enrolled onto.</para>
///
/// <para><b>Deactivation refuses while the plan is still being sold.</b> A plan with an Active version
/// attached to an active policy is a product members are still being enrolled onto; switching it off would
/// leave those enrolments resolving against a product the catalogue says is withdrawn, and nothing downstream
/// would say so. Same refusal as the payer's (19.7), and for the same reason. Detach it from the policies
/// first, or leave it active until you have.</para>
///
/// <para><b>What deactivation is NOT.</b> It does not touch a single plan version. Superseded and Retired
/// versions stay resolvable forever, because a claim for care given last March must still be judged by
/// March's rules — that invariant belongs to <see cref="PlanEndpoints"/> and nothing here weakens it.
/// Deactivating a plan withdraws it from the catalogue; it does not rewrite history.</para>
/// </summary>
public static class PlanAdminEndpoints
{
    public static void MapPlanAdmin(RouteGroupBuilder v1)
    {
        MapDetail(v1);
        MapUpdate(v1);
        MapStatus(v1);
        MapHistory(v1);
    }

    // ---- read --------------------------------------------------------------------------------------------

    private static void MapDetail(RouteGroupBuilder v1)
    {
        // "Which plan" and "what is riding on it" are never asked separately — the second is why anybody
        // opens the first. One round trip, so the two halves cannot be shown from different moments.
        v1.MapGet("/plans/{id:guid}", async (Guid id, PolicyDbContext db, PolicyGate gate, CancellationToken ct) =>
        {
            if (await gate.CheckAsync(PolicyPolicies.Read, ct) is { } denied) return denied;

            var plan = await db.Plans.AsNoTracking().FirstOrDefaultAsync(p => p.PlanId == id && !p.IsDeleted, ct);
            if (plan is null) return NotFound();

            return Results.Ok(new PlanDetailView(PlanAdminView.From(plan), await BookAsync(db, id, ct)));
        })
        .Produces<PlanDetailView>();
    }

    /// <summary>The plan's book of business, in grouped aggregates rather than a query per number.</summary>
    private static async Task<PlanBookView> BookAsync(PolicyDbContext db, Guid planId, CancellationToken ct)
    {
        var versions = db.PlanVersions.AsNoTracking().Where(v => v.PlanId == planId);

        var versionCount = await versions.CountAsync(ct);
        var draft = await versions.CountAsync(v => v.Status == PlanVersionStatus.Draft, ct);
        var active = await versions.CountAsync(v => v.Status == PlanVersionStatus.Active, ct);
        var superseded = await versions.CountAsync(v => v.Status == PlanVersionStatus.Superseded, ct);

        // The sellable window, derived: earliest version start, latest end. A null MAX over a nullable column
        // is ambiguous — it means both "no versions" and "one open-ended version" — so the open-ended case is
        // asked separately and wins, because an open-ended plan has no last day rather than an unknown one.
        DateOnly? firstFrom = await versions.MinAsync(v => (DateOnly?)v.EffectiveFrom, ct);
        var openEnded = await versions.AnyAsync(v => v.EffectiveTo == null, ct);
        DateOnly? lastTo = openEnded ? null : await versions.MaxAsync(v => v.EffectiveTo, ct);

        var policyPlans = db.PolicyPlans.AsNoTracking()
            .Where(pp => !pp.IsDeleted && versions.Any(v => v.PlanVersionId == pp.PlanVersionId));

        var policyIds = policyPlans.Select(pp => pp.PolicyId).Distinct();
        var policyCount = await policyIds.CountAsync(ct);
        var activePolicyCount = await db.Policies.AsNoTracking()
            .CountAsync(p => !p.IsDeleted && p.Status == PolicyStatus.Active && policyIds.Contains(p.PolicyId), ct);

        // Members are counted through policy_plan, not through the policy: a policy can carry several plans,
        // and "members on this policy" is a different and much larger number than "members on this plan".
        var members = db.Enrollments.AsNoTracking()
            .Where(e => !e.IsDeleted && policyPlans.Any(pp => pp.PolicyPlanId == e.PolicyPlanId));
        var memberCount = await members.CountAsync(ct);
        var activeMemberCount = await members.CountAsync(e => e.Status == EnrollmentStatus.Active, ct);

        return new PlanBookView(versionCount, draft, active, superseded,
            policyCount, activePolicyCount, memberCount, activeMemberCount, firstFrom, lastTo);
    }

    // ---- update ------------------------------------------------------------------------------------------

    private static void MapUpdate(RouteGroupBuilder v1)
    {
        v1.MapPut("/plans/{id:guid}", async (
            Guid id, UpdatePlan req, PolicyDbContext db, PolicyGate gate, IAuditClient audit, IOutbox outbox,
            TimeProvider clock, CancellationToken ct) =>
        {
            ArgumentNullException.ThrowIfNull(req);
            if (await gate.CheckAsync(PolicyPolicies.Admin, ct) is { } denied) return denied;
            if (string.IsNullOrWhiteSpace(req.NameEn) || string.IsNullOrWhiteSpace(req.NameAr))
                return ProblemResults.Invalid("PLAN_NAME_REQUIRED",
                    "A plan needs a name in both languages: half the platform renders in Arabic.");
            if (string.IsNullOrWhiteSpace(req.Category))
                return ProblemResults.Invalid("PLAN_CATEGORY_REQUIRED", "A plan needs a category.");

            var plan = await db.Plans.FirstOrDefaultAsync(p => p.PlanId == id && !p.IsDeleted, ct);
            if (plan is null) return NotFound();

            var before = Signature(plan);
            plan.NameEn = req.NameEn.Trim();
            plan.NameAr = req.NameAr.Trim();
            plan.Description = Blank(req.Description);
            plan.Category = req.Category.Trim();
            Stamp(plan, gate, clock);

            await using var tx = await db.Database.BeginTransactionAsync(ct);
            if (await SaveOrConflict(db, ct) is { } conflict) return conflict;

            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "plan", EntityId = plan.PlanId.ToString(), Action = AuditAction.Update,
                ActorUserId = gate.Subject, DecisionOutcome = "plan-updated", DecisionReasonCode = before,
            }, ct);
            // The names ride the event because reporting-service labels a plan from this feed; a rename that
            // never reaches it leaves every dashboard showing the old name with no way to tell it is stale.
            await outbox.EnqueueAsync("PlanUpdated", "policy.events", new
            {
                tenantId = plan.TenantId, planId = plan.PlanId, plan.PlanCode, plan.NameEn, plan.NameAr,
                plan.Category,
            }, ct);
            await tx.CommitAsync(ct);

            return Results.Ok(PlanAdminView.From(plan));
        })
        .Produces<PlanAdminView>();
    }

    // ---- deactivate / reactivate -------------------------------------------------------------------------

    private static void MapStatus(RouteGroupBuilder v1)
    {
        v1.MapPost("/plans/{id:guid}/deactivate", async (
            Guid id, ChangePolicyStatus req, PolicyDbContext db, PolicyGate gate, IAuditClient audit,
            IOutbox outbox, TimeProvider clock, CancellationToken ct) =>
            await SetStatusAsync(id, req, CatalogStatus.Inactive, db, gate, audit, outbox, clock, ct))
        .Produces<PlanAdminView>();

        v1.MapPost("/plans/{id:guid}/reactivate", async (
            Guid id, ChangePolicyStatus req, PolicyDbContext db, PolicyGate gate, IAuditClient audit,
            IOutbox outbox, TimeProvider clock, CancellationToken ct) =>
            await SetStatusAsync(id, req, CatalogStatus.Active, db, gate, audit, outbox, clock, ct))
        .Produces<PlanAdminView>();
    }

    private static async Task<IResult> SetStatusAsync(
        Guid id, ChangePolicyStatus req, CatalogStatus target, PolicyDbContext db, PolicyGate gate,
        IAuditClient audit, IOutbox outbox, TimeProvider clock, CancellationToken ct)
    {
        if (await gate.CheckAsync(PolicyPolicies.Admin, ct) is { } denied) return denied;

        var reason = req?.Reason?.Trim();
        if (string.IsNullOrWhiteSpace(reason) || reason.Length < 10)
            return ProblemResults.Invalid("PLAN_STATUS_REASON_REQUIRED",
                "Say why, in a sentence somebody reading this record next year would understand.");

        var plan = await db.Plans.FirstOrDefaultAsync(p => p.PlanId == id && !p.IsDeleted, ct);
        if (plan is null) return NotFound();
        if (plan.Status == target)
            return ProblemResults.Conflict("PLAN_ALREADY_IN_STATUS", $"This plan is already {target}.");

        // THE REFUSAL. See the class remarks: a plan still attached to an active policy is a product members
        // are being enrolled onto.
        if (target == CatalogStatus.Inactive)
        {
            var live = await db.PolicyPlans.AsNoTracking()
                .Where(pp => !pp.IsDeleted
                             && db.PlanVersions.Any(v => v.PlanVersionId == pp.PlanVersionId && v.PlanId == id))
                .Select(pp => pp.PolicyId).Distinct()
                .CountAsync(pid => db.Policies.Any(p => p.PolicyId == pid && !p.IsDeleted
                                                        && p.Status == PolicyStatus.Active), ct);
            if (live > 0)
            {
                await audit.EmitAsync(new AuditEventDraft
                {
                    EntityType = "plan", EntityId = id.ToString(), Action = AuditAction.Update,
                    ActorUserId = gate.Subject, TenantId = gate.Principal?.TenantId,
                    DecisionOutcome = "deactivation-refused", DecisionReasonCode = $"active-policies:{live}",
                }, ct);
                return ProblemResults.Conflict("PLAN_IN_USE",
                    $"This plan is still attached to {live} active " + (live == 1 ? "policy" : "policies") +
                    ". Detach it there first — withdrawing it from the catalogue while members are being " +
                    "enrolled onto it would leave those enrolments resolving against a product the catalogue " +
                    "says is gone, and nothing downstream would say so.");
            }
        }

        var before = Signature(plan);
        plan.Status = target;
        plan.StatusReason = reason;
        plan.StatusChangedAt = clock.GetUtcNow();
        plan.StatusChangedBy = gate.SubjectId;
        Stamp(plan, gate, clock);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (await SaveOrConflict(db, ct) is { } conflict) return conflict;

        await audit.EmitAsync(new AuditEventDraft
        {
            EntityType = "plan", EntityId = plan.PlanId.ToString(), Action = AuditAction.StateChange,
            ActorUserId = gate.Subject,
            DecisionOutcome = target == CatalogStatus.Active ? "reactivated" : "deactivated",
            DecisionReasonCode = before,
        }, ct);
        await outbox.EnqueueAsync(
            target == CatalogStatus.Active ? "PlanReactivated" : "PlanDeactivated", "policy.events",
            new { tenantId = plan.TenantId, planId = plan.PlanId, plan.PlanCode, reason }, ct);
        await tx.CommitAsync(ct);

        return Results.Ok(PlanAdminView.From(plan));
    }

    // ---- history -----------------------------------------------------------------------------------------

    private static void MapHistory(RouteGroupBuilder v1)
    {
        // The operational twin, not the audit chain — see 0021's header and 19.7 §3.3. Both are written on
        // every change; they answer different questions for different people.
        v1.MapGet("/plans/{id:guid}/history", async (
            Guid id, PolicyDbContext db, PolicyGate gate, CancellationToken ct) =>
        {
            if (await gate.CheckAsync(PolicyPolicies.Admin, ct) is { } denied) return denied;
            if (!await db.Plans.AsNoTracking().AnyAsync(p => p.PlanId == id, ct)) return NotFound();

            var rows = await db.PlanHistory.AsNoTracking()
                .Where(h => h.PlanId == id)
                .OrderByDescending(h => h.HistoryId)
                .Take(200)
                .ToListAsync(ct);

            return Results.Ok(new PlanHistoryPage(id, rows.ConvertAll(PlanHistoryEntryView.From)));
        })
        .Produces<PlanHistoryPage>();
    }

    // ---- shared ------------------------------------------------------------------------------------------

    private static void Stamp(Plan p, PolicyGate gate, TimeProvider clock)
    {
        p.UpdatedAt = clock.GetUtcNow();
        p.UpdatedBy = gate.SubjectId;
        p.UpdatedByName = gate.Principal?.DisplayName;
    }

    /// <summary>What the row said before this write, compact enough for the audit event's reason code. The
    /// history twin holds the full snapshot; this makes the AUDIT entry self-describing without a join into
    /// a store its reader may not have.</summary>
    private static string Signature(Plan p) => $"{p.NameEn}|{p.Category}|{p.Status}";

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static IResult NotFound() =>
        Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");

    private static async Task<IResult?> SaveOrConflict(PolicyDbContext db, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return null;
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            return ProblemResults.Conflict("DUPLICATE_KEY", "A plan with this code already exists.");
        }
        catch (DbUpdateConcurrencyException)
        {
            return ProblemResults.Conflict("PLAN_CHANGED",
                "Somebody else changed this plan while you were editing it. Reload and reapply your change.");
        }
    }
}
