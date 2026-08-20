using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Auth.Authorization;
using Mersal.Authz;
using Mersal.Emr.Domain;
using Mersal.Emr.Infrastructure;
using Mersal.Events;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Emr.Api;

/// <summary>Phase 3.3 — reception walk-in queue + reminders. Check-in enqueues a min-necessary ticket;
/// reception calls-next / requeues / removes; the queue stays consistent with appointment status (cancel /
/// no-show remove tickets — handled in <see cref="AppointmentTransitionService"/>). Reminders fire in-app now
/// with SMS/WhatsApp stubs behind the same <see cref="IReminderChannel"/> interface.</summary>
public static class QueueModule
{
    /// <summary>25.1 — the caller's branch reach mode. See <see cref="BranchQueryScope"/>: asking about
    /// ActiveBranchId directly is correct for two modes and silently unrestricted for the third.</summary>
    private static ScopeMode BranchModeOf(IHbmpPrincipalAccessor me) =>
        me.Principal is null ? ScopeMode.MemberScoped : BranchScopeModes.ModeFor(me.Principal);

    public static void MapQueue(this WebApplication app)
    {
        var write = app.MapGroup("/api/v1").RequireAuthorization(HbmpPolicies.Scope("appointment:write"));
        var read = app.MapGroup("/api/v1").RequireAuthorization(HbmpPolicies.Scope("appointment:read"));

        // POST /appointments/{id}/check-in — Booked→CheckedIn + enqueue.
        write.MapPost("/appointments/{id:guid}/check-in", async (
            Guid id, CheckInRequest req, HttpRequest http, AppointmentTransitionService transitions,
            IAuditClient audit, IOutbox outbox, IHbmpPrincipalAccessor me, BranchScopeState branch,
            EmrDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            // 14.4 — a desk may only check in its own branch's arrivals.
            if (await AppointmentEndpointsShared.DenyIfOutsideBranchAsync(id, branch, db, ct) is { } outOfScope)
            {
                await audit.EmitAsync(new AuditEventDraft
                {
                    EntityType = "appointment", EntityId = id.ToString(), Action = AuditAction.Decision,
                    ActorUserId = me.Principal?.Subject, DecisionOutcome = "BranchScopeDenied",
                }, ct);
                return outOfScope;
            }

            // CheckInAsync flips the appointment AND issues the queue ticket, but owns no transaction of its
            // own — unlike Book/Reschedule/Cancel/NoShow, which take an insideTransaction callback because
            // they do. So the handler opens one and the check-in joins it: the state change and ApptCheckedIn
            // commit together, and a failure leaves the person neither checked in nor announced as checked in.
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var result = await transitions.CheckInAsync(id, req.MemberNo, req.DisplayName, req.Priority,
                AppointmentEndpointsShared.IfMatch(http), clock.GetUtcNow(), me.Principal?.Subject, ct);
            var problem = AppointmentEndpointsShared.MapFailure(result.Outcome);
            if (problem is not null) return problem;

            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "appointment", EntityId = id.ToString(), Action = AuditAction.StateChange,
                ActorUserId = me.Principal?.Subject, DecisionOutcome = "ApptCheckedIn",
            }, ct);
            await outbox.EnqueueAsync("ApptCheckedIn", "emr.events",
                new
                {
                    // The reporting consumer binds RLS from the envelope and dead-letters what it cannot
                    // attribute, so a check-in without this never became a fact.
                    tenantId = me.Principal?.TenantId,
                    appointmentId = id, beneficiaryId = result.Appointment!.BeneficiaryId,
                    // The clinic, for the read model's per-clinic encounter counts.
                    locationId = result.Appointment!.LocationId,
                }, ct);
            await tx.CommitAsync(ct);
            return Results.Ok(AppointmentResponse.From(result.Appointment!));
        })
        .Produces<AppointmentResponse>();

        // GET /queues — ordered, minimum-necessary queue for a clinic/doctor.
        // 32.6 — locationId and providerId are OPTIONAL now, and that is why this endpoint had no caller.
        //
        // They were mandatory Guids, so the only answerable question was "who is waiting for THIS provider at
        // THIS location". A reception desk has neither in hand: it knows its branch. The desk therefore could
        // not ask, so nothing asked, so tickets issued on every check-in were never read by anything —
        // accumulating in Waiting for ever while the board beside them showed the same people from the
        // appointments table.
        //
        // Dropping the filters is safe for a BranchScoped caller because ApplyBranchScope narrows the answer
        // to their branch either way. It is NOT safe for a MemberScoped one: `ApplyBranchScope` is
        // deliberately unrestricted for that mode, so an unfiltered call from the call centre — which holds
        // appointment:read — would list every person waiting in every branch on the platform. That is refused
        // below rather than silently served, because widening a disclosure as a side effect of removing a
        // required parameter is exactly the kind of change nobody reviews as a disclosure change.
        read.MapGet("/queues", async (
            Guid? locationId, Guid? providerId, Guid? doctorId, BranchScopeState branch, IHbmpPrincipalAccessor me, EmrDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            // IsBranchRestricted, not `== BranchScoped`. A clinics manager is BranchSetScoped — narrowed to
            // the branches they hold a grant to — and ApplyBranchScope narrows them correctly; asking for the
            // single-branch mode alone would refuse them a queue they are entitled to. BranchScope.cs names
            // this exact mistake, in the other direction, as the one this helper exists to prevent.
            if (locationId is null && providerId is null
                && !BranchScopeModes.IsBranchRestricted(BranchModeOf(me)))
                return Results.Problem(
                    statusCode: 422, title: "queue-scope-required", type: "urn:hbmp:queue-scope-required",
                    detail: "A caller who is not narrowed to a branch must name a location or provider. "
                          + "Without one this would be the whole platform's waiting room.");

            var now = clock.GetUtcNow();
            var q = db.Set<QueueTicket>().AsNoTracking()
                .Where(t => (locationId == null || t.LocationId == locationId)
                            && (providerId == null || t.ProviderId == providerId)
                            && (doctorId == null || t.DoctorId == doctorId)
                            && t.State == QueueTicketState.Waiting);
            // 14.4 — BranchScoped callers see only their active branch's queue; 25.1 — a set-scoped clinics
            // manager sees every branch they hold a grant to.
            q = q.ApplyBranchScope(t => t.BranchId, BranchModeOf(me), branch.Context);
            var tickets = await q.ToListAsync(ct);
            var ordered = QueueRules.Ordered(tickets).ToList();
            return Results.Ok(ordered.Select((t, i) => QueueItemView.From(t, i + 1, now)));
        })
        .Produces<IEnumerable<QueueItemView>>();

        // POST /queues/call-next — pop the head (Waiting→InConsultation).
        // 32.6 — the same optionality as the read above, and the same refusal for an unnarrowed caller. The
        // head of "every branch's queue" is not a person anybody at a desk is about to call.
        write.MapPost("/queues/call-next", async (
            Guid? locationId, Guid? providerId, Guid? doctorId, BranchScopeState branch, EmrDbContext db,
            IAuditClient audit, IHbmpPrincipalAccessor me, TimeProvider clock, CancellationToken ct) =>
        {
            if (locationId is null && providerId is null
                && !BranchScopeModes.IsBranchRestricted(BranchModeOf(me)))
                return Results.Problem(
                    statusCode: 422, title: "queue-scope-required", type: "urn:hbmp:queue-scope-required",
                    detail: "A caller who is not narrowed to a branch must name a location or provider.");

            var now = clock.GetUtcNow();
            var waiting = await db.Set<QueueTicket>()
                .Where(t => (locationId == null || t.LocationId == locationId)
                            && (providerId == null || t.ProviderId == providerId)
                            && (doctorId == null || t.DoctorId == doctorId)
                            && t.State == QueueTicketState.Waiting)
                // The branch narrowing the READ has always applied, applied to the WRITE as well. Calling the
                // next patient is the act that moves somebody, and it must not reach across a branch boundary
                // the same request could not read across.
                .ApplyBranchScope(t => t.BranchId, BranchModeOf(me), branch.Context)
                .ToListAsync(ct);
            var head = QueueRules.Ordered(waiting).FirstOrDefault();
            if (head is null) return Results.NoContent();

            head.State = QueueTicketState.InConsultation;
            head.CalledAt = now;
            await db.SaveChangesAsync(ct);
            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "queue_ticket", EntityId = head.QueueId.ToString(), Action = AuditAction.StateChange,
                ActorUserId = me.Principal?.Subject, DecisionOutcome = "QueueCallNext",
            }, ct);
            return Results.Ok(QueueItemView.From(head, 0, now));
        })
        .Produces<QueueItemView>();

        // POST /queues/{queueId}/requeue — send back to Waiting.
        write.MapPost("/queues/{queueId:guid}/requeue", (Guid queueId, EmrDbContext db, TimeProvider clock, CancellationToken ct) =>
            MutateTicket(queueId, QueueRules.CanRequeue, t => { t.State = QueueTicketState.Waiting; t.CalledAt = null; t.EnqueuedAt = clock.GetUtcNow(); }, db, ct));

        // POST /queues/{queueId}/remove — drop from the queue.
        write.MapPost("/queues/{queueId:guid}/remove", (Guid queueId, EmrDbContext db, TimeProvider clock, CancellationToken ct) =>
            MutateTicket(queueId, QueueRules.CanRemove, t => t.State = QueueTicketState.Removed, db, ct));

        // POST /queues/{queueId}/complete — consultation finished (InConsultation→Done).
        write.MapPost("/queues/{queueId:guid}/complete", (Guid queueId, EmrDbContext db, TimeProvider clock, CancellationToken ct) =>
            MutateTicket(queueId, s => s == QueueTicketState.InConsultation, t => t.State = QueueTicketState.Done, db, ct));

        // POST /appointments/reminders/run?withinMinutes=60 — fire Upcoming reminders for imminent bookings.
        write.MapPost("/appointments/reminders/run", async (
            int? withinMinutes, EmrDbContext db, ReminderDispatcher reminders, TimeProvider clock, CancellationToken ct) =>
        {
            var now = clock.GetUtcNow();
            var horizon = now.AddMinutes(withinMinutes ?? 60);
            var due = await db.Appointments.AsNoTracking()
                .Where(a => a.Status == AppointmentStatus.Booked
                            && a.ScheduledStart >= now && a.ScheduledStart <= horizon)
                .ToListAsync(ct);
            foreach (var a in due)
                await reminders.DispatchAsync(a.TenantId, a.AppointmentId, a.BeneficiaryId, a.ProviderId, a.ScheduledStart,
                    ReminderKind.Upcoming, ReminderChannel.InApp, ct);
            return Results.Ok(new { fired = due.Count });
        });
    }

    private static async Task<IResult> MutateTicket(
        Guid queueId, Func<QueueTicketState, bool> guard, Action<QueueTicket> mutate, EmrDbContext db, CancellationToken ct)
    {
        var t = await db.Set<QueueTicket>().FirstOrDefaultAsync(x => x.QueueId == queueId, ct);
        if (t is null) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");
        if (!guard(t.State))
            return Results.Problem(statusCode: 409, title: "Queue action not allowed", type: "urn:hbmp:queue-transition-denied");
        mutate(t);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { t.QueueId, state = t.State.ToString() });
    }
}
