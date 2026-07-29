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
                new { appointmentId = id, beneficiaryId = result.Appointment!.BeneficiaryId }, ct);
            return Results.Ok(AppointmentResponse.From(result.Appointment!));
        });

        // GET /queues — ordered, minimum-necessary queue for a clinic/doctor.
        read.MapGet("/queues", async (
            Guid locationId, Guid providerId, Guid? doctorId, BranchScopeState branch, EmrDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            var now = clock.GetUtcNow();
            var q = db.Set<QueueTicket>().AsNoTracking()
                .Where(t => t.LocationId == locationId && t.ProviderId == providerId
                            && (doctorId == null || t.DoctorId == doctorId)
                            && t.State == QueueTicketState.Waiting);
            // 14.4 — BranchScoped callers see only their active branch's queue.
            if (branch.Context.ActiveBranchId is { } active) q = q.Where(t => t.BranchId == active);
            var tickets = await q.ToListAsync(ct);
            var ordered = QueueRules.Ordered(tickets).ToList();
            return Results.Ok(ordered.Select((t, i) => QueueItemView.From(t, i + 1, now)));
        });

        // POST /queues/call-next — pop the head (Waiting→InConsultation).
        write.MapPost("/queues/call-next", async (
            Guid locationId, Guid providerId, Guid? doctorId, EmrDbContext db, IAuditClient audit,
            IHbmpPrincipalAccessor me, TimeProvider clock, CancellationToken ct) =>
        {
            var now = clock.GetUtcNow();
            var waiting = await db.Set<QueueTicket>()
                .Where(t => t.LocationId == locationId && t.ProviderId == providerId
                            && (doctorId == null || t.DoctorId == doctorId)
                            && t.State == QueueTicketState.Waiting)
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
        });

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
                await reminders.DispatchAsync(a.AppointmentId, a.BeneficiaryId, a.ProviderId, a.ScheduledStart,
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
