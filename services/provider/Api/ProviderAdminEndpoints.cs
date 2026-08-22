using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Auth.Authorization;
using Mersal.Authz;
using Mersal.Events;
using Mersal.Provider.Domain;
using Mersal.Provider.Infrastructure;
using Mersal.Time;
using Microsoft.EntityFrameworkCore;
using ProviderEntity = Mersal.Provider.Domain.Provider;

namespace Mersal.Provider.Api;

/// <summary>
/// Phase 19.9 — administering the provider network (design 58).
///
/// ============================================================================================================
/// WHAT WAS MISSING
/// ============================================================================================================
/// Phase 2b built the provider domain as a creation pipeline: create a provider, add a location, add a
/// contract, add a line, activate. Every one of those verbs is a POST that appends, and there was no second
/// verb anywhere. A provider's legal name could not be corrected after the day it was typed. A location could
/// not be renamed, could not be moved, and — because exactly one primary is enforced by a partial-unique
/// index — the primary could never be changed at all: adding a second primary answered 409, and there was no
/// way to demote the first. A contract's dates could not be fixed. A priced line could not be repriced or
/// removed. A credential could not be renewed. A provider user could not be revoked individually, only by
/// suspending the whole provider. And an opened termination request could never be withdrawn: the table has
/// had a <c>withdrawn_at</c> column and a <c>Withdrawn</c> status since 0013, and nothing has ever written
/// either — so the only way out of a termination somebody opened by mistake was for a second person to
/// approve it, which terminates the provider.
///
/// ============================================================================================================
/// THE READINESS CHECKLIST
/// ============================================================================================================
/// Activation is guarded on four conditions and has always answered a blocked attempt with the FIRST one that
/// fails, as a sentence, after the operator pressed the button. <see cref="ReadinessView"/> returns all four
/// as facts, so the screen can show what is outstanding before anything is attempted. The guard is unchanged
/// and still the authority — this reads the same domain function rather than restating it.
///
/// ============================================================================================================
/// WHAT IS REFUSED, AND WHAT IS MERELY REPORTED (design 57's asymmetry)
/// ============================================================================================================
/// Deactivating the PRIMARY location is refused while the provider has another location to promote, because a
/// provider with no primary location fails its own activation gate and nothing would say so.
///
/// Terminating a provider's last in-effect contract is NOT refused — ending a contract is the operation, not a
/// side effect of one. But it is reported: the response says the provider is now Active in the directory and
/// routable for nothing, which is precisely the pair of disagreeing truths this platform keeps producing by
/// staying silent.
/// </summary>
public static class ProviderAdminEndpoints
{
    /// <summary>Ten characters, matching the policy portal and the SPA's own gate. A one-word reason is
    /// indistinguishable from no reason to whoever reads the record next year. Shared with
    /// <see cref="OnboardingEndpoints"/>, whose suspend and terminate checked only for blank until 19.9.</summary>
    internal const int MinReason = 10;

    public static void MapProviderAdmin(this WebApplication app)
    {
        var read = app.MapGroup("/api/v1").RequireAuthorization(HbmpPolicies.Scope("provider:read"));
        // Ordinary provider metadata: an address, a phone number, a staff account. A provider's OWN
        // administrator holds this scope and is confined by ABAC and RLS to their own row, which is right —
        // correcting their own address is their job.
        var write = app.MapGroup("/api/v1").RequireAuthorization(HbmpPolicies.Scope("provider:write"));
        // The COMMERCIAL half, and the reason 19.1b split this scope out in the first place: `provider:write`
        // covered "add a location" and "sign a contract" alike, and one of those is the price Mersal pays.
        //
        // `provider_admin` — the contracted hospital's own administrator — holds `provider:write` and does
        // NOT hold `provider:admin` (identity 0007). So without this split, every endpoint below would have
        // let a provider edit the dates of their own contract with Mersal, reprice their own tariff lines,
        // and decide that their own licence is Valid. RLS would have permitted every one of them: the rows
        // are theirs. The scope is the only thing that draws this line.
        var commercial = app.MapGroup("/api/v1").RequireAuthorization(HbmpPolicies.Scope("provider:admin"));

        MapProvider(write, commercial, read);
        MapLocations(write, read);
        MapContracts(commercial, read);
        MapCredentials(commercial, read);
        MapUsers(write, read);
    }

    // ════════════════════════════════════════════════════════════════════════════════════════════════════
    // Provider — detail, edit, history, withdrawing a termination
    // ════════════════════════════════════════════════════════════════════════════════════════════════════

