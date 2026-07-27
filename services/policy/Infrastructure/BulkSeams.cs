using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Mersal.Policy.Infrastructure;

// Phase 19.5b — the cross-service write seams the bulk engine uses.
//
// Two of the seven job types change data policy-service does not own: ContactUpdate belongs to patient-service
// and ProviderTierAssignment to provider-service. Both are called with the CALLER's token, exactly as the 19.5
// aggregation is, so each owning service applies its own authorization, its own validation and its own audit.
// A bulk engine that wrote into another schema directly would be a way to bypass every one of those — and the
// person who authored the file would look, in that service's audit trail, like nobody at all.

public sealed record ContactSnapshot(Guid ContactId, string ContactType, string Value, string? PreferredChannel, bool IsPrimary);

public abstract record ContactWriteResult
{
    public sealed record Written(Guid ContactId, ContactSnapshot? Previous) : ContactWriteResult;
    public sealed record NotFound : ContactWriteResult;
    public sealed record Rejected(string Detail) : ContactWriteResult;
    /// <summary>The owning service could not be reached. Distinct from a rejection: a row that could not be
    /// ATTEMPTED must not be recorded as one that was refused, or a retry looks like a duplicate.</summary>
    public sealed record Unavailable(string Detail) : ContactWriteResult;
}

public interface IBeneficiaryContactWriter
{
    Task<ContactWriteResult> UpsertAsync(
        Guid beneficiaryId, string contactType, string value, bool isPrimary, string? preferredChannel,
        string? bearerToken, CancellationToken ct = default);

    Task<IReadOnlyList<ContactSnapshot>?> GetAsync(Guid beneficiaryId, string? bearerToken, CancellationToken ct = default);
}

