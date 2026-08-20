using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Auth.Authorization;
using Mersal.Authz;
using Mersal.Events;
using Mersal.Policy.Domain;
using Mersal.Policy.Infrastructure;
using Mersal.Time;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Policy.Api;

/// <summary>
/// Phase 19.1 — payer / plan / effective-dated plan version + benefit configuration (design 38 §3, §4.1).
///
/// The lifecycle these endpoints implement is the whole point of the module: a version is authored as a
/// <c>Draft</c> (freely editable), <b>validated</b>, then <b>activated</b> — at which moment it becomes the
/// benefit configuration in force and can never be edited again. Changing a live plan is therefore not an
/// update but an <b>amendment</b>: clone the active version into a new draft, edit that, activate it, and the
/// predecessor closes at the successor's start date and becomes <c>Superseded</c>. Superseded versions are kept
/// forever and stay resolvable, because a claim for care given last March must be judged by March's rules.
/// </summary>
public static class PlanEndpoints
{
    public static void MapPlanAdministration(this IEndpointRouteBuilder app)
    {
        // policy:read on the group; each write additionally requires policy:admin at the gate, so a reader can
        // browse the configuration they are adjudicated against without being able to author it.
        var v1 = app.MapGroup("/api/v1").RequireAuthorization(HbmpPolicies.Scope("policy:read"));

        MapPayers(v1);
        MapPlans(v1);
        MapPlanVersions(v1);
        MapBenefitCategories(v1);

        // The two PRICING lookups sit outside that group, on `policy:read` OR `eligibility:check`.
        //
        // They were inside it, and the group's scope requirement runs BEFORE the ABAC gate — so a pharmacist
        // quoting at a counter got a bare 403 from the framework and the narrower `policy:price-lookup`
        // action never ran. The shared pricing path forwards the fulfiller's own token, so this was every
        // quote on the platform: the client could not tell that refusal from "this plan does not price
        // pharmacy", and a permission error was reported to a patient as a fact about their benefit.
        //
        // AnyScope, not a widened group: the rest of plan administration stays behind `policy:read`, which is
        // the whole benefit product. What opens here is two questions about the plan the caller is already
        // quoting from — and the gate re-checks `PriceLookup` inside, so the scope is the door and the action
        // is still the lock.
        var pricing = app.MapGroup("/api/v1")
            .RequireAuthorization(HbmpPolicies.AnyScope("policy:read", "eligibility:check"));
        MapPricingLookups(pricing);
    }

    // ---- Benefit categories ------------------------------------------------------------------------------
    //
    // 19.6 — the plan-version editor's ROW SET. Without this the only way for a client to learn which benefit
    // categories exist is to read them off a plan version that already prices them, which cannot show the
    // category nobody has configured yet — precisely the row an administrator opens the editor to add.
    // Reference data: codes and names, no PHI, no amounts, so it sits behind the ordinary read policy.
    private static void MapBenefitCategories(RouteGroupBuilder v1)
    {
        v1.MapGet("/benefit-categories", async (PolicyDbContext db, PolicyGate gate, CancellationToken ct) =>
        {
            var denied = await gate.CheckAsync(PolicyPolicies.Read, ct);
            if (denied is not null) return denied;
            var rows = await db.BenefitCategories.AsNoTracking()
                .OrderBy(c => c.Code).ToListAsync(ct);
            return Results.Ok(rows.Select(c => new BenefitCategoryView(c.BenefitCategoryId, c.Code, c.Name)));
        })
        .Produces<IEnumerable<BenefitCategoryView>>();
    }

