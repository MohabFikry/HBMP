using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Policy.Infrastructure;

/// <summary>
/// Phase 19.2 — is this beneficiary a real, Active person?
///
/// Enrolling a Pending, Suspended or Blocked beneficiary would generate coverage that eligibility then refuses
/// on every visit: a membership that looks live in every report and works nowhere. patient-service owns the
/// answer, so this reads it rather than duplicating the lifecycle here.
/// </summary>
public interface IBeneficiaryStatusProbe
{
    /// <returns>The member status string, or null when the beneficiary does not exist.</returns>
    Task<string?> GetStatusAsync(Guid beneficiaryId, string? bearerToken, CancellationToken ct = default);
}

/// <summary>Reads patient-service, forwarding the caller's bearer so the lookup is authorized as them.</summary>
public sealed class HttpBeneficiaryStatusProbe(HttpClient http) : IBeneficiaryStatusProbe
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<string?> GetStatusAsync(Guid beneficiaryId, string? bearerToken, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/beneficiaries/{beneficiaryId}");
        if (!string.IsNullOrWhiteSpace(bearerToken))
        {
            var token = bearerToken.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                ? bearerToken["Bearer ".Length..] : bearerToken;
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        using var resp = await http.SendAsync(req, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        // NOT fail-soft. An unreachable patient-service means we cannot tell an Active beneficiary from a
        // Blocked one, and enrolling on that basis is exactly the mistake this check exists to prevent.
        resp.EnsureSuccessStatusCode();
        var dto = await resp.Content.ReadFromJsonAsync<BeneficiaryDto>(Json, ct);
        return dto?.Status;
    }

    private sealed record BeneficiaryDto(Guid BeneficiaryId, string Status);
}

/// <summary>Issues the human-facing member number (<c>MEM-YYYY-NNNNNN</c>, 0A §3).</summary>
public interface IMemberNoIssuer
{
    Task<string> NextAsync(DateOnly effectiveFrom, CancellationToken ct = default);
}

/// <summary>
/// Sequential per year, derived from the highest existing number rather than a counter table.
///
/// The uniqueness guarantee is the partial unique index on <c>member_no</c>, not this method: two concurrent
/// enrolments can compute the same next number, and the loser gets a 23505 the caller retries. Reading the max
/// is the cheap path that is right almost always; the index is what makes "almost" safe.
/// </summary>
public sealed class SequentialMemberNoIssuer(PolicyDbContext db) : IMemberNoIssuer
{
    public async Task<string> NextAsync(DateOnly effectiveFrom, CancellationToken ct = default)
    {
        var prefix = $"MEM-{effectiveFrom.Year}-";
        var highest = await db.Enrollments.AsNoTracking()
            .Where(e => e.MemberNo.StartsWith(prefix))
            .OrderByDescending(e => e.MemberNo)
            .Select(e => e.MemberNo)
            .FirstOrDefaultAsync(ct);

        var next = 1;
        if (highest is not null && int.TryParse(highest[prefix.Length..], out var parsed)) next = parsed + 1;
        return $"{prefix}{next:D6}";
    }
}
