using System.Net.Http.Headers;
using System.Text.Json;
using Mersal.Case.Domain;
using Mersal.Case.Infrastructure;

namespace Mersal.Case.Api;

/// <summary>
/// Phase 20.2 — the case beneficiary-360, RE-POINTED at the one canonical profile contract (design 39 §2).
///
/// <para><b>What this replaces and why.</b> The previous assembler fanned out to five sibling endpoints of its
/// own and mapped them into <see cref="Beneficiary360"/>. That made case-service the platform's SECOND
/// aggregation path — a second place that decides what a coordinator may see about a patient, diverging from
/// the first the moment either changed. Design 39 §2 is explicit that a fifth aggregate would guarantee drift,
/// so the fan-out is gone: this asks profile-service for the sections a case manager's matrix row grants, and
/// shapes the answer into the DTO existing callers already consume.</para>
///
/// <para><b>Existing callers keep their shape.</b> The endpoint, the route and the
/// <see cref="Beneficiary360"/> contract are unchanged. What changed is where the data comes from — and three
/// of the five endpoints the old assembler called (<c>/care-plan-summary</c>, <c>/coordination-summary</c>,
/// <c>/beneficiaries/{id}/appointments</c>) never existed, so those blocks were silently empty in every
/// environment. The active-diagnosis list is now populated for the first time, from the profile's
/// past-medical-history section, which the matrix grants an assigned case manager as a summary.</para>
///
/// <para><b>The gates are unchanged and now doubled.</b> The call carries the caller's own bearer, so
/// profile-service authorizes it, and each owning service authorizes profile-service's onward call. An
/// unassigned case manager reaches neither: their matrix row makes the sections Restricted, which is fetched
/// from nothing.</para>
/// </summary>
public sealed class ProfileBackedBeneficiary360Assembler(IHttpClientFactory factory) : IBeneficiary360Assembler
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Exactly the sections build prompt 20.2 names for the case portal, plus past-medical-history —
    /// which is where the coord-visible diagnosis summary lives (11-permission-matrix §4).</summary>
    private const string Sections = "header,alerts,coverage,pastMedicalHistory,caseManagement,notes,timeline";

    public async Task<Beneficiary360?> AssembleAsync(CaseFile c, string? bearerToken, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(c);

        var profile = await GetProfileAsync(c.BeneficiaryId, bearerToken, ct);
        // Fail-CLOSED, as before: a coordination view that cannot be assembled is a 502, never a partial leak.
        if (profile is null) return null;

        using var doc = profile;
        var sections = Index(doc.RootElement);

        // Coverage is still the spine of the coordination view. Restricted or unavailable → no view.
        if (!sections.TryGetValue("coverage", out var coverage) || !IsVisible(coverage)) return null;
        var coverageData = coverage.GetProperty("data");
        var firstCategory = coverageData.TryGetProperty("categories", out var cats)
            && cats.ValueKind == JsonValueKind.Array && cats.GetArrayLength() > 0
                ? cats[0]
                : default;

        var header = sections.TryGetValue("header", out var h) && IsVisible(h) ? h.GetProperty("data") : default;

        return new Beneficiary360(
            c.CaseId, c.CaseNo,
            new BeneficiaryHeader(
                c.BeneficiaryId,
                Str(header, "displayName") ?? "—",
                Mask(Str(header, "memberNo"))),
            new CoverageSummary(
                Str(coverageData, "waitingPeriodState") is "Serving" ? "Review" : "Eligible",
                Str(coverageData, "policyNo") ?? Str(coverageData, "planLabel") ?? "—",
                Str(firstCategory, "category") ?? "—",
                Dec(firstCategory, "annualLimit"),
                Dec(firstCategory, "remaining")),
            // The care plan has no section in the design-39 contract and had no working source before either;
            // the coordination TASKS on the case are what a coordinator actually plans against.
            CarePlanFrom(sections),
            // Appointments are not a profile section (design 39 §3 covers encounters, i.e. visits that
            // happened). The old assembler's appointments endpoint did not exist, so this block was empty then
            // too — the case portal reads forthcoming appointments from the appointments module directly.
            [],
            [],
            ClinicalFrom(sections));
    }

    private static CarePlanSummary CarePlanFrom(IReadOnlyDictionary<string, JsonElement> sections)
    {
        if (!sections.TryGetValue("caseManagement", out var cm) || !IsVisible(cm))
            return new CarePlanSummary("None", [], null);

        var data = cm.GetProperty("data");
        var goals = data.TryGetProperty("tasks", out var tasks) && tasks.ValueKind == JsonValueKind.Array
            ? tasks.EnumerateArray().Select(t => Str(t, "title") ?? "—").Take(10).ToList()
            : [];
        return new CarePlanSummary(goals.Count > 0 ? "Active" : "None", goals, null);
    }

    private static ClinicalSummary ClinicalFrom(IReadOnlyDictionary<string, JsonElement> sections)
    {
        var diagnoses = new List<CodedDiagnosis>();
        if (sections.TryGetValue("pastMedicalHistory", out var pmh) && IsVisible(pmh)
            && pmh.GetProperty("data").TryGetProperty("conditions", out var conditions)
            && conditions.ValueKind == JsonValueKind.Array)
        {
            diagnoses.AddRange(conditions.EnumerateArray().Select(d => new CodedDiagnosis(
                Str(d, "system") ?? "ICD-10", Str(d, "code") ?? "", Str(d, "display") ?? "")));
        }

        // Notes, prescriptions and results stay MASKED — the shape cannot carry their content, and for a case
        // manager the profile returns those sections Restricted anyway, so there is no count to report. Zero
        // with SummaryOnly=true says "summary only", which is the honest answer to a question the coordinator's
        // access does not let anyone ask.
        return new ClinicalSummary(diagnoses, MaskedSection.None, MaskedSection.None, MaskedSection.None);
    }

    private async Task<JsonDocument?> GetProfileAsync(Guid beneficiaryId, string? bearer, CancellationToken ct)
    {
        try
        {
            var http = factory.CreateClient("profile");
            using var request = new HttpRequestMessage(
                HttpMethod.Get, $"/api/v1/patients/{beneficiaryId}/profile?sections={Sections}&purpose=coordination");
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

    /// <summary>Visible AND carrying data. A Restricted or Unavailable section has no <c>data</c> property at
    /// all, so this is a presence check rather than a state string comparison.</summary>
    private static bool IsVisible(JsonElement section) =>
        section.TryGetProperty("state", out var st) && st.GetString() == "Visible"
        && section.TryGetProperty("data", out _);

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static decimal? Dec(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d) ? d : null;

    private static string Mask(string? memberId) =>
        string.IsNullOrEmpty(memberId) ? "••••" : "••••" + memberId[Math.Max(0, memberId.Length - 4)..];
}
