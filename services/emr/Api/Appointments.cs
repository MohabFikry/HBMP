using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Auth.Authorization;
using Mersal.Emr.Domain;
using Mersal.Emr.Infrastructure;
using Mersal.Events;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Emr.Api;

/// <summary>Phase 3.1 — appointment scheduling: recurring-availability slot materialization and
/// concurrency-safe booking (no double-book), with referral/follow-up linkage and a waitlist fallback.</summary>
public static class AppointmentsModule
{
    // Africa/Cairo wall-clock for interpreting availability times (display TZ per CLAUDE.md).
    private static TimeSpan CairoOffset(DateOnly on)
    {
        try
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById(
                OperatingSystem.IsWindows() ? "Egypt Standard Time" : "Africa/Cairo");
            return tz.GetUtcOffset(on.ToDateTime(TimeOnly.MinValue));
        }
        catch (TimeZoneNotFoundException) { return TimeSpan.FromHours(2); }
        catch (InvalidTimeZoneException) { return TimeSpan.FromHours(2); }
    }

    public static void MapAppointments(this WebApplication app)
    {
        var write = app.MapGroup("/api/v1").RequireAuthorization(HbmpPolicies.Scope("appointment:write"));
        var read = app.MapGroup("/api/v1").RequireAuthorization(HbmpPolicies.Scope("appointment:read"));

        // POST /appointment-slots — materialize bookable slots from a recurring availability rule.
        write.MapPost("/appointment-slots", async (
            CreateSlotsRequest req, EmrDbContext db, IHbmpPrincipalAccessor me, TimeProvider clock, CancellationToken ct) =>
        {
            if (req.SlotMinutes <= 0 || req.EndTime <= req.StartTime || req.ToDate < req.FromDate)
                return Results.Problem(statusCode: 400, title: "Invalid availability window", type: "urn:hbmp:invalid-availability");

            var availability = new ProviderAvailability
            {
                AvailabilityId = Guid.NewGuid(), ProviderId = req.ProviderId, LocationId = req.LocationId,
                DoctorId = req.DoctorId, DayOfWeek = req.DayOfWeek,
                StartTime = req.StartTime, EndTime = req.EndTime, SlotMinutes = req.SlotMinutes,
            };
            db.ProviderAvailabilities.Add(availability);

            var generated = SlotGeneration.Generate(availability, req.FromDate, req.ToDate, CairoOffset(req.FromDate));

            // Idempotent materialization: skip slot definitions that already exist for this provider/location/doctor.
            var starts = generated.Select(s => s.SlotStart).ToList();
            var existing = await db.AppointmentSlots.AsNoTracking()
                .Where(s => s.ProviderId == req.ProviderId && s.LocationId == req.LocationId
                            && s.DoctorId == req.DoctorId && starts.Contains(s.SlotStart))
                .Select(s => s.SlotStart).ToListAsync(ct);
            var existingSet = existing.ToHashSet();
            var fresh = generated.Where(s => !existingSet.Contains(s.SlotStart)).ToList();
            db.AppointmentSlots.AddRange(fresh);
            await db.SaveChangesAsync(ct);

            return Results.Ok(new
            {
                availabilityId = availability.AvailabilityId,
                created = fresh.Count,
                skippedExisting = existingSet.Count,
                slots = fresh.Select(s => SlotResponse.From(s, open: true)),
            });
        });

        // GET /appointment-slots — min-necessary slot list (scheduling only); onlyOpen hides held/past slots.
        read.MapGet("/appointment-slots", async (
            Guid providerId, Guid locationId, DateTimeOffset? from, DateTimeOffset? to, bool onlyOpen,
            EmrDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            var now = clock.GetUtcNow();
            var lo = from ?? now;
            var hi = to ?? now.AddDays(14);
            var slots = await db.AppointmentSlots.AsNoTracking()
                .Where(s => s.ProviderId == providerId && s.LocationId == locationId
                            && s.SlotStart >= lo && s.SlotStart <= hi)
                .OrderBy(s => s.SlotStart).ToListAsync(ct);

            var taken = await ActiveHeldSlotIds(db, ct);
            var view = slots.Select(s => SlotResponse.From(s, open: !taken.Contains(s.SlotId) && s.SlotStart > now));
            if (onlyOpen) view = view.Where(s => s.Open);
            return Results.Ok(view);
        });

        // POST /appointments — concurrency-safe booking (US-020).
        write.MapPost("/appointments", async (
            BookAppointmentRequest req, HttpRequest http, EmrDbContext db, AppointmentBookingService booking,
            IAuditClient audit, IOutbox outbox, IHbmpPrincipalAccessor me, TimeProvider clock, CancellationToken ct) =>
        {
            var idem = http.Headers["Idempotency-Key"].ToString();
            if (string.IsNullOrWhiteSpace(idem))
                return Results.Problem(statusCode: 400, title: "Idempotency-Key header is required", type: "urn:hbmp:idempotency-required");
            if (!Enum.TryParse<AppointmentType>(req.AppointmentType, out var type))
                return Results.Problem(statusCode: 400, title: "Unknown appointment type", type: "urn:hbmp:invalid-appointment-type");
            if (!AppointmentTypeLabels.LinkageSatisfied(type, req.ReferralRef, req.OriginEncounterId))
                return Results.Problem(statusCode: 400, type: "urn:hbmp:missing-linkage",
                    title: type == AppointmentType.Referral ? "Referral bookings require a referralRef (REF-*)"
                                                            : "Follow-up bookings require an originEncounterId");

            var actor = me.Principal?.Subject;
            var now = clock.GetUtcNow();

            // Resolve the slot to hold: explicit slotId, else auto-take the earliest open slot (non-walk-in).
            AppointmentSlot? slot = null;
            if (req.SlotId is { } sid)
            {
                slot = await db.AppointmentSlots.AsNoTracking().FirstOrDefaultAsync(s => s.SlotId == sid, ct);
                if (slot is null)
                    return Results.Problem(statusCode: 404, title: "Slot not found", type: "urn:hbmp:slot-not-found");
            }
            else if (type != AppointmentType.WalkIn)
            {
                slot = await EarliestOpenSlot(db, req.ProviderId, req.LocationId, now, ct);
                if (slot is null)
                    return await OfferWaitlistOrNextSlots(req, type, db, audit, actor, now, ct);
            }

            var appt = new Appointment
            {
                AppointmentId = Guid.NewGuid(),
                BeneficiaryId = req.BeneficiaryId, ProviderId = req.ProviderId, LocationId = req.LocationId,
                SlotId = slot?.SlotId, AppointmentType = type, Status = AppointmentStatus.Booked,
                ScheduledStart = slot?.SlotStart ?? req.ScheduledStart ?? now,
                ScheduledEnd = slot?.SlotEnd ?? req.ScheduledEnd ?? now.AddMinutes(15),
                ReferralRef = req.ReferralRef, OriginEncounterId = req.OriginEncounterId,
                IdempotencyKey = idem, CreatedBy = actor, CreatedAt = now, UpdatedAt = now,
            };

            var result = await booking.BookAsync(appt, ct);
            switch (result.Outcome)
            {
                case BookOutcome.SlotNotFound:
                    return Results.Problem(statusCode: 404, title: "Slot not found", type: "urn:hbmp:slot-not-found");
                case BookOutcome.SlotTaken:
                    var next = await NextOpenSlots(db, req.ProviderId, req.LocationId, now, ct);
                    return Results.Problem(statusCode: 409, title: "Slot already booked", type: "urn:hbmp:slot-taken",
                        detail: "That slot was taken by another booking. Choose one of the next available slots.",
                        extensions: new Dictionary<string, object?> { ["nextSlots"] = next });
            }

            var booked = result.Appointment!;
            // Replay of a prior Idempotency-Key returns the existing appointment with no new side-effects.
            var isReplay = booked.AppointmentId != appt.AppointmentId;
            if (!isReplay)
            {
                await audit.EmitAsync(new AuditEventDraft
                {
                    EntityType = "appointment", EntityId = booked.AppointmentId.ToString(), Action = AuditAction.Create,
                    ActorUserId = actor, DecisionOutcome = "ApptBooked",
                    AfterState = $"{{\"type\":\"{type}\",\"slotId\":\"{booked.SlotId}\"}}",
                }, ct);
                await outbox.EnqueueAsync("ApptBooked", "emr.events", new
                {
                    appointmentId = booked.AppointmentId, beneficiaryId = booked.BeneficiaryId,
                    providerId = booked.ProviderId, locationId = booked.LocationId,
                    slotId = booked.SlotId, appointmentType = type.ToString(),
                    scheduledStart = booked.ScheduledStart, scheduledEnd = booked.ScheduledEnd,
                }, ct);
                if (type == AppointmentType.Referral && booked.ReferralRef is { Length: > 0 } refRef)
                    await outbox.EnqueueAsync("ReferralScheduled", "emr.events",
                        new { referralRef = refRef, appointmentId = booked.AppointmentId }, ct);
            }

            return Results.Created($"/api/v1/appointments/{booked.AppointmentId}", AppointmentResponse.From(booked));
        });

        read.MapGet("/appointments/{id:guid}", async (Guid id, HttpResponse resp, EmrDbContext db, CancellationToken ct) =>
        {
            var a = await db.Appointments.AsNoTracking().FirstOrDefaultAsync(x => x.AppointmentId == id, ct);
            if (a is null) return Results.NotFound();
            resp.Headers.ETag = $"\"{a.RowVersion}\"";   // clients echo this back as If-Match on transitions
            return Results.Ok(AppointmentResponse.From(a));
        });

        // POST /appointments/{id}/reschedule — atomic release-old + book-new (US-021).
        write.MapPost("/appointments/{id:guid}/reschedule", async (
            Guid id, RescheduleRequest req, HttpRequest http, EmrDbContext db, AppointmentTransitionService transitions,
            IdempotencyStore idem, IAuditClient audit, IOutbox outbox, IHbmpPrincipalAccessor me, TimeProvider clock,
            CancellationToken ct) =>
        {
            var (replay, key) = await CheckIdempotency(http, idem, db, ct);
            if (replay is not null) return replay;

            var result = await transitions.RescheduleAsync(id, req.NewSlotId, IfMatch(http), clock.GetUtcNow(), ct);
            var problem = MapFailure(result.Outcome);
            if (problem is not null) return await AuditAndReturn(problem, audit, me, "ApptRescheduleDenied", id, result.Outcome, ct);

            var appt = result.Appointment!;
            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "appointment", EntityId = id.ToString(), Action = AuditAction.StateChange,
                ActorUserId = me.Principal?.Subject, DecisionOutcome = "ApptRescheduled",
                AfterState = $"{{\"slotId\":\"{appt.SlotId}\"}}",
            }, ct);
            await outbox.EnqueueAsync("ApptRescheduled", "emr.events",
                new { appointmentId = id, newSlotId = appt.SlotId, scheduledStart = appt.ScheduledStart }, ct);
            await Record(idem, key, "reschedule", id, 200, db, ct);
            return Results.Ok(AppointmentResponse.From(appt));
        });

        // POST /appointments/{id}/cancel — release slot + reason; promote waitlist (US-021).
        write.MapPost("/appointments/{id:guid}/cancel", async (
            Guid id, CancelRequest req, HttpRequest http, EmrDbContext db, AppointmentTransitionService transitions,
            IdempotencyStore idem, IAuditClient audit, IOutbox outbox, IHbmpPrincipalAccessor me, TimeProvider clock,
            CancellationToken ct) =>
        {
            var (replay, key) = await CheckIdempotency(http, idem, db, ct);
            if (replay is not null) return replay;

            var result = await transitions.CancelAsync(id, req.Reason, IfMatch(http), clock.GetUtcNow(), ct);
            var problem = MapFailure(result.Outcome);
            if (problem is not null) return await AuditAndReturn(problem, audit, me, "ApptCancelDenied", id, result.Outcome, ct);

            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "appointment", EntityId = id.ToString(), Action = AuditAction.StateChange,
                ActorUserId = me.Principal?.Subject, DecisionOutcome = "ApptCancelled", DecisionReasonCode = req.Reason,
            }, ct);
            await outbox.EnqueueAsync("ApptCancelled", "emr.events", new { appointmentId = id, reason = req.Reason }, ct);
            await PromotionSideEffects(result, outbox, ct);
            await Record(idem, key, "cancel", id, 200, db, ct);
            return Results.Ok(AppointmentResponse.From(result.Appointment!));
        });

        // POST /appointments/{id}/no-show — guarded; reporting flag + backfill (US-022).
        write.MapPost("/appointments/{id:guid}/no-show", async (
            Guid id, HttpRequest http, EmrDbContext db, AppointmentTransitionService transitions,
            IdempotencyStore idem, IAuditClient audit, IOutbox outbox, IHbmpPrincipalAccessor me, TimeProvider clock,
            CancellationToken ct) =>
        {
            var (replay, key) = await CheckIdempotency(http, idem, db, ct);
            if (replay is not null) return replay;

            var result = await transitions.NoShowAsync(id, IfMatch(http), clock.GetUtcNow(), NoShowGrace, ct);
            var problem = MapFailure(result.Outcome);
            if (problem is not null) return await AuditAndReturn(problem, audit, me, "ApptNoShowDenied", id, result.Outcome, ct);

            var appt = result.Appointment!;
            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "appointment", EntityId = id.ToString(), Action = AuditAction.StateChange,
                ActorUserId = me.Principal?.Subject, DecisionOutcome = "ApptNoShow",
            }, ct);
            await outbox.EnqueueAsync("ApptNoShow", "emr.events",
                new { appointmentId = id, beneficiaryId = appt.BeneficiaryId, noShowCount = result.NoShowCount }, ct);
            await PromotionSideEffects(result, outbox, ct);
            // Repeat no-shows → Case Manager follow-up (05 X3).
            if (result.NoShowCount >= RepeatNoShowThreshold)
                await outbox.EnqueueAsync("BeneficiaryNoShowThresholdReached", "emr.events",
                    new { beneficiaryId = appt.BeneficiaryId, noShowCount = result.NoShowCount }, ct);
            await Record(idem, key, "no-show", id, 200, db, ct);
            return Results.Ok(AppointmentResponse.From(appt));
        });
    }

    private const int RepeatNoShowThreshold = 3;
    private static readonly TimeSpan NoShowGrace = TimeSpan.FromMinutes(15);

    private static uint? IfMatch(HttpRequest http)
    {
        var raw = http.Headers.IfMatch.ToString().Trim().Trim('"');
        return uint.TryParse(raw, out var v) ? v : null;
    }

    // Idempotency: a seen key replays the prior outcome (re-fetch + 200) instead of re-applying.
    private static async Task<(IResult? Replay, string? Key)> CheckIdempotency(
        HttpRequest http, IdempotencyStore idem, EmrDbContext db, CancellationToken ct)
    {
        var key = http.Headers["Idempotency-Key"].ToString();
        if (string.IsNullOrWhiteSpace(key)) return (null, null);
        var prior = await idem.FindAsync(key, ct);
        if (prior is null) return (null, key);
        if (prior.AppointmentId is { } aid)
        {
            var a = await db.Appointments.AsNoTracking().FirstOrDefaultAsync(x => x.AppointmentId == aid, ct);
            if (a is not null) return (Results.Ok(AppointmentResponse.From(a)), key);
        }
        return (Results.StatusCode(prior.StatusCode), key);
    }

    private static async Task Record(IdempotencyStore idem, string? key, string op, Guid id, int status, EmrDbContext db, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(key)) await idem.RecordAsync(key, op, id, status, ct);
    }

    private static async Task PromotionSideEffects(TransitionResult result, IOutbox outbox, CancellationToken ct)
    {
        if (result.Promoted is { } w)
            await outbox.EnqueueAsync("ApptWaitlistPromoted", "emr.events",
                new { waitlistId = w.WaitlistId, beneficiaryId = w.BeneficiaryId, providerId = w.ProviderId }, ct);
    }

    private static IResult? MapFailure(TransitionOutcome outcome) => outcome switch
    {
        TransitionOutcome.Ok => null,
        TransitionOutcome.NotFound => Results.NotFound(),
        TransitionOutcome.IllegalTransition => Results.Problem(statusCode: 409, title: "Transition not allowed",
            type: "urn:hbmp:transition-denied", detail: "The appointment is not in a state that allows this action."),
        TransitionOutcome.SlotTaken => Results.Problem(statusCode: 409, title: "Slot already booked", type: "urn:hbmp:slot-taken"),
        TransitionOutcome.SlotNotFound => Results.Problem(statusCode: 404, title: "Slot not found", type: "urn:hbmp:slot-not-found"),
        TransitionOutcome.PreconditionFailed => Results.Problem(statusCode: 412, title: "Version mismatch",
            type: "urn:hbmp:precondition-failed", detail: "The appointment changed since you last read it; re-fetch and retry."),
        _ => Results.Problem(statusCode: 400),
    };

    private static async Task<IResult> AuditAndReturn(
        IResult problem, IAuditClient audit, IHbmpPrincipalAccessor me, string outcome, Guid id, TransitionOutcome reason, CancellationToken ct)
    {
        await audit.EmitAsync(new AuditEventDraft
        {
            EntityType = "appointment", EntityId = id.ToString(), Action = AuditAction.Decision,
            ActorUserId = me.Principal?.Subject, DecisionOutcome = outcome, DecisionReasonCode = reason.ToString(),
        }, ct);
        return problem;
    }

    private static async Task<HashSet<Guid>> ActiveHeldSlotIds(EmrDbContext db, CancellationToken ct)
    {
        var ids = await db.Appointments.AsNoTracking()
            .Where(a => a.SlotId != null
                        && (a.Status == AppointmentStatus.Booked || a.Status == AppointmentStatus.CheckedIn))
            .Select(a => a.SlotId!.Value).ToListAsync(ct);
        return ids.ToHashSet();
    }

    private static async Task<AppointmentSlot?> EarliestOpenSlot(
        EmrDbContext db, Guid providerId, Guid locationId, DateTimeOffset now, CancellationToken ct)
    {
        var taken = await ActiveHeldSlotIds(db, ct);
        return await db.AppointmentSlots.AsNoTracking()
            .Where(s => s.ProviderId == providerId && s.LocationId == locationId && s.SlotStart > now
                        && !taken.Contains(s.SlotId))
            .OrderBy(s => s.SlotStart).FirstOrDefaultAsync(ct);
    }

    private static async Task<List<SlotResponse>> NextOpenSlots(
        EmrDbContext db, Guid providerId, Guid locationId, DateTimeOffset now, CancellationToken ct)
    {
        var taken = await ActiveHeldSlotIds(db, ct);
        var slots = await db.AppointmentSlots.AsNoTracking()
            .Where(s => s.ProviderId == providerId && s.LocationId == locationId && s.SlotStart > now
                        && !taken.Contains(s.SlotId))
            .OrderBy(s => s.SlotStart).Take(3).ToListAsync(ct);
        return slots.Select(s => SlotResponse.From(s, open: true)).ToList();
    }

    // No slot available: create a waitlist entry if the caller opted in (202), else offer next slots (409).
    private static async Task<IResult> OfferWaitlistOrNextSlots(
        BookAppointmentRequest req, AppointmentType type, EmrDbContext db, IAuditClient audit,
        string? actor, DateTimeOffset now, CancellationToken ct)
    {
        if (!req.JoinWaitlistIfFull)
        {
            var next = await NextOpenSlots(db, req.ProviderId, req.LocationId, now, ct);
            return Results.Problem(statusCode: 409, title: "No slot available", type: "urn:hbmp:no-slot",
                detail: "No open slot for the requested provider/location. Retry with joinWaitlistIfFull or pick a listed slot.",
                extensions: new Dictionary<string, object?> { ["nextSlots"] = next });
        }

        var entry = new WaitlistEntry
        {
            WaitlistId = Guid.NewGuid(), BeneficiaryId = req.BeneficiaryId,
            ProviderId = req.ProviderId, LocationId = req.LocationId, AppointmentType = type,
            ReferralRef = req.ReferralRef, OriginEncounterId = req.OriginEncounterId,
            Status = WaitlistStatus.Waitlisted, CreatedBy = actor, CreatedAt = now,
        };
        db.WaitlistEntries.Add(entry);
        await db.SaveChangesAsync(ct);
        await audit.EmitAsync(new AuditEventDraft
        {
            EntityType = "waitlist_entry", EntityId = entry.WaitlistId.ToString(), Action = AuditAction.Create,
            ActorUserId = actor, DecisionOutcome = "ApptWaitlisted",
        }, ct);
        return Results.Accepted($"/api/v1/waitlist/{entry.WaitlistId}", WaitlistResponse.From(entry));
    }
}
