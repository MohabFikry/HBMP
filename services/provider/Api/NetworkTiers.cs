using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Auth.Authorization;
using Mersal.Authz;
using Mersal.Events;
using Mersal.Provider.Domain;
using Mersal.Provider.Infrastructure;
using Mersal.Time;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Provider.Api;

/// <summary>
/// Phase 19.1b — network tier administration (design 38 §3, §4.1b).
///
/// The Network Team defines the tiers and decides which providers, locations and contract service lines sit in
/// them, with effective dates. policy-service then prices a benefit PER TIER (policy.benefit_rule_tier) — it
/// reads the resolved tier and never writes here. Two properties carry the design:
///
/// <list type="bullet">
/// <item><b>Resolution is at the SERVICE DATE, most-specific-wins.</b> A provider moving from T2 to T1 on
/// 1 March must not change what February's already-adjudicated claim was priced at.</item>
/// <item><b>Resolution FAILS SAFE.</b> A provider with no assignment resolves to the out-of-network tier, never
/// to "in network by omission" — the failure mode that pays the best rate to a provider nobody negotiated
/// with.</item>
/// </list>
/// </summary>
public static class NetworkTierEndpoints
{
    public static void MapNetworkTiers(this WebApplication app)
    {
        // Reads are open to provider:read (the tier codes are the vocabulary eligibility and claims speak in).
        // Writes additionally pass NetworkTierGate, which requires the Network Team role AND provider:admin.
        var read = app.MapGroup("/api/v1/network-tiers").RequireAuthorization(HbmpPolicies.Scope("provider:read"));
        var write = app.MapGroup("/api/v1/network-tiers").RequireAuthorization(HbmpPolicies.Scope("provider:admin"));

        MapTierCrud(read, write);
        MapAssignments(read, write);
        MapResolve(read);
    }

