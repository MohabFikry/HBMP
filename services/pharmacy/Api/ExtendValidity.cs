using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Auth.Authorization;
using Mersal.Authz;
using Mersal.Events;
using Mersal.Pharmacy.Domain;
using Mersal.Pharmacy.Infrastructure;
using Mersal.Validity;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Pharmacy.Api;

/// <summary>
/// Puts an expired prescription back in date, on the authority of an approved authorization.
/// </summary>
/// <remarks>
/// <para>
/// Called by approvals-service when a reviewer approves a validity-extension request, with the REVIEWER's
/// own token. It is gated on <c>auth:decide</c> — held by <c>medical_approval</c> and
/// <c>medical_director</c> and by nobody else — which states the rule plainly: only someone who may decide
/// an authorization may move a prescription's expiry. No pharmacist, and no prescriber, can reach this.
/// </para>
/// <para>
/// The new window is the tenant's CONFIGURED period counted from the decision, not from the original issue
/// date. That is the "fixed reset" the approval flow was specified around: approving means "this is good for
/// another full period from today", so there is no figure for the reviewer to get wrong, and an extension
/// granted on a prescription that lapsed months ago is not born half-used.
/// </para>
/// <para>
/// Idempotent on the authorization id. A retried apply after a timeout must not stack a second period on
/// the first — approvals sends <c>Idempotency-Key: extend:{authorizationId}</c> and a replay returns the
/// expiry already granted.
/// </para>
/// </remarks>
public static class ExtendValidityEndpoints
{
    public static void MapExtendValidity(this WebApplication app)
    {
        app.MapPost("/api/v1/prescriptions/{id:guid}/extend-validity", async (
            Guid id, ExtendValidityRequest req, HttpRequest http, PharmacyDbContext db,
            IValidityPolicySource validity, IAuditClient audit, IOutbox outbox,
            IHbmpPrincipalAccessor me, TimeProvider clock, CancellationToken ct) =>
        {
            var rx = await db.Prescriptions.FirstOrDefaultAsync(p => p.PrescriptionId == id, ct);
            if (rx is null) return Results.Problem(statusCode: 404, title: "Not Found",
                type: "https://mersal.foundation/problems/not-found");

            var now = clock.GetUtcNow();

            // Replay: the same authorization applied twice returns what it granted the first time. Compared
            // on the authorization, not on the clock — two approvals a minute apart are two grants, the same
            // approval retried is one.
            if (rx.ValidityExtendedBy == req.AuthorizationId)
                return Results.Ok(new { prescriptionId = rx.PrescriptionId, rx.RxNo, expiresAt = rx.ExpiresAt, replayed = true });

            // A cancelled or fully-dispensed prescription is FINISHED, not merely out of date. Extending one
            // would resurrect something that stopped for a reason nothing here can see — the prescriber
            // withdrew it, or the patient already has the medication.
            if (rx.Status is RxStatus.Cancelled or RxStatus.Rejected or RxStatus.Dispensed)
                return Results.Problem(statusCode: 409, title: "not-extendable", type: "urn:hbmp:not-extendable",
                    detail: $"A prescription in status {rx.Status} cannot be revalidated — it did not stop being "
                            + "dispensable because of its date.");

            var previous = rx.ExpiresAt;
            var newExpiry = ValidityPolicy.ExpiryFor(now, await validity.DaysAsync(ValidityArtefact.Prescription, http.Headers.Authorization.ToString(), ct));

            await using var tx = await db.Database.BeginTransactionAsync(ct);
            rx.ExpiresAt = newExpiry;
            rx.ValidityExtendedBy = req.AuthorizationId;
            rx.ValidityExtendedAt = now;
            // Back to Approved from Expired. Anything else was never moved by the sweeper and keeps whatever
            // it had — a PartiallyDispensed prescription stays PartiallyDispensed.
            if (rx.Status == RxStatus.Expired) rx.Status = RxStatus.Approved;

            await outbox.EnqueueAsync("RxValidityExtended", "pharmacy.events", new
            {
                tenantId = rx.TenantId, prescriptionId = rx.PrescriptionId, rx.RxNo, rx.BeneficiaryId,
                authorizationId = req.AuthorizationId, req.AuthNo, previousExpiry = previous, newExpiry,
            }, ct);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "prescription", EntityId = rx.PrescriptionId.ToString(), Action = AuditAction.StateChange,
                ActorUserId = me.Principal?.Subject, TenantId = rx.TenantId,
                BeforeState = $"{{\"expiresAt\":\"{previous:O}\"}}",
                AfterState = $"{{\"expiresAt\":\"{newExpiry:O}\",\"authorizationId\":\"{req.AuthorizationId}\"}}",
                DecisionOutcome = "ValidityExtended", DecisionReasonCode = req.AuthNo,
                Purpose = "validity-extension", Severity = AuditSeverity.Notice,
            }, ct);

            return Results.Ok(new { prescriptionId = rx.PrescriptionId, rx.RxNo, expiresAt = newExpiry, replayed = false });
        }).RequireAuthorization(HbmpPolicies.Scope("auth:decide"));
    }
}

/// <param name="AuthNo">The AUTH-YYYY-NNNNNN reference, for the audit line and the event. Display only.</param>
public sealed record ExtendValidityRequest(Guid AuthorizationId, string? AuthNo);
