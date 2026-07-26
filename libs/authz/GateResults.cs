using Microsoft.AspNetCore.Http;

namespace Mersal.Authz;

/// <summary>The single RFC-7807 problem shape every service *Gate returns on an access decision (16.6 dedup).
/// Behaviour is identical to the ~14 hand-rolled copies it replaces: a 401 <c>urn:hbmp:unauthenticated</c> when
/// there is no principal, and a 403 <c>urn:hbmp:&lt;area&gt;-access-denied</c> carrying an optional human detail
/// and a machine <c>reason</c> extension. Keeping one definition means the edge contract can't drift per service.</summary>
public static class GateResults
{
    public static IResult Unauthenticated() =>
        Results.Problem(statusCode: 401, title: "unauthenticated", type: "urn:hbmp:unauthenticated");

    public static IResult Forbidden(string type, string? detail = null, string? reason = null) =>
        Results.Problem(
            statusCode: 403, title: "access-denied", type: type, detail: detail,
            extensions: reason is null ? null : new Dictionary<string, object?> { ["reason"] = reason });
}
