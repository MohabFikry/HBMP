using Mersal.Audit.Client;
using Mersal.Auth.Authorization;
using Mersal.Authz;
using Mersal.CallCentre.Domain;
using Mersal.CallCentre.Infrastructure;

namespace Mersal.CallCentre.Api;

/// <summary>Phase 15.3 — appointment book / reschedule / cancel FROM the call, delegated to the emr engine (the
/// no-double-book invariant, Idempotency-Key and If-Match all live in emr and are preserved). Every action requires
/// the interaction to be verified for the bound beneficiary (else 403 + audit); cancel requires a reason code (else
/// 422). Each successful change is linked to the call_interaction, audited by call_ref, and triggers the existing
/// notification confirmation. Branch + specialty are SELECTORS on slot discovery, never restrictions (MemberScoped).</summary>
public static class CallAppointments
{
    public static void MapCallAppointments(this IEndpointRouteBuilder app)
    {
        var v1 = app.MapGroup("/api/v1/call-centre");

        // --- Slot discovery across ALL branches (branch/specialty are selectors) ----------------------------
        v1.MapGet("/slots", async (HttpRequest http, CallDeps deps, IAppointmentGateway emr, CancellationToken ct) =>
        {
            var denied = await deps.Gate.CheckAsync(CallCentrePolicies.Read, "slot-discovery", ct);
            if (denied is not null) return denied;
            var qs = http.QueryString.HasValue ? http.QueryString.Value : "";
            var result = await emr.SearchSlotsAsync(qs ?? "", http.Headers.Authorization, ct);
            return Passthrough(result);
        }).RequireAuthorization(HbmpPolicies.Scope("callcentre:read"));

        // --- Book -------------------------------------------------------------------------------------------
        v1.MapPost("/appointments", async (BookFromCallRequest req, HttpRequest http, CallDeps deps, IAppointmentGateway emr, CancellationToken ct) =>
        {
            var denied = await deps.Gate.CheckAsync(CallCentrePolicies.Act, "book-appointment", ct);
            if (denied is not null) return denied;
            var gate = await RequireVerified(deps, req.InteractionId, req.BeneficiaryId, "book", ct);
            if (gate is not null) return gate;

            // Referral bookings set appointmentType=Referral so the existing ReferralScheduled event fires (15.4).
            var apptType = string.IsNullOrWhiteSpace(req.ReferralRef) ? req.AppointmentType : "Referral";
            var body = new
            {
                beneficiaryId = req.BeneficiaryId, slotId = req.SlotId, appointmentType = apptType,
                branchId = req.BranchId, referralRef = req.ReferralRef, originEncounterId = req.OriginEncounterId,
                // Passed through untouched. The note is capped and refused by emr (AppointmentNote), and the
                // doctor is checked against the branch there too — re-validating here would be a second copy
                // of a rule that must not be able to disagree with the first.
                doctorId = req.DoctorId, note = req.Note,
            };
            var result = await emr.BookAsync(body, http.Headers.Authorization, IdemKey(http), ct);
            if (result.IsSuccess && result.AppointmentId is { } apptId)
                await LinkAndNotify(deps, req.InteractionId, req.BeneficiaryId, apptId, CallAppointmentAction.Book,
                    null, req.BranchId?.ToString(), ct);
            return Passthrough(result);
        }).RequireAuthorization(HbmpPolicies.Scope("callcentre:act"));

        // --- Reschedule -------------------------------------------------------------------------------------
        v1.MapPost("/appointments/{id:guid}/reschedule", async (Guid id, RescheduleFromCallRequest req, HttpRequest http, CallDeps deps, IAppointmentGateway emr, CancellationToken ct) =>
        {
            var denied = await deps.Gate.CheckAsync(CallCentrePolicies.Act, "reschedule-appointment", ct);
            if (denied is not null) return denied;
            var beneficiary = await deps.Verification.BoundBeneficiaryAsync(req.InteractionId, ct);
            var gate = await RequireVerified(deps, req.InteractionId, beneficiary ?? Guid.Empty, "reschedule", ct);
            if (gate is not null) return gate;

            var result = await emr.RescheduleAsync(id, new { newSlotId = req.NewSlotId },
                http.Headers.Authorization, IdemKey(http), IfMatch(http), ct);
            if (result.IsSuccess)
                await LinkAndNotify(deps, req.InteractionId, beneficiary!.Value, id, CallAppointmentAction.Reschedule, null, null, ct);
            return Passthrough(result);
        }).RequireAuthorization(HbmpPolicies.Scope("callcentre:act"));

        // --- Cancel (reason mandatory) ----------------------------------------------------------------------
        v1.MapPost("/appointments/{id:guid}/cancel", async (Guid id, CancelFromCallRequest req, HttpRequest http, CallDeps deps, IAppointmentGateway emr, CancellationToken ct) =>
        {
            var denied = await deps.Gate.CheckAsync(CallCentrePolicies.Act, "cancel-appointment", ct);
            if (denied is not null) return denied;
            if (req.ReasonCode is null)
                return Results.Problem(statusCode: 422, title: "reason-required",
                    detail: "A cancellation reason code is required from the call centre.", type: "urn:hbmp:reason-required");
            var beneficiary = await deps.Verification.BoundBeneficiaryAsync(req.InteractionId, ct);
            var gate = await RequireVerified(deps, req.InteractionId, beneficiary ?? Guid.Empty, "cancel", ct);
            if (gate is not null) return gate;

            var result = await emr.CancelAsync(id, new { reasonCode = req.ReasonCode.ToString(), note = req.Note },
                http.Headers.Authorization, IdemKey(http), IfMatch(http), ct);
            if (result.IsSuccess)
                await LinkAndNotify(deps, req.InteractionId, beneficiary!.Value, id, CallAppointmentAction.Cancel, req.ReasonCode, null, ct);
            return Passthrough(result);
        }).RequireAuthorization(HbmpPolicies.Scope("callcentre:act"));
    }