    // ---- Tier CRUD ---------------------------------------------------------------------------------------
    private static void MapTierCrud(RouteGroupBuilder read, RouteGroupBuilder write)
    {
        write.MapPost("", async (CreateNetworkTier req, ProviderDbContext db, NetworkTierGate gate,
            IAuditClient audit, IOutbox outbox, TimeProvider clock, CancellationToken ct) =>
        {
            if (await gate.CheckAsync(ct) is { } denied) return denied;
            if (string.IsNullOrWhiteSpace(req.TierCode))
                return ProblemResults.Invalid("TIER_CODE_REQUIRED", "A tier code is required.");
            if (req.Rank <= 0)
                return ProblemResults.Invalid("BAD_RANK", "Rank must be 1 or greater (1 = most preferred).");

            var now = clock.GetUtcNow();
            var tier = new NetworkTier
            {
                NetworkTierId = Guid.NewGuid(), TenantId = gate.TenantId!,
                TierCode = req.TierCode.Trim().ToUpperInvariant(),
                NameEn = req.NameEn, NameAr = req.NameAr, Rank = req.Rank, Description = req.Description,
                IsOutOfNetwork = req.IsOutOfNetwork,
                CreatedAt = now, UpdatedAt = now, CreatedBy = gate.Subject, UpdatedBy = gate.Subject,
            };
            // 24.3 — the network tier IS the price a claim adjudicates at. A tier row whose event is lost
            // is a tariff downstream never learns about; an event without the row is worse.
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            db.NetworkTiers.Add(tier);
            if (await SaveOrConflict(db, ct) is { } conflict) return conflict;

            await audit.EmitAsync(Draft(tier, AuditAction.Create, gate, "created"), ct);
            await outbox.EnqueueAsync("NetworkTierCreated", "provider.events", new
            {
                tenantId = tier.TenantId, networkTierId = tier.NetworkTierId, tier.TierCode, tier.Rank, tier.IsOutOfNetwork,
            }, ct);
            await tx.CommitAsync(ct);
            return Results.Created($"/api/v1/network-tiers/{tier.NetworkTierId}", NetworkTierView.From(tier));
        });

        read.MapGet("", async (ProviderDbContext db, string? status, CancellationToken ct) =>
        {
            var q = db.NetworkTiers.AsNoTracking().Where(t => !t.IsDeleted);
            if (status is not null && Enum.TryParse<NetworkTierStatus>(status, out var s)) q = q.Where(t => t.Status == s);
            var rows = await q.OrderBy(t => t.Rank).ToListAsync(ct);
            return Results.Ok(rows.Select(NetworkTierView.From));
        });

        read.MapGet("/{id:guid}", async (Guid id, ProviderDbContext db, CancellationToken ct) =>
        {
            var tier = await db.NetworkTiers.AsNoTracking().FirstOrDefaultAsync(t => t.NetworkTierId == id && !t.IsDeleted, ct);
            return tier is null ? NotFound() : Results.Ok(NetworkTierView.From(tier));
        });

        // Labels and rank may be corrected. tier_code and is_out_of_network may NOT: both are referenced by
        // benefit_rule_tier rows and by already-adjudicated claims, so changing them rewrites the meaning of
        // history rather than fixing a typo. Retire the tier and create the right one.
        write.MapPut("/{id:guid}", async (Guid id, UpdateNetworkTier req, ProviderDbContext db, NetworkTierGate gate,
            IAuditClient audit, IOutbox outbox, TimeProvider clock, CancellationToken ct) =>
        {
            if (await gate.CheckAsync(ct) is { } denied) return denied;
            var tier = await db.NetworkTiers.FirstOrDefaultAsync(t => t.NetworkTierId == id && !t.IsDeleted, ct);
            if (tier is null) return NotFound();
            if (req.Rank is { } rank && rank <= 0)
                return ProblemResults.Invalid("BAD_RANK", "Rank must be 1 or greater.");

            await using var tx = await db.Database.BeginTransactionAsync(ct);
            if (req.NameEn is not null) tier.NameEn = req.NameEn;
            if (req.NameAr is not null) tier.NameAr = req.NameAr;
            if (req.Description is not null) tier.Description = req.Description;
            if (req.Rank is { } newRank) tier.Rank = newRank;
            tier.UpdatedAt = clock.GetUtcNow();
            tier.UpdatedBy = gate.Subject;
            if (await SaveOrConflict(db, ct) is { } conflict) return conflict;

            await audit.EmitAsync(Draft(tier, AuditAction.Update, gate, "updated"), ct);
            await outbox.EnqueueAsync("NetworkTierUpdated", "provider.events",
                new { tenantId = tier.TenantId, networkTierId = tier.NetworkTierId, tier.TierCode }, ct);
            await tx.CommitAsync(ct);
            return Results.Ok(NetworkTierView.From(tier));
        });

        // Retire, never delete: a claim adjudicated last year was priced at this tier and that history has to
        // stay readable. Retiring frees the tier's rank for a successor.
        write.MapPost("/{id:guid}/retire", async (Guid id, RetireNetworkTier req, ProviderDbContext db,
            NetworkTierGate gate, IAuditClient audit, IOutbox outbox, IBusinessCalendar calendar,
            TimeProvider clock, CancellationToken ct) =>
        {
            if (await gate.CheckAsync(ct) is { } denied) return denied;
            if (string.IsNullOrWhiteSpace(req.Reason))
                return ProblemResults.Invalid("REASON_REQUIRED", "A reason is required to retire a tier.");
            var tier = await db.NetworkTiers.FirstOrDefaultAsync(t => t.NetworkTierId == id && !t.IsDeleted, ct);
            if (tier is null) return NotFound();
            if (tier.Status == NetworkTierStatus.Retired) return Results.Ok(NetworkTierView.From(tier));

            // Retiring a tier that still governs providers would silently drop them to out-of-network on the
            // next resolution. Make the Network Team move them first, and say how many.
            var today = calendar.Today();
            var stillAssigned = await db.NetworkAssignments.AsNoTracking()
                .Where(a => a.NetworkTierId == id && a.Status == NetworkAssignmentStatus.Active && !a.IsDeleted
                            && (a.EffectiveTo == null || a.EffectiveTo > today))
                .CountAsync(ct);
            if (stillAssigned > 0)
                return ProblemResults.Conflict("TIER_STILL_ASSIGNED",
                    $"{stillAssigned} assignment(s) still point at this tier; reassign or close them first.");

            await using var tx = await db.Database.BeginTransactionAsync(ct);
            tier.Status = NetworkTierStatus.Retired;
            tier.UpdatedAt = clock.GetUtcNow();
            tier.UpdatedBy = gate.Subject;
            await db.SaveChangesAsync(ct);

            await audit.EmitAsync(Draft(tier, AuditAction.StateChange, gate, "retired", req.Reason), ct);
            await outbox.EnqueueAsync("NetworkTierRetired", "provider.events",
                new { tenantId = tier.TenantId, networkTierId = tier.NetworkTierId, tier.TierCode }, ct);
            await tx.CommitAsync(ct);
            return Results.Ok(NetworkTierView.From(tier));
        });
    }

