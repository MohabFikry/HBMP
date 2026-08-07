using Mersal.Orders.Domain;

namespace Mersal.Orders.Api;

/// <summary>
/// 29.2b — what an EXTERNAL delivering provider sees (design 45 §2b).
///
/// <para><b>This is a projection type, not a filtered view of a larger one.</b> The withheld fields are ABSENT
/// from the type, so they cannot be serialised by accident — the platform's rule that "a withheld field is
/// absent from the JSON, never hidden in the client" (design 39 §1) is enforced here by there being nothing to
/// hide. A DTO with a nullable <c>Diagnosis</c> that is left null would serialise as <c>"diagnosis": null</c>
/// and would become non-null the first time somebody populated it "for the internal case".</para>
///
/// <para><b>An external centre is not a Mersal clinician</b>, so this is the narrowest projection on the
/// platform. It carries what is needed to verify the person at the counter and deliver the right service, and
/// nothing else:</para>
/// <list type="table">
/// <item><term>Sees</term><description>beneficiary identity sufficient to verify the person present, the
/// ordered service and its code, sessions authorised and delivered, authorisation status and validity, and the
/// clinical context the ordering doctor CHOSE to share.</description></item>
/// <item><term>Never sees</term><description>the EMR, notes, other encounters, other providers' rows,
/// diagnoses beyond the doctor's chosen context, coverage amounts, cost-share or claim values.</description></item>
/// </list>
///
/// <para><b>Why no money.</b> A delivering centre is paid under its contract, which it already knows. Coverage
/// amounts and cost-share describe the BENEFICIARY's entitlement, and a centre that can see how much of a
/// benefit remains has both a reason and a means to shape what it recommends.</para>
/// </summary>
public sealed record ProcedureQueueItem(
    Guid OrderId,
    string OrderNo,
    string OrderType,
    string Status,

    /// <summary>Identity sufficient to verify the person present — and NULL on the queue.
    ///
    /// <para>The queue is a list of WORK; a centre browsing a list of refugees' names is a disclosure nobody
    /// asked for. The name is populated only on the counter path (<c>/search</c>), which requires a SECOND
    /// identifier and audits the retrieval — a card is shared and photographed, so it is a lookup key and
    /// never proof of identity.</para></summary>
    Guid BeneficiaryId,
    string? BeneficiaryDisplayName,
    string? BeneficiaryPhotoUrl,

    string CodeSystem,
    string Code,
    string? Description,
    string? ProcedureTypeCode,

    /// <summary>Sessions AUTHORISED — from the approved scope, never the requested one (design 45 §2).</summary>
    int SessionsAuthorised,
    /// <summary>Sessions delivered so far. Together these render "4 of 6 sessions delivered", the SAME sentence
    /// the ordering doctor's worklist shows — a course that reads differently at each end is one somebody
    /// delivers twice.</summary>
    int SessionsDelivered,

    bool Authorised,
    DateTimeOffset? ValidUntil,
    bool Expired,

    /// <summary>The referral reason / clinical context the ordering doctor DELIBERATELY shared. Null when they
    /// shared none — which is the default, and reads as "not disclosed", never as "no diagnosis".</summary>
    string? SharedClinicalContext)
{
    public int SessionsRemaining => Math.Max(0, SessionsAuthorised - SessionsDelivered);

    /// <summary>Progress as the centre and the doctor both see it.</summary>
    public string ProgressLabel => $"{SessionsDelivered} of {SessionsAuthorised} sessions delivered";

    /// <summary>
    /// Project an order for the delivering provider.
    ///
    /// <para>Takes the beneficiary display fields as arguments rather than reading them from the order,
    /// because orders-service does not hold them: they come from patient-service through the audited,
    /// second-identifier-gated lookup, under the CALLER's token. Composing them here rather than letting the
    /// client fetch them separately is what keeps the projection a server-side decision.</para>
    /// </summary>
    public static ProcedureQueueItem From(
        InvestigationOrder order, OrderLine line, string? displayName, string? photoUrl, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(line);

        var (delivered, authorised) = ProcedureSessions.Progress(line);

        return new ProcedureQueueItem(
            OrderId: order.OrderId,
            OrderNo: order.OrderNo,
            OrderType: OrderTypes.Canonical(order.OrderType).ToString(),
            Status: order.Status.ToString(),
            BeneficiaryId: order.BeneficiaryId,
            BeneficiaryDisplayName: displayName,
            BeneficiaryPhotoUrl: photoUrl,
            CodeSystem: line.CodeSystem.ToString(),
            Code: line.Code,
            Description: line.Description,
            ProcedureTypeCode: line.ProcedureTypeCode,
            SessionsAuthorised: authorised,
            SessionsDelivered: delivered,
            Authorised: order.AuthorizationId is not null,
            ValidUntil: order.ExpiresAt,
            // Against the CLOCK, not the status: the expiry sweeper runs hourly, so between lapsing and being
            // swept the row still reads Active and a status-only test would offer the centre work that consume
            // then refuses.
            Expired: order.ExpiresAt is { } exp && exp <= now,
            SharedClinicalContext: order.SharedClinicalContext);
    }
}
