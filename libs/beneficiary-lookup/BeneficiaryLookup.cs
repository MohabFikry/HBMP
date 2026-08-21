using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Mersal.BeneficiaryLookup;

/// <summary>
/// How a resolve attempt ended.
/// </summary>
/// <remarks>
/// FOUR outcomes, because they mean four different things to the person standing at the counter or the bench,
/// and only ONE of them is "this member has nothing". Collapsing them into a nullable Guid is how a fulfiller
/// whose token could not read the directory was told a member with three live items had none — a 200 carrying
/// a wrong answer, which is worse than an error because nothing about it invites a second look.
/// </remarks>
public enum ResolveOutcome
{
    /// <summary>One beneficiary matched every identifier supplied.</summary>
    Resolved,
    /// <summary>Nobody matched, or more than one did. A real, final answer about the identifiers given.</summary>
    NotFound,
    /// <summary>Fewer than two identifiers. A card number alone is a lookup key, not an authenticator.</summary>
    TooFewIdentifiers,
    /// <summary>patient-service could not be asked — refused, unreachable or erroring.</summary>
    Unavailable,
}

/// <summary>The result of resolving a person from identifiers.</summary>
/// <param name="Outcome">How the attempt ended.</param>
/// <param name="BeneficiaryId">Who matched, when exactly one did.</param>
/// <param name="DisplayName">
/// The name patient-service DISCLOSED to this caller, or null when it disclosed none.
///
/// <para>32.6 — carried here rather than fetched again, because the resolve response already contains the
/// field-projected record and the projector has already decided what this caller may see. A second lookup
/// would be a second disclosure decision about the same person in the same act, and the two would drift.</para>
///
/// <para>Null means WITHHELD OR ABSENT, never "no name". A counter that renders it must show nothing rather
/// than invent a placeholder: the point of the name is to verify the person present, and a fabricated one
/// verifies nobody.</para>
/// </param>
public readonly record struct BeneficiaryResolution(ResolveOutcome Outcome, Guid? BeneficiaryId, string? DisplayName = null)
{
    public static BeneficiaryResolution Resolved(Guid id, string? displayName = null) =>
        new(ResolveOutcome.Resolved, id, displayName);
    public static readonly BeneficiaryResolution NotFound = new(ResolveOutcome.NotFound, null);
    public static readonly BeneficiaryResolution TooFew = new(ResolveOutcome.TooFewIdentifiers, null);
    public static readonly BeneficiaryResolution Unavailable = new(ResolveOutcome.Unavailable, null);
}

/// <summary>
/// Resolves a beneficiary id from a card number, passport or member number, via patient-service.
/// </summary>
/// <remarks>
/// <para>
/// <b>Shared rather than duplicated per service.</b> Two fulfilment counters asking "who is this member" must
/// answer identically — including on the failure paths, which are the ones that matter. A second
/// implementation would drift on exactly the case nobody tests: what a 403 from the directory means.
/// </para>
/// <para>
/// TWO identifiers are required, enforced here as well as server-side. A card number is printed on something
/// that is shared, photographed and reused, so it is a lookup key and never proof of identity (doc 43 §7 D5).
/// Refusing locally too matters: sending one identifier and reading the 422 back would make the endpoint an
/// existence oracle for a single card number.
/// </para>
/// </remarks>
public interface IBeneficiaryResolver
{
    Task<BeneficiaryResolution> ResolveAsync(
        string? cardNumber, string? passport, string? memberNo, string? bearerToken, CancellationToken ct = default);
}

/// <summary>The patient-service implementation. The CALLER's token is forwarded — the platform has no service
/// accounts, so every directory read is attributable to the person who asked.</summary>
public sealed class HttpBeneficiaryResolver(IHttpClientFactory factory) : IBeneficiaryResolver
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<BeneficiaryResolution> ResolveAsync(
        string? cardNumber, string? passport, string? memberNo, string? bearerToken, CancellationToken ct = default)
    {
        var q = new List<string>();
        if (!string.IsNullOrWhiteSpace(cardNumber)) q.Add($"cardNumber={Uri.EscapeDataString(cardNumber)}");
        if (!string.IsNullOrWhiteSpace(passport)) q.Add($"passport={Uri.EscapeDataString(passport)}");
        if (!string.IsNullOrWhiteSpace(memberNo)) q.Add($"memberNo={Uri.EscapeDataString(memberNo)}");
        if (q.Count < 2) return BeneficiaryResolution.TooFew;

        try
        {
            var patient = factory.CreateClient("patient");
            using var req = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/beneficiaries/resolve?{string.Join('&', q)}");
            if (!string.IsNullOrWhiteSpace(bearerToken))
            {
                var token = bearerToken.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                    ? bearerToken["Bearer ".Length..] : bearerToken;
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
            using var resp = await patient.SendAsync(req, ct);

            // 404 is an ANSWER — those identifiers match nobody (or match two people, which resolves to nobody
            // by design). Anything else that is not a success is a failure to ASK: a 403 because the caller may
            // not read the directory, a 5xx, a timeout. The two must not share a return value, because only the
            // first one means "this member has nothing".
            if (resp.StatusCode == HttpStatusCode.NotFound) return BeneficiaryResolution.NotFound;
            if (!resp.IsSuccessStatusCode) return BeneficiaryResolution.Unavailable;

            var body = await resp.Content.ReadFromJsonAsync<ResolveDto>(Json, ct);
            return body?.BeneficiaryId is { } id
                ? BeneficiaryResolution.Resolved(id, DisplayName(body.Beneficiary))
                : BeneficiaryResolution.Unavailable;   // a 200 with no id is a contract breach, not a miss
        }
        catch (HttpRequestException) { return BeneficiaryResolution.Unavailable; }
        catch (TaskCanceledException) { return BeneficiaryResolution.Unavailable; }
    }

    /// <summary>
    /// Build a display name from the fields patient-service actually handed over.
    /// </summary>
    /// <remarks>
    /// <para>Every part is optional because the projector may withhold any of them, and the result is null
    /// when nothing survived — the caller then shows no name at all. Joining what is present is not the same
    /// as filling in what is missing: "Amal —" would read as a surname the record does not have.</para>
    /// </remarks>
    /// <summary>The name parts, in the order they are spoken. Not a set — order is the whole point.</summary>
    private static readonly string[] NameParts = ["givenName", "middleName", "familyName"];

    private static string? DisplayName(IReadOnlyDictionary<string, JsonElement>? disclosed)
    {
        if (disclosed is null) return null;
        var parts = NameParts
            .Select(k => disclosed.TryGetValue(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .ToArray();
        return parts.Length == 0 ? null : string.Join(' ', parts);
    }

    /// <summary>The resolve payload. <c>beneficiary</c> is the FIELD-PROJECTED record — its keys are whatever
    /// the caller was entitled to, so it is read as a dictionary rather than a fixed shape.</summary>
    private sealed record ResolveDto(Guid? BeneficiaryId, IReadOnlyDictionary<string, JsonElement>? Beneficiary);
}

public static class BeneficiaryLookupServiceCollectionExtensions
{
    /// <summary>Wire the shared beneficiary lookup into a fulfilment service (pharmacy, orders).</summary>
    public static IServiceCollection AddHbmpBeneficiaryLookup(this IServiceCollection services)
    {
        services.AddScoped<IBeneficiaryResolver, HttpBeneficiaryResolver>();
        return services;
    }
}
