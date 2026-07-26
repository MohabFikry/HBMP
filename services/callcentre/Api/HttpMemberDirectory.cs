using System.Net.Http.Headers;
using System.Text.Json;
using Mersal.CallCentre.Domain;
using Mersal.CallCentre.Infrastructure;

namespace Mersal.CallCentre.Api;

/// <summary>Composes the Call Centre member view from sibling services under the caller's bearer token (each sibling
/// enforces its own authorization — defense in depth). Identity + coverage/limits come from eligibility's reception
/// search (already clinical-free, phone-searchable); appointments across ALL branches from emr; open referrals from
/// pharmacy; contacts from patient. Every sibling is fail-soft: an unreachable section degrades to empty — never
/// fabricated, and never clinical (the target DTOs cannot hold clinical fields).</summary>
public sealed class HttpMemberDirectory(IHttpClientFactory factory) : IMemberDirectory
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // A sensible default challenge set. The concrete availability is narrowed to what the reception card exposes
    // (MemberNo) plus the always-available demographic/contact challenges.
    private static readonly string[] BaseChallenges = ["DateOfBirth", "Phone", "NationalId", "FullName"];

    public async Task<MemberSearchResult> SearchAsync(string query, string? bearer, CancellationToken ct = default)
    {
        var resp = await GetAsync<ReceptionSearchDto>("eligibility", $"/api/v1/reception/search?q={Uri.EscapeDataString(query)}", bearer, ct);
        var matches = (resp?.Results ?? []).Select(card =>
        {
            var challenges = new List<string>();
            if (!string.IsNullOrWhiteSpace(card.Identity?.MemberNo)) challenges.Add("MemberNo");
            challenges.AddRange(BaseChallenges);
            return new MemberMatch(
                card.Identity?.BeneficiaryId ?? Guid.Empty,
                card.Identity?.DisplayName ?? "—",
                card.Identity?.MemberNo,
                challenges);
        }).Where(m => m.BeneficiaryId != Guid.Empty).ToList();
        return new MemberSearchResult(query, matches.Count, matches);
    }

    public async Task<Member360?> AssembleAsync(Guid beneficiaryId, string? bearer, CancellationToken ct = default)
    {
        // Identity + coverage are the spine — resolved via the reception card (clinical-free). Without it the member
        // cannot be presented, so return null (→ 404). A search by the id surfaces the single card.
        var recep = await GetAsync<ReceptionSearchDto>("eligibility", $"/api/v1/reception/search?q={beneficiaryId}", bearer, ct);
        var card = (recep?.Results ?? []).FirstOrDefault(c => c.Identity?.BeneficiaryId == beneficiaryId)
                   ?? (recep?.Results ?? []).FirstOrDefault();
        if (card?.Identity is null) return null;

        var appts = await GetAsync<List<AppointmentDto>>("emr", $"/api/v1/beneficiaries/{beneficiaryId}/appointments", bearer, ct) ?? [];
        var contacts = await GetAsync<List<ContactDto>>("patient", $"/api/v1/beneficiaries/{beneficiaryId}/contacts", bearer, ct) ?? [];
        var referrals = await GetAsync<List<ReferralDto>>("pharmacy", $"/api/v1/beneficiaries/{beneficiaryId}/referrals?status=open", bearer, ct) ?? [];
        var followUps = await GetAsync<List<FollowUpDto>>("emr", $"/api/v1/beneficiaries/{beneficiaryId}/follow-ups?status=due", bearer, ct) ?? [];

        return new Member360(
            new MemberIdentity(beneficiaryId, card.Identity.MemberNo, card.Identity.DisplayName ?? "—",
                card.Identity.AgeBand, card.Identity.Status ?? "Pending", StatusCue.For(card.Identity.Status ?? "Pending")),
            (card.RemainingLimits ?? []).Select(l => new CoverageLine(l.Category ?? "—", l.AnnualLimit, l.RemainingLimit)).ToList(),
            contacts.Select(c => new MemberContact(c.ContactId, c.Kind ?? "Phone", c.Value ?? "", c.IsPrimary, c.PreferredChannel)).ToList(),
            appts.Select(a => new MemberAppointment(a.AppointmentId, a.AppointmentType ?? "—", a.Status ?? "—",
                a.ScheduledStart, a.BranchName, a.DoctorName, a.Specialty,
                CanReschedule: IsChangeable(a.Status), CanCancel: IsChangeable(a.Status))).ToList(),
            referrals.Select(r => new MemberReferral(r.ReferralRef ?? "—", r.Status ?? "—", r.RequestedSpecialty, r.CreatedAt)).ToList(),
            followUps.Select(f => new MemberFollowUp(f.OriginEncounterId, f.Reason, f.DueDate, f.Specialty)).ToList());
    }

    private static bool IsChangeable(string? status) =>
        status is "Scheduled" or "Booked" or "Confirmed";

    private async Task<T?> GetAsync<T>(string client, string path, string? bearer, CancellationToken ct)
    {
        try
        {
            var http = factory.CreateClient(client);
            using var req = new HttpRequestMessage(HttpMethod.Get, path);
            if (!string.IsNullOrWhiteSpace(bearer))
            {
                var token = bearer.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? bearer["Bearer ".Length..] : bearer;
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return default;
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            return await JsonSerializer.DeserializeAsync<T>(stream, Json, ct);
        }
        catch (HttpRequestException) { return default; }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return default; }
    }

    // Sibling response shapes (only the fields we project — nothing clinical is even deserialized).
    private sealed record ReceptionSearchDto(List<ReceptionCardDto>? Results);
    private sealed record ReceptionCardDto(ReceptionIdentityDto? Identity, List<LimitDto>? RemainingLimits);
    private sealed record ReceptionIdentityDto(Guid BeneficiaryId, string? MemberNo, string? DisplayName, string? AgeBand, string? Status);
    private sealed record LimitDto(string? Category, decimal? AnnualLimit, decimal? RemainingLimit);
    private sealed record AppointmentDto(Guid AppointmentId, string? AppointmentType, string? Status,
        DateTimeOffset ScheduledStart, string? BranchName, string? DoctorName, string? Specialty);
    private sealed record ContactDto(Guid ContactId, string? Kind, string? Value, bool IsPrimary, string? PreferredChannel);
    private sealed record ReferralDto(string? ReferralRef, string? Status, string? RequestedSpecialty, DateTimeOffset? CreatedAt);
    private sealed record FollowUpDto(Guid? OriginEncounterId, string? Reason, DateOnly? DueDate, string? Specialty);
}