    // ---- Payers ------------------------------------------------------------------------------------------
    private static void MapPayers(RouteGroupBuilder v1)
    {
        v1.MapPost("/payers", async (CreatePayer req, PolicyDbContext db, PolicyGate gate, IAuditClient audit,
            IOutbox outbox, TimeProvider clock, CancellationToken ct) =>
        {
            var denied = await gate.CheckAsync(PolicyPolicies.Admin, ct);
            if (denied is not null) return denied;
            if (!Enum.TryParse<PayerType>(req.PayerType, out var type))
                return ProblemResults.Invalid("UNKNOWN_PAYER_TYPE", $"'{req.PayerType}' is not a payer type.");
            if (string.IsNullOrWhiteSpace(req.PayerCode))
                return ProblemResults.Invalid("PAYER_CODE_REQUIRED", "A payer code is required.");

            var now = clock.GetUtcNow();
            var payer = new Payer
            {
                PayerId = Guid.NewGuid(), PayerCode = req.PayerCode.Trim(),
                NameEn = req.NameEn, NameAr = req.NameAr, PayerType = type,
                Contact = req.Contact ?? "{}",
                CreatedAt = now, UpdatedAt = now, CreatedBy = gate.SubjectId, UpdatedBy = gate.SubjectId,
            };
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            db.Payers.Add(payer);
            if (await SaveOrConflict(db, ct) is { } conflict) return conflict;

            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "payer", EntityId = payer.PayerId.ToString(),
                Action = AuditAction.Create, ActorUserId = gate.Subject,
            }, ct);
            await outbox.EnqueueAsync("PayerCreated", "policy.events",
                new
                {
                    tenantId = payer.TenantId, payerId = payer.PayerId, payer.PayerCode,
                    payerType = type.ToString(),
                    // The NAMES, so the dashboard can label a payer instead of printing eight characters of
                    // its uuid. reporting-service keeps a dimension-label table for exactly this and had no
                    // feed for it; `AnalyticsQueries.Label` falls back to `id.ToString()[..8]` — deliberately,
                    // because a truncated id sends someone looking while "Unknown payer" hides the gap.
                    payer.NameEn, payer.NameAr,
                }, ct);
            await tx.CommitAsync(ct);
            return Results.Created($"/api/v1/payers/{payer.PayerId}", PayerView.From(payer));
        })
        .Produces<PayerView>();

        v1.MapGet("/payers", async (PolicyDbContext db, PolicyGate gate, CancellationToken ct) =>
        {
            var denied = await gate.CheckAsync(PolicyPolicies.Read, ct);
            if (denied is not null) return denied;
            var rows = await db.Payers.AsNoTracking().Where(p => !p.IsDeleted)
                .OrderBy(p => p.PayerCode).ToListAsync(ct);
            return Results.Ok(rows.Select(PayerView.From));
        })
        .Produces<IEnumerable<PayerView>>();

        v1.MapGet("/payers/{id:guid}", async (Guid id, PolicyDbContext db, PolicyGate gate, CancellationToken ct) =>
        {
            var denied = await gate.CheckAsync(PolicyPolicies.Read, ct);
            if (denied is not null) return denied;
            var payer = await db.Payers.AsNoTracking().FirstOrDefaultAsync(p => p.PayerId == id && !p.IsDeleted, ct);
            return payer is null ? NotFound() : Results.Ok(PayerView.From(payer));
        })
        .Produces<PayerView>();
    }

    // ---- Plans -------------------------------------------------------------------------------------------
    private static void MapPlans(RouteGroupBuilder v1)
    {
        v1.MapPost("/plans", async (CreatePlan req, PolicyDbContext db, PolicyGate gate, IAuditClient audit,
            TimeProvider clock, CancellationToken ct) =>
        {
            var denied = await gate.CheckAsync(PolicyPolicies.Admin, ct);
            if (denied is not null) return denied;
            if (string.IsNullOrWhiteSpace(req.PlanCode))
                return ProblemResults.Invalid("PLAN_CODE_REQUIRED", "A plan code is required.");

            var now = clock.GetUtcNow();
            var plan = new Plan
            {
                PlanId = Guid.NewGuid(), PlanCode = req.PlanCode.Trim(),
                NameEn = req.NameEn, NameAr = req.NameAr, Description = req.Description,
                Category = req.Category,
                CreatedAt = now, UpdatedAt = now, CreatedBy = gate.SubjectId, UpdatedBy = gate.SubjectId,
            };
            db.Plans.Add(plan);
            if (await SaveOrConflict(db, ct) is { } conflict) return conflict;

            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "plan", EntityId = plan.PlanId.ToString(),
                Action = AuditAction.Create, ActorUserId = gate.Subject,
            }, ct);
            return Results.Created($"/api/v1/plans/{plan.PlanId}", PlanView.From(plan));
        })
        .Produces<PlanView>();

        v1.MapGet("/plans", async (PolicyDbContext db, PolicyGate gate, CancellationToken ct) =>
        {
            var denied = await gate.CheckAsync(PolicyPolicies.Read, ct);
            if (denied is not null) return denied;
            var rows = await db.Plans.AsNoTracking().Where(p => !p.IsDeleted).OrderBy(p => p.PlanCode).ToListAsync(ct);
            return Results.Ok(rows.Select(PlanView.From));
        })
        .Produces<IEnumerable<PlanView>>();

        // Amend = clone the version in force into a new Draft. This is the ONLY way to change a live plan.
        v1.MapPost("/plans/{id:guid}/amend", async (Guid id, PolicyDbContext db, PolicyGate gate,
            IAuditClient audit, TimeProvider clock, CancellationToken ct) =>
        {
            var denied = await gate.CheckAsync(PolicyPolicies.Admin, ct);
            if (denied is not null) return denied;

            var active = await db.PlanVersions.AsNoTracking().Include(v => v.Rules).ThenInclude(r => r.Tiers)
                .FirstOrDefaultAsync(v => v.PlanId == id && v.Status == PlanVersionStatus.Active, ct);
            if (active is null)
                return ProblemResults.Conflict("NO_ACTIVE_VERSION", "This plan has no Active version to amend.");

            var existingDraft = await db.PlanVersions.AsNoTracking()
                .AnyAsync(v => v.PlanId == id && v.Status == PlanVersionStatus.Draft, ct);
            if (existingDraft)
                return ProblemResults.Conflict("DRAFT_EXISTS", "This plan already has an open draft; edit or discard it first.");

            var now = clock.GetUtcNow();
            var nextNo = await db.PlanVersions.Where(v => v.PlanId == id).MaxAsync(v => (int?)v.VersionNo, ct) ?? 0;
            var draft = new PlanVersion
            {
                PlanVersionId = Guid.NewGuid(), PlanId = id, VersionNo = nextNo + 1,
                // The successor's window is open-ended and starts where the author later decides; seeding it at
                // the predecessor's start would collide the moment it activates, so we seed "today" and let the
                // author move it while it is still a draft.
                EffectiveFrom = BusinessCalendar.DateIn(now),
                Status = PlanVersionStatus.Draft,
                CreatedAt = now, UpdatedAt = now, CreatedBy = gate.SubjectId, UpdatedBy = gate.SubjectId,
                Rules = [.. active.Rules.Select(r => CloneRule(r, now, gate.SubjectId))],
            };
            foreach (var rule in draft.Rules) rule.PlanVersionId = draft.PlanVersionId;
            db.PlanVersions.Add(draft);
            if (await SaveOrConflict(db, ct) is { } conflict) return conflict;

            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "plan_version", EntityId = draft.PlanVersionId.ToString(),
                Action = AuditAction.Create, ActorUserId = gate.Subject, DecisionOutcome = "amend",
            }, ct);
            return Results.Created($"/api/v1/plan-versions/{draft.PlanVersionId}",
                PlanVersionView.From(draft, await CategoryCodesAsync(db, ct)));
        })
        .Produces<PlanVersionView>();
    }

    /// <summary>The two lookups the shared pricing path calls on every quote, reachable by anyone entitled to
    /// ask what a member pays. Narrower than the rest of plan administration on purpose — see the note in
    /// <c>MapPlanAdministration</c>.</summary>
    private static void MapPricingLookups(RouteGroupBuilder v1)
    {
        // The resolver, exposed for eligibility / authorization / claims: the configuration in force on a
        // SERVICE DATE. Consumers must call this rather than reading "the active version" (invariant 1).
        v1.MapGet("/plans/{id:guid}/version-at", async (Guid id, DateOnly date, IPlanVersionResolver resolver,
            PolicyDbContext db, PolicyGate gate, CancellationToken ct) =>
        {
            // PriceLookup, not Read: a pharmacist quoting at a counter must be able to ask which terms apply
            // today without being handed the whole benefit product to find out.
            var denied = await gate.CheckAsync(PolicyPolicies.PriceLookup, ct);
            if (denied is not null) return denied;
            var version = await resolver.ResolveAsync(id, date, ct);
            return version is null
                ? ProblemResults.Conflict("NO_VERSION_IN_FORCE", $"Plan {id} had no benefit configuration in force on {date:yyyy-MM-dd}.")
                : Results.Ok(PlanVersionView.From(version, await CategoryCodesAsync(db, ct)));
        })
        .Produces<PlanVersionView>();

        v1.MapGet("/plan-versions/{id:guid}/cost-share", async (Guid id, string benefitCategoryCode,
            Guid networkTierId, PolicyDbContext db, PolicyGate gate, CancellationToken ct) =>
        {
            // PriceLookup, not Read. This route is the one the SHARED pricing path calls on every quote, with
            // the fulfiller's own token forwarded; gating it on policy:read meant a pharmacist got a 403 that
            // the pricing client could not tell apart from "this plan does not price pharmacy", so a
            // permission failure was reported to a patient as a fact about their benefit.
            var denied = await gate.CheckAsync(PolicyPolicies.PriceLookup, ct);
            if (denied is not null) return denied;

            var category = await db.BenefitCategories.AsNoTracking()
                .FirstOrDefaultAsync(c => c.Code == benefitCategoryCode, ct);
            if (category is null)
                return ProblemResults.Invalid("UNKNOWN_BENEFIT_CATEGORY", $"'{benefitCategoryCode}' is not a benefit category.");

            var rule = await db.BenefitRules.AsNoTracking().Include(r => r.Tiers)
                .FirstOrDefaultAsync(r => r.PlanVersionId == id && r.BenefitCategoryId == category.BenefitCategoryId, ct);
            if (rule is null) return NotFound();

            var tier = rule.Tiers.FirstOrDefault(t => t.NetworkTierId == networkTierId);
            // 404, not a default. Activation refuses to leave an Active tier unpriced, so a miss here means the
            // caller is asking about a tier that did not exist when this version was authored — and inventing a
            // price for it is exactly the guess this layer exists to prevent.
            if (tier is null) return NotFound();

            return Results.Ok(new CostShareView(
                tier.NetworkTierId, tier.TierCode, tier.IsCovered,
                tier.CopayFixed, tier.CopayPercent, tier.CoinsurancePercent,
                // The deductible AND the waiver both travel. Flattening a waiver into a null deductible here
                // would lose the distinction the field exists for — "this category is exempt from the plan's
                // 200 EGP" is not "this plan has no deductible", and only one of them survives an amendment.
                rule.Deductible, rule.DeductibleWaived,
                tier.CopayCountsTowardDeductible,
                tier.ResolvesPreauth(rule), tier.ResolvesLimit(rule)));
        })
        .Produces<CostShareView>();
    }

    /// <summary>benefit-category id → code, for projecting a rule set the caller can write back (19.6).
    /// Internal because the membership endpoints project the same catalogue onto a plan-change preview — a
    /// second copy of this query is a second place for the two to start naming categories differently.</summary>
    internal static async Task<IReadOnlyDictionary<Guid, string>> CategoryCodesAsync(
        PolicyDbContext db, CancellationToken ct) =>
        await db.BenefitCategories.AsNoTracking().ToDictionaryAsync(c => c.BenefitCategoryId, c => c.Code, ct);

    // ---- Plan versions -----------------------------------------------------------------------------------
    private static void MapPlanVersions(RouteGroupBuilder v1)
    {
        v1.MapPost("/plan-versions", async (CreatePlanVersion req, PolicyDbContext db, PolicyGate gate,
            IAuditClient audit, TimeProvider clock, CancellationToken ct) =>
        {
            var denied = await gate.CheckAsync(PolicyPolicies.Admin, ct);
            if (denied is not null) return denied;
            if (!await db.Plans.AnyAsync(p => p.PlanId == req.PlanId && !p.IsDeleted, ct))
                return ProblemResults.Invalid("UNKNOWN_PLAN", $"Plan {req.PlanId} does not exist.");

            var now = clock.GetUtcNow();
            var nextNo = await db.PlanVersions.Where(v => v.PlanId == req.PlanId).MaxAsync(v => (int?)v.VersionNo, ct) ?? 0;
            var version = new PlanVersion
            {
                PlanVersionId = Guid.NewGuid(), PlanId = req.PlanId, VersionNo = nextNo + 1,
                EffectiveFrom = req.EffectiveFrom, EffectiveTo = req.EffectiveTo,
                Status = PlanVersionStatus.Draft,
                CreatedAt = now, UpdatedAt = now, CreatedBy = gate.SubjectId, UpdatedBy = gate.SubjectId,
            };
            db.PlanVersions.Add(version);
            if (await SaveOrConflict(db, ct) is { } conflict) return conflict;

            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "plan_version", EntityId = version.PlanVersionId.ToString(),
                Action = AuditAction.Create, ActorUserId = gate.Subject,
            }, ct);
            return Results.Created($"/api/v1/plan-versions/{version.PlanVersionId}", PlanVersionView.From(version));
        })
        .Produces<PlanVersionView>();

        v1.MapGet("/plan-versions/{id:guid}", async (Guid id, PolicyDbContext db, PolicyGate gate, CancellationToken ct) =>
        {
            var denied = await gate.CheckAsync(PolicyPolicies.Read, ct);
            if (denied is not null) return denied;
            var version = await db.PlanVersions.AsNoTracking().Include(v => v.Rules).ThenInclude(r => r.Tiers)
                .FirstOrDefaultAsync(v => v.PlanVersionId == id, ct);
            return version is null
                ? NotFound()
                : Results.Ok(PlanVersionView.From(version, await CategoryCodesAsync(db, ct)));
        })
        .Produces<PlanVersionView>();

        v1.MapGet("/plans/{planId:guid}/versions", async (Guid planId, PolicyDbContext db, PolicyGate gate, CancellationToken ct) =>
        {
            var denied = await gate.CheckAsync(PolicyPolicies.Read, ct);
            if (denied is not null) return denied;
            var rows = await db.PlanVersions.AsNoTracking().Include(v => v.Rules).ThenInclude(r => r.Tiers)
                .Where(v => v.PlanId == planId).OrderByDescending(v => v.VersionNo).ToListAsync(ct);
            var codes = await CategoryCodesAsync(db, ct);
            return Results.Ok(rows.Select(v => PlanVersionView.From(v, codes)));
        })
        .Produces<IEnumerable<PlanVersionView>>();

        // Replace the draft's benefit configuration wholesale. A rule set is a unit — accepting partial edits
        // would let an author activate a version they only half-reviewed.
        v1.MapPut("/plan-versions/{id:guid}/rules", async (Guid id, SetBenefitRules req, PolicyDbContext db,
            PolicyGate gate, IAuditClient audit, INetworkTierCatalog tiers, HttpContext http,
            TimeProvider clock, CancellationToken ct) =>
        {
            var denied = await gate.CheckAsync(PolicyPolicies.Admin, ct);
            if (denied is not null) return denied;

            var version = await db.PlanVersions.Include(v => v.Rules).ThenInclude(r => r.Tiers)
                .FirstOrDefaultAsync(v => v.PlanVersionId == id, ct);
            if (version is null) return NotFound();
            if (!version.IsEditable)
                return Immutable(version);

            var categories = await db.BenefitCategories.AsNoTracking().ToDictionaryAsync(c => c.Code, c => c.BenefitCategoryId, ct);
            // 19.1b — the tier catalogue, so a rule can only price tiers that actually exist. Read as the
            // caller, not as the service, and NOT fail-soft: an unreadable catalogue means we cannot tell a
            // valid grid from an invalid one, which is a reason to refuse the write.
            var tierCatalog = (await tiers.ActiveTiersAsync(Bearer(http), ct))
                .ToDictionary(t => t.NetworkTierId, t => t.TierCode);

            var now = clock.GetUtcNow();
            var replacement = new List<BenefitRule>();
            foreach (var r in req.Rules)
            {
                if (!categories.TryGetValue(r.BenefitCategoryCode, out var categoryId))
                    return ProblemResults.Invalid("UNKNOWN_BENEFIT_CATEGORY", $"'{r.BenefitCategoryCode}' is not a benefit category.");
                if (r.LimitType is not null && !Enum.TryParse<LimitType>(r.LimitType, out _))
                    return ProblemResults.Invalid("UNKNOWN_LIMIT_TYPE", $"'{r.LimitType}' is not a limit type.");
                if (!Enum.TryParse<ResetPeriod>(r.ResetPeriod ?? "None", out var reset))
                    return ProblemResults.Invalid("UNKNOWN_RESET_PERIOD", $"'{r.ResetPeriod}' is not a reset period.");
                // "Waived" and "there isn't one" are different statements, and only the first needs a figure
                // to waive. The DB has always enforced this (ck_benefit_rule_waiver_needs_deductible); until
                // now the handler did not, so an author who set the flag without a deductible got a 500 with
                // no indication of which of their rules was wrong.
                if (r.DeductibleWaived && r.Deductible is null)
                    return ProblemResults.Invalid("WAIVER_WITHOUT_DEDUCTIBLE",
                        $"'{r.BenefitCategoryCode}' waives a deductible but names none. A category with no "
                        + "deductible leaves the waiver off; the flag records an exemption from a figure that exists.");

                var rule = new BenefitRule
                {
                    RuleId = Guid.NewGuid(), PlanVersionId = version.PlanVersionId, BenefitCategoryId = categoryId,
                    IsCovered = r.IsCovered,
                    LimitType = r.LimitType is null ? null : Enum.Parse<LimitType>(r.LimitType),
                    LimitValue = r.LimitValue, ResetPeriod = reset,
                    Deductible = r.Deductible, DeductibleWaived = r.DeductibleWaived,
                    WaitingPeriodDays = r.WaitingPeriodDays, RequiresPreauth = r.RequiresPreauth,
                    PreauthCostThreshold = r.PreauthCostThreshold,
                    Exclusions = r.Exclusions ?? "[]", Notes = r.Notes,
                    CreatedAt = now, UpdatedAt = now, CreatedBy = gate.SubjectId, UpdatedBy = gate.SubjectId,
                };

                foreach (var t in r.Tiers ?? [])
                {
                    if (!tierCatalog.TryGetValue(t.NetworkTierId, out var tierCode))
                        return ProblemResults.Invalid("UNKNOWN_NETWORK_TIER",
                            $"{t.NetworkTierId} is not an Active network tier. Tiers are created by the Network Team.");
                    rule.Tiers.Add(new BenefitRuleTier
                    {
                        RuleTierId = Guid.NewGuid(), BenefitRuleId = rule.RuleId,
                        NetworkTierId = t.NetworkTierId, TierCode = tierCode, IsCovered = t.IsCovered,
                        CopayFixed = t.CopayFixed, CopayPercent = t.CopayPercent,
                        CoinsurancePercent = t.CoinsurancePercent,
                        CopayCountsTowardDeductible = t.CopayCountsTowardDeductible,
                        RequiresPreauthOverride = t.RequiresPreauthOverride, LimitMultiplier = t.LimitMultiplier,
                        CreatedAt = now, UpdatedAt = now, CreatedBy = gate.SubjectId, UpdatedBy = gate.SubjectId,
                    });
                }
                if (rule.Tiers.Select(t => t.NetworkTierId).Distinct().Count() != rule.Tiers.Count)
                    return ProblemResults.Invalid("DUPLICATE_TIER",
                        $"Category {r.BenefitCategoryCode} prices the same network tier more than once.");

                replacement.Add(rule);
            }
            if (replacement.Select(r => r.BenefitCategoryId).Distinct().Count() != replacement.Count)
                return ProblemResults.Invalid("DUPLICATE_CATEGORY", "A benefit category may appear at most once in a version.");

            db.BenefitRuleTiers.RemoveRange(version.Rules.SelectMany(r => r.Tiers));
            db.BenefitRules.RemoveRange(version.Rules);
            db.BenefitRules.AddRange(replacement);
            version.UpdatedAt = now;
            version.UpdatedBy = gate.SubjectId;
            if (await SaveOrConflict(db, ct) is { } conflict) return conflict;

            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "plan_version", EntityId = version.PlanVersionId.ToString(),
                Action = AuditAction.Update, ActorUserId = gate.Subject, DecisionOutcome = "rules-set",
            }, ct);
            var saved = await db.PlanVersions.AsNoTracking().Include(v => v.Rules).ThenInclude(r => r.Tiers)
                .FirstAsync(v => v.PlanVersionId == id, ct);
            return Results.Ok(PlanVersionView.From(saved, await CategoryCodesAsync(db, ct)));
        })
        .Produces<PlanVersionView>();

        // 19.1b — THE cost-share lookup approvals, eligibility and claims all resolve against, via the shared
        // libs/benefit-pricing path. It answers "what did this version agree for this category at this tier",
        // and nothing more: no arithmetic happens here, because the split has to be computed in exactly one
        // place (libs/money) or the eligibility card and the claim stop agreeing.
        // Dry run: exactly the checks activation applies, without the state change. Lets an author fix a plan
        // before the irreversible step rather than discovering the problems through a 422.
        v1.MapPost("/plan-versions/{id:guid}/validate", async (Guid id, PolicyDbContext db, PolicyGate gate,
            INetworkTierCatalog tiers, HttpContext http, IBusinessCalendar calendar, CancellationToken ct) =>
        {
            var denied = await gate.CheckAsync(PolicyPolicies.Read, ct);
            if (denied is not null) return denied;
            var version = await db.PlanVersions.AsNoTracking().Include(v => v.Rules).ThenInclude(r => r.Tiers)
                .FirstOrDefaultAsync(v => v.PlanVersionId == id, ct);
            if (version is null) return NotFound();

            var problems = PlanVersionValidation.Validate(version, calendar.Today(),
                await tiers.ActiveTiersAsync(Bearer(http), ct));
            return Results.Ok(new { valid = problems.Count == 0, problems });
        });

        v1.MapPost("/plan-versions/{id:guid}/activate", async (Guid id, PolicyDbContext db, PolicyGate gate,
            IAuditClient audit, IOutbox outbox, INetworkTierCatalog tiers, HttpContext http,
            IBusinessCalendar calendar, TimeProvider clock, CancellationToken ct) =>
        {
            var denied = await gate.CheckAsync(PolicyPolicies.Admin, ct);
            if (denied is not null) return denied;

            var version = await db.PlanVersions.Include(v => v.Rules).ThenInclude(r => r.Tiers)
                .FirstOrDefaultAsync(v => v.PlanVersionId == id, ct);
            if (version is null) return NotFound();
            if (version.Status != PlanVersionStatus.Draft)
                return Immutable(version);

            // 19.1b — the tier catalogue is read at ACTIVATION, not only at authoring time. A tier added after
            // the draft was written must still be priced before that draft can go live, or the plan activates
            // with a hole in its cost-share grid that nobody edited into it.
            var problems = PlanVersionValidation.Validate(version, calendar.Today(),
                await tiers.ActiveTiersAsync(Bearer(http), ct));
            if (problems.Count > 0)
                return ProblemResults.Unprocessable("PLAN_VERSION_INVALID",
                    "This version cannot be activated.", new Dictionary<string, object?> { ["problems"] = problems });

            var now = clock.GetUtcNow();
            // Activation supersedes one version and activates another, and each announces itself. All four
            // facts are one change of what members are entitled to: opened here, before the outgoing version
            // is even read, so a concurrent activation cannot slip between the read and the supersede.
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            // Close the outgoing version at the incoming one's start date. Because the window is half-open the
            // two abut exactly: [.., from) then [from, ..) — no gap for a service date to fall through, and no
            // day covered twice (which the 0005 exclusion constraint would reject anyway).
            var outgoing = await db.PlanVersions
                .FirstOrDefaultAsync(v => v.PlanId == version.PlanId && v.Status == PlanVersionStatus.Active, ct);
            if (outgoing is not null)
            {
                if (version.EffectiveFrom <= outgoing.EffectiveFrom)
                    return ProblemResults.Unprocessable("STARTS_BEFORE_PREDECESSOR",
                        $"This version starts on {version.EffectiveFrom:yyyy-MM-dd}, on or before the version it would supersede ({outgoing.EffectiveFrom:yyyy-MM-dd}).");
                outgoing.EffectiveTo = version.EffectiveFrom;
                outgoing.Status = PlanVersionStatus.Superseded;
                outgoing.SupersededByVersionId = version.PlanVersionId;
                outgoing.UpdatedAt = now;
                outgoing.UpdatedBy = gate.SubjectId;
            }

            version.Status = PlanVersionStatus.Active;
            version.ActivatedAt = now;
            version.ActivatedBy = gate.SubjectId;
            version.UpdatedAt = now;
            version.UpdatedBy = gate.SubjectId;
            if (await SaveOrConflict(db, ct) is { } conflict) return conflict;

            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "plan_version", EntityId = version.PlanVersionId.ToString(),
                Action = AuditAction.StateChange, ActorUserId = gate.Subject, DecisionOutcome = "activated",
            }, ct);
            await outbox.EnqueueAsync("PlanVersionActivated", "policy.events", new
            {
                tenantId = version.TenantId, planId = version.PlanId, planVersionId = version.PlanVersionId,
                version.VersionNo, effectiveFrom = version.EffectiveFrom, effectiveTo = version.EffectiveTo,
            }, ct);
            if (outgoing is not null)
            {
                await audit.EmitAsync(new AuditEventDraft
                {
                    EntityType = "plan_version", EntityId = outgoing.PlanVersionId.ToString(),
                    Action = AuditAction.StateChange, ActorUserId = gate.Subject, DecisionOutcome = "superseded",
                }, ct);
                await outbox.EnqueueAsync("PlanVersionSuperseded", "policy.events", new
                {
                    tenantId = outgoing.TenantId, planId = outgoing.PlanId, planVersionId = outgoing.PlanVersionId,
                    supersededBy = version.PlanVersionId, effectiveTo = outgoing.EffectiveTo,
                }, ct);
            }
            await tx.CommitAsync(ct);

            var saved = await db.PlanVersions.AsNoTracking().Include(v => v.Rules).ThenInclude(r => r.Tiers)
                .FirstAsync(v => v.PlanVersionId == id, ct);
            return Results.Ok(PlanVersionView.From(saved, await CategoryCodesAsync(db, ct)));
        });
    }

    // ---- helpers -----------------------------------------------------------------------------------------

    /// <summary>Clone a rule AND its cost-share grid into a fresh draft. Cloning the rule alone would produce a
    /// draft that prices nothing — and since activation now rejects an unpriced tier, an amendment would fail
    /// validation for a grid the author never touched.</summary>
    private static BenefitRule CloneRule(BenefitRule source, DateTimeOffset now, Guid? actor)
    {
        var clone = new BenefitRule
        {
            RuleId = Guid.NewGuid(),
            BenefitCategoryId = source.BenefitCategoryId, IsCovered = source.IsCovered,
            LimitType = source.LimitType, LimitValue = source.LimitValue, ResetPeriod = source.ResetPeriod,
            Deductible = source.Deductible, DeductibleWaived = source.DeductibleWaived,
            WaitingPeriodDays = source.WaitingPeriodDays, RequiresPreauth = source.RequiresPreauth,
            PreauthCostThreshold = source.PreauthCostThreshold,
            Exclusions = source.Exclusions, Notes = source.Notes,
            CreatedAt = now, UpdatedAt = now, CreatedBy = actor, UpdatedBy = actor,
        };
        clone.Tiers.AddRange(source.Tiers.Select(t => new BenefitRuleTier
        {
            RuleTierId = Guid.NewGuid(), BenefitRuleId = clone.RuleId,
            NetworkTierId = t.NetworkTierId, TierCode = t.TierCode, IsCovered = t.IsCovered,
            CopayFixed = t.CopayFixed, CopayPercent = t.CopayPercent, CoinsurancePercent = t.CoinsurancePercent,
            CopayCountsTowardDeductible = t.CopayCountsTowardDeductible, RequiresPreauthOverride = t.RequiresPreauthOverride, LimitMultiplier = t.LimitMultiplier,
            CreatedAt = now, UpdatedAt = now, CreatedBy = actor, UpdatedBy = actor,
        }));
        return clone;
    }

    /// <summary>The caller's bearer, forwarded to provider-service so the tier catalogue is read as THEM. A
    /// service-to-service token would let a plan be validated against tiers the author cannot see.</summary>
    private static string? Bearer(HttpContext http) => http.Request.Headers.Authorization.FirstOrDefault();

    private static IResult NotFound() =>
        Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");

    /// <summary>The 409 an activated version answers every write with. The database refuses it too (0005
    /// triggers) — this is the polite half of the same rule.</summary>
    private static IResult Immutable(PlanVersion version) =>
        ProblemResults.Conflict("PLAN_VERSION_IMMUTABLE",
            $"Version {version.VersionNo} is {version.Status} and immutable. Amend the plan to create a new version.");

    /// <summary>Translate the database's own invariants into the API's vocabulary. A unique-key clash and an
    /// overlapping effective range are both legitimate client errors, not 500s — and the immutability triggers
    /// raise here too, for any path that reached the DB without going through <see cref="Immutable"/>.</summary>
    private static async Task<IResult?> SaveOrConflict(PolicyDbContext db, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return null;
        }
        // Only the states we can explain are translated; anything else keeps its stack and becomes a 500, because
        // a database error we have not reasoned about is not something to report to a client as their mistake.
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException pg
                                           && pg.SqlState is "23505" or "23P01" or "P0001")
        {
            var pgEx = (Npgsql.PostgresException)ex.InnerException!;
            return pgEx.SqlState switch
            {
                "23505" => ProblemResults.Conflict("DUPLICATE_KEY", "A record with this code or version already exists."),
                "23P01" => ProblemResults.Conflict("OVERLAPPING_VERSION",
                    "Another version of this plan already covers part of that effective range."),
                _ => ProblemResults.Conflict("PLAN_VERSION_IMMUTABLE", pgEx.MessageText),
            };
        }
    }
}