    // ---- Assignments -------------------------------------------------------------------------------------
    private static void MapAssignments(RouteGroupBuilder read, RouteGroupBuilder write)
    {
        write.MapPost("/{id:guid}/assignments", async (Guid id, CreateTierAssignment req, ProviderDbContext db,
            NetworkTierGate gate, IAuditClient audit, IOutbox outbox, TimeProvider clock, CancellationToken ct) =>
        {
            if (await gate.CheckAsync(ct) is { } denied) return denied;
            if (!Enum.TryParse<NetworkAssignmentScope>(req.Scope, out var scope))
                return ProblemResults.Invalid("UNKNOWN_SCOPE", $"'{req.Scope}' is not an assignment scope.");
            if (req.EffectiveTo is { } to && to <= req.EffectiveFrom)
                return ProblemResults.Invalid("BAD_WINDOW", "effectiveTo must be after effectiveFrom (the end is exclusive).");

            var tier = await db.NetworkTiers.AsNoTracking().FirstOrDefaultAsync(t => t.NetworkTierId == id && !t.IsDeleted, ct);
            if (tier is null) return NotFound();
            if (tier.Status != NetworkTierStatus.Active)
                return ProblemResults.Conflict("TIER_RETIRED", "A retired tier cannot take new assignments.");

            // Resolve the owning provider from the scope ref. This both validates the ref exists and supplies
            // the denormalized provider_id the RLS predicate needs (0008).
            var owner = await OwningProviderAsync(db, scope, req.ScopeRef, ct);
            if (owner is null)
                return ProblemResults.Invalid("UNKNOWN_SCOPE_REF",
                    $"No {scope} with id {req.ScopeRef} exists in this tenant.");

            var now = clock.GetUtcNow();
            var assignment = new ProviderNetworkAssignment
            {
                AssignmentId = Guid.NewGuid(), TenantId = gate.TenantId!, NetworkTierId = id,
                ProviderId = owner.Value, Scope = scope, ScopeRef = req.ScopeRef,
                EffectiveFrom = req.EffectiveFrom, EffectiveTo = req.EffectiveTo,
                CreatedAt = now, UpdatedAt = now, CreatedBy = gate.Subject, UpdatedBy = gate.Subject,
            };
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            db.NetworkAssignments.Add(assignment);
            if (await SaveOrConflict(db, ct) is { } conflict) return conflict;

            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "provider_network_assignment", EntityId = assignment.AssignmentId.ToString(),
                Action = AuditAction.Create, ActorUserId = gate.Subject, TenantId = gate.TenantId,
                DecisionOutcome = $"{scope}->{tier.TierCode}",
            }, ct);
            await outbox.EnqueueAsync("ProviderTierAssigned", "provider.events", new
            {
                tenantId = assignment.TenantId, assignmentId = assignment.AssignmentId,
                networkTierId = id, tier.TierCode, providerId = assignment.ProviderId,
                scope = scope.ToString(), scopeRef = assignment.ScopeRef,
                effectiveFrom = assignment.EffectiveFrom, effectiveTo = assignment.EffectiveTo,
            }, ct);
            await tx.CommitAsync(ct);
            return Results.Created($"/api/v1/network-tiers/assignments/{assignment.AssignmentId}",
                TierAssignmentView.From(assignment, tier.TierCode));
        });

        read.MapGet("/{id:guid}/assignments", async (Guid id, ProviderDbContext db, CancellationToken ct) =>
        {
            var rows = await db.NetworkAssignments.AsNoTracking()
                .Where(a => a.NetworkTierId == id && !a.IsDeleted)
                .OrderByDescending(a => a.EffectiveFrom).ToListAsync(ct);
            return Results.Ok(rows.Select(a => TierAssignmentView.From(a, null)));
        });

        // Withdrawing an assignment is THREE acts, not two, and the response says which one happened.
        //
        //   Revoked    it had not taken effect yet — it never governed a service, so erasing it changes nothing.
        //   Closed     it is in force and is ENDING (effective_to = today). It keeps governing its own window,
        //              which is what a tier move must do: February's care stays priced at February's tier.
        //   Corrected  it WAS in force and should never have been (wrong provider, wrong tier). Retroactively
        //              voided. Without this verb a week-old mis-assignment leaves a week of wrong resolution
        //              standing with no legitimate way to fix it — closing it would only stop it going forward.
        //
        // A correction is REFUSED once any claim has adjudicated against the assignment. At that point money has
        // moved on the strength of that tier, and quietly rewriting it would leave settled claims referencing a
        // tier the record no longer admits to. The fix from there is a claims adjustment, not a tier edit.
        write.MapDelete("/assignments/{assignmentId:guid}", async (Guid assignmentId, string? reason, bool? correct,
            ProviderDbContext db, NetworkTierGate gate, IAuditClient audit, IOutbox outbox,
            IAdjudicatedClaimProbe claims, IBusinessCalendar calendar, TimeProvider clock, CancellationToken ct) =>
        {
            if (await gate.CheckAsync(ct) is { } denied) return denied;
            if (string.IsNullOrWhiteSpace(reason))
                return ProblemResults.Invalid("REASON_REQUIRED", "A reason is required to withdraw an assignment.");

            var a = await db.NetworkAssignments.FirstOrDefaultAsync(x => x.AssignmentId == assignmentId && !x.IsDeleted, ct);
            if (a is null) return NotFound();
            if (a.Status != NetworkAssignmentStatus.Active)
                return Results.Ok(new { a.AssignmentId, outcome = $"Already{a.Status}" });

            // 24.3 — a withdrawn or corrected assignment changes what a claim prices against. The write
            // and its event commit together; every early return above changed nothing.
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var today = calendar.Today();
            var now = clock.GetUtcNow();
            string outcome;

            if (correct == true)
            {
                var adjudicated = await claims.CountAdjudicatedAgainstAsync(a, ct);
                if (adjudicated > 0)
                    return ProblemResults.Conflict("ASSIGNMENT_HAS_ADJUDICATED_CLAIMS",
                        $"{adjudicated} claim(s) have already been adjudicated against this tier assignment. " +
                        "Correcting it would leave settled claims referencing a tier the record no longer admits to — " +
                        "raise a claims adjustment instead.");
                a.Status = NetworkAssignmentStatus.Corrected;
                outcome = "Corrected";
            }
            else if (a.EffectiveFrom > today)
            {
                a.Status = NetworkAssignmentStatus.Revoked;
                outcome = "Revoked";
            }
            else
            {
                if (a.EffectiveTo is not null && a.EffectiveTo <= today)
                    return ProblemResults.Conflict("ALREADY_CLOSED",
                        "This assignment has already ended. To void it retroactively, correct it instead.");
                a.EffectiveTo = today;
                outcome = "Closed";
            }

            a.RevokedReason = reason;
            a.UpdatedAt = now;
            a.UpdatedBy = gate.Subject;
            if (await SaveOrConflict(db, ct) is { } conflict) return conflict;

            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "provider_network_assignment", EntityId = a.AssignmentId.ToString(),
                Action = AuditAction.StateChange, ActorUserId = gate.Subject, TenantId = gate.TenantId,
                DecisionOutcome = outcome, DecisionReasonCode = reason,
                // A correction rewrites what the tier map says about the PAST, so it is audited as its own
                // high-signal act rather than blending into the ordinary churn of tier moves.
                FieldClasses = outcome == "Corrected" ? ["network-tier-correction"] : [],
            }, ct);
            await outbox.EnqueueAsync(
                outcome == "Corrected" ? "ProviderTierCorrected" : "ProviderTierRevoked", "provider.events", new
                {
                    tenantId = a.TenantId, assignmentId = a.AssignmentId, networkTierId = a.NetworkTierId,
                    providerId = a.ProviderId, outcome, effectiveTo = a.EffectiveTo, reason,
                }, ct);
            await tx.CommitAsync(ct);
            return Results.Ok(new { a.AssignmentId, outcome, effectiveTo = a.EffectiveTo });
        });
    }

    // ---- Resolution --------------------------------------------------------------------------------------
    private static void MapResolve(RouteGroupBuilder read)
    {
        // THE endpoint eligibility, approvals and claims call. Note serviceDate is REQUIRED: a resolver that
        // defaulted to today would quietly answer the wrong question for every retrospective adjudication,
        // which is the single failure this whole layer exists to prevent.
        read.MapGet("/resolve", async (Guid providerId, DateOnly serviceDate, Guid? locationId, string? serviceCode,
            ProviderDbContext db, CancellationToken ct) =>
        {
            var provider = await db.Providers.AsNoTracking()
                .FirstOrDefaultAsync(p => p.ProviderId == providerId && !p.IsDeleted, ct);
            if (provider is null) return NotFound();

            if (locationId is { } loc && !await db.Locations.AsNoTracking()
                    .AnyAsync(l => l.LocationId == loc && l.ProviderId == providerId && !l.IsDeleted, ct))
                return ProblemResults.Invalid("LOCATION_NOT_OF_PROVIDER",
                    $"Location {loc} does not belong to provider {providerId}.");

            var refs = new List<Guid> { providerId };
            if (locationId is { } l2) refs.Add(l2);

            // A service code narrows to the contract service line in force on the SERVICE DATE. provider_contract
            // carries its own no-overlap exclusion (0001), so at most one contract — and therefore at most one
            // line per code — is in effect on any given day.
            if (!string.IsNullOrWhiteSpace(serviceCode))
            {
                var lineIds = await db.Contracts.AsNoTracking()
                    .Where(c => c.ProviderId == providerId && !c.IsDeleted
                                && c.EffectiveFrom <= serviceDate
                                && (c.EffectiveTo == null || c.EffectiveTo >= serviceDate))
                    .SelectMany(c => c.ServiceLines.Where(sl => sl.Code == serviceCode).Select(sl => sl.ServiceLineId))
                    .ToListAsync(ct);
                refs.AddRange(lineIds);
            }

            var candidates = await db.NetworkAssignments.AsNoTracking()
                .Where(a => !a.IsDeleted && a.Status == NetworkAssignmentStatus.Active && refs.Contains(a.ScopeRef))
                .ToListAsync(ct);

            // The candidates' tiers plus the out-of-network default, which is what resolution falls back to.
            var tierIds = candidates.Select(a => a.NetworkTierId).Distinct().ToList();
            var tiers = await db.NetworkTiers.AsNoTracking()
                .Where(t => !t.IsDeleted && (tierIds.Contains(t.NetworkTierId)
                                             || (t.IsOutOfNetwork && t.Status == NetworkTierStatus.Active)))
                .ToDictionaryAsync(t => t.NetworkTierId, t => t, ct);

            var resolved = NetworkTierResolution.Resolve(candidates, tiers, serviceDate);
            if (resolved is null)
                return ProblemResults.Conflict("NO_DEFAULT_TIER",
                    "No assignment matched and no Active out-of-network tier is configured to fall back to.");

            return Results.Ok(TierResolutionView.From(resolved, providerId, locationId, serviceCode, serviceDate));
        });
    }

    // ---- helpers -----------------------------------------------------------------------------------------

    /// <summary>The provider that owns a scope ref, or null when the ref does not exist (RLS keeps this
    /// tenant-scoped). Also the validation that a caller cannot assign a tier to something imaginary.</summary>
    private static async Task<Guid?> OwningProviderAsync(
        ProviderDbContext db, NetworkAssignmentScope scope, Guid scopeRef, CancellationToken ct) => scope switch
    {
        NetworkAssignmentScope.Provider => await db.Providers.AsNoTracking()
            .Where(p => p.ProviderId == scopeRef && !p.IsDeleted)
            .Select(p => (Guid?)p.ProviderId).FirstOrDefaultAsync(ct),

        NetworkAssignmentScope.Location => await db.Locations.AsNoTracking()
            .Where(l => l.LocationId == scopeRef && !l.IsDeleted)
            .Select(l => (Guid?)l.ProviderId).FirstOrDefaultAsync(ct),

        NetworkAssignmentScope.ContractServiceLine => await db.ServiceLines.AsNoTracking()
            .Where(sl => sl.ServiceLineId == scopeRef)
            .Join(db.Contracts.AsNoTracking().Where(c => !c.IsDeleted),
                sl => sl.ContractId, c => c.ContractId, (sl, c) => (Guid?)c.ProviderId)
            .FirstOrDefaultAsync(ct),

        _ => null,
    };

    private static AuditEventDraft Draft(NetworkTier t, AuditAction action, NetworkTierGate gate,
        string? outcome = null, string? reason = null) => new()
    {
        EntityType = "network_tier", EntityId = t.NetworkTierId.ToString(), Action = action,
        ActorUserId = gate.Subject, TenantId = gate.TenantId,
        DecisionOutcome = outcome, DecisionReasonCode = reason,
    };

    private static IResult NotFound() =>
        Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");

    /// <summary>Translate the database's own invariants into the API's vocabulary — a duplicate tier code, a
    /// second Active out-of-network tier, a rank clash and an overlapping assignment window are all legitimate
    /// client errors rather than 500s.</summary>
    private static async Task<IResult?> SaveOrConflict(ProviderDbContext db, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return null;
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException pg
                                           && pg.SqlState is "23505" or "23P01")
        {
            var pgEx = (Npgsql.PostgresException)ex.InnerException!;
            return pgEx.SqlState == "23P01"
                ? ProblemResults.Conflict("OVERLAPPING_ASSIGNMENT",
                    "This provider, location or service line is already assigned to a tier for part of that period.")
                : pgEx.ConstraintName switch
                {
                    "uq_network_tier_single_oon" => ProblemResults.Conflict("DUPLICATE_OON_TIER",
                        "An Active out-of-network tier already exists; resolution must have exactly one to fall back to."),
                    "uq_network_tier_rank" => ProblemResults.Conflict("DUPLICATE_RANK",
                        "Another Active tier already holds that rank."),
                    _ => ProblemResults.Conflict("DUPLICATE_KEY", "A tier with this code already exists."),
                };
        }
    }
}
