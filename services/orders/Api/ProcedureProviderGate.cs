using Mersal.Auth;
using Mersal.Authz;
using Mersal.Orders.Domain;

namespace Mersal.Orders.Api;

/// <summary>
/// 29.2b — authorization for the EXTERNAL delivering provider (design 45 §2b).
///
/// <para><b>This gate asks the ownership question of the ROW, not of the caller.</b> That sentence is the
/// entire reason the class exists. Audit R3 found <c>DispensingGate</c> building its ABAC resource as
/// <c>new ResourceRef { ProviderId = p.ProviderId }</c> — the caller's OWN provider id — so the
/// provider-ownership rule compared the caller against themselves, always passed, and any authenticated
/// pharmacist with <c>pharmacy:read</c> browsed the whole network queue. <c>FulfillmentGate</c> is built the
/// same way and is saved only by the separate capability filter.</para>
///
/// <para>Nothing failed when that happened, which is why it survived: no error, no empty screen — the queue
/// simply contained other pharmacies' work, and a queue with other people's work in it looks like a busy
/// queue.</para>
///
/// <para><b>So there is no ABAC round-trip here at all.</b> The engine is the right tool for "may this ROLE
/// perform this ACTION", and the scope check at the endpoint covers that. It is the wrong tool for "is this
/// particular row mine", because answering that requires the row, and every convenient way to hand it a
/// resource lets the caller's own identity stand in for the answer. <see cref="ProviderOwnership"/> is a pure
/// comparison of two ids that cannot be satisfied by the caller alone.</para>
/// </summary>
public sealed class ProcedureProviderGate(IHbmpPrincipalAccessor me)
{
    /// <summary>The caller's provider id, or null when they are not bound to one. A token without a provider
    /// binding is not an external provider, and this returns null rather than <c>Guid.Empty</c> so it can
    /// never be compared equal to an unassigned order.</summary>
    public Guid? CallerProviderId =>
        Guid.TryParse(me.Principal?.ProviderId, out var id) && id != Guid.Empty ? id : null;

    /// <summary>May the caller open the procedure portal at all? Role + a real provider binding. This is the
    /// COARSE check; it never decides which rows are visible — <see cref="AuthorizeOrder"/> does, per row.</summary>
    public IResult? AuthorizePortal()
    {
        if (me.Principal is null) return GateResults.Unauthenticated();
        return CallerProviderId is null
            ? Deny("You are not associated with a delivering provider.", "no-provider-binding")
            : null;
    }

    /// <summary>
    /// May the caller see or act on THIS order? The row's <c>AssignedProviderId</c> must equal theirs.
    /// </summary>
    /// <remarks>
    /// Returns 404, not 403, when the order belongs to someone else. A 403 confirms the order EXISTS, and to a
    /// competitor centre holding a valid order number that is a membership oracle — "does Mersal have work
    /// outstanding for this beneficiary?" — answerable without ever being authorised for any of it. Not-found
    /// and not-yours are indistinguishable to a caller who is entitled to neither, which is the point.
    /// </remarks>
    public IResult? AuthorizeOrder(InvestigationOrder? order)
    {
        if (me.Principal is null) return GateResults.Unauthenticated();
        if (CallerProviderId is null)
            return Deny("You are not associated with a delivering provider.", "no-provider-binding");

        return ProviderOwnership.MayAccess(CallerProviderId, order?.AssignedProviderId)
            ? null
            : Results.Problem(statusCode: 404, title: "Not Found",
                type: "https://mersal.foundation/problems/not-found",
                detail: "No such order is routed to your organisation.");
    }

    private static IResult Deny(string detail, string reason) =>
        GateResults.Forbidden("urn:hbmp:procedure-access-denied", detail: detail, reason: reason);
}
