using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Auth.Authorization;
using Mersal.Authz;
using Mersal.Events;
using Mersal.Pharmacy.Domain;
using Mersal.Pharmacy.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Pharmacy.Api;

/// <summary>Phase 4.3 referral creation (US-034, 23-state-machines §4). A treating doctor raises a referral to a
/// target specialty/provider; it enters Requested and emits ReferralRequested. Acceptance/scheduling/loop-closure
/// are downstream (the appointments flow already emits ReferralScheduled when a REF-* appointment is booked).</summary>
public static class ReferralEndpoints
{
    public static void MapReferrals(this WebApplication app)
    {
        var v1 = app.MapGroup("/api/v1/referrals").RequireAuthorization();

        v1.MapPost("", async (
            CreateReferralRequest req, HttpRequest http, PharmacyDbContext db, PharmacyGate gate,
            SequenceIssuer seq, IAuditClient audit, IOutbox outbox, IHbmpPrincipalAccessor me,
            TimeProvider clock, CancellationToken ct) =>
        {
            var idem = http.Headers["Idempotency-Key"].ToString();
            if (string.IsNullOrWhiteSpace(idem))
                return Results.Problem(statusCode: 400, title: "Idempotency-Key header is required", type: "urn:hbmp:idempotency-required");

            var existing = await db.Referrals.AsNoTracking().FirstOrDefaultAsync(r => r.IdempotencyKey == idem, ct);
            if (existing is not null) return Results.Ok(ReferralResponse.From(existing));

            if (string.IsNullOrWhiteSpace(req.TargetSpecialty))
                return Results.Problem(statusCode: 422, title: "missing-specialty", type: "urn:hbmp:missing-specialty",
                    detail: "A referral must name a target specialty.");

            var bearer = http.Headers.Authorization.ToString();
            var denied = await gate.CheckAsync(PharmacyPolicies.ReferralCreate, "referral", null, req.BeneficiaryId, bearer, ct);
            if (denied is not null) return denied;

            var now = clock.GetUtcNow();
            var actor = me.Principal?.Subject;
            var providerId = Guid.TryParse(me.Principal?.ProviderId, out var pg) ? pg : Guid.Empty;

            var referral = new Referral
            {
                ReferralId = Guid.NewGuid(), ReferralNo = ReferralNo.Format(now.Year, await seq.NextAsync("referral_seq", now.Year, ct)),
                BeneficiaryId = req.BeneficiaryId, EncounterId = req.EncounterId, ReferringProviderId = providerId,
                TargetSpecialty = req.TargetSpecialty, TargetProviderId = req.TargetProviderId, Reason = req.Reason,
                Status = ReferralStatus.Requested, RequestedAt = now, IdempotencyKey = idem, CreatedBy = actor,
            };

            await using var tx = await db.Database.BeginTransactionAsync(ct);
            db.Referrals.Add(referral);
            await db.SaveChangesAsync(ct);
            await outbox.EnqueueAsync("ReferralRequested", "pharmacy.events",
                new { referralId = referral.ReferralId, referral.ReferralNo, referral.TargetSpecialty, beneficiaryId = referral.BeneficiaryId }, ct);
            await tx.CommitAsync(ct);

            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "referral", EntityId = referral.ReferralId.ToString(), Action = AuditAction.Create,
                ActorUserId = actor, DecisionOutcome = "Requested",
                AfterState = $"{{\"referralNo\":\"{referral.ReferralNo}\",\"specialty\":\"{referral.TargetSpecialty}\"}}",
            }, ct);

            return Results.Created($"/api/v1/referrals/{referral.ReferralId}", ReferralResponse.From(referral));
        }).RequireAuthorization(HbmpPolicies.Scope("referral:write"));

        v1.MapGet("/{id:guid}", async (Guid id, HttpRequest http, PharmacyDbContext db, PharmacyGate gate, CancellationToken ct) =>
        {
            var r = await db.Referrals.AsNoTracking().FirstOrDefaultAsync(x => x.ReferralId == id, ct);
            if (r is null) return Results.NotFound();
            var denied = await gate.CheckAsync(PharmacyPolicies.ReferralCreate, "referral", id.ToString(), r.BeneficiaryId, http.Headers.Authorization.ToString(), ct);
            if (denied is not null) return denied;
            return Results.Ok(ReferralResponse.From(r));
        });
    }
}
