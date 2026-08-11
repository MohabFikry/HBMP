using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Auth.Authorization;
using Mersal.Authz;
using Mersal.Emr.Domain;
using Mersal.Emr.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Emr.Api;

/// <summary>
/// The weekly pattern, as something a clinic can actually administer (design 42 §4/§6).
///
/// <para><b>The gap this fills.</b> <c>emr.provider_availability</c> has carried the weekly recurring rule
/// since 0002 and had no read, update or delete anywhere on the platform. Its only writer was
/// <c>POST /appointment-slots</c>, as a side effect of materializing a calendar. So a coordinator could record
/// that a doctor was on LEAVE next Tuesday — the exception layer, 25.4 — and could not state, change or even
/// see what the doctor's Tuesday normally was. The Roster screen opened by describing "the weekly pattern"
/// and then showed only the exceptions to it.</para>
///
/// <para><b>Capacity lives here.</b> <see cref="ProviderAvailability.MaxPerDay"/> is the answer to "how many
/// patients will this clinician see in a day", which the window length cannot express: six hours at fifteen
/// minutes offers twenty-four appointments, and a clinician who can safely take twenty says twenty. It is
/// enforced in <see cref="SlotGeneration"/> — the one place availability is computed, per design 42 §7 rule 5
/// — and again at booking, because a cap that only shapes the calendar is not a cap for any path that books
/// without consuming a slot.</para>
///
/// <para><b>Reads are wider than writes, deliberately.</b> The desk needs to know when a clinic runs
/// (<c>appointment:read</c>); only the people who run it may change that (<c>branch:roster:write</c>). Both
/// are narrowed to the caller's branch reach — reads through <see cref="BranchQueryScope"/>, writes through
/// <see cref="BranchWriteScope"/>.</para>
/// </summary>
public static class ProviderAvailabilityEndpoints
{
    public static void MapProviderAvailability(this WebApplication app)
    {
        var read = app.MapGroup("/api/v1").RequireAuthorization(HbmpPolicies.Scope("appointment:read"));
        var write = app.MapGroup("/api/v1").RequireAuthorization(HbmpPolicies.Scope("branch:roster:write"));

        // GET /provider-availability — the weekly pattern for the clinics in reach.
        read.MapGet("/provider-availability", async (
            Guid? branchId, Guid? doctorId,
            BranchScopeState branch, EmrDbContext db, CancellationToken ct) =>
        {
            var q = db.ProviderAvailabilities.AsNoTracking()
                .ApplyBranchScope(a => a.BranchId, branch.Mode, branch.Context, branchId);
            if (doctorId is { } d) q = q.Where(a => a.DoctorId == d);

            var rows = await q
                .OrderBy(a => a.DoctorId).ThenBy(a => a.DayOfWeek).ThenBy(a => a.StartTime)
                .Take(1000).ToListAsync(ct);

            return Results.Ok(rows.Select(ToView));
        });

        // POST /provider-availability — state the rule. 409 when one already exists for this key, naming it,
        // because "you already have a Tuesday" with no way to find it is not an error anyone can act on.
        write.MapPost("/provider-availability", async (
            UpsertAvailabilityRequest req, BranchScopeState branch, IHbmpPrincipalAccessor me,
            EmrDbContext db, IAuditClient audit, TimeProvider clock, CancellationToken ct) =>
        {
            var tenant = me.Principal?.TenantId;
            if (string.IsNullOrEmpty(tenant)) return Results.Problem(statusCode: 403, title: "no tenant scope on principal");

            if (Validate(req) is { } invalid) return invalid;

            var (targetBranch, denied) = BranchWriteScope.ResolveTarget(branch.Mode, branch.Context, req.BranchId);
            if (denied is not null) return denied;

            var existing = await db.ProviderAvailabilities.AsNoTracking().FirstOrDefaultAsync(
                a => a.TenantId == tenant && a.ProviderId == req.ProviderId && a.LocationId == req.LocationId
                     && a.DoctorId == req.DoctorId && a.BranchId == targetBranch && a.DayOfWeek == req.DayOfWeek, ct);
            if (existing is not null)
                return Results.Problem(
                    statusCode: 409, title: "availability-rule-exists", type: RuleExistsProblemType,
                    detail: "This clinician already has a pattern for that day at this clinic. Edit it rather than adding a second — two patterns for one day generate two sets of slots.",
                    extensions: new Dictionary<string, object?> { ["availabilityId"] = existing.AvailabilityId });

            var now = clock.GetUtcNow();
            var row = new ProviderAvailability
            {
                AvailabilityId = Guid.NewGuid(), TenantId = tenant,
                ProviderId = req.ProviderId, LocationId = req.LocationId, BranchId = targetBranch,
                DoctorId = req.DoctorId, DayOfWeek = req.DayOfWeek,
                StartTime = req.StartTime, EndTime = req.EndTime, SlotMinutes = req.SlotMinutes,
                MaxPerDay = req.MaxPerDay,
                CreatedAt = now, CreatedBy = me.Principal?.Subject,
                UpdatedAt = now, UpdatedBy = me.Principal?.Subject, UpdatedByName = me.Principal?.DisplayName,
            };
            db.ProviderAvailabilities.Add(row);
            await db.SaveChangesAsync(ct);

            await audit.EmitAsync(Draft(row, AuditAction.Create, me, tenant, "availability-created"), ct);
            return Results.Created($"/api/v1/provider-availability/{row.AvailabilityId}", ToView(row));
        });

        // PUT /provider-availability/{id} — hours, slot length, cap.
        //
        // The rule's IDENTITY (practitioner, clinic, weekday) is not editable here. Moving a Tuesday pattern
        // to a Wednesday is retiring one rule and stating another: the slots already generated from it, and
        // the appointments booked into those, belong to the Tuesday.
        write.MapPut("/provider-availability/{id:guid}", async (
            Guid id, UpsertAvailabilityRequest req, BranchScopeState branch, IHbmpPrincipalAccessor me,
            EmrDbContext db, IAuditClient audit, TimeProvider clock, CancellationToken ct) =>
        {
            var tenant = me.Principal?.TenantId;
            if (string.IsNullOrEmpty(tenant)) return Results.Problem(statusCode: 403, title: "no tenant scope on principal");

            if (Validate(req) is { } invalid) return invalid;

            var row = await db.ProviderAvailabilities
                .FirstOrDefaultAsync(a => a.AvailabilityId == id && a.TenantId == tenant, ct);
            if (row is null) return NotFound();

            if (BranchWriteScope.RefuseUnlessWritable(branch.Mode, branch.Context, row.BranchId) is { } refused)
                return refused;

            var before = $"{row.StartTime:HH\\:mm}-{row.EndTime:HH\\:mm}/{row.SlotMinutes}m/cap:{row.MaxPerDay?.ToString() ?? "none"}";

            row.StartTime = req.StartTime;
            row.EndTime = req.EndTime;
            row.SlotMinutes = req.SlotMinutes;
            // A PUT states the rule in full, so a null cap HERE means uncapped — unlike the slot-materialization
            // path, where an omitted cap leaves the existing one alone. The difference is that removing a cap
            // is the thing being done on this route, and a by-product on that one.
            row.MaxPerDay = req.MaxPerDay;
            row.UpdatedAt = clock.GetUtcNow();
            row.UpdatedBy = me.Principal?.Subject;
            row.UpdatedByName = me.Principal?.DisplayName;

            await db.SaveChangesAsync(ct);
            await audit.EmitAsync(Draft(row, AuditAction.Update, me, tenant, "availability-updated", before), ct);

            return Results.Ok(ToView(row));
        });

        // DELETE /provider-availability/{id} — soft delete, and it does NOT retract the calendar.
        //
        // Retiring a pattern stops FUTURE materialization. Slots already generated stay, and appointments
        // booked into them stay booked: the roster must not be able to cancel a refugee's appointment as a
        // side effect, which is the same rule a lapsed licence follows (design 42 §7 rule 6). Clearing the
        // remaining days is a roster exception, where it comes with an impact preview and a reason.
        write.MapDelete("/provider-availability/{id:guid}", async (
            Guid id, BranchScopeState branch, IHbmpPrincipalAccessor me,
            EmrDbContext db, IAuditClient audit, TimeProvider clock, CancellationToken ct) =>
        {
            var tenant = me.Principal?.TenantId;
            if (string.IsNullOrEmpty(tenant)) return Results.Problem(statusCode: 403, title: "no tenant scope on principal");

            var row = await db.ProviderAvailabilities
                .FirstOrDefaultAsync(a => a.AvailabilityId == id && a.TenantId == tenant, ct);
            if (row is null) return NotFound();

            if (BranchWriteScope.RefuseUnlessWritable(branch.Mode, branch.Context, row.BranchId) is { } refused)
                return refused;

            row.IsDeleted = true;
            row.UpdatedAt = clock.GetUtcNow();
            row.UpdatedBy = me.Principal?.Subject;
            row.UpdatedByName = me.Principal?.DisplayName;
            await db.SaveChangesAsync(ct);

            await audit.EmitAsync(Draft(row, AuditAction.SoftDelete, me, tenant, "availability-retired"), ct);

            return Results.Ok(new { availabilityId = id, retired = true, slotsRemoved = 0 });
        });

        // GET /provider-availability/{id}/history — the operational timeline (design 42 §7 rule 14).
        //
        // NOT the audit trail. The audit chain is hash-linked, tamper-evident and readable only by
        // Security/Compliance/DPO, and widening it to clinic staff to answer "who narrowed our Tuesday" would
        // hand them the whole compliance record to answer an operational question. This reads the history twin
        // the 0025 trigger writes, under the same branch reach as the rule itself.
        read.MapGet("/provider-availability/{id:guid}/history", async (
            Guid id, BranchScopeState branch, IHbmpPrincipalAccessor me, EmrDbContext db, CancellationToken ct) =>
        {
            var tenant = me.Principal?.TenantId;
            var row = await db.ProviderAvailabilities.AsNoTracking()
                .IgnoreQueryFilters()   // a retired rule still has a history, and that is when it is asked for
                .FirstOrDefaultAsync(a => a.AvailabilityId == id && a.TenantId == tenant, ct);
            if (row is null) return NotFound();

            if (BranchWriteScope.RefuseUnlessWritable(branch.Mode, branch.Context, row.BranchId) is { } refused)
                return refused;

            var rows = await db.ProviderAvailabilityHistory.AsNoTracking()
                .Where(h => h.AvailabilityId == id && h.TenantId == tenant)
                .OrderBy(h => h.HistoryId)
                .Take(200)
                .ToListAsync(ct);

            return Results.Ok(new { availabilityId = id, entries = rows.Select(AvailabilityHistoryView.From) });
        });
    }

