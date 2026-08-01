using System.Net;
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
    //
    // FullName is NOT here, and must not be added back: the display name is shown on the search hit below, so
    // "confirm your name" is a question the agent can answer off their own screen. See VerificationPolicy —
    // a type is only challengeable while its value stays undisclosed pre-verification.
    private static readonly string[] BaseChallenges = ["DateOfBirth", "Phone", "NationalId"];

    public async Task<MemberSearchResult> SearchAsync(string query, string? bearer, CancellationToken ct = default)
    {
        // Required: an empty search result and a refused search must never look the same to the agent.
        var resp = await GetAsync<ReceptionSearchDto>("eligibility", $"/api/v1/reception/search?q={Uri.EscapeDataString(query)}", bearer, ct, required: true);
        var matches = (resp?.Results ?? []).Select(card =>
        {
            var challenges = new List<string>();
            if (!string.IsNullOrWhiteSpace(card.Identity?.MemberNo)) challenges.Add("MemberNo");
            challenges.AddRange(BaseChallenges);
            return new MemberMatch(
                card.Identity?.BeneficiaryId ?? Guid.Empty,
                card.Identity?.DisplayName ?? "—",
                // MASKED at the source, so the full value never crosses the wire pre-verification. Masking in
                // the UI instead would leave it sitting in the network tab and in any client that skips the
                // formatting — the value has to not be sent, not merely not be drawn.
                VerificationPolicy.MaskIdentifier(card.Identity?.MemberNo),
                challenges);
        }).Where(m => m.BeneficiaryId != Guid.Empty).ToList();
        return new MemberSearchResult(query, matches.Count, matches);
    }

    public async Task<Member360?> AssembleAsync(
        Guid beneficiaryId, string? bearer, Guid? interactionId = null, CancellationToken ct = default)
    {
        _ = interactionId;   // this path reads eligibility/emr directly; only profile-service gates on it.
        // Identity + coverage are the spine — resolved via the reception card (clinical-free). Without it the member
        // cannot be presented, so return null (→ 404). A search by the id surfaces the single card.
        var recep = await GetAsync<ReceptionSearchDto>("eligibility", $"/api/v1/reception/search?q={beneficiaryId}", bearer, ct, required: true);
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
                CanReschedule: IsChangeable(a.Status), CanCancel: IsChangeable(a.Status),
                RowVersion: a.RowVersion)).ToList(),
            referrals.Select(r => new MemberReferral(r.ReferralRef ?? "—", r.Status ?? "—", r.RequestedSpecialty, r.CreatedAt)).ToList(),
            // f.Reason is deliberately NOT projected — see MemberFollowUp. It is not deserialized either.
            followUps.Select(f => new MemberFollowUp(f.OriginEncounterId, f.DueDate, f.Specialty)).ToList());
    }

    private static bool IsChangeable(string? status) =>
        status is "Scheduled" or "Booked" or "Confirmed";

    /// <summary>Read a sibling service.
    ///
    /// <paramref name="required"/> marks a call the caller CANNOT do without. Everything used to degrade to
    /// default on any failure, which meant a 403 from a sibling — a scope the agent lacks, i.e. a configuration
    /// fault — arrived at the UI as "no such member". A wrong answer that looks like a valid one is worse than
    /// an error: the agent tells the member they are not registered. Required calls now surface the refusal;
    /// optional side panels still degrade, because an appointment list that failed to load is genuinely better
    /// shown empty than blocking the whole 360.</summary>
    private async Task<T?> GetAsync<T>(string client, string path, string? bearer, CancellationToken ct, bool required = false)
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
            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden && required)
                throw new SiblingRefusedException(client, path, (int)resp.StatusCode);
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
        DateTimeOffset ScheduledStart, string? BranchName, string? DoctorName, string? Specialty, uint RowVersion);
    private sealed record ContactDto(Guid ContactId, string? Kind, string? Value, bool IsPrimary, string? PreferredChannel);
    private sealed record ReferralDto(string? ReferralRef, string? Status, string? RequestedSpecialty, DateTimeOffset? CreatedAt);
    // No Reason property: the clinical free-text on an emr follow-up is not deserialized at all, so it cannot
    // reach this process, let alone the agent. "Only the fields we project" is enforced by the shape itself.
    private sealed record FollowUpDto(Guid? OriginEncounterId, DateOnly? DueDate, string? Specialty);
}


/// <summary>A sibling service refused this call (401/403). Distinct from "found nothing" on purpose: the two used
/// to be indistinguishable, and the UI presented a permissions fault as an absent member.</summary>
public sealed class SiblingRefusedException(string service, string path, int status)
    : Exception($"{service} refused {path} with {status}")
{
    public string Service { get; } = service;
    public int Status { get; } = status;
}
