using Microsoft.AspNetCore.Http;

namespace Mersal.Authz;

/// <summary>RFC-7807 problem+json for request-level failures (bad input, conflict, unprocessable), replacing the
/// anonymous <c>new { error = "..." }</c> bodies the services used to return. The machine-readable code moves into
/// a <c>code</c> extension member, so a client keeps a stable programmatic signal while the envelope is a standard
/// <c>application/problem+json</c> document — the same contract the SPA's <c>readProblem()</c> already parses. Twin
/// of <see cref="GateResults"/>, which covers the authz-decision (401/403) failures. (16.9 error consistency.)</summary>
public static class ProblemResults
{
    /// <summary>400 — the request was malformed or violated a precondition the client can fix.</summary>
    public static IResult Invalid(string code, string? detail = null) =>
        Build(400, "bad-request", "urn:hbmp:bad-request", code, detail, null);

    /// <summary>404 — no such entity. Distinct from a 403: "it does not exist" and "you may not see it" send an
    /// administrator down completely different paths, and answering the second with the first sends them to
    /// raise a data-loss incident over a permission setting (19.5).</summary>
    public static IResult NotFound(string code, string? detail = null) =>
        Build(404, "not-found", "https://mersal.foundation/problems/not-found", code, detail, null);

    /// <summary>409 — the request conflicts with current state (e.g. a uniqueness or lifecycle rule).</summary>
    public static IResult Conflict(string code, string? detail = null) =>
        Build(409, "conflict", "urn:hbmp:conflict", code, detail, null);

    /// <summary>422 — the request was well-formed but semantically rejected (e.g. failed a domain validator).
    /// <paramref name="extra"/> carries structured detail such as a per-field error list.</summary>
    public static IResult Unprocessable(string code, string? detail = null, IReadOnlyDictionary<string, object?>? extra = null) =>
        Build(422, "unprocessable", "urn:hbmp:unprocessable", code, detail, extra);

    static IResult Build(int status, string title, string type, string code, string? detail, IReadOnlyDictionary<string, object?>? extra)
    {
        var ext = new Dictionary<string, object?> { ["code"] = code };
        if (extra is not null)
            foreach (var kv in extra) ext[kv.Key] = kv.Value;
        return Results.Problem(statusCode: status, title: title, type: type, detail: detail, extensions: ext);
    }
}
