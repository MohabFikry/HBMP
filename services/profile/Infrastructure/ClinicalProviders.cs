using System.Globalization;
using Mersal.Authz;
using Mersal.Profile.Domain;

namespace Mersal.Profile.Infrastructure;

// The clinical and operational sections. Every one calls the service that OWNS the data, under the caller's own
// token, so that service applies the gate it always applied — treating-relationship in emr, provider-ownership
// in orders/pharmacy, the design-37 §6 sensitive gate in orders, case-assignment in case. The profile adds
// section shaping on top and nothing else (design 39 §1).

/// <summary>Section 2 — allergies, critical flags and interaction warnings. Always first, always prominent: an
/// alert a user has to scroll to is an alert that was not shown.</summary>
public sealed class AlertsSectionProvider(CallerScopedHttp http) : ISectionProvider
{
    public string Key => ProfileSections.Alerts;

    public async Task<object?> FetchAsync(SectionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var doc = await http.GetAsync("emr", $"/api/v1/beneficiaries/{request.BeneficiaryId}/allergies",
            request.Caller, ct);
        if (doc is null) return null;

        var allergies = doc.RootElement.EnumerateArray()
            .Select(a => new AllergyAlert(
                a.Str("allergenDisplay") ?? a.Str("allergenId") ?? "(unspecified)",
                a.Str("reaction"),
                a.Str("severity") ?? "Unknown"))
            .ToList();

        return new AlertsSection(allergies, [], [], []);
    }
}

/// <summary>
/// Sections 4 and 5 — past medical history and encounters, from ONE emr call.
///
/// <para>They share a fetch because they share a gate and a source: asking emr twice would double the
/// treating-relationship check, double the PHI-read audit event, and make one user's single glance at a patient
/// look like two accesses in the review.</para>
/// </summary>
public sealed class ClinicalContextSource(CallerScopedHttp http) : IDisposable
{
    private readonly object _sync = new();
    private Task<System.Text.Json.JsonDocument?>? _fetch;

    public async Task<System.Text.Json.JsonElement?> GetAsync(
        Guid beneficiaryId, CallerCredentials caller, CancellationToken ct)
    {
        Task<System.Text.Json.JsonDocument?> fetch;
        lock (_sync)
        {
            _fetch ??= http.GetAsync("emr", $"/api/v1/beneficiaries/{beneficiaryId}/profile-context", caller, ct);
            fetch = _fetch;
        }

        // A failure replays to BOTH dependent sections, so neither renders as an empty record when the truth is
        // that emr did not answer.
        var document = await fetch;
        return document?.RootElement;
    }

    public void Dispose()
    {
        if (_fetch is { IsCompletedSuccessfully: true }) _fetch.Result?.Dispose();
    }
}

public sealed class PastMedicalHistoryProvider(ClinicalContextSource source) : ISectionProvider
{
    public string Key => ProfileSections.PastMedicalHistory;

    public async Task<object?> FetchAsync(SectionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var root = await source.GetAsync(request.BeneficiaryId, request.Caller, ct);
        if (root is not { } doc) return null;

        var conditions = doc.Array("conditions")
            .Select(c => new CodedCondition(
                c.Str("system") ?? "ICD-10", c.Str("code") ?? "", c.Str("display") ?? "",
                c.Str("clinicalStatus"), c.Day("onsetOn")))
            .ToList();

        var records = doc.Array("uploadedRecords")
            .Select(r => new HistoricalRecord(
                r.Uuid("linkId") ?? Guid.Empty, r.Str("documentClass") ?? "PastMedicalHistory",
                r.Str("title") ?? "(untitled)", r.Day("documentDate")))
            .ToList();

        return new PastMedicalHistorySection(conditions, doc.Str("narrative"), records);
    }
}

public sealed class EncountersSectionProvider(ClinicalContextSource source) : ISectionProvider
{
    public string Key => ProfileSections.Encounters;

    public async Task<object?> FetchAsync(SectionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var root = await source.GetAsync(request.BeneficiaryId, request.Caller, ct);
        if (root is not { } doc) return null;

        var rows = doc.Array("encounters")
            .Select(e => new EncounterRow(
                e.Str("encounterRef") ?? e.Str("encounterId") ?? "(unknown)",
                e.Moment("occurredAt") ?? default,
                e.Str("branchName"), e.Str("clinicianName"), e.Str("specialty"),
                e.Str("reason"), e.Str("status") ?? "Unknown", e.Str("encounterId"),
                e.Str("branchId"), e.Str("clinicianId")))
            .ToList();

        return new EncountersSection(rows);
    }
}

/// <summary>
/// Section 6 — investigations and results, <b>sensitivity-gated by orders-service</b>.
///
/// <para>The one section where the profile could most easily become a bypass. It is not, because the gate is
/// upstream: orders-service decides per line whether this caller may see a value, and a restricted line arrives
/// with <c>restricted:true</c> and no value at all. This provider has nothing to redact — which is precisely the
/// property design 39 §7.1 asks for.</para>
/// </summary>
public sealed class InvestigationsSectionProvider(CallerScopedHttp http) : ISectionProvider
{
    public string Key => ProfileSections.Investigations;