public sealed class HttpBeneficiaryContactWriter(HttpClient http) : IBeneficiaryContactWriter
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<ContactWriteResult> UpsertAsync(
        Guid beneficiaryId, string contactType, string value, bool isPrimary, string? preferredChannel,
        string? bearerToken, CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/beneficiaries/{beneficiaryId}/contacts")
            {
                Content = JsonContent.Create(new { contactType, value, isPrimary, preferredChannel }, options: Json),
            };
            Authorize(req, bearerToken);
            using var resp = await http.SendAsync(req, ct);

            if (resp.StatusCode == HttpStatusCode.NotFound) return new ContactWriteResult.NotFound();
            if (!resp.IsSuccessStatusCode)
                return (int)resp.StatusCode >= 500
                    ? new ContactWriteResult.Unavailable($"patient-service returned {(int)resp.StatusCode}")
                    : new ContactWriteResult.Rejected(await resp.Content.ReadAsStringAsync(ct));

            var dto = await resp.Content.ReadFromJsonAsync<UpsertDto>(Json, ct);
            return dto is null
                ? new ContactWriteResult.Rejected("patient-service returned no contact reference")
                : new ContactWriteResult.Written(dto.ContactId, dto.Previous);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new ContactWriteResult.Unavailable(ex.Message);
        }
    }

    public async Task<IReadOnlyList<ContactSnapshot>?> GetAsync(
        Guid beneficiaryId, string? bearerToken, CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/beneficiaries/{beneficiaryId}/contacts");
            Authorize(req, bearerToken);
            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadFromJsonAsync<List<ContactSnapshot>>(Json, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    private static void Authorize(HttpRequestMessage req, string? bearerToken)
    {
        if (string.IsNullOrWhiteSpace(bearerToken)) return;
        var token = bearerToken.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? bearerToken["Bearer ".Length..] : bearerToken;
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private sealed record UpsertDto(Guid ContactId, ContactSnapshot? Previous);
}

public abstract record TierAssignmentResult
{
    public sealed record Assigned(Guid AssignmentId) : TierAssignmentResult;
    public sealed record Rejected(string Code, string Detail) : TierAssignmentResult;
    public sealed record Unavailable(string Detail) : TierAssignmentResult;
}

public interface INetworkTierAssignmentWriter
{
    Task<Guid?> ResolveTierAsync(string tierCode, string? bearerToken, CancellationToken ct = default);

    Task<TierAssignmentResult> AssignAsync(
        Guid networkTierId, string scope, Guid scopeRef, DateOnly effectiveFrom, DateOnly? effectiveTo,
        string? bearerToken, CancellationToken ct = default);

    /// <summary>Withdraw an assignment this engine created. <paramref name="correct"/> asks provider-service
    /// for the CORRECTION verb — retroactively void, the right verb for "this row should never have been
    /// applied", which is exactly what a rolled-back bulk row is. provider-service still refuses it once a
    /// claim has adjudicated against the assignment, and that refusal is the guard, not this call.</summary>
    Task<TierAssignmentResult> WithdrawAsync(
        Guid assignmentId, string reason, bool correct, string? bearerToken, CancellationToken ct = default);
}

public sealed class HttpNetworkTierAssignmentWriter(HttpClient http) : INetworkTierAssignmentWriter
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<Guid?> ResolveTierAsync(string tierCode, string? bearerToken, CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/network-tiers");
            Authorize(req, bearerToken);
            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;
            var tiers = await resp.Content.ReadFromJsonAsync<List<TierDto>>(Json, ct);
            return tiers?.FirstOrDefault(t =>
                string.Equals(t.TierCode, tierCode, StringComparison.OrdinalIgnoreCase))?.NetworkTierId;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    public async Task<TierAssignmentResult> AssignAsync(
        Guid networkTierId, string scope, Guid scopeRef, DateOnly effectiveFrom, DateOnly? effectiveTo,
        string? bearerToken, CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/network-tiers/{networkTierId}/assignments")
            {
                Content = JsonContent.Create(new { scope, scopeRef, effectiveFrom, effectiveTo }, options: Json),
            };
            Authorize(req, bearerToken);
            using var resp = await http.SendAsync(req, ct);

            if (!resp.IsSuccessStatusCode)
                return (int)resp.StatusCode >= 500
                    ? new TierAssignmentResult.Unavailable($"provider-service returned {(int)resp.StatusCode}")
                    : new TierAssignmentResult.Rejected("TIER_ASSIGNMENT_REFUSED", await resp.Content.ReadAsStringAsync(ct));

            var dto = await resp.Content.ReadFromJsonAsync<AssignmentDto>(Json, ct);
            return dto is null
                ? new TierAssignmentResult.Rejected("NO_REFERENCE", "provider-service returned no assignment reference")
                : new TierAssignmentResult.Assigned(dto.AssignmentId);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new TierAssignmentResult.Unavailable(ex.Message);
        }
    }

    public async Task<TierAssignmentResult> WithdrawAsync(
        Guid assignmentId, string reason, bool correct, string? bearerToken, CancellationToken ct = default)
    {
        try
        {
            var url = $"/api/v1/network-tiers/assignments/{assignmentId}" +
                      $"?reason={Uri.EscapeDataString(reason)}&correct={(correct ? "true" : "false")}";
            using var req = new HttpRequestMessage(HttpMethod.Delete, url);
            Authorize(req, bearerToken);
            using var resp = await http.SendAsync(req, ct);

            if (!resp.IsSuccessStatusCode)
                return (int)resp.StatusCode >= 500
                    ? new TierAssignmentResult.Unavailable($"provider-service returned {(int)resp.StatusCode}")
                    : new TierAssignmentResult.Rejected("WITHDRAWAL_REFUSED", await resp.Content.ReadAsStringAsync(ct));
            return new TierAssignmentResult.Assigned(assignmentId);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new TierAssignmentResult.Unavailable(ex.Message);
        }
    }

    private static void Authorize(HttpRequestMessage req, string? bearerToken)
    {
        if (string.IsNullOrWhiteSpace(bearerToken)) return;
        var token = bearerToken.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? bearerToken["Bearer ".Length..] : bearerToken;
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private sealed record TierDto(Guid NetworkTierId, string TierCode);
    private sealed record AssignmentDto(Guid AssignmentId);
}
