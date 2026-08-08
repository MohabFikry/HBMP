using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Mersal.Validity;

/// <summary>Resolves the validity period in force for an artefact, in days.</summary>
public interface IValidityPolicySource
{
    /// <summary>
    /// How many days something written NOW should stay actionable.
    /// </summary>
    /// <remarks>
    /// Returns a number, always. There is no "unknown" and no nullable: every caller is on a clinical write
    /// path, and the only alternatives to a number are refusing to write the prescription or writing one
    /// that never expires. See <see cref="ValidityPolicy.DefaultDays"/> for why the fallback is conservative
    /// rather than absent.
    /// </remarks>
    Task<int> DaysAsync(ValidityArtefact artefact, string? bearerToken, CancellationToken ct = default);
}

/// <summary>The platform default for every artefact — the test/offline stand-in.</summary>
public sealed class DefaultValidityPolicySource : IValidityPolicySource
{
    public Task<int> DaysAsync(ValidityArtefact artefact, string? bearerToken, CancellationToken ct = default)
        => Task.FromResult(ValidityPolicy.DefaultDays);
}

/// <summary>
/// Reads the tenant's configured validity periods from admin-service, cached, with a last-known-good fallback.
/// </summary>
/// <remarks>
/// <para>
/// <b>Three layers, and the order matters.</b> A fresh read (cached briefly, because a supervisor who changes
/// the number expects the next prescription to use it); then the last value this process successfully read,
/// held far longer; then the compiled-in default. A config outage therefore degrades to the number that was
/// correct five minutes ago, and a cold start during an outage degrades to ten days.
/// </para>
/// <para>
/// <b>What it must never do is degrade to "no expiry".</b> That is the state this whole feature exists to
/// remove, and it is exactly what a naive <c>try { … } catch { return null; }</c> would reintroduce — quietly,
/// on the one day the config service is down, in a way no screen would report. The fail-safe direction here
/// is SHORTER validity, never longer: a prescription that expires sooner than the tenant intended sends a
/// patient back for an extension, which is a nuisance; one that never expires is a clinical decision with no
/// end date.
/// </para>
/// </remarks>
public sealed class HttpValidityPolicySource(
    HttpClient http, IMemoryCache cache, ILogger<HttpValidityPolicySource> logger) : IValidityPolicySource
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Short: a supervisor changing the window should see it take effect within a few minutes,
    /// and the endpoint returns four integers.</summary>
    private static readonly TimeSpan FreshFor = TimeSpan.FromMinutes(5);

    /// <summary>Long: this is the "what did we last know" tier, and it is only ever consulted when the fresh
    /// read failed. A day is well past the point where someone would have noticed the outage.</summary>
    private static readonly TimeSpan LastGoodFor = TimeSpan.FromHours(24);

    public async Task<int> DaysAsync(ValidityArtefact artefact, string? bearerToken, CancellationToken ct = default)
    {
        var freshKey = $"validity:fresh:{artefact}";
        if (cache.TryGetValue<int>(freshKey, out var fresh) && ValidityPolicy.IsInRange(fresh)) return fresh;

        var lastGoodKey = $"validity:last-good:{artefact}";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/admin/validity-policy");
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
                string.Equals(i.Artefact, artefact.ToString(), StringComparison.OrdinalIgnoreCase));

            // A 200 that does not mention this artefact is a contract breach, not a configured absence — the
            // endpoint answers for every artefact by construction. Treated as a failed read.
            if (item is null) throw new InvalidOperationException($"validity-policy response omitted {artefact}");

            var days = ValidityPolicy.DaysFrom(item.Days.ToString(System.Globalization.CultureInfo.InvariantCulture));
            cache.Set(freshKey, days, FreshFor);
            cache.Set(lastGoodKey, days, LastGoodFor);
            return days;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            if (cache.TryGetValue<int>(lastGoodKey, out var lastGood) && ValidityPolicy.IsInRange(lastGood))
            {
                logger.LogWarning(ex,
                    "Validity policy for {Artefact} could not be read; using the last known value of {Days} days.",
                    artefact, lastGood);
                return lastGood;
            }

            // Loud, because this one IS a degradation nobody chose: the tenant may have configured something
            // other than ten days and this process has never managed to find out what.
            logger.LogError(ex,
                "Validity policy for {Artefact} could not be read and none is cached; falling back to the "
                + "platform default of {Days} days. Items written now may expire sooner than this tenant intends.",
                artefact, ValidityPolicy.DefaultDays);
            return ValidityPolicy.DefaultDays;
        }
    }

    private sealed record PolicyDto(List<ItemDto>? Items);
    private sealed record ItemDto(string Artefact, int Days);
}
