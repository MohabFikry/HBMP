using System.Net.Http.Headers;
using System.Text.Json;
using Mersal.Case.Domain;
using Mersal.Case.Infrastructure;

namespace Mersal.Case.Api;

/// <summary>Assembles the beneficiary-360 coordination view by calling the sibling services (eligibility/policy,
/// approvals, appointments, and the emr COORDINATION SUMMARY projection) with the caller's bearer token — so each
/// sibling enforces its own authorization (defense in depth). It maps their responses into the field-scoped
/// <see cref="Beneficiary360"/> DTO: coverage limits, care-plan status, appointment + approval STATUS, and a
/// clinical SUMMARY where diagnosis is coord-visible while notes/rx/results are represented only as MASKED counts.
///
/// Each sibling is optional and fail-soft (a section that can't be reached degrades to empty/None), EXCEPT the
/// coverage summary: without eligibility the coordination view is not meaningful, so a missing coverage response
/// makes the whole assembly return null (the endpoint then fails closed with 502). Until the emr coordination
/// projection exists the clinical section degrades to masked-empty — never fabricated PHI.</summary>
public sealed class HttpBeneficiary360Assembler(IHttpClientFactory factory) : IBeneficiary360Assembler
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<Beneficiary360?> AssembleAsync(CaseFile c, string? bearerToken, CancellationToken ct = default)
    {
        var coverage = await GetAsync<CoverageDto>("eligibility", $"/api/v1/beneficiaries/{c.BeneficiaryId}/coverage-summary", bearerToken, ct);
        if (coverage is null) return null;   // fail-closed: coverage is the spine of the coordination view

        var appts = await GetAsync<List<AppointmentDto>>("appointments", $"/api/v1/beneficiaries/{c.BeneficiaryId}/appointments?window=coordination", bearerToken, ct) ?? [];
        var approvals = await GetAsync<List<ApprovalDto>>("approvals", $"/api/v1/beneficiaries/{c.BeneficiaryId}/authorizations?status=open", bearerToken, ct) ?? [];
        var carePlan = await GetAsync<CarePlanDto>("emr", $"/api/v1/beneficiaries/{c.BeneficiaryId}/care-plan-summary", bearerToken, ct);
        var clinical = await GetAsync<ClinicalSummaryDto>("emr", $"/api/v1/beneficiaries/{c.BeneficiaryId}/coordination-summary", bearerToken, ct);

        return new Beneficiary360(
            c.CaseId, c.CaseNo,
            new BeneficiaryHeader(c.BeneficiaryId, coverage.DisplayName ?? "—", Mask(coverage.MemberId)),
            new CoverageSummary(coverage.Status ?? "Review", coverage.PolicyName ?? "—",
                coverage.CoverageCategory ?? "—", coverage.AnnualLimit, coverage.RemainingLimit),
            carePlan is null
                ? new CarePlanSummary("None", [], null)
                : new CarePlanSummary(carePlan.Status ?? "Active", carePlan.Goals ?? [], carePlan.ReviewDue),
            appts.Select(a => new AppointmentSummary(a.AppointmentId, a.Clinic ?? "—", a.When, a.Status ?? "—")).ToList(),
            approvals.Select(a => new ApprovalSummary(a.AuthNo ?? "—", a.Status ?? "—", a.Priority ?? "Routine", a.DecidedAt)).ToList(),
            clinical is null
                ? ClinicalSummary.Empty
                : new ClinicalSummary(
                    (clinical.ActiveDiagnoses ?? []).Select(d => new CodedDiagnosis(d.System ?? "ICD-10", d.Code ?? "", d.Display ?? "")).ToList(),
                    MaskedSection.Of(clinical.NoteCount),
                    MaskedSection.Of(clinical.PrescriptionCount),
                    MaskedSection.Of(clinical.ResultCount)));
    }

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

    private static string Mask(string? memberId) =>
        string.IsNullOrEmpty(memberId) ? "••••" : "••••" + memberId[Math.Max(0, memberId.Length - 4)..];

    private sealed record CoverageDto(string? DisplayName, string? MemberId, string? Status, string? PolicyName,
        string? CoverageCategory, decimal? AnnualLimit, decimal? RemainingLimit);
    private sealed record AppointmentDto(Guid AppointmentId, string? Clinic, DateTimeOffset When, string? Status);
    private sealed record ApprovalDto(string? AuthNo, string? Status, string? Priority, DateTimeOffset? DecidedAt);
    private sealed record CarePlanDto(string? Status, List<string>? Goals, DateTimeOffset? ReviewDue);
    private sealed record ClinicalSummaryDto(List<DiagnosisDto>? ActiveDiagnoses, int NoteCount, int PrescriptionCount, int ResultCount);
    private sealed record DiagnosisDto(string? System, string? Code, string? Display);
}
