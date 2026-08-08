using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Mersal.Policy.Infrastructure;

// Phase 19.5 — what policy-service must ASK for rather than hold (design 38 §4.6: "AGGREGATE, do not
// duplicate: call the owning services with the caller's token").
//
// ============================================================================================================
// THE CALLER'S TOKEN, AND THE OWNER'S PROJECTION — NOT OURS
// ============================================================================================================
// Every call here forwards the caller's bearer, so patient-service authorizes the SAME principal and applies
// its own FieldProjector and its own PHI-read audit. A reception user composing a 360 therefore receives the
// contact block and not a UNHCR registration number, decided by the service that owns that distinction.
//
// The alternative — policy-service calling with a service account and trimming afterwards — is how an
// aggregator quietly becomes a way around the min-necessary rules of every service it aggregates. It would
// also make the disclosure audit say "policy-service read this record", which is true and useless.
//
// The payloads come back as opaque dictionaries and are passed through UNMODELLED. Re-typing them here would
// mean a field patient-service classifies as PII arriving in a shape policy-service decided; the point of
// aggregation is that the owner's answer travels intact.

/// <summary>The administrative half of a beneficiary, as its owner chose to disclose it to THIS caller.</summary>
public sealed record BeneficiaryAdministrativeFacts(IReadOnlyDictionary<string, object?> Record);

/// <summary>Reads the beneficiary record from patient-service. Null means "could not ask" — never "no such
/// person" — and the 360 says so rather than rendering an empty section.</summary>
public interface IBeneficiaryAdministrativeSource
{
    Task<BeneficiaryAdministrativeFacts?> GetAsync(Guid beneficiaryId, string? bearerToken, CancellationToken ct = default);

    /// <summary>Resolve beneficiary ids by identifier or name for member query. Returns the matches and whether
    /// the result was TRUNCATED — a silently truncated identity filter turns "find everyone called Ahmed" into
    /// a subset that looks complete.</summary>
    Task<(IReadOnlyList<Guid> Ids, bool Truncated)?> SearchAsync(
        string? identifierType, string? identifierValue, string? name, string? bearerToken, CancellationToken ct = default);

    /// <summary>Names for one PAGE of member-query results. Batched at the owner: 25 round trips to render 25
    /// rows is not a design, it is an outage waiting for a busy morning.</summary>
    Task<IReadOnlyDictionary<Guid, BeneficiarySummary>?> SummariesAsync(
        IReadOnlyCollection<Guid> beneficiaryIds, string? bearerToken, CancellationToken ct = default);
}

/// <summary>The minimum a member list needs to be usable: who this is, and whether they are a live
/// beneficiary. No identifiers, no contacts — a list is the highest-volume disclosure in the system.</summary>
/// <summary>Name + status + card number for one page of somebody else's list. Deliberately narrow — see
/// patient-service's <c>BeneficiarySummaryEndpoints</c> for why the rest of the record is not here.</summary>
public sealed record BeneficiarySummary(
    Guid BeneficiaryId, string? GivenName, string? FamilyName, string? Status, string? CardNumber = null);

public sealed class HttpBeneficiaryAdministrativeSource(HttpClient http) : IBeneficiaryAdministrativeSource
{
    /// <summary>The identity-filter cap. Beyond this the caller is told to narrow rather than handed a
    /// truncated set dressed up as an answer.</summary>
    public const int MaxIdentityMatches = 200;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<BeneficiaryAdministrativeFacts?> GetAsync(
        Guid beneficiaryId, string? bearerToken, CancellationToken ct = default)
    {
        using var req = Authorized(HttpMethod.Get, $"/api/v1/beneficiaries/{beneficiaryId}", bearerToken);
        try
        {
            using var resp = await http.SendAsync(req, ct);
            // 403 included: "you may not read this person" is an answer the 360 must render as a withheld
            // section, not as a person with no contact details.
            if (!resp.IsSuccessStatusCode) return null;
            var record = await resp.Content.ReadFromJsonAsync<Dictionary<string, object?>>(Json, ct);
            return record is null ? null : new BeneficiaryAdministrativeFacts(record);
        }
        catch (HttpRequestException) { return null; }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return null; }
    }

    public async Task<(IReadOnlyList<Guid> Ids, bool Truncated)?> SearchAsync(
        string? identifierType, string? identifierValue, string? name, string? bearerToken, CancellationToken ct = default)
    {
        var query = new List<string> { $"pageSize={MaxIdentityMatches}" };
        if (!string.IsNullOrWhiteSpace(identifierType)) query.Add($"identifierType={Uri.EscapeDataString(identifierType)}");
        if (!string.IsNullOrWhiteSpace(identifierValue)) query.Add($"identifierValue={Uri.EscapeDataString(identifierValue)}");
        if (!string.IsNullOrWhiteSpace(name)) query.Add($"name={Uri.EscapeDataString(name)}");

        using var req = Authorized(HttpMethod.Get, $"/api/v1/beneficiaries?{string.Join('&', query)}", bearerToken);
        try
        {
            using var resp = await http.SendAsync(req, ct);
            if (resp.StatusCode == HttpStatusCode.NotFound) return ([], false);
            if (!resp.IsSuccessStatusCode) return null;
            var page = await resp.Content.ReadFromJsonAsync<SearchPage>(Json, ct);
            var ids = page?.Items?.Select(i => i.TryGetValue("beneficiaryId", out var v)
                            && Guid.TryParse(v?.ToString(), out var g) ? g : (Guid?)null)
                        .Where(g => g is not null).Select(g => g!.Value).Distinct().ToList() ?? [];
            return (ids, ids.Count >= MaxIdentityMatches);
        }
        catch (HttpRequestException) { return null; }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return null; }
    }

    public async Task<IReadOnlyDictionary<Guid, BeneficiarySummary>?> SummariesAsync(
        IReadOnlyCollection<Guid> beneficiaryIds, string? bearerToken, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(beneficiaryIds);
        if (beneficiaryIds.Count == 0) return new Dictionary<Guid, BeneficiarySummary>();

        var ids = string.Join(',', beneficiaryIds.Take(PageCap));
        using var req = Authorized(HttpMethod.Get, $"/api/v1/beneficiaries/summaries?ids={ids}", bearerToken);
        try
        {
            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;
            var rows = await resp.Content.ReadFromJsonAsync<List<BeneficiarySummary>>(Json, ct);
            return rows?.ToDictionary(r => r.BeneficiaryId) ?? [];
        }
        catch (HttpRequestException) { return null; }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return null; }
    }

    /// <summary>One page's worth — the same cap <c>PageRequest.MaxPageSize</c> enforces.</summary>
    private const int PageCap = 100;

    private static HttpRequestMessage Authorized(HttpMethod method, string url, string? bearerToken)
    {
        var req = new HttpRequestMessage(method, url);
        if (!string.IsNullOrWhiteSpace(bearerToken))
        {
            var token = bearerToken.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                ? bearerToken["Bearer ".Length..] : bearerToken;
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        return req;
    }

    private sealed record SearchPage(int Page, int PageSize, List<Dictionary<string, object?>>? Items);
}
