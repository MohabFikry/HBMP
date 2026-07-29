using System.Net.Http.Headers;
using System.Text.Json;
using Mersal.CallCentre.Domain;
using Mersal.CallCentre.Infrastructure;

namespace Mersal.CallCentre.Api;

/// <summary>
/// Phase 20.2 — the Call Centre member 360, RE-POINTED at the one canonical profile contract (design 39 §2:
/// "call-centre member 360 → becomes the call-centre PROJECTION of the profile").
///
/// <para><b>What moved and what did not.</b> Identity, coverage and open referrals now come from
/// profile-service, so "what may the call centre see about a member" is answered once — in the design-39 §4
/// matrix — instead of once here and once there. Appointments, contacts and due follow-ups do NOT come from
/// the profile, because they are not profile sections: the profile models a member's RECORD, and those three
/// are the call-centre's ACTION affordances (the slots an agent can move, the number they can correct, the
/// follow-up they can book). Design 39 §3 has no cell for any of them, and inventing one so this class could
/// be shorter would have widened the contract to fit the implementation.</para>
///
/// <para><b>The verification gate is untouched.</b> It stays exactly where phase 15 put it — on the endpoint,
/// before this is ever called — and profile-service independently refuses a call-centre principal that cannot
/// name a verified interaction (ADR-0026). Two checks of the same rule, from the one source of truth.</para>
///
/// <para><b>Known cost.</b> The onward profile call and <see cref="HttpMemberDirectory"/> both resolve identity,
/// so a 360 makes one extra eligibility read. That is the transitional price of removing the duplicate
/// decision; collapsing it means giving the profile contract an <c>appointments</c> section, which is a design
/// change to doc 39 rather than a refactor, and is not one to make quietly inside a consolidation.</para>
/// </summary>
public sealed class ProfileBackedMemberDirectory(IHttpClientFactory factory, HttpMemberDirectory inner)
    : IMemberDirectory
{
    /// <summary>Only the sections the profile actually owns for this role. `callHistory` is deliberately absent:
    /// the workspace has its own call list, and the 360 is about the MEMBER, not about our contact log.</summary>
    private const string Sections = "header,alerts,coverage,referrals";

    /// <summary>Pre-verification search is not an aggregate — it is a lookup that returns a name and which
    /// identifier TYPES to challenge on. It stays exactly as phase 15 built it.</summary>
    public Task<MemberSearchResult> SearchAsync(string query, string? bearerToken, CancellationToken ct = default) =>
        inner.SearchAsync(query, bearerToken, ct);

    public async Task<Member360?> AssembleAsync(
        Guid beneficiaryId, string? bearer, Guid? interactionId = null, CancellationToken ct = default)
    {
        var profile = await GetProfileAsync(beneficiaryId, bearer, interactionId, ct);
        // Fail-CLOSED: without the profile there is no identity spine, and a 360 that invents one is worse than
        // a 404 — an agent would read a member's coverage next to somebody else's name.
        if (profile is null) return null;

        using var doc = profile;
        var sections = Index(doc.RootElement);
        if (!sections.TryGetValue("header", out var header) || !IsVisible(header)) return null;
        var identity = header.GetProperty("data");

        var status = Str(identity, "status") ?? "Pending";
        var coverage = sections.TryGetValue("coverage", out var cov) && IsVisible(cov)
            ? cov.GetProperty("data")
            : default;

        // The call-centre ACTION affordances, from the services that own them. Fail-soft exactly as before: an
        // unreachable section degrades to empty, never to fabricated data.
        var actions = await inner.AssembleAsync(beneficiaryId, bearer, interactionId, ct);

        return new Member360(
            new MemberIdentity(
                beneficiaryId,
                Str(identity, "memberNo"),
                Str(identity, "displayName") ?? "—",
                Str(identity, "ageBand"),
                status,
                StatusCue.For(status)),
            CoverageLinesFrom(coverage),
            actions?.Contacts ?? [],
            actions?.Appointments ?? [],
            ReferralsFrom(sections),
            actions?.FollowUpsDue ?? []);
    }

    private static IReadOnlyList<CoverageLine> CoverageLinesFrom(JsonElement coverage)
    {
        if (coverage.ValueKind != JsonValueKind.Object
            || !coverage.TryGetProperty("categories", out var categories)
            || categories.ValueKind != JsonValueKind.Array)
            return [];

        return [.. categories.EnumerateArray().Select(c => new CoverageLine(
            Str(c, "category") ?? "—", Dec(c, "annualLimit"), Dec(c, "remaining")))];
    }

    private static IReadOnlyList<MemberReferral> ReferralsFrom(IReadOnlyDictionary<string, JsonElement> sections)
    {
        if (!sections.TryGetValue("referrals", out var section) || !IsVisible(section)) return [];
        var data = section.GetProperty("data");
        if (!data.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) return [];

        // OPEN referrals only — the agent's affordance is "convert this to a booking", and a completed referral
        // is not convertible. The profile returns the full history; narrowing to what is actionable is the
        // call-centre's own view of it.
        return [.. items.EnumerateArray()
            .Where(r => Str(r, "status") is "Requested" or "Accepted" or "Scheduled")
            .Select(r => new MemberReferral(
                Str(r, "referralNo") ?? Str(r, "referralRef") ?? "—",
                Str(r, "status") ?? "—",
                Str(r, "targetSpecialty") ?? Str(r, "requestedSpecialty"),
                Moment(r, "createdAt")))];
    }

    private async Task<JsonDocument?> GetProfileAsync(Guid beneficiaryId, string? bearer, Guid? interactionId, CancellationToken ct)
    {
        try
        {
            var http = factory.CreateClient("profile");
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                // interactionId is what lets profile-service confirm the caller was verified on THIS call.
                $"/api/v1/patients/{beneficiaryId}/profile?sections={Sections}&purpose=call-centre"
                + (interactionId is { } id ? $"&interactionId={id}" : ""));
            if (!string.IsNullOrWhiteSpace(bearer))
            {
                var token = bearer.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                    ? bearer["Bearer ".Length..] : bearer;
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return null;
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        }
        catch (HttpRequestException) { return null; }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return null; }
    }

    private static Dictionary<string, JsonElement> Index(JsonElement root)
    {
        var map = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (!root.TryGetProperty("sections", out var sections) || sections.ValueKind != JsonValueKind.Array)
            return map;
        foreach (var s in sections.EnumerateArray())
        {
            if (s.TryGetProperty("key", out var k) && k.GetString() is { } key) map[key] = s;
        }
        return map;
    }

    private static bool IsVisible(JsonElement section) =>
        section.TryGetProperty("state", out var st) && st.GetString() == "Visible"
        && section.TryGetProperty("data", out _);

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static decimal? Dec(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d) ? d : null;

    private static DateTimeOffset? Moment(JsonElement e, string name) =>
        Str(e, name) is { } s && DateTimeOffset.TryParse(
            s, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out var d) ? d : null;
}
