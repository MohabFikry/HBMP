using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Mersal.Policy.Domain;

namespace Mersal.Policy.Infrastructure;

// Phase 19.4 — the facts a utilization report needs that policy-service does not own.
//
// ============================================================================================================
// WHY THESE ARE READS AND NOT A COPY
// ============================================================================================================
// Encounter counts belong to emr-service, authorization outcomes to approvals-service, claim value to
// claims-service. Copying any of them here would create a second version of a number whose owner keeps
// changing it — and the copy is always the one someone reads on the day it is stale.
//
// Each source is asked SEPARATELY and fails SEPARATELY. A composed call would mean an approvals outage blanks
// the claim value too, and a report that hides three facts because one service is down is a report nobody can
// use during exactly the incident they need it for.
//
// MIN-NECESSARY. Every contract below returns COUNTS AND AMOUNTS ONLY. No diagnosis, no procedure code, no
// service description reaches policy-service, because a utilization report is read by Finance and the Network
// Team — roles that must never see clinical content (11-permission-matrix). The narrowness is enforced at the
// source endpoint, not by trimming here: a projection applied after the wire is a projection that has already
// put PHI in a log.

/// <summary>Which members, over which service-date window. The window is on the SERVICE date, never on when
/// the record was created — care delivered in March is March's utilization however late it was keyed in.</summary>
public readonly record struct UtilizationFactWindow(
    IReadOnlyCollection<Guid> BeneficiaryIds, DateOnly From, DateOnly To);

/// <summary>Authorization outcomes over the window.</summary>
public sealed record AuthorizationFacts(int Raised, int Approved, int Denied);

/// <summary>Claim value over the window. Amounts only — the claims schema carries no clinical column at all
/// (36 §2), which is why this is a safe source for a Finance-facing report.</summary>
public sealed record ClaimFacts(decimal Claimed, decimal Approved, decimal MemberShare, string CurrencyCode);

/// <summary>emr-service: how many encounters, and nothing else about them.</summary>
public interface IEncounterFactSource
{
    /// <returns>null when the source could not be reached — NOT zero. See <see cref="ExternalUtilization.Unavailable"/>.</returns>
    Task<int?> CountAsync(UtilizationFactWindow window, string? bearerToken, CancellationToken ct = default);
}

/// <summary>approvals-service: raised / approved / denied.</summary>
public interface IAuthorizationFactSource
{
    Task<AuthorizationFacts?> GetAsync(UtilizationFactWindow window, string? bearerToken, CancellationToken ct = default);
}

/// <summary>claims-service: claimed / approved / member share.</summary>
public interface IClaimFactSource
{
    Task<ClaimFacts?> GetAsync(UtilizationFactWindow window, string? bearerToken, CancellationToken ct = default);
}

/// <summary>Shared HTTP mechanics: forward the caller's bearer, build the query, and turn any failure into a
/// null rather than an exception — a utilization report degrades, it does not 500 because one sibling is
/// restarting.</summary>
public abstract class UtilizationFactClient(HttpClient http)
{
    protected static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The URL cap on how many ids go in one request. Above this the caller batches, because a query
    /// string long enough to be truncated by a proxy silently narrows the report to whoever fitted.</summary>
    public const int MaxIdsPerRequest = 200;

    protected async Task<T?> GetAsync<T>(string path, UtilizationFactWindow window, string? bearerToken, CancellationToken ct)
        where T : class
    {
        var ids = string.Join(',', window.BeneficiaryIds);
        var url = $"{path}?from={window.From:yyyy-MM-dd}&to={window.To:yyyy-MM-dd}&beneficiaryIds={ids}";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(bearerToken))
        {
            var token = bearerToken.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                ? bearerToken["Bearer ".Length..] : bearerToken;
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        try
        {
            using var resp = await http.SendAsync(req, ct);
            // A 403 lands here too, and null is the right answer for it: the caller is not entitled to this
            // fact, so the report shows it as unavailable rather than as zero. Fabricating a zero would let a
            // narrower role read a wider role's report and believe the blanks.
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadFromJsonAsync<T>(Json, ct);
        }
        catch (HttpRequestException) { return null; }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return null; }
    }
}

public sealed class HttpEncounterFactSource(HttpClient http) : UtilizationFactClient(http), IEncounterFactSource
{
    public async Task<int?> CountAsync(UtilizationFactWindow window, string? bearerToken, CancellationToken ct = default) =>
        (await GetAsync<CountDto>("/api/v1/encounters/utilization", window, bearerToken, ct))?.EncounterCount;

    private sealed record CountDto(int EncounterCount);
}

public sealed class HttpAuthorizationFactSource(HttpClient http) : UtilizationFactClient(http), IAuthorizationFactSource
{
    public Task<AuthorizationFacts?> GetAsync(UtilizationFactWindow window, string? bearerToken, CancellationToken ct = default) =>
        GetAsync<AuthorizationFacts>("/api/v1/authorizations/utilization", window, bearerToken, ct);
}

public sealed class HttpClaimFactSource(HttpClient http) : UtilizationFactClient(http), IClaimFactSource
{
    public Task<ClaimFacts?> GetAsync(UtilizationFactWindow window, string? bearerToken, CancellationToken ct = default) =>
        GetAsync<ClaimFacts>("/api/v1/claims/utilization", window, bearerToken, ct);
}

/// <summary>What came back, and — just as important — what did not.</summary>
public sealed record UtilizationFacts(ExternalUtilization External, IReadOnlyList<string> UnavailableSources)
{
    public bool IsComplete => UnavailableSources.Count == 0;
}

/// <summary>
/// Asks all three sources concurrently and reports which ones answered.
///
/// The unavailable list is part of the response, not a log line. Someone comparing two groups' utilization has
/// to know that one of them is missing its claim value, and the only place they will reliably see that is on
/// the report itself.
/// </summary>
public sealed class UtilizationFactComposer(
    IEncounterFactSource encounters, IAuthorizationFactSource authorizations, IClaimFactSource claims)
{
    public async Task<UtilizationFacts> ComposeAsync(
        UtilizationFactWindow window, string? bearerToken, CancellationToken ct = default)
    {
        if (window.BeneficiaryIds.Count == 0)
            return new UtilizationFacts(new ExternalUtilization(0, 0, 0, 0, 0m, 0m, 0m), []);

        var encounterTask = encounters.CountAsync(window, bearerToken, ct);
        var authTask = authorizations.GetAsync(window, bearerToken, ct);
        var claimTask = claims.GetAsync(window, bearerToken, ct);
        await Task.WhenAll(encounterTask, authTask, claimTask);

        var encounterCount = await encounterTask;
        var auth = await authTask;
        var claim = await claimTask;

        var unavailable = new List<string>();
        if (encounterCount is null) unavailable.Add("emr-service");
        if (auth is null) unavailable.Add("approvals-service");
        if (claim is null) unavailable.Add("claims-service");

        return new UtilizationFacts(
            new ExternalUtilization(
                encounterCount,
                auth?.Raised, auth?.Approved, auth?.Denied,
                claim?.Claimed, claim?.Approved, claim?.MemberShare,
                claim?.CurrencyCode ?? "EGP"),
            unavailable);
    }

    /// <summary>Format a date the way every fact endpoint parses it. Invariant, never the ambient culture —
    /// an Arabic-locale host must not send an Arabic-numeral date to a sibling service.</summary>
    public static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
