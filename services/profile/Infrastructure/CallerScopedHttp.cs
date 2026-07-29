using System.Net;
using System.Text.Json;
using Mersal.Profile.Domain;

namespace Mersal.Profile.Infrastructure;

/// <summary>
/// The one way this service talks to another service: a GET carrying the CALLER'S bearer.
///
/// <para>There is deliberately no overload that omits the credentials and no fallback that acquires a token of
/// its own. A composition service that can authenticate as itself is a service that returns a complete profile
/// to someone entitled to a third of it — and it looks correct, which is why design 39 §7.2 makes this an
/// invariant rather than a guideline. The architecture test asserts no client-credentials path exists; this
/// type is what makes that assertion easy to keep true.</para>
/// </summary>
public sealed class CallerScopedHttp(IHttpClientFactory factory)
{
    /// <summary>
    /// Fetch and parse JSON from an owning service under the caller's token.
    /// </summary>
    /// <returns>The parsed document, or <c>null</c> for 404 / 204 — "nothing exists here", which the composer
    /// renders as NotApplicable.</returns>
    /// <exception cref="SectionUnavailableException">Any other non-success status, or a transport failure. The
    /// composer turns this into <c>Unavailable</c> — never into an empty section.</exception>
    public async Task<JsonDocument?> GetAsync(
        string clientName, string path, CallerCredentials caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);

        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.TryAddWithoutValidation("Authorization", caller.Authorization);
        if (!string.IsNullOrWhiteSpace(caller.ActiveBranch))
            request.Headers.TryAddWithoutValidation("X-Active-Branch", caller.ActiveBranch);
        if (!string.IsNullOrWhiteSpace(caller.CorrelationId))
            request.Headers.TryAddWithoutValidation("X-Correlation-Id", caller.CorrelationId);

        var client = factory.CreateClient(clientName);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NoContent) return null;

        // A 403 from the owning service is the SECOND layer doing its job (design 39 §1). It is not an error to
        // paper over: the section is genuinely withheld, so it surfaces as such rather than as a broken profile.
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
            throw new SectionForbiddenException($"{clientName} declined this caller for {path}.");

        if (!response.IsSuccessStatusCode)
            throw new SectionUnavailableException($"{clientName} returned {(int)response.StatusCode} for {path}.");

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }
}

/// <summary>The owning service could not answer. Degrades ONE section to Unavailable.</summary>
public sealed class SectionUnavailableException : Exception
{
    public SectionUnavailableException(string message) : base(message) { }
    public SectionUnavailableException() { }
    public SectionUnavailableException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Small, forgiving readers over an upstream document. The profile deliberately parses upstream JSON
/// rather than taking a project reference on another service's DTOs: a compile-time coupling between fifteen
/// services and their aggregator is how a composition layer becomes a deployment bottleneck.</summary>
public static class Json
{
    public static JsonElement? Prop(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null
            ? v : null;

    public static string? Str(this JsonElement e, string name) =>
        e.Prop(name) is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;

    public static bool Bool(this JsonElement e, string name) =>
        e.Prop(name) is { ValueKind: JsonValueKind.True } ? true : false;

    public static int? Num(this JsonElement e, string name) =>
        e.Prop(name) is { ValueKind: JsonValueKind.Number } v && v.TryGetInt32(out var i) ? i : null;

    public static decimal? Dec(this JsonElement e, string name) =>
        e.Prop(name) is { ValueKind: JsonValueKind.Number } v && v.TryGetDecimal(out var d) ? d : null;

    public static Guid? Uuid(this JsonElement e, string name) =>
        e.Str(name) is { } s && Guid.TryParse(s, out var g) ? g : null;

    public static DateTimeOffset? Moment(this JsonElement e, string name) =>
        e.Str(name) is { } s && DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out var d) ? d : null;

    public static DateOnly? Day(this JsonElement e, string name) =>
        e.Str(name) is { } s && DateOnly.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, out var d)
            ? d : null;

    public static IEnumerable<JsonElement> Array(this JsonElement e, string name) =>
        e.Prop(name) is { ValueKind: JsonValueKind.Array } a ? a.EnumerateArray() : [];
}