    /// <summary>403 + audit when the interaction isn't verified for the beneficiary this action touches.</summary>
    private static async Task<IResult?> RequireVerified(CallDeps deps, Guid interactionId, Guid beneficiaryId, string action, CancellationToken ct)
    {
        if (beneficiaryId != Guid.Empty && await deps.Verification.IsVerifiedAsync(interactionId, beneficiaryId, ct))
            return null;
        var i = await deps.Db.Interactions.FindAsync([interactionId], ct);
        await deps.AuditAsync("call_centre_appointment", beneficiaryId.ToString(), AuditAction.StateChange,
            $"Denied{action}NotVerified", i?.CallRef, severity: AuditSeverity.Warning);
        return Results.Problem(statusCode: 403, title: "not-verified",
            detail: "The caller must be verified on this interaction before appointment actions.",
            type: "urn:hbmp:callcentre-not-verified");
    }

    /// <summary>Record the appointment↔interaction link, audit it, and trigger the notification confirmation.</summary>
    private static async Task LinkAndNotify(CallDeps deps, Guid interactionId, Guid beneficiaryId, Guid appointmentId,
        CallAppointmentAction action, CallCancelReason? reason, string? branchId, CancellationToken ct)
    {
        var i = await deps.Db.Interactions.FindAsync([interactionId], ct);
        var link = new AppointmentLink
        {
            LinkId = Guid.NewGuid(), InteractionId = interactionId, CallRef = i?.CallRef ?? "—",
            TenantId = deps.Tenant ?? "unknown", BeneficiaryId = beneficiaryId, AppointmentId = appointmentId,
            Action = action, CancelReason = reason, BranchId = branchId, CreatedBy = deps.Subject,
            CreatedAt = deps.Clock.GetUtcNow(),
        };
        // The link is the call-centre's record that this appointment was touched on this call, and the
        // confirmation is what the member actually receives. A link with no confirmation is an agent who
        // believes the member was told; a confirmation with no link is a message no one can trace to a call.
        await using var tx = await deps.Db.Database.BeginTransactionAsync(ct);
        deps.Db.AppointmentLinks.Add(link);
        await deps.Db.SaveChangesAsync(ct);

        // Confirmation to the member's preferred channel (notification-service resolves channel; clinical-free).
        await deps.Outbox.EnqueueAsync("AppointmentConfirmationRequested", "notification.events",
            new { beneficiaryId, appointmentId, action = action.ToString(), callRef = link.CallRef, tenantId = link.TenantId }, ct);
        await tx.CommitAsync(ct);
        await deps.AuditAsync("call_centre_appointment", appointmentId.ToString(), AuditAction.StateChange,
            $"Appointment{action}", link.CallRef, severity: AuditSeverity.Notice, after: reason?.ToString());
    }

    private static string? IdemKey(HttpRequest http) =>
        http.Headers.TryGetValue("Idempotency-Key", out var v) ? v.ToString() : null;

    private static string? IfMatch(HttpRequest http) =>
        http.Headers.TryGetValue("If-Match", out var v) ? v.ToString() : null;

    /// <summary>Pass the emr response through faithfully so 409/412/422 semantics reach the agent unchanged.</summary>
    private static IResult Passthrough(GatewayResult r) =>
        Results.Content(r.Body ?? "", "application/json", statusCode: r.StatusCode);
}
