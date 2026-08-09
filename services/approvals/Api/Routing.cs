using System.Text.Json;
using Mersal.Approvals.Domain;
using Mersal.Approvals.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Approvals.Api;

/// <summary>The wire shape orders-service and pharmacy-service already publish when something is routed for a
/// decision — <c>OrderPendingApproval</c> and <c>RxSubmitted</c>, read off <see cref="Mersal.Events.ApprovalRoutingFeed"/>.</summary>
/// <remarks>
/// <para>One record for both events, because the two payloads differ only in what they call the thing being
/// asked about. Modelling them separately would mean two parsers and two validation lists for one question.</para>
/// <para><b>Nothing clinical crosses here</b>, which is the same rule the ingestion endpoint states: an
/// authorization holds the REQUEST, and the reviewer's clinical context is fetched separately, under the
/// reviewer's own token, by <see cref="HttpClinicalContextClient"/>.</para>
/// </remarks>
public sealed record RoutingMessage(
    string? TenantId,
    Guid BeneficiaryId,
    Guid? EncounterId,
    Guid? ProviderId,
    /// <summary>The order id (OrderPendingApproval) or prescription id (RxSubmitted).</summary>
    Guid? OrderId,
    Guid? PrescriptionId,
    /// <summary>ORD-2026-000900 / RX-2026-000410 — the reference a human can look up.</summary>
    string? OrderNo,
    string? RxNo,
    /// <summary>Why routing gated it. Free text from the producer; shown to the reviewer, never parsed.</summary>
    string? Reason,
    /// <summary>The ordering clinician — who a decision notice is addressed to (§11.3).</summary>
    string? OrderedByUserId,
    /// <summary>
    /// pharmacy only, and the reason this consumer cannot simply act on every message it receives.
    /// <c>RxSubmitted</c> fires for EVERY prescription; the routing outcome is this flag, not the event name.
    /// </summary>
    bool? RequiresApproval,
    IReadOnlyList<string>? ServiceCodes);

public enum RoutingOutcome
{
    /// <summary>An authorization was created and is waiting on the reviewer worklist.</summary>
    Raised,
    /// <summary>Correctly nothing to do: an ungated prescription. Acked, not dead-lettered.</summary>
    NotGated,
    /// <summary>This event id has already been ingested — a redelivery.</summary>
    Duplicate,
    /// <summary>The message could not be trusted; see the reason. Dead-lettered, never guessed at.</summary>
    Refused,
}

public sealed record RoutingResult(RoutingOutcome Outcome, Guid? AuthorizationId, string? AuthNo, string? Reason);

