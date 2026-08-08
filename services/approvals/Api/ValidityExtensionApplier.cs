using System.Net.Http.Headers;
using System.Text.Json;
using Mersal.Approvals.Domain;

namespace Mersal.Approvals.Api;

/// <summary>
/// Carries an approved extension back to the service that owns the expired item.
/// </summary>
/// <remarks>
/// <para>
/// approvals-service records the DECISION; pharmacy and orders own the thing being decided about, and only
/// they may move its expiry. So an approval has to reach them.
/// </para>
/// <para>
/// <b>It runs BEFORE the decision is committed, and a failure refuses the decision.</b> The alternative
/// orderings are both worse. Record-then-call leaves an authorization that says Approved beside a
/// prescription the counter still cannot dispense — the pharmacist is told yes by one screen and no by the
/// next, with nothing to explain the disagreement. Fire-and-forget is the same state, reached silently. This
/// way the reviewer either gets both or neither, and a failure is something they can see and retry.
/// </para>
/// <para>
/// The reviewer's OWN token is forwarded. pharmacy and orders gate their extend endpoints on
/// <c>auth:decide</c> — "only someone who may decide an authorization may move an expiry" — which is an
/// honest statement of where the authority lives and needs no machine credential to express.
/// </para>
/// </remarks>
public interface IValidityExtensionApplier
{
    /// <summary>Reset the item's validity. Returns the new expiry, or null with a reason when it could not be applied.</summary>
    Task<ExtensionOutcome> ApplyAsync(Authorization auth, string? bearerToken, CancellationToken ct = default);
}

/// <param name="NewExpiry">Null when <paramref name="Applied"/> is false.</param>
public readonly record struct ExtensionOutcome(bool Applied, DateTimeOffset? NewExpiry, string? Failure)
{
    public static ExtensionOutcome Ok(DateTimeOffset expiry) => new(true, expiry, null);
    public static ExtensionOutcome Failed(string reason) => new(false, null, reason);
}

public sealed class HttpValidityExtensionApplier(
    IHttpClientFactory factory, ILogger<HttpValidityExtensionApplier> logger) : IValidityExtensionApplier
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<ExtensionOutcome> ApplyAsync(Authorization auth, string? bearerToken, CancellationToken ct = default)
    {
        if (auth.Source != AuthSource.ValidityExtension)
            return ExtensionOutcome.Failed("not a validity-extension authorization");
        if (!Guid.TryParse(auth.SourceRef, out var itemId))
            return ExtensionOutcome.Failed("the authorization carries no usable item reference");

        var itemType = ItemTypeOf(auth);
        var (clientName, path) = itemType switch
        {
            ExtendableItem.Prescription => ("pharmacy", $"/api/v1/prescriptions/{itemId}/extend-validity"),
            ExtendableItem.InvestigationOrder => ("orders", $"/api/v1/investigation-orders/{itemId}/extend-validity"),
            _ => (null, null),
        };
        if (clientName is null || path is null)
            return ExtensionOutcome.Failed("the authorization does not say what kind of item it extends");

        try
        {
            var http = factory.CreateClient(clientName);
            using var req = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = JsonContent.Create(new { authorizationId = auth.AuthorizationId, authNo = auth.AuthNo }, options: Json),
            };
            // Idempotent on the authorization: a retried apply after a timeout must not stack a second
            // validity period on top of the first.
            req.Headers.Add("Idempotency-Key", $"extend:{auth.AuthorizationId}");
            if (!string.IsNullOrWhiteSpace(bearerToken))
            {
                var token = bearerToken.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                    ? bearerToken["Bearer ".Length..] : bearerToken;
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                logger.LogError("extension apply refused by {Service}: {Status} {Body}", clientName, (int)resp.StatusCode, body);
                return ExtensionOutcome.Failed($"{clientName}-service refused the extension ({(int)resp.StatusCode}).");
            }

            var result = await resp.Content.ReadFromJsonAsync<ExtendDto>(Json, ct);
            return result?.ExpiresAt is { } expiry
                ? ExtensionOutcome.Ok(expiry)
                // A 200 with no expiry is a contract breach, not a quiet success. Treated as a failure so the
                // decision is refused rather than recorded against an unknown outcome.
                : ExtensionOutcome.Failed($"{clientName}-service accepted the extension but returned no new expiry.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogError(ex, "extension apply could not reach {Service}", clientName);
            return ExtensionOutcome.Failed($"{clientName}-service could not be reached, so the extension was not applied.");
        }
    }

    /// <summary>Reads the item type back out of the request's stored scope.</summary>
    internal static ExtendableItem ItemTypeOf(Authorization auth)
    {
        try
        {
            using var doc = JsonDocument.Parse(auth.RequestedScope);
            if (doc.RootElement.TryGetProperty("itemType", out var t)
                && Enum.TryParse<ExtendableItem>(t.GetString(), ignoreCase: true, out var parsed))
                return parsed;
        }
        catch (JsonException) { /* falls through to the default below */ }
        return ExtendableItem.Prescription;
    }

    private sealed record ExtendDto(DateTimeOffset? ExpiresAt);
}