    private static void MapProvider(RouteGroupBuilder write, RouteGroupBuilder commercial, RouteGroupBuilder read)
    {
        // --- The administration read: identity + readiness + what hangs off it ---------------------------
        read.MapGet("/providers/{id:guid}/administration", async (
            Guid id, ProviderDbContext db, ProviderAccessGuard guard, IHbmpPrincipalAccessor me,
            IBusinessCalendar calendar, CancellationToken ct) =>
        {
            var (p, _, deny) = await LoadGuarded(db, id, guard, me, ct);
            if (deny is not null) return deny;

            var today = calendar.Today();
            var readiness = Readiness(p!, today);
            var pending = await db.TerminationRequests.AsNoTracking()
                .FirstOrDefaultAsync(r => r.ProviderId == id && r.Status == TerminationRequestStatus.Requested, ct);
            var activeUsers = await db.Users.AsNoTracking()
                .CountAsync(u => u.ProviderId == id && u.Status == ProviderUserStatus.Active, ct);

            return Results.Ok(Detail(p!, readiness, pending, activeUsers, today, Grantable(me)));
        }).Produces<ProviderDetailView>();

        // --- Edit identity --------------------------------------------------------------------------------
        write.MapPut("/providers/{id:guid}", async (
            Guid id, UpdateProvider req, ProviderDbContext db, IAuditClient audit, IHbmpPrincipalAccessor me,
            TimeProvider clock, IBusinessCalendar calendar, CancellationToken ct) =>
        {
            var tenant = me.Principal?.TenantId;
            var p = await Load(db, id, tenant, ct);
            if (p is null) return ProblemResults.NotFound("PROVIDER_NOT_FOUND");

            // A terminated provider is a closed record. Editing one produces a tidier row and changes
            // nothing anybody can act on — and it would rewrite the name under contracts already settled.
            if (p.Status == ProviderStatus.Terminated)
                return ProblemResults.Conflict("PROVIDER_TERMINATED",
                    "This provider is terminated. Its record is kept as it stood; correcting it now would " +
                    "rewrite the name that appears on contracts and claims already settled against it.");

            // The code is checked rather than ignored. It is what a claim, a contract and an invoice cite.
            if (!string.Equals(req.ProviderCode?.Trim(), p.ProviderCode, StringComparison.Ordinal))
                return ProblemResults.Conflict("PROVIDER_CODE_IMMUTABLE",
                    $"The provider code cannot be changed. {p.ProviderCode} is cited by every contract, claim " +
                    "and invoice already raised against this provider; changing it here would leave those " +
                    "pointing at a code nothing answers to.");

            if (string.IsNullOrWhiteSpace(req.LegalName))
                return ProblemResults.Invalid("LEGAL_NAME_REQUIRED", "A legal name is required.");
            if (!Enum.TryParse<ProviderType>(req.ProviderType, out var type))
                return ProblemResults.Invalid("UNKNOWN_PROVIDER_TYPE", $"Unknown provider type '{req.ProviderType}'.");

            p.LegalName = req.LegalName.Trim();
            p.ProviderType = type;
            p.CommercialName = Trimmed(req.CommercialName);
            p.TaxId = Trimmed(req.TaxId);
            p.Phone = Trimmed(req.Phone);
            p.Email = Trimmed(req.Email);
            p.Notes = Trimmed(req.Notes);
            Stamp(p, me, clock);
            await db.SaveChangesAsync(ct);
            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "provider", EntityId = id.ToString(), Action = AuditAction.Update,
                ActorUserId = me.Principal?.Subject, TenantId = tenant, ProviderId = id.ToString(),
            }, ct);

            var today = calendar.Today();
            return Results.Ok(Detail(p, Readiness(p, today), null, 0, today, Grantable(me)));
        }).Produces<ProviderDetailView>();

        // --- The provider's own change timeline ------------------------------------------------------------
        //
        // Offered to READERS, not only to writers. "Who changed this, and why" is a question a claims officer
        // disputing a tariff has every reason to ask, and the projection carries no field the caller could not
        // already read on the live record.
        read.MapGet("/providers/{id:guid}/history", async (
            Guid id, ProviderDbContext db, ProviderAccessGuard guard, IHbmpPrincipalAccessor me, CancellationToken ct) =>
        {
            var (_, _, deny) = await LoadGuarded(db, id, guard, me, ct);
            if (deny is not null) return deny;

            var rows = await db.ProviderHistory.AsNoTracking()
                .Where(h => h.ProviderId == id)
                .OrderByDescending(h => h.HistoryId).Take(200).ToListAsync(ct);
            return Results.Ok(new
            {
                entries = rows.Select(h => Snapshot.Project(h.HistoryId, h.Operation, h.ChangedAt, h.RowSnapshot,
                    "legal_name", "commercial_name", "provider_type", "status", "onboarding_state",
                    "tax_id", "phone", "email")),
            });
        });

        // --- Withdraw an open termination request ---------------------------------------------------------
        //
        // 0013 gave the table a `withdrawn_at` column and a `Withdrawn` status and nothing has ever written
        // either. Until now the only exit from a termination somebody opened by mistake was for a second
        // person to approve it — which terminates the provider. A control with no cancel is not a control.
        //
        // The withdrawal is NOT restricted to the requester. Dual control exists so that no ONE person can
        // terminate a provider; it does not exist to stop a colleague closing a request that should not have
        // been opened. Withdrawing is the safe direction, and both subjects are recorded.
        commercial.MapPost("/providers/{id:guid}/terminate/withdraw", async (
            Guid id, DeactivateWithReason req, ProviderDbContext db, IAuditClient audit,
            IHbmpPrincipalAccessor me, TimeProvider clock, CancellationToken ct) =>
        {
            if (ReasonTooShort(req?.Reason)) return ShortReason();
            var tenant = me.Principal?.TenantId;
            var p = await Load(db, id, tenant, ct);
            if (p is null) return ProblemResults.NotFound("PROVIDER_NOT_FOUND");

            var pending = await db.TerminationRequests
                .FirstOrDefaultAsync(r => r.ProviderId == id && r.Status == TerminationRequestStatus.Requested, ct);
            if (pending is null)
                return ProblemResults.Conflict("NO_OPEN_TERMINATION",
                    "There is no open termination request for this provider.");

            pending.Status = TerminationRequestStatus.Withdrawn;
            pending.WithdrawnAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "provider", EntityId = id.ToString(), Action = AuditAction.StateChange,
                ActorUserId = me.Principal?.Subject, TenantId = tenant, ProviderId = id.ToString(),
                DecisionOutcome = "TerminationWithdrawn",
                DecisionReasonCode = $"{req!.Reason.Trim()} (requested by: {pending.RequestedBy})",
            }, ct);
            return Results.Ok(new { pending.RequestId, status = pending.Status.ToString() });
        });
    }

    // ════════════════════════════════════════════════════════════════════════════════════════════════════
    // Locations
    // ════════════════════════════════════════════════════════════════════════════════════════════════════

    private static void MapLocations(RouteGroupBuilder write, RouteGroupBuilder read)
    {
        // The existing GET /providers/{id}/locations returns only LIVE rows and is what the pickers use.
        // This one is the administrative read: it includes deactivated locations, because "we used to be in
        // Alexandria and closed it in March" is the answer to half the questions asked of this screen.
        read.MapGet("/providers/{id:guid}/locations/all", async (
            Guid id, ProviderDbContext db, ProviderAccessGuard guard, IHbmpPrincipalAccessor me, CancellationToken ct) =>
        {
            var (_, _, deny) = await LoadGuarded(db, id, guard, me, ct);
            if (deny is not null) return deny;
            var rows = await db.Locations.AsNoTracking().Where(l => l.ProviderId == id)
                .OrderByDescending(l => l.IsPrimary).ThenBy(l => l.Name).ToListAsync(ct);
            return Results.Ok(rows.Select(l => new LocationView(
                l.LocationId, l.Name, l.Governorate, l.Address, l.GeoLat, l.GeoLng, l.IsPrimary, l.IsDeleted,
                l.DeactivationReason, l.DeactivatedAt)));
        }).Produces<IEnumerable<LocationView>>();

        write.MapPut("/providers/{id:guid}/locations/{locationId:guid}", async (
            Guid id, Guid locationId, UpdateLocation req, ProviderDbContext db, IAuditClient audit,
            IHbmpPrincipalAccessor me, TimeProvider clock, CancellationToken ct) =>
        {
            var tenant = me.Principal?.TenantId;
            var loc = await db.Locations.FirstOrDefaultAsync(
                l => l.LocationId == locationId && l.ProviderId == id && l.TenantId == tenant, ct);
            if (loc is null) return ProblemResults.NotFound("LOCATION_NOT_FOUND");
            if (loc.IsDeleted)
                return ProblemResults.Conflict("LOCATION_DEACTIVATED",
                    "This location is deactivated. Its record is kept as it stood on the day it closed.");
            if (string.IsNullOrWhiteSpace(req.Name))
                return ProblemResults.Invalid("NAME_REQUIRED", "A location name is required.");

            loc.Name = req.Name.Trim();
            loc.Governorate = Trimmed(req.Governorate);
            loc.Address = Trimmed(req.Address);
            loc.GeoLat = req.GeoLat;
            loc.GeoLng = req.GeoLng;
            loc.UpdatedBy = me.Principal?.Subject;
            loc.UpdatedByName = me.Principal?.DisplayName;
            loc.UpdatedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
            await audit.EmitAsync(Draft("provider_location", locationId, AuditAction.Update, me, tenant, id), ct);
            return Results.Ok(ToView(loc));
        }).Produces<LocationView>();

        // --- Make this the primary location ---------------------------------------------------------------
        //
        // Exactly one primary per provider is a partial-unique index (0001), so promoting a second one has
        // always answered 409 and there was no demote. The two writes are therefore ONE transaction and the
        // demote goes first: the reverse order violates the index, and a crash between two separate commits
        // leaves the provider with no primary at all — which silently fails its own activation gate.
        write.MapPost("/providers/{id:guid}/locations/{locationId:guid}/primary", async (
            Guid id, Guid locationId, ProviderDbContext db, IAuditClient audit, IHbmpPrincipalAccessor me,
            TimeProvider clock, CancellationToken ct) =>
        {
            var tenant = me.Principal?.TenantId;
            var loc = await db.Locations.FirstOrDefaultAsync(
                l => l.LocationId == locationId && l.ProviderId == id && l.TenantId == tenant, ct);
            if (loc is null) return ProblemResults.NotFound("LOCATION_NOT_FOUND");
            if (loc.IsDeleted)
                return ProblemResults.Conflict("LOCATION_DEACTIVATED",
                    "A deactivated location cannot be made primary. Reactivate it first, or promote another.");
            if (loc.IsPrimary) return Results.Ok(ToView(loc));

            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var current = await db.Locations
                .Where(l => l.ProviderId == id && l.IsPrimary && !l.IsDeleted).ToListAsync(ct);
            foreach (var c in current)
            {
                c.IsPrimary = false;
                c.UpdatedBy = me.Principal?.Subject;
                c.UpdatedByName = me.Principal?.DisplayName;
                c.UpdatedAt = clock.GetUtcNow();
            }
            await db.SaveChangesAsync(ct);
            loc.IsPrimary = true;
            loc.UpdatedBy = me.Principal?.Subject;
            loc.UpdatedByName = me.Principal?.DisplayName;
            loc.UpdatedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
            await audit.EmitAsync(Draft("provider_location", locationId, AuditAction.StateChange, me, tenant, id,
                outcome: "Primary"), ct);
            await tx.CommitAsync(ct);
            return Results.Ok(ToView(loc));
        }).Produces<LocationView>();

        write.MapPost("/providers/{id:guid}/locations/{locationId:guid}/deactivate", async (
            Guid id, Guid locationId, DeactivateWithReason req, ProviderDbContext db, IAuditClient audit,
            IHbmpPrincipalAccessor me, TimeProvider clock, CancellationToken ct) =>
        {
            if (ReasonTooShort(req?.Reason)) return ShortReason();
            var tenant = me.Principal?.TenantId;
            var loc = await db.Locations.FirstOrDefaultAsync(
                l => l.LocationId == locationId && l.ProviderId == id && l.TenantId == tenant, ct);
            if (loc is null) return ProblemResults.NotFound("LOCATION_NOT_FOUND");
            if (loc.IsDeleted) return ProblemResults.Conflict("LOCATION_DEACTIVATED", "Already deactivated.");

            // Refused, not warned. Activation requires a primary location, so a provider left without one
            // fails its own gate — and would keep answering "Active" in the directory until somebody tried
            // to reactivate it months later and could not work out why.
            if (loc.IsPrimary)
            {
                var others = await db.Locations.CountAsync(
                    l => l.ProviderId == id && !l.IsDeleted && l.LocationId != locationId, ct);
                return ProblemResults.Conflict("LOCATION_IS_PRIMARY", others > 0
                    ? "This is the provider's primary location. Make another location primary first — a " +
                      "provider with no primary location fails its own activation check."
                    : "This is the provider's only location, and a provider with no primary location cannot " +
                      "be activated. Add the replacement location first, or suspend the provider instead.");
            }

            loc.IsDeleted = true;
            loc.DeactivatedAt = clock.GetUtcNow();
            loc.DeactivatedBy = me.Principal?.Subject;
            loc.DeactivationReason = req!.Reason.Trim();
            loc.UpdatedBy = me.Principal?.Subject;
            loc.UpdatedByName = me.Principal?.DisplayName;
            loc.UpdatedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
            await audit.EmitAsync(Draft("provider_location", locationId, AuditAction.StateChange, me, tenant, id,
                outcome: "Deactivated", reason: req.Reason.Trim()), ct);
            return Results.Ok(ToView(loc));
        }).Produces<LocationView>();

        write.MapPost("/providers/{id:guid}/locations/{locationId:guid}/reactivate", async (
            Guid id, Guid locationId, DeactivateWithReason req, ProviderDbContext db, IAuditClient audit,
            IHbmpPrincipalAccessor me, TimeProvider clock, CancellationToken ct) =>
        {
            if (ReasonTooShort(req?.Reason)) return ShortReason();
            var tenant = me.Principal?.TenantId;
            var loc = await db.Locations.FirstOrDefaultAsync(
                l => l.LocationId == locationId && l.ProviderId == id && l.TenantId == tenant, ct);
            if (loc is null) return ProblemResults.NotFound("LOCATION_NOT_FOUND");
            if (!loc.IsDeleted) return ProblemResults.Conflict("LOCATION_ACTIVE", "This location is already active.");

            loc.IsDeleted = false;
            loc.DeactivatedAt = null;
            loc.DeactivatedBy = null;
            loc.DeactivationReason = null;
            // Reopening never restores the primary flag: the partial-unique index would reject it if another
            // location has since become primary, and silently reinstating it would move the provider's
            // official address as a side effect of an unrelated action.
            loc.IsPrimary = false;
            loc.UpdatedBy = me.Principal?.Subject;
            loc.UpdatedByName = me.Principal?.DisplayName;
            loc.UpdatedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
            await audit.EmitAsync(Draft("provider_location", locationId, AuditAction.StateChange, me, tenant, id,
                outcome: "Reactivated", reason: req!.Reason.Trim()), ct);
            return Results.Ok(ToView(loc));
        }).Produces<LocationView>();

        read.MapGet("/providers/{id:guid}/locations/{locationId:guid}/history", async (
            Guid id, Guid locationId, ProviderDbContext db, ProviderAccessGuard guard,
            IHbmpPrincipalAccessor me, CancellationToken ct) =>
        {
            var (_, _, deny) = await LoadGuarded(db, id, guard, me, ct);
            if (deny is not null) return deny;
            var rows = await db.LocationHistory.AsNoTracking()
                .Where(h => h.LocationId == locationId)
                .OrderByDescending(h => h.HistoryId).Take(200).ToListAsync(ct);
            return Results.Ok(new
            {
                entries = rows.Select(h => Snapshot.Project(h.HistoryId, h.Operation, h.RecordedAt, h.RowSnapshot,
                    "name", "governorate", "address", "is_primary", "is_deleted")),
            });
        });
    }

    // ════════════════════════════════════════════════════════════════════════════════════════════════════
    // Contracts and their priced lines
    // ════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary><paramref name="write"/> here is the COMMERCIAL group (`provider:admin`): every route in this
    /// section changes what Mersal pays or what a claim is priced at.</summary>
    private static void MapContracts(RouteGroupBuilder write, RouteGroupBuilder read)
    {
        write.MapPut("/contracts/{contractId:guid}", async (
            Guid contractId, UpdateContract req, ProviderDbContext db, IAuditClient audit,
            IHbmpPrincipalAccessor me, TimeProvider clock, IBusinessCalendar calendar, CancellationToken ct) =>
        {
            var tenant = me.Principal?.TenantId;
            var c = await db.Contracts.FirstOrDefaultAsync(
                x => x.ContractId == contractId && x.TenantId == tenant && !x.IsDeleted, ct);
            if (c is null) return ProblemResults.NotFound("CONTRACT_NOT_FOUND");
            if (c.Status is ContractStatus.Terminated)
                return ProblemResults.Conflict("CONTRACT_TERMINATED",
                    "This contract is terminated. Its terms are kept as they stood; raise a new contract instead.");

            var today = calendar.Today();

            // A DRAFT contract has priced nothing, so everything about it is still an edit. Once it is Active
            // the window it priced under is history, and rewriting history is how a claim adjudicated in March
            // ends up sitting outside the contract that paid it.
            if (c.Status != ContractStatus.Draft)
            {
                if (!string.Equals(req.ContractNo?.Trim(), c.ContractNo, StringComparison.Ordinal))
                    return ProblemResults.Conflict("CONTRACT_NO_IMMUTABLE",
                        $"Contract number {c.ContractNo} is cited by every claim already priced under it and " +
                        "cannot be changed once the contract is in force.");
                if (req.EffectiveFrom != c.EffectiveFrom)
                    return ProblemResults.Conflict("CONTRACT_START_IMMUTABLE",
                        "The start date of a contract already in force cannot be moved. Claims have been " +
                        "priced against this window; changing where it begins would put them outside it.");
                if (req.EffectiveTo is { } to && to < today)
                    return ProblemResults.Conflict("CONTRACT_END_IN_PAST",
                        "A contract already in force cannot be end-dated in the past — every claim priced " +
                        "since that date would fall outside it. Terminate the contract instead, which ends " +
                        "it from today and says why.");
            }

            if (string.IsNullOrWhiteSpace(req.ContractNo))
                return ProblemResults.Invalid("CONTRACT_NO_REQUIRED", "A contract number is required.");
            if (req.EffectiveTo is { } end && end < req.EffectiveFrom)
                return ProblemResults.Invalid("CONTRACT_WINDOW_BACKWARDS",
                    "A contract cannot end before it begins.");

            var siblings = await db.Contracts.AsNoTracking()
                .Where(x => x.ProviderId == c.ProviderId && !x.IsDeleted && x.Status != ContractStatus.Terminated)
                .ToListAsync(ct);
            if (ContractRules.OverlapsAny(siblings, req.EffectiveFrom, req.EffectiveTo, excludeContractId: contractId))
                return ProblemResults.Conflict("CONTRACT_OVERLAP",
                    "This window overlaps another contract with the same provider. Two contracts in force on " +
                    "the same day means two prices for the same service and nothing to choose between them.");

            c.ContractNo = req.ContractNo.Trim();
            c.EffectiveFrom = req.EffectiveFrom;
            c.EffectiveTo = req.EffectiveTo;
            c.UpdatedBy = me.Principal?.Subject;
            c.UpdatedByName = me.Principal?.DisplayName;
            c.UpdatedAt = clock.GetUtcNow();
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException)
            {
                return ProblemResults.Conflict("CONTRACT_CONFLICT",
                    "That contract number is already used, or the window overlaps another contract.");
            }
            await audit.EmitAsync(Draft("provider_contract", contractId, AuditAction.Update, me, tenant, c.ProviderId), ct);
            return Results.Ok(ToView(c, await LineCount(db, contractId, ct), today));
        }).Produces<ContractView>();

        // --- Terminate a contract -------------------------------------------------------------------------
        //
        // NOT refused when it is the last one in effect. Ending a contract IS the operation; refusing it would
        // leave an operator whose counterparty has walked away with no way to record that. But the
        // consequence is REPORTED, because it is the pair of truths this platform keeps letting disagree: the
        // directory goes on saying Active while capability derivation returns nothing, so the provider is
        // visible, selectable, and routable for not one service.
        write.MapPost("/contracts/{contractId:guid}/terminate", async (
            Guid contractId, DeactivateWithReason req, ProviderDbContext db, IAuditClient audit, IOutbox outbox,
            IHbmpPrincipalAccessor me, TimeProvider clock, IBusinessCalendar calendar, CancellationToken ct) =>
        {
            if (ReasonTooShort(req?.Reason)) return ShortReason();
            var tenant = me.Principal?.TenantId;
            var c = await db.Contracts.FirstOrDefaultAsync(
                x => x.ContractId == contractId && x.TenantId == tenant && !x.IsDeleted, ct);
            if (c is null) return ProblemResults.NotFound("CONTRACT_NOT_FOUND");
            if (c.Status == ContractStatus.Terminated)
                return ProblemResults.Conflict("CONTRACT_TERMINATED", "This contract is already terminated.");

            var today = calendar.Today();
            var provider = await db.Providers.Include(p => p.Contracts).ThenInclude(x => x.ServiceLines)
                .FirstOrDefaultAsync(p => p.ProviderId == c.ProviderId && p.TenantId == tenant, ct);

            await using var tx = await db.Database.BeginTransactionAsync(ct);
            c.Status = ContractStatus.Terminated;
            c.StatusReason = req!.Reason.Trim();
            c.StatusActor = me.Principal?.Subject;
            c.StatusActorName = me.Principal?.DisplayName;
            c.StatusChangedAt = clock.GetUtcNow();
            c.UpdatedBy = me.Principal?.Subject;
            c.UpdatedByName = me.Principal?.DisplayName;
            c.UpdatedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);

            // Recomputed AFTER the write, from the same rows the router will read.
            var stillRoutable = provider is not null
                && provider.Contracts.Any(x => x.ContractId != contractId && ContractRules.InEffect(x, today));
            var unroutable = provider is { Status: ProviderStatus.Active } && !stillRoutable;

            await audit.EmitAsync(Draft("provider_contract", contractId, AuditAction.StateChange, me, tenant,
                c.ProviderId, outcome: "Terminated", reason: req.Reason.Trim()), ct);
            await outbox.EnqueueAsync("ContractTerminated", "provider.events",
                new { contractId = c.ContractId, providerId = c.ProviderId, c.ContractNo, tenantId = tenant }, ct);
            await tx.CommitAsync(ct);

            return Results.Ok(new
            {
                c.ContractId,
                status = c.Status.ToString(),
                // The fact, named. The SPA renders it as a sentence; nothing has to infer it.
                providerBecomesUnroutable = unroutable,
                providerStatus = provider?.Status.ToString(),
            });
        });

        read.MapGet("/contracts/{contractId:guid}/service-lines", async (
            Guid contractId, ProviderDbContext db, IHbmpPrincipalAccessor me, CancellationToken ct) =>
        {
            var tenant = me.Principal?.TenantId;
            var c = await db.Contracts.AsNoTracking().FirstOrDefaultAsync(
                x => x.ContractId == contractId && x.TenantId == tenant && !x.IsDeleted, ct);
            if (c is null) return ProblemResults.NotFound("CONTRACT_NOT_FOUND");
            // The whole PRICE is withheld from a caller without provider:finance — not zeroed. A zero reads
            // as "free", which is a different and much worse claim than "you are not being shown this".
            var canSeePrice = me.Principal?.HasScope("provider:finance") ?? false;
            var rows = await db.ServiceLines.AsNoTracking()
                .Where(l => l.ContractId == contractId).OrderBy(l => l.Code).ToListAsync(ct);
            return Results.Ok(rows.Select(l => new ServiceLineView(
                l.ServiceLineId, l.ServiceType.ToString(), l.CodeSystem.ToString(), l.Code,
                canSeePrice ? l.AgreedPrice : null, canSeePrice ? l.CurrencyCode : null)));
        }).Produces<IEnumerable<ServiceLineView>>();

        // --- Reprice a line -------------------------------------------------------------------------------
        //
        // DRAFT ONLY, and this is the one place the rule is strict. Adding a code to a live contract is
        // additive — no claim already adjudicated can be affected by a service that was not on the list.
        // REPRICING one is not: it changes what a claim submitted yesterday and adjudicated tomorrow is worth,
        // with nothing recording that the number moved. A tariff in force is superseded by a new contract, not
        // edited in place.
        write.MapPut("/contracts/{contractId:guid}/service-lines/{lineId:guid}", async (
            Guid contractId, Guid lineId, UpdateServiceLine req, ProviderDbContext db, IAuditClient audit,
            IHbmpPrincipalAccessor me, CancellationToken ct) =>
        {
            var tenant = me.Principal?.TenantId;
            var c = await db.Contracts.FirstOrDefaultAsync(
                x => x.ContractId == contractId && x.TenantId == tenant && !x.IsDeleted, ct);
            if (c is null) return ProblemResults.NotFound("CONTRACT_NOT_FOUND");
            if (c.Status != ContractStatus.Draft) return TariffInForce(c);
            if (req.AgreedPrice < 0)
                return ProblemResults.Invalid("PRICE_NEGATIVE", "An agreed price cannot be negative.");

            var line = await db.ServiceLines.FirstOrDefaultAsync(
                l => l.ServiceLineId == lineId && l.ContractId == contractId, ct);
            if (line is null) return ProblemResults.NotFound("SERVICE_LINE_NOT_FOUND");

            line.AgreedPrice = req.AgreedPrice;
            line.CurrencyCode = string.IsNullOrWhiteSpace(req.CurrencyCode) ? line.CurrencyCode : req.CurrencyCode.Trim().ToUpperInvariant();
            await db.SaveChangesAsync(ct);
            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "contract_service_line", EntityId = lineId.ToString(), Action = AuditAction.Update,
                ActorUserId = me.Principal?.Subject, TenantId = tenant, ProviderId = c.ProviderId.ToString(),
                FieldClasses = ["financials"],
            }, ct);
            return Results.Ok(new ServiceLineView(line.ServiceLineId, line.ServiceType.ToString(),
                line.CodeSystem.ToString(), line.Code, line.AgreedPrice, line.CurrencyCode));
        }).Produces<ServiceLineView>();

        write.MapDelete("/contracts/{contractId:guid}/service-lines/{lineId:guid}", async (
            Guid contractId, Guid lineId, ProviderDbContext db, IAuditClient audit,
            IHbmpPrincipalAccessor me, CancellationToken ct) =>
        {
            var tenant = me.Principal?.TenantId;
            var c = await db.Contracts.FirstOrDefaultAsync(
                x => x.ContractId == contractId && x.TenantId == tenant && !x.IsDeleted, ct);
            if (c is null) return ProblemResults.NotFound("CONTRACT_NOT_FOUND");
            if (c.Status != ContractStatus.Draft) return TariffInForce(c);

            var line = await db.ServiceLines.FirstOrDefaultAsync(
                l => l.ServiceLineId == lineId && l.ContractId == contractId, ct);
            if (line is null) return ProblemResults.NotFound("SERVICE_LINE_NOT_FOUND");

            db.ServiceLines.Remove(line);
            await db.SaveChangesAsync(ct);
            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "contract_service_line", EntityId = lineId.ToString(), Action = AuditAction.StateChange,
                ActorUserId = me.Principal?.Subject, TenantId = tenant, ProviderId = c.ProviderId.ToString(),
                DecisionOutcome = "LineRemoved", FieldClasses = ["financials"],
            }, ct);
            return Results.NoContent();
        });

        read.MapGet("/contracts/{contractId:guid}/history", async (
            Guid contractId, ProviderDbContext db, IHbmpPrincipalAccessor me, CancellationToken ct) =>
        {
            var tenant = me.Principal?.TenantId;
            var c = await db.Contracts.AsNoTracking().FirstOrDefaultAsync(
                x => x.ContractId == contractId && x.TenantId == tenant, ct);
            if (c is null) return ProblemResults.NotFound("CONTRACT_NOT_FOUND");
            var rows = await db.ContractHistory.AsNoTracking()
                .Where(h => h.ContractId == contractId)
                .OrderByDescending(h => h.HistoryId).Take(200).ToListAsync(ct);
            return Results.Ok(new
            {
                entries = rows.Select(h => Snapshot.Project(h.HistoryId, h.Operation, h.RecordedAt, h.RowSnapshot,
                    "contract_no", "status", "effective_from", "effective_to")),
            });
        });
    }

    // ════════════════════════════════════════════════════════════════════════════════════════════════════
    // Credentials — the documents the activation gate is actually about
    // ════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary><paramref name="write"/> here is the COMMERCIAL group (`provider:admin`): deciding that a
    /// licence is valid is Mersal's credentialing decision, not the licence holder's.</summary>
    private static void MapCredentials(RouteGroupBuilder write, RouteGroupBuilder read)
    {
        read.MapGet("/providers/{id:guid}/credentials", async (
            Guid id, ProviderDbContext db, ProviderAccessGuard guard, IHbmpPrincipalAccessor me,
            IBusinessCalendar calendar, CancellationToken ct) =>
        {
            var (_, _, deny) = await LoadGuarded(db, id, guard, me, ct);
            if (deny is not null) return deny;
            var today = calendar.Today();
            var rows = await db.Credentials.AsNoTracking().Where(c => c.ProviderId == id)
                .OrderByDescending(c => c.IsMandatory).ThenBy(c => c.CredentialType).ToListAsync(ct);
            return Results.Ok(rows.Select(c => new CredentialView(
                c.CredentialId, c.CredentialType, c.Status.ToString(), c.ValidFrom, c.ValidTo, c.DocumentId,
                c.IsMandatory, c.IsDeleted, CredentialRules.IsValidOn(c, today),
                c.ValidTo is { } to ? to.DayNumber - today.DayNumber : null)));
        }).Produces<IEnumerable<CredentialView>>();

        write.MapPut("/providers/{id:guid}/credentials/{credentialId:guid}", async (
            Guid id, Guid credentialId, UpdateCredential req, ProviderDbContext db, IAuditClient audit,
            IHbmpPrincipalAccessor me, CancellationToken ct) =>
        {
            var tenant = me.Principal?.TenantId;
            var cred = await db.Credentials.FirstOrDefaultAsync(
                c => c.CredentialId == credentialId && c.ProviderId == id && c.TenantId == tenant, ct);
            if (cred is null) return ProblemResults.NotFound("CREDENTIAL_NOT_FOUND");
            if (cred.IsDeleted) return ProblemResults.Conflict("CREDENTIAL_WITHDRAWN", "This credential is withdrawn.");
            if (string.IsNullOrWhiteSpace(req.CredentialType))
                return ProblemResults.Invalid("CREDENTIAL_TYPE_REQUIRED", "A credential type is required.");
            if (!Enum.TryParse<CredentialStatus>(req.Status, out var status))
                return ProblemResults.Invalid("UNKNOWN_CREDENTIAL_STATUS", $"Unknown status '{req.Status}'.");
            if (req.ValidTo is { } to && req.ValidFrom is { } from && to < from)
                return ProblemResults.Invalid("CREDENTIAL_WINDOW_BACKWARDS",
                    "A credential cannot expire before it becomes valid.");

            // A credential marked Valid with no document behind it is exactly what makes the activation gate
            // ceremonial: the check passes, nobody has seen a licence, and the provider goes live.
            if (status == CredentialStatus.Valid && req.DocumentId is null && cred.DocumentId is null)
                return ProblemResults.Unprocessable("CREDENTIAL_NEEDS_DOCUMENT",
                    "A credential cannot be marked valid without the document it certifies. Activation is " +
                    "gated on these being valid — marking one so with nothing attached passes the check " +
                    "without anybody having seen a licence.");
            // Expiry is what makes a credential enforceable as at a date. Without one, "valid" means valid
            // forever, which is not a thing a licence is.
            if (status == CredentialStatus.Valid && req.ValidTo is null)
                return ProblemResults.Unprocessable("CREDENTIAL_NEEDS_EXPIRY",
                    "A valid credential needs an expiry date. Without one it can never lapse, and the " +
                    "expiry sweep that warns you 30 days out has nothing to work from.");

            cred.CredentialType = req.CredentialType.Trim();
            cred.Status = status;
            cred.ValidFrom = req.ValidFrom;
            cred.ValidTo = req.ValidTo;
            cred.DocumentId = req.DocumentId ?? cred.DocumentId;
            cred.IsMandatory = req.IsMandatory;
            await db.SaveChangesAsync(ct);
            await audit.EmitAsync(Draft("provider_credential", credentialId, AuditAction.Update, me, tenant, id), ct);
            return Results.Ok(new { cred.CredentialId, status = cred.Status.ToString() });
        });

        write.MapPost("/providers/{id:guid}/credentials/{credentialId:guid}/withdraw", async (
            Guid id, Guid credentialId, DeactivateWithReason req, ProviderDbContext db, IAuditClient audit,
            IHbmpPrincipalAccessor me, IBusinessCalendar calendar, CancellationToken ct) =>
        {
            if (ReasonTooShort(req?.Reason)) return ShortReason();
            var tenant = me.Principal?.TenantId;
            var cred = await db.Credentials.FirstOrDefaultAsync(
                c => c.CredentialId == credentialId && c.ProviderId == id && c.TenantId == tenant, ct);
            if (cred is null) return ProblemResults.NotFound("CREDENTIAL_NOT_FOUND");
            if (cred.IsDeleted) return ProblemResults.Conflict("CREDENTIAL_WITHDRAWN", "Already withdrawn.");

            cred.IsDeleted = true;
            await db.SaveChangesAsync(ct);
            await audit.EmitAsync(Draft("provider_credential", credentialId, AuditAction.StateChange, me, tenant, id,
                outcome: "Withdrawn", reason: req!.Reason.Trim()), ct);

            // Withdrawing a MANDATORY credential can take the provider below its own activation bar while it
            // is live. The status is not changed here — that is a decision with its own dual control and its
            // own reason — but the caller is told, because otherwise the next person to notice is whoever
            // tries to reactivate it in six months.
            var p = await db.Providers.Include(x => x.Credentials).Include(x => x.Contracts).Include(x => x.Locations)
                .FirstOrDefaultAsync(x => x.ProviderId == id && x.TenantId == tenant, ct);
            var readiness = p is null ? null : Readiness(p, calendar.Today());
            return Results.Ok(new
            {
                cred.CredentialId,
                withdrawn = true,
                providerNoLongerMeetsActivationBar = p is { Status: ProviderStatus.Active } && readiness is { CanActivate: false },
                readiness,
            });
        });
    }

    // ════════════════════════════════════════════════════════════════════════════════════════════════════
    // Provider users
    // ════════════════════════════════════════════════════════════════════════════════════════════════════

    private static void MapUsers(RouteGroupBuilder write, RouteGroupBuilder read)
    {
        read.MapGet("/providers/{id:guid}/users", async (
            Guid id, ProviderDbContext db, ProviderAccessGuard guard, IHbmpPrincipalAccessor me, CancellationToken ct) =>
        {
            var (_, _, deny) = await LoadGuarded(db, id, guard, me, ct);
            if (deny is not null) return deny;
            var rows = await db.Users.AsNoTracking().Where(u => u.ProviderId == id)
                .OrderBy(u => u.Status).ThenBy(u => u.SubjectRef).ToListAsync(ct);
            return Results.Ok(rows.Select(u => new ProviderUserView(
                u.UserId, u.SubjectRef, u.Role, u.Status.ToString(), u.CreatedAt, u.RevokedAt)));
        }).Produces<IEnumerable<ProviderUserView>>();

        // Revoking ONE user. Until now the only way to take an account away was to suspend the whole
        // provider, which revokes every account they have and stops routing to them — an outsized answer to
        // "this person left".
        write.MapPost("/providers/{id:guid}/users/{userId:guid}/revoke", async (
            Guid id, Guid userId, DeactivateWithReason req, ProviderDbContext db, IAuditClient audit,
            IOutbox outbox, IHbmpPrincipalAccessor me, TimeProvider clock, CancellationToken ct) =>
        {
            if (ReasonTooShort(req?.Reason)) return ShortReason();
            var tenant = me.Principal?.TenantId;
            var user = await db.Users.FirstOrDefaultAsync(
                u => u.UserId == userId && u.ProviderId == id && u.TenantId == tenant, ct);
            if (user is null) return ProblemResults.NotFound("PROVIDER_USER_NOT_FOUND");
            if (user.Status == ProviderUserStatus.Revoked)
                return ProblemResults.Conflict("USER_REVOKED", "This account is already revoked.");

            // 24.3 — the revocation and the event identity acts on commit together. A user revoked here but
            // still able to sign in is the failure this transaction exists to prevent.
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            user.Status = ProviderUserStatus.Revoked;
            user.RevokedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
            await audit.EmitAsync(new AuditEventDraft
            {
                // `Grant` is the platform's action for the provisioning of access; a revocation is the same
                // action in the other direction, which is how the provisioning endpoint already records it.
                EntityType = "provider_user", EntityId = userId.ToString(), Action = AuditAction.Grant,
                ActorUserId = me.Principal?.Subject, TenantId = tenant, ProviderId = id.ToString(),
                DecisionOutcome = "Revoked", DecisionReasonCode = req!.Reason.Trim(),
            }, ct);
            await outbox.EnqueueAsync("ProviderUsersRevoked", "provider.events",
                new { providerId = id, count = 1, userId, reason = req.Reason.Trim(), tenantId = tenant }, ct);
            await tx.CommitAsync(ct);
            return Results.Ok(new { user.UserId, status = user.Status.ToString() });
        });
    }

    // ════════════════════════════════════════════════════════════════════════════════════════════════════
    // Shared
    // ════════════════════════════════════════════════════════════════════════════════════════════════════

    private static IResult TariffInForce(ProviderContract c) => ProblemResults.Conflict("CONTRACT_NOT_DRAFT",
        $"Contract {c.ContractNo} is {c.Status} — its prices are what claims are being settled at. A tariff " +
        "in force is superseded by a new contract, not edited in place: changing a number here would change " +
        "what a claim submitted yesterday is worth, with nothing recording that it moved. New CODES may still " +
        "be added, because a service that was not on the list cannot have been priced under it.");

    private static bool ReasonTooShort(string? reason) =>
        string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < MinReason;

    private static IResult ShortReason() => ProblemResults.Invalid("REASON_REQUIRED",
        $"Say why, in a sentence of at least {MinReason} characters. It is stored on the record and read by " +
        "whoever has to understand this decision next year.");

    private static string? Trimmed(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static Task<ProviderEntity?> Load(ProviderDbContext db, Guid id, string? tenant, CancellationToken ct) =>
        db.Providers.Include(x => x.Locations).Include(x => x.Contracts).Include(x => x.Credentials)
            .FirstOrDefaultAsync(x => x.ProviderId == id && x.TenantId == tenant && !x.IsDeleted, ct);

    /// <summary>Load + the ABAC provider-ownership check every read here shares. A provider user reading
    /// another provider is denied and audited by the guard itself.</summary>
    private static async Task<(ProviderEntity? provider, string? tenant, IResult? deny)> LoadGuarded(
        ProviderDbContext db, Guid id, ProviderAccessGuard guard, IHbmpPrincipalAccessor me, CancellationToken ct)
    {
        var tenant = me.Principal?.TenantId;
        var p = await Load(db, id, tenant, ct);
        if (p is null) return (null, tenant, ProblemResults.NotFound("PROVIDER_NOT_FOUND"));
        var decision = await guard.AuthorizeAsync(me.Require(), p.TenantId, p.ProviderId.ToString(), ct);
        if (!decision.IsAllowed)
            return (null, tenant, Results.Problem(statusCode: 403, title: "provider access denied", detail: decision.ReasonCode));
        return (p, tenant, null);
    }

    private static void Stamp(ProviderEntity p, IHbmpPrincipalAccessor me, TimeProvider clock)
    {
        p.UpdatedBy = me.Principal?.Subject;
        p.UpdatedByName = me.Principal?.DisplayName;
        p.UpdatedAt = clock.GetUtcNow();
    }

    /// <summary>The four facts the activation guard evaluates, plus the guard's own verdict. The guard is
    /// still the authority — this calls it rather than restating what it checks.</summary>
    private static ReadinessView Readiness(ProviderEntity p, DateOnly on)
    {
        var r = new OnboardingWorkflow.Readiness(
            HasPrimaryLocation: p.Locations.Any(l => l.IsPrimary && !l.IsDeleted),
            HasMandatoryCredentials: p.Credentials.Any(c => c.IsMandatory && !c.IsDeleted),
            MandatoryCredentialsValid: CredentialRules.MandatoryCredentialsSatisfied(p.Credentials, on),
            HasActiveContract: p.Contracts.Any(c => ContractRules.InEffect(c, on)));
        var guard = OnboardingWorkflow.GuardActivation(r);
        return new ReadinessView(r.HasPrimaryLocation, r.HasMandatoryCredentials, r.MandatoryCredentialsValid,
            r.HasActiveContract, guard.Allowed, guard.Reason);
    }

    /// <summary>The provider-scoped roles this caller may grant, asked of the SoD rule itself rather than
    /// listed again here.</summary>
    private static IReadOnlyList<string> Grantable(IHbmpPrincipalAccessor me)
    {
        var roles = me.Principal?.Roles ?? new HashSet<string>();
        return ProviderUserRules.ProviderScopedRoles
            .Where(r => ProviderUserRules.CanProvision(roles, r).Allowed)
            .OrderBy(r => r, StringComparer.Ordinal)
            .ToList();
    }

    private static ProviderDetailView Detail(
        ProviderEntity p, ReadinessView readiness, ProviderTerminationRequest? pending, int activeUsers,
        DateOnly on, IReadOnlyList<string> grantable) =>
        new(p.ProviderId, p.ProviderCode, p.LegalName, p.ProviderType.ToString(),
            ProviderTypeLabels.Label(p.ProviderType), p.Status.ToString(), p.OnboardingState.ToString(),
            p.CommercialName, p.TaxId, p.Phone, p.Email, p.Notes,
            p.StatusReason, p.StatusActorName, p.StatusChangedAt,
            p.CreatedAt, p.UpdatedAt, p.CreatedByName, p.UpdatedByName,
            readiness,
            pending is null ? null : new PendingTerminationView(pending.RequestId, pending.Reason, pending.RequestedBy, pending.RequestedAt),
            new ProviderBookView(
                p.Locations.Count(l => !l.IsDeleted),
                p.Contracts.Count(c => !c.IsDeleted),
                p.Contracts.Count(c => ContractRules.InEffect(c, on)),
                p.Credentials.Count(c => !c.IsDeleted),
                activeUsers),
            grantable);

    private static LocationView ToView(ProviderLocation l) => new(
        l.LocationId, l.Name, l.Governorate, l.Address, l.GeoLat, l.GeoLng, l.IsPrimary, l.IsDeleted,
        l.DeactivationReason, l.DeactivatedAt);

    private static ContractView ToView(ProviderContract c, int lines, DateOnly on) => new(
        c.ContractId, c.ContractNo, c.Status.ToString(), c.EffectiveFrom, c.EffectiveTo, lines,
        ContractRules.InEffect(c, on), c.StatusReason, c.StatusActorName, c.StatusChangedAt);

    private static Task<int> LineCount(ProviderDbContext db, Guid contractId, CancellationToken ct) =>
        db.ServiceLines.CountAsync(l => l.ContractId == contractId, ct);

    private static AuditEventDraft Draft(
        string entityType, Guid entityId, AuditAction action, IHbmpPrincipalAccessor me, string? tenant,
        Guid providerId, string? outcome = null, string? reason = null) => new()
    {
        EntityType = entityType, EntityId = entityId.ToString(), Action = action,
        ActorUserId = me.Principal?.Subject, TenantId = tenant, ProviderId = providerId.ToString(),
        DecisionOutcome = outcome, DecisionReasonCode = reason,
    };
}