    public const string RuleExistsProblemType = "urn:hbmp:availability-rule-exists";

    private static IResult NotFound() =>
        Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");

    /// <summary>
    /// The rule has to make sense as a rule before it is stored. A backwards window and a non-positive slot
    /// length are both refused here rather than at generation, where the symptom is an empty calendar and no
    /// explanation.
    /// </summary>
    private static IResult? Validate(UpsertAvailabilityRequest req)
    {
        if (req.SlotMinutes <= 0 || req.EndTime <= req.StartTime)
            return Results.Problem(statusCode: 400, title: "Invalid availability window",
                type: "urn:hbmp:invalid-availability",
                detail: "The session must end after it starts, and a slot must be at least a minute long.");

        if (req.MaxPerDay is { } cap && cap <= 0)
            return Results.Problem(statusCode: 400, title: "Invalid daily cap", type: "urn:hbmp:invalid-availability",
                // A cap of zero is not "uncapped", it is "closed" — and a closed clinic is a roster exception,
                // which carries a reason and an impact preview. Allowing 0 here would be a second, silent way
                // to shut a clinic with neither.
                detail: "A cap of zero would close the clinic silently. Leave it empty for no cap, or record a closure on the roster.");

        return null;
    }

    private static AuditEventDraft Draft(
        ProviderAvailability row, AuditAction action, IHbmpPrincipalAccessor me, string tenant,
        string outcome, string? before = null) => new()
        {
            EntityType = "provider_availability", EntityId = row.AvailabilityId.ToString(), Action = action,
            ActorUserId = me.Principal?.Subject, TenantId = tenant, ActorMfa = me.Principal?.MfaSatisfied ?? false,
            DecisionOutcome = outcome, DecisionReasonCode = before,
        };

    private static object ToView(ProviderAvailability a) => new
    {
        availabilityId = a.AvailabilityId,
        providerId = a.ProviderId,
        locationId = a.LocationId,
        branchId = a.BranchId,
        doctorId = a.DoctorId,
        dayOfWeek = (int)a.DayOfWeek,
        startTime = a.StartTime.ToString("HH\\:mm"),
        endTime = a.EndTime.ToString("HH\\:mm"),
        slotMinutes = a.SlotMinutes,
        maxPerDay = a.MaxPerDay,
        // The slot count the WINDOW yields, and the count actually offered once the cap applies. Both, because
        // "24 slots, capped at 20" is the sentence a coordinator is trying to read, and showing only one of
        // the numbers makes the cap either invisible or unexplained.
        slotsFromWindow = SlotGeneration.WindowSlotCount(a.StartTime, a.EndTime, a.SlotMinutes),
        slotsPerDay = SlotGeneration.EffectiveSlotsPerDay(a.StartTime, a.EndTime, a.SlotMinutes, a.MaxPerDay),
        updatedAt = a.UpdatedAt,
        updatedBy = a.UpdatedBy,
        updatedByName = a.UpdatedByName,
    };
}
