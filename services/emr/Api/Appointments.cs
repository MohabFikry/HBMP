using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Auth.Authorization;
using Mersal.Authz;
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

    /// <summary>
    /// 25.3 — the CLINIC's calendar date for an instant, in Africa/Cairo.
    ///
    /// Licence expiry is a DATE and appointments are instants, so something has to bridge them, and doing it
    /// in UTC would be wrong for exactly the appointments nearest a boundary: Cairo runs two to three hours
    /// ahead, so an early-morning slot on 1 October is still 30 September in UTC. Judging a licence that
    /// expired on 30 September against that would let the first appointments of October through — a
    /// once-a-lapse, hard-to-reproduce hole, which is the worst kind.
    /// </summary>
    private static DateOnly ClinicDateOf(DateTimeOffset instant)
    {
        var utcDate = DateOnly.FromDateTime(instant.UtcDateTime);
        return DateOnly.FromDateTime(instant.ToOffset(CairoOffset(utcDate)).DateTime);
    }

    /// <summary>
    /// 25.1 — the caller's branch REACH MODE, which decides what the branch predicate means.
    ///
    /// Reads used to ask <c>ActiveBranchId is { } active</c> directly. That is correct for the two modes that
    /// existed when it was written and silently wrong for a set-scoped clinics manager, who has no single
    /// active branch until they filter — so the condition was false, no predicate was applied, and a manager
    /// holding three grants would have read all six branches. See <see cref="BranchQueryScope"/>.
    /// </summary>
    private static ScopeMode BranchModeOf(IHbmpPrincipalAccessor me) =>
        me.Principal is null ? ScopeMode.MemberScoped : BranchScopeModes.ModeFor(me.Principal);

    public static void MapAppointments(this WebApplication app)
    {
        // Desk writes: booking's own slot administration, plus the arrival decisions (check-in, no-show) that
        // only someone physically at the branch can make.
        var write = app.MapGroup("/api/v1").RequireAuthorization(HbmpPolicies.Scope("appointment:write"));
        var read = app.MapGroup("/api/v1").RequireAuthorization(HbmpPolicies.Scope("appointment:read"));

        // RESERVATION writes — book, reschedule, cancel. Reachable by the desk (appointment:write) OR by a
        // reservation-only caller such as the call centre (appointment:reserve), which must never be able to
        // check a patient in or mark a no-show. Before this split there was one write scope for both, so the
        // call centre could either be given check-in it must not have, or be unable to book at all: it had the
        // latter, and its entire booking path returned a bare 403 from this service.
        var reserve = app.MapGroup("/api/v1")
            .RequireAuthorization(HbmpPolicies.AnyScope("appointment:write", "appointment:reserve"));

        // POST /appointment-slots — materialize bookable slots from a recurring availability rule.
        write.MapPost("/appointment-slots", async (
            CreateSlotsRequest req, EmrDbContext db, IPractitionerBranchDirectory practitioners,
            IHbmpPrincipalAccessor me, BranchScopeState branch, TimeProvider clock, CancellationToken ct) =>
        {
            if (req.SlotMinutes <= 0 || req.EndTime <= req.StartTime || req.ToDate < req.FromDate)
                return Results.Problem(statusCode: 400, title: "Invalid availability window", type: "urn:hbmp:invalid-availability");

            // Resolved the same way a booking is (design 37 §3): a branch-scoped caller materializes slots for
            // its own branch and may not name another.
            var (slotBranch, slotDenied) = AppointmentEndpointsShared.ResolveBookingBranch(branch, req.BranchId);
            if (slotDenied is not null) return slotDenied;

            // 18.C2 (W7 / FR-BRN-026) — the FIRST of the two gates, and the one that matters more: refusing
            // here means the bad slots are never materialized, so no patient can be booked into them. Catching
            // it only at booking time would leave a doctor's calendar full of appointments at a branch they
            // do not work at, each needing to be cancelled and the patient rung back.
            //
            // 25.3 — asked AS AT `fromDate`, and asked about the LICENCE too. Both facts are date-dependent
            // and this request spans a range, so `licenceExpiry` is carried into generation below rather than
            // being resolved to a single yes/no here: a licence expiring on 30 September must yield September
            // slots and no October ones, not an all-or-nothing refusal for the whole request.
            DateOnly? bookableUntil = null;
            if (req.DoctorId is { } doctorId && slotBranch is { } branchId)
            {
                var bookability = await practitioners.BookabilityAsync(doctorId, branchId, req.FromDate, ct);
                if (PractitionerBranchRules.Refuse(bookability.ServesBranch, doctorId, branchId) is { } reason)
                    return Results.Problem(statusCode: 422, title: "practitioner-not-at-branch",
                        type: PractitionerBranchRules.ProblemType, detail: reason);

                // 25.4 — the licence AND the assignment both bound the run, at different points. Combined in
                // one place so neither call site can forget one of them.
                bookableUntil = SlotGeneration.BookableUntil(bookability.LicenceExpiry, bookability.AssignmentValidTo);

                // Already lapsed before the range even opens: refuse outright rather than answering
                // "created: 0", which reads as a quiet success and tells nobody why the calendar stayed empty.
                if (PractitionerBranchRules.RefuseExpiredLicence(
                        bookability.LicenceValid, bookability.LicenceExpiry, doctorId, req.FromDate) is { } licenceReason)
                    return Results.Problem(statusCode: 422, title: "practitioner-licence-expired",
                        type: PractitionerBranchRules.LicenceExpiredProblemType, detail: licenceReason);
            }

            // 25.4 (design 42 §4/§7 rule 5) — leave, holidays, closures and ad-hoc clinics, loaded ONCE and
            // handed to the single availability computation. Every consumer resolves through that function;
            // a second place deciding whether a slot exists is the bug.
            var rosterExceptions = await RosterExceptionEndpoints.OverlappingAsync(
                db, slotBranch, req.DoctorId, req.FromDate, req.ToDate, ct);

            var availability = new ProviderAvailability
            {
                AvailabilityId = Guid.NewGuid(), ProviderId = req.ProviderId, LocationId = req.LocationId,
                // Was validated and then dropped, so the rule — and every slot generated from it — ended up
                // branchless. SlotGeneration copies this onto each slot.
                BranchId = slotBranch,
                DoctorId = req.DoctorId, DayOfWeek = req.DayOfWeek,
                StartTime = req.StartTime, EndTime = req.EndTime, SlotMinutes = req.SlotMinutes,
            };
            db.ProviderAvailabilities.Add(availability);

            // 25.3 — the licence is a bound on the generation WINDOW, not a filter applied afterwards.
            // Availability is computed in exactly one place (design 42 §7 rule 5), and that place has to know
            // the last date this practitioner may lawfully be booked.
            var generated = SlotGeneration.Generate(
                availability, req.FromDate, req.ToDate, CairoOffset(req.FromDate), bookableUntil, rosterExceptions);

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
            Guid? doctorId,
            EmrDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            var now = clock.GetUtcNow();
            var lo = from ?? now;
            var hi = to ?? now.AddDays(14);
            var q = db.AppointmentSlots.AsNoTracking()
                .Where(s => s.ProviderId == providerId && s.LocationId == locationId
                            && s.SlotStart >= lo && s.SlotStart <= hi);
            // 14.5 — the booking screen picks a DOCTOR before a time, so it needs that doctor's slots rather
            // than the whole clinic's. Filtered server-side: the alternative is shipping every clinician's
            // fortnight to the browser and hiding most of it, which is slower and puts one doctor's calendar
            // in front of a client that asked about another.
            if (doctorId is { } d) q = q.Where(s => s.DoctorId == d);

            var slots = await q.OrderBy(s => s.SlotStart).ToListAsync(ct);

            var taken = await ActiveHeldSlotIds(db, ct);
            var view = slots.Select(s => SlotResponse.From(s, open: !taken.Contains(s.SlotId) && s.SlotStart > now));
            if (onlyOpen) view = view.Where(s => s.Open);
            return Results.Ok(view);
        });

        // GET /appointment-days — per-DAY open counts, for the booking calendar.
        //
        // The calendar needs one number per day across a month; fetching every slot and grouping in the
        // browser would ship thousands of rows to render thirty cells, and would still be wrong for a month
        // that exceeds the page limit. Counting where the data is keeps the payload proportional to what is
        // actually displayed.
        read.MapGet("/appointment-days", async (
            Guid providerId, Guid locationId, DateTimeOffset from, DateTimeOffset to, Guid? doctorId,
            EmrDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            if (to < from) return Results.Problem(statusCode: 400, title: "to must not precede from",
                type: "urn:hbmp:invalid-range");

            var now = clock.GetUtcNow();
            var q = db.AppointmentSlots.AsNoTracking()
                .Where(s => s.ProviderId == providerId && s.LocationId == locationId
                            && s.SlotStart >= from && s.SlotStart <= to && s.SlotStart > now);
            if (doctorId is { } d) q = q.Where(s => s.DoctorId == d);

            var taken = await ActiveHeldSlotIds(db, ct);
            var rows = await q.Select(s => new { s.SlotId, s.SlotStart }).Take(10000).ToListAsync(ct);

            // Grouped by the CAIRO civil day, not the UTC one. A 23:30 UTC slot is tomorrow in Cairo, and a
            // calendar that disagreed with the times printed beside it would be read as a bug — the same
            // two-hour mismatch AppointmentDay exists to prevent on the day board.
            var days = rows
                .Where(r => !taken.Contains(r.SlotId))
                .GroupBy(r => DateOnly.FromDateTime(r.SlotStart.ToOffset(CairoOffset(DateOnly.FromDateTime(r.SlotStart.UtcDateTime))).DateTime))
                .Select(g => new AppointmentDayResponse(g.Key, g.Count()))
                .OrderBy(d => d.Day)
                .ToList();
            return Results.Ok(days);
        });

        // GET /branch-clinics — the clinics a caller may actually book into, derived from the slots that exist.
        //
        // Reception needs to name a provider + location to book, and /api/v1/providers is correctly 403 for the
        // front desk: reading the provider DIRECTORY (contracts, onboarding state, the whole network) is not
        // reception's business. What the desk legitimately needs is far narrower — "which clinics in my branch
        // have times I can book?" — and that is answerable entirely from the slot table under the
        // appointment:read scope the desk already holds. Deriving it from bookable slots rather than from a
        // provider list also means a clinic with no availability never appears, so the desk cannot pick a
        // clinic and then find it empty.
        read.MapGet("/branch-clinics", async (
            Guid? branchId, BranchScopeState branch, IHbmpPrincipalAccessor me, EmrDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            var now = clock.GetUtcNow();
            var q = db.AppointmentSlots.AsNoTracking().Where(s => s.SlotStart > now);
            // A branch-scoped caller sees only its active branch; a SET-scoped clinics manager sees every
            // branch they hold a grant to, narrowed if they have filtered; an unrestricted caller (call
            // centre) may narrow to one explicitly and otherwise sees every branch it can reach.
            q = q.ApplyBranchScope(s => s.BranchId, BranchModeOf(me), branch.Context, branchId);

            var taken = await ActiveHeldSlotIds(db, ct);
            var rows = await q
                .Select(s => new { s.SlotId, s.ProviderId, s.LocationId, s.BranchId })
                .Take(5000).ToListAsync(ct);

            var clinics = rows
                .Where(r => !taken.Contains(r.SlotId))
                .GroupBy(r => new { r.ProviderId, r.LocationId, r.BranchId })
                .Select(g => new BranchClinicResponse(
                    g.Key.ProviderId, g.Key.LocationId, g.Key.BranchId, g.Count()))
                .OrderByDescending(c => c.OpenSlots)
                .ToList();
            return Results.Ok(clinics);
        });

        // GET /booking/doctor-availability — which DOCTORS can actually be booked, and how soon.
        //
        // Answered entirely from emr's own slot table, exactly as /branch-clinics above is, and for the same
        // reason: "who has open times in this branch" is emr's question about emr's data, so it needs nothing
        // but the appointment:read the desk already holds.
        //
        // What it deliberately does NOT do is name the doctor or their specialty. Those live in
        // provider-service, and fetching them here would mean emr calling a sibling on the caller's behalf to
        // assemble a richer answer than the caller could obtain themselves — the aggregation shape the
        // platform forbids (profile-service's NoServiceAccountArchitectureTests spells out why). The booking
        // screen holds `practitioner:read` (identity 0018) and asks provider-service directly for names and
        // specialties, then keeps only the doctors this endpoint says are bookable. Two authorized reads,
        // each from the service that owns the data, joined by the client that is entitled to both.
        read.MapGet("/booking/doctor-availability", async (
            Guid? branchId, BranchScopeState branch, IHbmpPrincipalAccessor me, EmrDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            var now = clock.GetUtcNow();
            var q = db.AppointmentSlots.AsNoTracking().Where(s => s.SlotStart > now && s.DoctorId != null);
            // Same branch rule as /branch-clinics — one implementation, three reach modes.
            q = q.ApplyBranchScope(s => s.BranchId, BranchModeOf(me), branch.Context, branchId);

            var taken = await ActiveHeldSlotIds(db, ct);
            var rows = await q.Select(s => new { s.SlotId, s.DoctorId, s.BranchId, s.SlotStart })
                .Take(5000).ToListAsync(ct);

            var doctors = rows
                .Where(r => !taken.Contains(r.SlotId))
                .GroupBy(r => new { DoctorId = r.DoctorId!.Value, r.BranchId })
                .Select(g => new DoctorAvailabilityResponse(
                    g.Key.DoctorId, g.Key.BranchId, g.Count(), g.Min(x => x.SlotStart)))
                .OrderBy(d => d.NextSlotStart)
                .ToList();
            return Results.Ok(doctors);
        });

        // GET /appointments/{id}/timeline — how this appointment got to where it is.
        //
        // Sourced from emr.appointment_history, which a row trigger has been filling since phase 3: every insert
        // and update snapshots the whole appointment as JSONB. That makes the timeline a read of data already
        // being kept rather than a new thing to maintain.
        //
        // Deliberately NOT the audit store. audit-service holds the hash-chained compliance record and requires
        // audit:read — Security/Compliance/DPO — because it spans every entity and carries before/after states.
        // The desk and the treating clinician need a far narrower thing: the status steps of ONE appointment. So
        // this serves exactly that, under the appointment:read they already hold, and only three fields per step
        // leave the service even though each snapshot contains the entire row.
        read.MapGet("/appointments/{id:guid}/timeline", async (
            Guid id, BranchScopeState branch, EmrDbContext db, CareTimelineWriter episode,
            CancellationToken ct) =>
        {
            // Same branch rule as reading the appointment itself — a timeline is a read of that appointment.
            if (await AppointmentEndpointsShared.DenyIfOutsideBranchAsync(id, branch, db, ct) is { } outOfScope)
                return outOfScope;

            var exists = await db.Appointments.AsNoTracking().AnyAsync(a => a.AppointmentId == id, ct);
            if (!exists) return Results.Problem(statusCode: 404, title: "Not Found",
                type: "https://mersal.foundation/problems/not-found");

            // TWO sources, merged (ADR-0031).
            //
            // The appointment's own STATUS history answers "booked, rescheduled, checked in" and nothing
            // else, because nothing that happens to the patient after arrival touches the appointment row.
            // The care episode answers the rest. A desk asking "why is this member still here at four
            // o'clock?" needs both, and needed them in one list — reading the visit's steps somewhere else
            // is how the question went unanswered.
            var status = await AppointmentTimeline.ReadAsync(db, id, ct);
            var care = await episode.ForAppointmentAsync(id, ct);

            var merged = status
                .Concat(care.Select(s => new TimelineRow(s.Step, s.OccurredAt, s.Actor, s.Source, s.Reference)))
                // Newest first, matching what AppointmentTimeline.ReadAsync already returns: whoever opens a
                // timeline is asking what just happened, not how it began.
                .OrderByDescending(r => r.At)
                .ToList();
            return Results.Ok(merged);
        });

        // POST /appointments — concurrency-safe booking (US-020).
        reserve.MapPost("/appointments", async (
            BookAppointmentRequest req, HttpRequest http, EmrDbContext db, AppointmentBookingService booking,
            ReminderDispatcher reminders, IPractitionerBranchDirectory practitioners, IMemberStatusProvider members,
            IAuditClient audit,
            IOutbox outbox, IHbmpPrincipalAccessor me, BranchScopeState branch, TimeProvider clock, CancellationToken ct) =>
        {
            var idem = http.Headers["Idempotency-Key"].ToString();
            if (string.IsNullOrWhiteSpace(idem))
                return Results.Problem(statusCode: 400, title: "Idempotency-Key header is required", type: "urn:hbmp:idempotency-required");
            if (!Enum.TryParse<AppointmentType>(req.AppointmentType, out var type))
                return Results.Problem(statusCode: 400, title: "Unknown appointment type", type: "urn:hbmp:invalid-appointment-type");
            // 14.5 — the booking note. Refused rather than truncated: quietly cutting an operator's note at
            // the cap loses the end of a sentence they believe they recorded, with nothing to tell them.
            var note = AppointmentNote.Normalize(req.Note);
            if (AppointmentNote.Refuse(note) is { } noteProblem)
                return Results.Problem(statusCode: 400, title: "note-too-long",
                    type: AppointmentNote.TooLongProblemType, detail: noteProblem);

            if (!AppointmentTypeLabels.LinkageSatisfied(type, req.ReferralRef, req.OriginEncounterId))
                return Results.Problem(statusCode: 400, type: "urn:hbmp:missing-linkage",
                    title: type == AppointmentType.Referral ? "Referral bookings require a referralRef (REF-*)"
                                                            : "Follow-up bookings require an originEncounterId");

            // 14.4/37 §3 — the booking's branch is decided HERE, server-side, before anything is written. A
            // BranchScoped desk books into its own active branch and may not name another; a call-centre agent
            // books into the branch it named. The row was previously created with branch_id NULL no matter who
            // booked it, which meant the reception board — which filters on exactly that column — could never
            // show a single appointment anyone had booked.
            var (bookingBranch, denied) = AppointmentEndpointsShared.ResolveBookingBranch(branch, req.BranchId);
            if (denied is not null) return denied;

            // 18.C2 (W7 / FR-BRN-027) — the second gate. Availability is not the only route to an appointment:
            // a walk-in is slotless, and a booking may name a doctor directly. Both bypass the slot table
            // entirely, so the check has to be repeated here rather than assumed from 026. It runs against the
            // RESOLVED branch, so a desk that names only a doctor is still checked against the branch it is
            // actually booking into.
            if (req.DoctorId is { } bookDoctorId && bookingBranch is { } bookBranchId)
            {
                // 25.3 — asked as at the date THIS appointment is for, not as at today. A booking made in July
                // for an October slot is judged against October: that is the whole difference between a gate
                // and a formality, and it is why the probe grew an `asOf`.
                var when = req.ScheduledStart
                    ?? (req.SlotId is { } probeSlotId
                        ? await db.AppointmentSlots.AsNoTracking()
                            .Where(x => x.SlotId == probeSlotId)
                            .Select(x => (DateTimeOffset?)x.SlotStart).FirstOrDefaultAsync(ct)
                        : null);
                var on = ClinicDateOf(when ?? clock.GetUtcNow());
                var bookability = await practitioners.BookabilityAsync(bookDoctorId, bookBranchId, on, ct);

                if (PractitionerBranchRules.Refuse(bookability.ServesBranch, bookDoctorId, bookBranchId) is { } reason)
                    return Results.Problem(statusCode: 422, title: "practitioner-not-at-branch",
                        type: PractitionerBranchRules.ProblemType, detail: reason);

                if (PractitionerBranchRules.RefuseExpiredLicence(
                        bookability.LicenceValid, bookability.LicenceExpiry, bookDoctorId, on) is { } licenceReason)
                    return Results.Problem(statusCode: 422, title: "practitioner-licence-expired",
                        type: PractitionerBranchRules.LicenceExpiredProblemType, detail: licenceReason);
            }

            // 14.5 — ELIGIBILITY. A suspended, expired or blocked member must not leave this call with an
            // appointment: the harm is that they travel to a clinic and are turned away, having been told by
            // us that they had a slot.
            //
            // Enforced HERE and not only in the booking screen. The screen does check, and should — telling
            // an operator at the point of search is far kinder than a refusal three steps later — but a UI
            // check is a courtesy, not a control: the call centre reaches this endpoint through its own
            // façade, and any client that skips the search step would otherwise book freely.
            //
            // UNKNOWN is allowed through, deliberately, and by the same reasoning as the practitioner probe
            // next to it: `GetStatusAsync` answers null when eligibility-service cannot be reached, and
            // refusing every booking on this platform because one sibling is briefly unavailable does more
            // harm than the occasional booking for a member whose status has lapsed — which check-in and the
            // visit gate will still catch before any care is given. Fail-open here is safe precisely BECAUSE
            // the same rule is enforced again at the door.
            var bearer = http.Headers.Authorization.FirstOrDefault();
            var memberStatus = await members.GetStatusAsync(req.BeneficiaryId, bearer, ct);
            if (memberStatus is { } known)
            {
                var gate = BookingGate.Evaluate(known);
                if (!gate.Allowed)
                {
                    await audit.EmitAsync(new AuditEventDraft
                    {
                        EntityType = "appointment", EntityId = req.BeneficiaryId.ToString(), Action = AuditAction.Decision,
                        ActorUserId = me.Principal?.Subject, DecisionOutcome = "ApptBookDeniedNotActive",
                        DecisionReasonCode = known.ToString(),
                    }, ct);
                    return Results.Problem(statusCode: 422, title: "member-not-active",
                        type: BookingGate.ProblemType, detail: gate.Guidance);
                }
            }

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
                BeneficiaryId = req.BeneficiaryId,
                // The SLOT is authoritative for where the appointment is, exactly as it is for the doctor
                // below: it is what the availability rule assigned. The call-centre façade sends only a
                // beneficiary, a slot and a branch — everything else it would have to guess — and trusting
                // req.* there wrote appointments with an all-zero provider and location while holding a real
                // slot: a booking that claimed to be at no clinic.
                ProviderId = slot?.ProviderId ?? req.ProviderId,
                LocationId = slot?.LocationId ?? req.LocationId,
                BranchId = bookingBranch,
                SlotId = slot?.SlotId,
                // The slot is the authority when there is one — it is what the availability rule assigned the
                // practitioner to. A slotless walk-in has only what the caller stated. Without this the
                // doctor's own worklist has nothing to filter on (migration 0009).
                DoctorId = slot?.DoctorId ?? req.DoctorId,
                AppointmentType = type, Status = AppointmentStatus.Booked,
                ScheduledStart = slot?.SlotStart ?? req.ScheduledStart ?? now,
                ScheduledEnd = slot?.SlotEnd ?? req.ScheduledEnd ?? now.AddMinutes(15),
                ReferralRef = req.ReferralRef, OriginEncounterId = req.OriginEncounterId,
                Note = note,
                // Trimmed to null when blank: an empty string here would make the board render an empty name
                // cell for a patient who does have one, which reads as data loss rather than as absence.
                BeneficiaryName = string.IsNullOrWhiteSpace(req.BeneficiaryName) ? null : req.BeneficiaryName.Trim(),
                // Attribution rides WITH the note: stamped only when there is one, so an appointment with no
                // note does not claim an author for it.
                NoteBy = note is null ? null : actor,
                NoteAt = note is null ? null : now,
                IdempotencyKey = idem, CreatedBy = actor, CreatedAt = now, UpdatedAt = now,
            };

            // 24.x — the appointment and the events announcing it commit together. Enqueued after the
            // fact, a crash held the slot with nothing downstream told: a patient booked into a slot
            // no board shows, and a referral marked scheduled that the referring clinician never sees.
            // The idempotent REPLAY path must not re-enqueue, which is why the guard is inside.
            var result = await booking.BookAsync(appt,
                insideTransaction: async (b, c) =>
                {
                    if (b.AppointmentId != appt.AppointmentId) return;   // replay: no new side-effects
                    await outbox.EnqueueAsync("ApptBooked", "emr.events", new
                    {
                        appointmentId = b.AppointmentId, beneficiaryId = b.BeneficiaryId,
                        providerId = b.ProviderId, locationId = b.LocationId,
                        slotId = b.SlotId, appointmentType = type.ToString(),
                        scheduledStart = b.ScheduledStart, scheduledEnd = b.ScheduledEnd,
                    }, c);
                    if (type == AppointmentType.Referral && b.ReferralRef is { Length: > 0 } r)
                        await outbox.EnqueueAsync("ReferralScheduled", "emr.events",
                            new { referralRef = r, appointmentId = b.AppointmentId }, c);
                },
                ct);
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
                    // The note's PRESENCE is audited, never its text. It is free text written at a desk and
                    // read by a clinician, so copying it into the hash-chained compliance store would put
                    // whatever an operator typed — including anything they should not have — into the one
                    // record that is deliberately immutable and cannot be corrected.
                    AfterState = $"{{\"type\":\"{type}\",\"slotId\":\"{booked.SlotId}\",\"hasNote\":{(booked.Note is null ? "false" : "true")}}}",
                }, ct);
                // ApptBooked + ReferralScheduled committed with the booking above.

                // Fire a booking reminder now (in-app live; SMS/WhatsApp are stubs). Honors preferred channel.
                var preferred = Enum.TryParse<ReminderChannel>(req.PreferredChannel, out var pc) ? pc : ReminderChannel.InApp;
                await reminders.DispatchAsync(booked.TenantId, booked.AppointmentId, booked.BeneficiaryId, booked.ProviderId,
                    booked.ScheduledStart, ReminderKind.Booked, preferred, ct);
            }

            return Results.Created($"/api/v1/appointments/{booked.AppointmentId}", AppointmentResponse.From(booked));
        });

        // GET /appointments — reception's day board (US-020). Defaults to today's appointments; an optional
        // ?status= filters to a single status (e.g. Scheduled for the check-in worklist). Ordered by start time.
        read.MapGet("/appointments", async (
            DateTimeOffset? date, DateTimeOffset? from, DateTimeOffset? to,
            string? status, Guid? branchId, bool? mine, BranchScopeState branch,
            IHbmpPrincipalAccessor me, EmrDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            // The Cairo civil day, not the UTC one — the board renders Cairo times, so it must select by them
            // (AppointmentDay explains the two-hour mismatch this replaces). Normalized to UTC for the query:
            // it is the same instant either way, but Npgsql rejects a non-zero offset on timestamptz.
            //
            // 14.5 — a RANGE, when the desk asks for one. `from`/`to` are inclusive CIVIL days expanded the
            // same way a single date is, so "Sun to Thu" covers Thursday's evening clinic rather than
            // stopping at Thursday 00:00. `date` remains the default and the single-day case, so every
            // existing caller is unaffected; passing both is not an error, the range simply wins because it
            // is the more specific request.
            DateTimeOffset lo, hi;
            if (from is { } f)
            {
                if (to is { } t0 && t0 < f)
                    return Results.Problem(statusCode: 400, title: "to must not precede from",
                        type: "urn:hbmp:invalid-range");
                var range = AppointmentDay.CairoRange(f, to ?? f, CairoOffset);
                lo = range.Start.ToUniversalTime();
                hi = range.End.ToUniversalTime();
            }
            else
            {
                var window = AppointmentDay.CairoWindow(date ?? clock.GetUtcNow(), CairoOffset);
                lo = window.Start.ToUniversalTime();
                hi = window.End.ToUniversalTime();
            }
            var q = db.Appointments.AsNoTracking().Where(a => a.ScheduledStart >= lo && a.ScheduledStart < hi);
            if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<AppointmentStatus>(status, ignoreCase: true, out var st))
                q = q.Where(a => a.Status == st);
            // 14.4 — BranchScoped callers see ONLY their active branch; 25.1 — a SET-scoped clinics manager
            // sees every branch they hold a grant to; member-scoped may optionally filter.
            q = q.ApplyBranchScope(a => a.BranchId, BranchModeOf(me), branch.Context, branchId);

            // ?mine=true — the doctor's OWN list. Narrows to appointments assigned to the caller, and does so
            // from the TOKEN's subject, never from a client-supplied id: a doctor asking for "my visits" must
            // not be able to ask for someone else's by changing a query parameter. An unparseable subject
            // yields no rows rather than everyone's (default-deny).
            if (mine == true)
            {
                var subject = me.Principal?.Subject;
                q = Guid.TryParse(subject, out var meId)
                    ? q.Where(a => a.DoctorId == meId)
                    : q.Where(_ => false);
            }
            var rows = await q.OrderBy(a => a.ScheduledStart).Take(200).ToListAsync(ct);

            // 14.5 — the patient's NAME on every row that has one.
            //
            // Two sources, in order: the appointment's own `beneficiary_name`, captured at BOOKING (0013),
            // and — for rows booked before that column existed — the queue ticket written at check-in. emr
            // holds no beneficiary demographics and never fetches them from a sibling to fill a gap; both
            // sources are values an operator already had in hand and handed over.
            var ids = rows.Select(a => a.AppointmentId).ToList();
            var names = await db.Set<QueueTicket>().AsNoTracking()
                .Where(t => ids.Contains(t.AppointmentId) && t.DisplayName != null)
                .Select(t => new { t.AppointmentId, t.DisplayName })
                .ToListAsync(ct);
            var nameByAppt = names
                .GroupBy(x => x.AppointmentId)
                .ToDictionary(g => g.Key, g => g.First().DisplayName);

            // The board's no-show button comes from the server's clock, not the browser's.
            var asOf = clock.GetUtcNow();
            return Results.Ok(rows.Select(a =>
                AppointmentResponse.From(a, asOf, a.BeneficiaryName ?? nameByAppt.GetValueOrDefault(a.AppointmentId))));
        });

        // GET /appointments/reassignment-needed — the OTHER half of the licence worklist (25.3).
        //
        // Design 42 §3: existing future appointments are FLAGGED, never cancelled, and a person decides who
        // covers the clinic. This is where that person looks. Without it, `reassignment_needed_at` is a column
        // that gets written and never read — which is the same as not flagging at all, except that it looks
        // like a control.
        //
        // Carries the patient's CONTACT affordances (name + the appointment reference) because the action is
        // to ring them. It carries no clinical content: this is a scheduling problem, and emr's other
        // min-necessary projections are not relaxed for a worklist.
        read.MapGet("/appointments/reassignment-needed", async (
            Guid? branchId, Guid? doctorId, BranchScopeState branch, IHbmpPrincipalAccessor me,
            EmrDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            var now = clock.GetUtcNow();
            var q = db.Appointments.AsNoTracking()
                .Where(a => a.ReassignmentNeededAt != null
                            // FUTURE only. A past appointment already happened; asking reception to reassign
                            // it buries the ones they can still do something about.
                            && a.ScheduledStart > now
                            && (a.Status == AppointmentStatus.Booked || a.Status == AppointmentStatus.CheckedIn));

            if (doctorId is { } d) q = q.Where(a => a.DoctorId == d);
            q = q.ApplyBranchScope(a => a.BranchId, BranchModeOf(me), branch.Context, branchId);

            var rows = await q.OrderBy(a => a.ScheduledStart).Take(500).ToListAsync(ct);
            return Results.Ok(new
            {
                asOf = now,
                count = rows.Count,
                appointments = rows.Select(a => new
                {
                    a.AppointmentId,
                    a.BeneficiaryId,
                    a.BranchId,
                    a.DoctorId,
                    a.ScheduledStart,
                    a.ScheduledEnd,
                    Status = a.Status.ToString(),
                    a.ReassignmentNeededAt,
                    a.BeneficiaryName,
                }),
            });
        });

        // GET /appointments/summary — the three counts the reception dashboard's cards show.
        //
        // Counted HERE rather than by tallying the board client-side. The board is capped at 200 rows, so a
        // busy branch would have produced cards that quietly disagreed with reality — and disagreed in the
        // direction of "fewer than there are", which is the direction nobody notices. It is also three
        // integers instead of two hundred rows for a number.
        read.MapGet("/appointments/summary", async (
            DateTimeOffset? date, Guid? branchId, BranchScopeState branch, IHbmpPrincipalAccessor me,
            EmrDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            var window = AppointmentDay.CairoWindow(date ?? clock.GetUtcNow(), CairoOffset);
            var lo = window.Start.ToUniversalTime();
            var hi = window.End.ToUniversalTime();

            var q = db.Appointments.AsNoTracking().Where(a => a.ScheduledStart >= lo && a.ScheduledStart < hi);
            // Same branch rule as the board it summarises — a card that counted other branches' appointments
            // would be a scoping hole dressed as a statistic.
            q = q.ApplyBranchScope(a => a.BranchId, BranchModeOf(me), branch.Context, branchId);

            // One round trip, grouped in the database.
            var byStatus = await q.GroupBy(a => a.Status)
                .Select(g => new { Status = g.Key, Count = g.Count() })
                .ToListAsync(ct);

            int Of(AppointmentStatus s) => byStatus.FirstOrDefault(x => x.Status == s)?.Count ?? 0;
            return Results.Ok(new AppointmentSummaryResponse(
                Total: byStatus.Sum(x => x.Count),
                CheckedIn: Of(AppointmentStatus.CheckedIn),
                NoShow: Of(AppointmentStatus.NoShow)));
        });

        read.MapGet("/appointments/{id:guid}", async (Guid id, HttpResponse resp, BranchScopeState branch, EmrDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            var a = await db.Appointments.AsNoTracking().FirstOrDefaultAsync(x => x.AppointmentId == id, ct);
            if (a is null) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");
            // 14.4 — a BranchScoped caller reaching a row in another branch is DENIED (not 404-empty).
            if (branch.Context.ActiveBranchId is { } active && a.BranchId is not null && a.BranchId != active)
                return Results.Problem(statusCode: 403, title: "branch-scope-denied", detail: "this appointment is not in your active branch");
            resp.Headers.ETag = $"\"{a.RowVersion}\"";   // clients echo this back as If-Match on transitions
            return Results.Ok(AppointmentResponse.From(a, clock.GetUtcNow()));
        });

        // POST /appointments/{id}/reschedule — atomic release-old + book-new (US-021).
        reserve.MapPost("/appointments/{id:guid}/reschedule", async (
            Guid id, RescheduleRequest req, HttpRequest http, EmrDbContext db, AppointmentTransitionService transitions,
            IdempotencyStore idem, IAuditClient audit, IOutbox outbox, IHbmpPrincipalAccessor me,
            BranchScopeState branch, TimeProvider clock, CancellationToken ct) =>
        {
            // 14.4 — the read endpoints refused cross-branch rows; the writes did not, so knowing an id was
            // enough to act on another branch's appointment.
            if (await AppointmentEndpointsShared.DenyIfOutsideBranchAsync(id, branch, db, ct) is { } outOfScope)
                return await AuditAndReturn(outOfScope, audit, me, "BranchScopeDenied", id, TransitionOutcome.NotFound, ct);

            var (replay, key) = await CheckIdempotency(http, idem, db, ct);
            if (replay is not null) return replay;

            // 24.x — the move and the event announcing it commit together. Split, a crash leaves the new
            // slot held with every downstream board still showing the old time.
            var result = await transitions.RescheduleAsync(id, req.NewSlotId, IfMatch(http), clock.GetUtcNow(), me.Principal?.Subject,
                insideTransaction: async (appt, c) =>
                {
                    await outbox.EnqueueAsync("ApptRescheduled", "emr.events",
                        new { appointmentId = id, newSlotId = appt.SlotId, scheduledStart = appt.ScheduledStart }, c);
                },
                ct);
            var problem = MapFailure(result.Outcome);
            if (problem is not null) return await AuditAndReturn(problem, audit, me, "ApptRescheduleDenied", id, result.Outcome, ct);

            var appt = result.Appointment!;
            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "appointment", EntityId = id.ToString(), Action = AuditAction.StateChange,
                ActorUserId = me.Principal?.Subject, DecisionOutcome = "ApptRescheduled",
                AfterState = $"{{\"slotId\":\"{appt.SlotId}\"}}",
            }, ct);
            await Record(idem, key, "reschedule", id, 200, db, ct);   // the event committed with the move
            return Results.Ok(AppointmentResponse.From(appt));
        });

        // POST /appointments/{id}/cancel — release slot + reason; promote waitlist (US-021).
        reserve.MapPost("/appointments/{id:guid}/cancel", async (
            Guid id, CancelRequest req, HttpRequest http, EmrDbContext db, AppointmentTransitionService transitions,
            IdempotencyStore idem, IAuditClient audit, IOutbox outbox, IHbmpPrincipalAccessor me,
            BranchScopeState branch, TimeProvider clock, CancellationToken ct) =>
        {
            // 14.4 — the read endpoints refused cross-branch rows; the writes did not, so knowing an id was
            // enough to act on another branch's appointment.
            if (await AppointmentEndpointsShared.DenyIfOutsideBranchAsync(id, branch, db, ct) is { } outOfScope)
                return await AuditAndReturn(outOfScope, audit, me, "BranchScopeDenied", id, TransitionOutcome.NotFound, ct);

            var (replay, key) = await CheckIdempotency(http, idem, db, ct);
            if (replay is not null) return replay;

            // 24.x — cancelling FREES A SLOT. If the event is lost the slot is bookable here and still
            // held everywhere that listens, which is how one slot ends up with two patients.
            var result = await transitions.CancelAsync(id, req.Reason, IfMatch(http), clock.GetUtcNow(), me.Principal?.Subject,
                insideTransaction: async (_, promoted, c) =>
                {
                    await outbox.EnqueueAsync("ApptCancelled", "emr.events",
                        new { appointmentId = id, reason = req.Reason }, c);
                    // The waitlist promotion happened inside this same transaction, so its event belongs
                    // here too — a promoted patient nobody was told about keeps waiting.
                    if (promoted is { } w)
                        await outbox.EnqueueAsync("ApptWaitlistPromoted", "emr.events",
                            new { waitlistId = w.WaitlistId, beneficiaryId = w.BeneficiaryId, providerId = w.ProviderId }, c);
                },
                ct);
            var problem = MapFailure(result.Outcome);
            if (problem is not null) return await AuditAndReturn(problem, audit, me, "ApptCancelDenied", id, result.Outcome, ct);

            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "appointment", EntityId = id.ToString(), Action = AuditAction.StateChange,
                ActorUserId = me.Principal?.Subject, DecisionOutcome = "ApptCancelled", DecisionReasonCode = req.Reason,
            }, ct);
            await Record(idem, key, "cancel", id, 200, db, ct);
            return Results.Ok(AppointmentResponse.From(result.Appointment!));
        });

        // POST /appointments/{id}/note — amend the general/administrative booking note.
        //
        // On the RESERVE group, not the desk-only one: the call centre writes these notes and must be able to
        // correct one it took down wrong mid-call. It is not a clinical write and never becomes one — the same
        // cap and the same refusal-rather-than-truncation as at booking (AppointmentNote, migration 0011).
        //
        // The edit lands in emr.appointment_history through the row trigger, and AppointmentTimeline emits it
        // as a `NoteEdited` step — so "who changed this and when" is answerable, which is the whole reason an
        // edit is allowed at all rather than requiring a cancel-and-rebook.
        reserve.MapPost("/appointments/{id:guid}/note", async (
            Guid id, UpdateBookingNoteRequest req, EmrDbContext db, IAuditClient audit,
            IHbmpPrincipalAccessor me, BranchScopeState branch, TimeProvider clock, CancellationToken ct) =>
        {
            if (await AppointmentEndpointsShared.DenyIfOutsideBranchAsync(id, branch, db, ct) is { } outOfScope)
                return outOfScope;

            var note = AppointmentNote.Normalize(req.Note);
            if (AppointmentNote.Refuse(note) is { } problem)
                return Results.Problem(statusCode: 400, title: "note-too-long",
                    type: AppointmentNote.TooLongProblemType, detail: problem);

            var appt = await db.Appointments.FirstOrDefaultAsync(a => a.AppointmentId == id, ct);
            if (appt is null) return Results.Problem(statusCode: 404, title: "Not Found",
                type: "https://mersal.foundation/problems/not-found");

            var editedAt = clock.GetUtcNow();
            appt.Note = note;
            // Cleared along with the note: attribution for a note that no longer exists would be a claim
            // about text nobody can read.
            appt.NoteBy = note is null ? null : me.Principal?.Subject;
            appt.NoteAt = note is null ? null : editedAt;
            appt.UpdatedAt = editedAt;
            appt.UpdatedBy = me.Principal?.Subject;
            await db.SaveChangesAsync(ct);

            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "appointment", EntityId = id.ToString(), Action = AuditAction.Update,
                ActorUserId = me.Principal?.Subject, DecisionOutcome = "ApptNoteEdited",
                // Presence, never the text — the same rule as at booking. Free text an operator typed must not
                // be copied into the hash-chained store, which cannot be corrected afterwards.
                AfterState = $"{{\"hasNote\":{(note is null ? "false" : "true")}}}",
            }, ct);

            return Results.Ok(AppointmentResponse.From(appt, clock.GetUtcNow(), appt.BeneficiaryName));
        });

        // POST /appointments/{id}/no-show — guarded; reporting flag + backfill (US-022).
        write.MapPost("/appointments/{id:guid}/no-show", async (
            Guid id, HttpRequest http, EmrDbContext db, AppointmentTransitionService transitions,
            IdempotencyStore idem, IAuditClient audit, IOutbox outbox, IHbmpPrincipalAccessor me,
            BranchScopeState branch, TimeProvider clock, CancellationToken ct) =>
        {
            // 14.4 — the read endpoints refused cross-branch rows; the writes did not, so knowing an id was
            // enough to act on another branch's appointment.
            if (await AppointmentEndpointsShared.DenyIfOutsideBranchAsync(id, branch, db, ct) is { } outOfScope)
                return await AuditAndReturn(outOfScope, audit, me, "BranchScopeDenied", id, TransitionOutcome.NotFound, ct);

            var (replay, key) = await CheckIdempotency(http, idem, db, ct);
            if (replay is not null) return replay;

            // 24.x — a no-show frees the slot AND may cross the repeat threshold that hands the
            // beneficiary to a case manager. Both events belong to the same commit as the transition.
            var result = await transitions.NoShowAsync(id, IfMatch(http), clock.GetUtcNow(), NoShowGrace, me.Principal?.Subject,
                insideTransaction: async (a, noShowCount, promoted, c) =>
                {
                    await outbox.EnqueueAsync("ApptNoShow", "emr.events",
                        // `locationId` — the clinic the slot belonged to, for the read model's no-show rate
                        // per clinic. A no-show count with no clinic cannot answer the question it exists for.
                        new { appointmentId = id, beneficiaryId = a.BeneficiaryId, noShowCount, locationId = a.LocationId }, c);
                    // Repeat no-shows → Case Manager follow-up (05 X3). Same commit: a beneficiary who
                    // crossed the threshold and whose event was lost is one nobody follows up.
                    if (noShowCount >= RepeatNoShowThreshold)
                        await outbox.EnqueueAsync("BeneficiaryNoShowThresholdReached", "emr.events",
                            new { beneficiaryId = a.BeneficiaryId, noShowCount }, c);
                    if (promoted is { } w)
                        await outbox.EnqueueAsync("ApptWaitlistPromoted", "emr.events",
                            new { waitlistId = w.WaitlistId, beneficiaryId = w.BeneficiaryId, providerId = w.ProviderId }, c);
                },
                ct);
            var problem = MapFailure(result.Outcome);
            if (problem is not null) return await AuditAndReturn(problem, audit, me, "ApptNoShowDenied", id, result.Outcome, ct);

            var appt = result.Appointment!;
            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "appointment", EntityId = id.ToString(), Action = AuditAction.StateChange,
                ActorUserId = me.Principal?.Subject, DecisionOutcome = "ApptNoShow",
            }, ct);
            await Record(idem, key, "no-show", id, 200, db, ct);
            return Results.Ok(AppointmentResponse.From(appt));
        });
    }

    private const int RepeatNoShowThreshold = 3;
    // The grace period is a DOMAIN rule (AppointmentWorkflow), not an endpoint detail: the board renders a
    // no-show button from the same constant the transition enforces.
    private static TimeSpan NoShowGrace => AppointmentWorkflow.NoShowGrace;

    private static uint? IfMatch(HttpRequest http) => AppointmentEndpointsShared.IfMatch(http);

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

    private static IResult? MapFailure(TransitionOutcome outcome) => AppointmentEndpointsShared.MapFailure(outcome);

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