/// <summary>
/// Turns "a clinician asked for something gated" into an authorization on the reviewer worklist.
/// </summary>
/// <remarks>
/// <para><b>This is the caller the ingestion seam never had.</b> <c>POST /api/v1/authorizations</c> (scope
/// <c>auth:ingest</c>) was written in phase 7 for "the phase-4 routing saga / the
/// OrderPendingApproval|RxSubmitted event consumer" and no such consumer existed, so a gated order or
/// prescription changed status, told the patient to wait, and reached nobody.</para>
/// <para><b>It creates the same row the endpoint creates</b>, in the same states, with the same Submitted
/// status and the same <c>ProcessedRequest</c> idempotency ledger — deliberately, so a request that arrived
/// by event and one that arrived by HTTP are the same object to every reviewer, report and decision path
/// after this point. What it does NOT reuse is the endpoint's HTTP plumbing: no machine token, no loopback
/// call, no second network hop that can fail between two services that are already talking.</para>
/// <para><b>The one rule it applies differently, and why.</b> The endpoint refuses a non-manual request that
/// names no requesting provider. That rule guards an EXTERNAL caller: a system posting an authorization
/// nobody can attribute. A prescription genuinely has no provider in this platform — a doctor's token is
/// practitioner-scoped and carries no <c>provider_id</c>, which is why <c>Prescription</c> has no such
/// column — so applying it here would dead-letter every gated prescription and leave the exact gap this
/// consumer closes. The attribution that DOES exist (the ordering clinician, the encounter, the order or
/// prescription number) is carried in full.</para>
/// </remarks>
public sealed class RoutedAuthorizationIngestor(ApprovalsDbContext db, AuthNoIssuer authNos, TimeProvider clock)
{
    public async Task<RoutingResult> IngestAsync(
        Guid eventId, string eventType, RoutingMessage msg, CancellationToken ct = default)
    {
        if (Validate(eventType, msg) is { } invalid)
            return new(RoutingOutcome.Refused, null, null, invalid);

        // An ungated prescription is not a refusal and not a failure — it is the answer "no decision is
        // needed", which is most prescriptions. Acked and forgotten.
        if (eventType == "RxSubmitted" && msg.RequiresApproval != true)
            return new(RoutingOutcome.NotGated, null, null, null);

        var source = eventType == "OrderPendingApproval" ? AuthSource.OrderLine : AuthSource.Prescription;
        var sourceRef = (source == AuthSource.OrderLine ? msg.OrderId : msg.PrescriptionId)!.Value.ToString();
        var itemNo = source == AuthSource.OrderLine ? msg.OrderNo : msg.RxNo;

        /*
         * THE IDEMPOTENCY KEY IS THE EVENT ID, and it is a row in the same ledger the endpoint writes to.
         *
         * A natural key — (source, sourceRef) — would be WRONG here, and quietly so. An amendment that leaves
         * the approved scope re-publishes the very same event for the very same order (design 46 §5), and
         * that second request is a real one: the authorisation's basis no longer holds and a reviewer must
         * look again. Deduping on the item would swallow it and leave an order sitting in PendingApproval
         * with nothing in any queue.
         *
         * The PRIMARY KEY on processed_request is what makes this a guard rather than a hope. The consumer
         * also checks its processed_event ledger first, but that is a read followed by a write, and this
         * consumer runs at prefetch 20: two deliveries of one message can both pass the check. Exactly one
         * can insert this row.
         */
        var idem = $"routed:{eventId}";
        if (await db.ProcessedRequests.AsNoTracking().FirstOrDefaultAsync(r => r.IdempotencyKey == idem, ct) is { } prior)
        {
            var existing = await db.Authorizations.AsNoTracking()
                .FirstOrDefaultAsync(a => a.AuthorizationId == prior.AuthorizationId, ct);
            return new(RoutingOutcome.Duplicate, prior.AuthorizationId, existing?.AuthNo, null);
        }

        var now = clock.GetUtcNow();
        var auth = new Authorization
        {
            AuthorizationId = Guid.NewGuid(),
            AuthNo = await authNos.NextAsync(now.Year, ct),
            BeneficiaryId = msg.BeneficiaryId,
            Kind = AuthKind.Review,
            Source = source,
            SourceRef = sourceRef,
            EncounterId = msg.EncounterId,
            RequestingProviderId = msg.ProviderId,
            ServiceCodes = Codes.Serialize(msg.ServiceCodes ?? []),
            RequestedScope = Scope(itemNo, msg.Reason),
            Priority = AuthPriority.Routine,
            Status = AuthStatus.Submitted,
            SubmittedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
            // The ORDERING CLINICIAN. `NotifyDecisionAsync` addresses the decision notice to this, and on
            // this path there is no caller to fall back to — a background consumer has no principal. An event
            // that carries no clinician produces an authorization with no addressee, which is honest: the
            // notice is then not sent rather than sent to a machine.
            IdempotencyKey = idem,
            CreatedBy = msg.OrderedByUserId,
        };

        db.Authorizations.Add(auth);
        db.ProcessedRequests.Add(new ProcessedRequest
        {
            IdempotencyKey = idem, Operation = $"route:{eventType}",
            AuthorizationId = auth.AuthorizationId, StatusCode = 201, CreatedAt = now,
        });
        await db.SaveChangesAsync(ct);

        return new(RoutingOutcome.Raised, auth.AuthorizationId, auth.AuthNo, null);
    }

    /// <summary>The reference and the routing reason, in the same <c>itemRef</c> key the worklist already
    /// reads for a fulfilment and a validity extension — one projection, one place to look.</summary>
    private static string Scope(string? itemNo, string? reason) =>
        JsonSerializer.Serialize(new { itemRef = itemNo, routedBecause = reason });

    /// <summary>
    /// Null when the message can be trusted; otherwise why it cannot.
    /// </summary>
    /// <remarks>
    /// A refused message is dead-lettered. An authorization stamped with a guessed tenant or pointing at no
    /// real order is worse than none, because it looks to a reviewer like a request somebody made — and the
    /// order it claims to be about is not waiting on anything, so approving it grants nothing and rejecting
    /// it blocks nothing.
    /// </remarks>
    public static string? Validate(string eventType, RoutingMessage m)
    {
        ArgumentNullException.ThrowIfNull(m);
        if (string.IsNullOrWhiteSpace(m.TenantId)) return "no tenant on the envelope";
        if (m.BeneficiaryId == Guid.Empty) return "no beneficiary";
        // An authorization must be attributable to somebody — a provider that raised it, or a person who did
        // (approvals migration 0010). Named here as well as enforced there, because a constraint violation
        // inside the consumer is an exception that gets requeued five times before anyone sees a reason.
        if (m.ProviderId is null && string.IsNullOrWhiteSpace(m.OrderedByUserId))
            return "neither a requesting provider nor an ordering clinician — nobody to attribute this to";

        return eventType switch
        {
            "OrderPendingApproval" => m.OrderId.GetValueOrDefault() == Guid.Empty ? "no orderId" : null,
            "RxSubmitted" => m.PrescriptionId.GetValueOrDefault() == Guid.Empty ? "no prescriptionId" : null,
            _ => $"\"{eventType}\" is not a routing event",
        };
    }
}