    public async Task<object?> FetchAsync(SectionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        // `own-orders` is passed to orders-service, which applies provider-ownership itself. The profile asks for
        // the narrower view; it does not perform the narrowing, because a filter the aggregator applies is a
        // filter the owning service has quietly stopped applying.
        var scope = request.Decision.Variant == ProfileVariants.OwnOrders ? "?scope=own" : string.Empty;
        using var doc = await http.GetAsync(
            "orders", $"/api/v1/investigation-orders/for-beneficiary/{request.BeneficiaryId}{scope}",
            request.Caller, ct);
        if (doc is null) return null;

        var rows = doc.RootElement.Array("items")
            .Select(i => new InvestigationRow(
                i.Str("orderNo") ?? "(unknown)",
                i.Uuid("lineId") ?? Guid.Empty,
                i.Str("category") ?? i.Str("orderType") ?? "Investigation",
                i.Moment("orderedOn") ?? default,
                i.Str("status") ?? "Unknown",
                i.Str("providerName"),
                i.Str("resultSummary"),
                i.Bool("restricted"),
                i.Str("sensitivityLevel"),
                i.Uuid("encounterId")))
            .ToList();

        return new InvestigationsSection(rows);
    }
}

/// <summary>Section 7 — prescriptions and dispensing. `own-rx` narrows to the calling pharmacy, upstream.</summary>
public sealed class PrescriptionsSectionProvider(CallerScopedHttp http) : ISectionProvider
{
    public string Key => ProfileSections.Prescriptions;

    public async Task<object?> FetchAsync(SectionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var scope = request.Decision.Variant == ProfileVariants.OwnRx ? "?scope=own" : string.Empty;
        using var doc = await http.GetAsync(
            "pharmacy", $"/api/v1/prescriptions/for-beneficiary/{request.BeneficiaryId}{scope}",
            request.Caller, ct);
        if (doc is null) return null;

        var rows = doc.RootElement.Array("items")
            .Select(r => new RxRow(
                r.Str("rxNo") ?? "(unknown)",
                r.Str("drugDisplay") ?? "(unspecified)",
                r.Str("status") ?? "Unknown",
                r.Moment("prescribedOn") ?? default,
                r.Moment("dispensedOn"),
                r.Str("batchNo"),
                r.Day("expiryDate"),
                r.Str("substitutedWith"),
                r.Uuid("encounterId")))
            .ToList();

        return new PrescriptionsSection(rows);
    }
}

/// <summary>Section 8 — authorization requests, decisions, rationale and validity.</summary>
public sealed class AuthorizationsSectionProvider(CallerScopedHttp http) : ISectionProvider
{
    public string Key => ProfileSections.Authorizations;

    public async Task<object?> FetchAsync(SectionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var doc = await http.GetAsync(
            "approvals", $"/api/v1/authorizations/for-beneficiary/{request.BeneficiaryId}", request.Caller, ct);
        if (doc is null) return null;

        var rows = doc.RootElement.Array("items")
            .Select(a => new AuthorizationRow(
                a.Str("authNo") ?? "(unknown)",
                a.Str("serviceCategory"),
                a.Str("status") ?? "Unknown",
                a.Moment("requestedAt") ?? default,
                a.Moment("decidedAt"),
                a.Day("validUntil"),
                a.Str("rationale"),
                a.Dec("approvedAmount")))
            .ToList();

        return new AuthorizationsSection(rows);
    }
}

/// <summary>Section 9 — open and closed referrals with their loop status.</summary>
public sealed class ReferralsSectionProvider(CallerScopedHttp http) : ISectionProvider
{
    public string Key => ProfileSections.Referrals;

    public async Task<object?> FetchAsync(SectionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var doc = await http.GetAsync(
            "pharmacy", $"/api/v1/referrals/for-beneficiary/{request.BeneficiaryId}", request.Caller, ct);
        if (doc is null) return null;

        var rows = doc.RootElement.Array("items")
            .Select(r => new ReferralRow(
                r.Str("referralNo") ?? r.Str("referralRef") ?? "(unknown)",
                r.Str("status") ?? "Unknown",
                r.Str("targetSpecialty") ?? r.Str("requestedSpecialty"),
                r.Moment("createdAt") ?? default,
                r.Moment("closedAt")))
            .ToList();

        return new ReferralsSection(rows);
    }
}

/// <summary>Section 12 — claims, cost share and settlement. <b>Never diagnoses</b>: the shape cannot carry one.</summary>
public sealed class FinancialSectionProvider(CallerScopedHttp http) : ISectionProvider
{
    public string Key => ProfileSections.Financial;

