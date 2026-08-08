using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Mersal.Validity;

/// <summary>What a document kind's policy is, resolved for the calling tenant.</summary>
public interface IDocumentValidityPolicySource
{
    /// <summary>
    /// The renewal cadence and the warning thresholds in force for this kind.
    /// </summary>
    /// <remarks>
    /// Returns values, always. There is no "unknown": every caller is deciding whether to warn somebody about
    /// an expiring document, and the alternatives to a number are warning nobody or warning constantly. See
    /// <see cref="DocumentValidityPolicy.DefaultDays"/> for why the fallback is a constant rather than absent.
    /// </remarks>
    Task<(int Days, IReadOnlyList<int> WarnDays)> ForAsync(
        DocumentKind kind, string? bearerToken, CancellationToken ct = default);
}

/// <summary>The platform defaults — the test/offline stand-in, and what a cold start during an outage gets.</summary>
public sealed class DefaultDocumentValidityPolicySource : IDocumentValidityPolicySource
{
    public Task<(int, IReadOnlyList<int>)> ForAsync(
        DocumentKind kind, string? bearerToken, CancellationToken ct = default) =>
        Task.FromResult((DocumentValidityPolicy.DefaultDays, DocumentValidityPolicy.DefaultWarnDays));
}

/// <summary>
/// Reads the tenant's configured document policy from admin-service, cached, with a last-known-good fallback.
/// </summary>
/// <remarks>
/// <para>
/// The deliberate twin of <see cref="HttpValidityPolicySource"/>, with the same three layers in the same
/// order: a briefly-cached fresh read, then the last value this process successfully read, then the
/// compiled-in default. A config outage degrades to the numbers that were correct five minutes ago; a cold
/// start during an outage degrades to 365 days and the [90, 60, 30] thresholds.
/// </para>
/// <para>
/// <b>What it must never degrade to is "warn nobody".</b> An empty threshold list would silence every
/// expiring credential on the platform on the one day the config service is down, and no screen would report
/// it — the failure would look exactly like a quiet week. So the fallback direction is toward MORE warning,
/// never less: a warning somebody did not need is a nuisance, a licence that lapsed unnoticed is a clinician
/// practising without one.
/// </para>
/// </remarks>
public sealed class HttpDocumentValidityPolicySource(
    HttpClient http, IMemoryCache cache, ILogger<HttpDocumentValidityPolicySource> logger)
    : IDocumentValidityPolicySource
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly TimeSpan FreshFor = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan LastGoodFor = TimeSpan.FromHours(24);

    private sealed record Policy(int Days, IReadOnlyList<int> WarnDays);

    public async Task<(int, IReadOnlyList<int>)> ForAsync(
        DocumentKind kind, string? bearerToken, CancellationToken ct = default)
    {
        var freshKey = $"doc-validity:fresh:{kind}";
        if (cache.TryGetValue<Policy>(freshKey, out var fresh) && fresh is not null)
            return (fresh.Days, fresh.WarnDays);

        var lastGoodKey = $"doc-validity:last-good:{kind}";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/admin/document-validity");
            if (!string.IsNullOrWhiteSpace(bearerToken))
            {
                var token = bearerToken.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                    ? bearerToken["Bearer ".Length..] : bearerToken;
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
            using var resp = await http.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();

            var body = await resp.Content.ReadFromJsonAsync<PolicyDto>(Json, ct);
            var item = body?.Items?.FirstOrDefault(i =>
                string.Equals(i.Kind, kind.ToString(), StringComparison.OrdinalIgnoreCase));

            // A 200 that does not mention this kind is a contract breach, not a configured absence — the
            // endpoint answers for every kind by construction. Treated as a failed read.
            if (item is null) throw new InvalidOperationException($"document-validity response omitted {kind}");

            var policy = new Policy(
                DocumentValidityPolicy.IsInRange(item.Days) ? item.Days : DocumentValidityPolicy.DefaultDays,
                item.WarnDays is { Count: > 0 }
                    ? DocumentValidityPolicy.WarnDaysFrom(DocumentValidityPolicy.WarnDaysToValue(item.WarnDays))
                    : DocumentValidityPolicy.DefaultWarnDays);

            cache.Set(freshKey, policy, FreshFor);
            cache.Set(lastGoodKey, policy, LastGoodFor);
            return (policy.Days, policy.WarnDays);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            if (cache.TryGetValue<Policy>(lastGoodKey, out var lastGood) && lastGood is not null)
            {
                logger.LogWarning(ex,
                    "Document validity policy for {Kind} could not be read; using the last known values "
                    + "({Days} days, thresholds {Warn}).", kind, lastGood.Days, string.Join(",", lastGood.WarnDays));
                return (lastGood.Days, lastGood.WarnDays);
            }

            logger.LogError(ex,
                "Document validity policy for {Kind} could not be read and none is cached; falling back to the "
                + "platform defaults ({Days} days, thresholds {Warn}). This tenant may have configured others.",
                kind, DocumentValidityPolicy.DefaultDays, string.Join(",", DocumentValidityPolicy.DefaultWarnDays));
            return (DocumentValidityPolicy.DefaultDays, DocumentValidityPolicy.DefaultWarnDays);
        }
    }

    private sealed record PolicyDto(List<ItemDto>? Items);
    private sealed record ItemDto(string Kind, int Days, List<int>? WarnDays);
}