    public async Task<object?> FetchAsync(SectionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var doc = await http.GetAsync(
            "claims", $"/api/v1/claims?beneficiaryId={request.BeneficiaryId}&take=100", request.Caller, ct);
        if (doc is null) return null;

        var rows = doc.RootElement.EnumerateArray()
            .Select(c => new FinancialClaimRow(
                c.Str("claimNo") ?? "(unknown)",
                c.Day("serviceDate") ?? default,
                c.Dec("billedAmount") ?? 0m,
                c.Dec("approvedAmount"),
                c.Dec("memberShare"),
                c.Str("status") ?? "Unknown"))
            .ToList();

        var owed = rows.Sum(r => r.MemberShare ?? 0m);
        return new FinancialSection("EGP", owed, rows.Count > 0 ? rows[0].Status : null, rows);
    }
}

/// <summary>Section 13 — assigned cases, coordination tasks and escalations. case-service applies the
/// assignment ABAC; an unassigned caller never reaches this provider, because the matrix stopped them first.</summary>
public sealed class CaseManagementSectionProvider(CallerScopedHttp http) : ISectionProvider
{
    public string Key => ProfileSections.CaseManagement;

    public async Task<object?> FetchAsync(SectionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var doc = await http.GetAsync(
            "case", $"/api/v1/cases/for-beneficiary/{request.BeneficiaryId}", request.Caller, ct);
        if (doc is null) return null;

        var root = doc.RootElement;
        return new CaseManagementSection(
            [.. root.Array("cases").Select(c => new CaseRow(
                c.Uuid("caseId") ?? Guid.Empty, c.Str("caseNo") ?? "(unknown)",
                c.Str("status") ?? "Unknown", c.Str("category"), c.Moment("openedAt") ?? default))],
            [.. root.Array("tasks").Select(t => new CoordinationTaskRow(
                t.Uuid("taskId") ?? Guid.Empty, t.Str("title") ?? "(untitled)",
                t.Str("status") ?? "Unknown", t.Day("dueOn")))],
            [.. root.Array("escalations").Select(e => new EscalationRow(
                e.Uuid("escalationId") ?? Guid.Empty, e.Str("reason") ?? "(unstated)",
                e.Str("status") ?? "Unknown", e.Moment("raisedAt") ?? default))]);
    }
}

/// <summary>
/// Section 15 — call history (design 39 §5b).
///
/// <para>The level is resolved from the SAME matrix cell that decided the section and sent to
/// callcentre-service, which clamps it again against the caller's own role. Two clamps of the same value is not
/// redundancy for its own sake: the profile is one of several callers of that endpoint, and the one that
/// enforces nothing is the one that gets called by something else next year.</para>
/// </summary>
public sealed class CallHistorySectionProvider(CallerScopedHttp http) : ISectionProvider
{
    public string Key => ProfileSections.CallHistory;

    public async Task<object?> FetchAsync(SectionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var level = ProfilePolicies.CallHistoryLevelFor(request.Context);
        if (level == CallHistoryLevel.None) return null;

        using var doc = await http.GetAsync(
            "callcentre",
            $"/api/v1/beneficiaries/{request.BeneficiaryId}/call-interactions?level={level.ToString().ToLowerInvariant()}&pageSize=50",
            request.Caller, ct);
        if (doc is null) return null;

        var root = doc.RootElement;
        var rows = root.Array("items").Select(ReadRow).ToList();
        return new CallHistorySection(
            root.Str("level") ?? level.ToString(), rows, root.Str("nextCursor"));
    }

    private static CallHistoryRow ReadRow(System.Text.Json.JsonElement r) => new(
        r.Str("callRef") ?? "(unknown)",
        r.Str("direction") ?? "Inbound",
        r.Moment("startedAt") ?? default,
        r.Moment("endedAt"),
        r.Num("durationSeconds"),
        r.Str("branchCode"),
        r.Str("agentDisplayName"),
        r.Str("reasonCode"),
        r.Str("outcome"),
        r.Prop("verification") is { } v
            ? new CallVerificationDetail(
                v.Str("result") ?? "Unknown",
                [.. v.Array("identifierTypes").Select(t => t.GetString() ?? string.Empty)])
            : null,
        r.Str("summary"),
        r.Bool("summaryEdited"),
        r.Prop("linkedArtifacts") is not null
            ? [.. r.Array("linkedArtifacts").Select(a => new LinkedArtifact(
                a.Str("type") ?? "Unknown", a.Str("ref") ?? string.Empty, a.Str("action")))]
            : null,
        // Server-generated upstream, from the SAME projected row. Never assembled here — a second assembler is a
        // second chance to include the field the projection dropped (design 39 §5b rule 1).
        r.Str("copyText") ?? string.Empty);
}

/// <summary>Formatting helpers shared by the providers.</summary>
internal static class ProviderFormat
{
    public static string Invariant(decimal value) => value.ToString(CultureInfo.InvariantCulture);
}
