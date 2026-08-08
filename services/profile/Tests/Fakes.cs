using Mersal.Authz;
using Mersal.Profile.Domain;

namespace Mersal.Profile.Tests;

/// <summary>A provider that answers with a fixed payload — the section's "happy path".</summary>
public sealed class FakeProvider(string key, object? payload) : ISectionProvider
{
    public string Key { get; } = key;
    public int Calls { get; private set; }

    public Task<object?> FetchAsync(SectionRequest request, CancellationToken ct)
    {
        Calls++;
        return Task.FromResult(payload);
    }
}

/// <summary>
/// Stands in for callcentre-service, which PROJECTS the rows to the caller's level before returning them.
///
/// <para>A fake that ignored the level and always returned the full row would make the finance test pass or fail
/// on the profile's behaviour alone — but the design puts that projection upstream (design 39 §5b), so a fake
/// that does not project is testing a platform that does not exist.</para>
/// </summary>
public sealed class CallHistoryFakeProvider : ISectionProvider
{
    public string Key => ProfileSections.CallHistory;

    public Task<object?> FetchAsync(SectionRequest request, CancellationToken ct)
    {
        var level = ProfilePolicies.CallHistoryLevelFor(request.Context);
        if (level == CallHistoryLevel.None) return Task.FromResult<object?>(null);
        // Meta carries no summary text — the same rule callcentre-service applies, applied here.
        return Task.FromResult<object?>(
            Fixtures.CallHistory(level.ToString(), withSummary: level >= CallHistoryLevel.Operational));
    }
}

/// <summary>A provider whose owning service is down. Must yield Unavailable, never an empty section.</summary>
public sealed class BrokenProvider(string key) : ISectionProvider
{
    public string Key { get; } = key;

    public Task<object?> FetchAsync(SectionRequest request, CancellationToken ct) =>
        throw new InvalidOperationException("upstream exploded");
}

/// <summary>A provider that never answers, to exercise the per-section timeout.</summary>
public sealed class HangingProvider(string key) : ISectionProvider
{
    public string Key { get; } = key;

    public async Task<object?> FetchAsync(SectionRequest request, CancellationToken ct)
    {
        await Task.Delay(Timeout.Infinite, ct);
        return null;
    }
}

/// <summary>Canned section payloads carrying obviously-identifiable marker strings, so a leak is greppable in
/// the serialized JSON rather than something a test has to know the shape of to notice.</summary>
public static class Fixtures
{
    public const string DiagnosisMarker = "TYPE-2-DIABETES-MELLITUS";
    public const string ResultMarker = "HAEMOGLOBIN-11-2";
    public const string DrugMarker = "METFORMIN-500MG";
    public const string RationaleMarker = "CLINICALLY-INDICATED-PER-GUIDELINE";
    public const string CallSummaryMarker = "MEMBER-ASKED-TO-MOVE-APPOINTMENT";
    public const string PhotoPath = "/api/v1/patients";
    public const string AllergyMarker = "PENICILLIN";
    public const string ReasonMarker = "CHEST-PAIN-ON-EXERTION";

    public static readonly Guid Beneficiary = Guid.Parse("11111111-1111-1111-1111-111111111111");

    public static HeaderSection Header() => new(
        Beneficiary, "MRS-M-014882", "Amal Hassan", "أمل حسن", "30-39", "F", "Active",
        StatusCue.For("Active"), "Nasr City", "ar", new ContactSummary("+201000000000", "WhatsApp"),
        $"{PhotoPath}/{Beneficiary}/photo");

    public static AlertsSection Alerts() => new(
        [new AllergyAlert(AllergyMarker, "Rash", "High")],
        [new FlagAlert("Critical", "Anticoagulated", "critical")],
        [], []);

    public static PastMedicalHistorySection Pmh() => new(
        [new CodedCondition("ICD-10", "E11", DiagnosisMarker, "Active", new DateOnly(2019, 3, 1))],
        "Long-standing, diet controlled.",
        [new HistoricalRecord(Guid.NewGuid(), "PastMedicalHistory", "2019 discharge summary", new DateOnly(2019, 4, 2))]);

    public static EncountersSection Encounters() => new(
        [new EncounterRow("ENC-2026-0001", DateTimeOffset.UtcNow.AddDays(-3), "Nasr City", "Dr Adel",
            "Internal Medicine", ReasonMarker, "Completed")]);

    public static InvestigationsSection Investigations(bool restricted = false) => new(
        [new InvestigationRow("ORD-2026-0007", Guid.NewGuid(), "Haematology", DateTimeOffset.UtcNow.AddDays(-2),
            "Completed", "Central Lab", restricted ? null : ResultMarker, restricted,
            restricted ? "HighlySensitive" : "Standard", "Lab")]);

    /// <summary>A row that arrives marked restricted but WITH a value — an upstream regression. The profile must
    /// strip it anyway; the test that proves so is the reason this fixture exists.</summary>
    public static InvestigationsSection LeakyInvestigations() => new(
        [new InvestigationRow("ORD-2026-0008", Guid.NewGuid(), "Psychiatry", DateTimeOffset.UtcNow,
            "Completed", "Central Lab", ResultMarker, true, "HighlySensitive", "Lab")]);

    public static PrescriptionsSection Prescriptions() => new(
        [new RxRow("RX-2026-0004", DrugMarker, "Dispensed", DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow, "B-4471", new DateOnly(2027, 1, 1), null)]);

    public static AuthorizationsSection Authorizations() => new(
        [new AuthorizationRow("AUTH-2026-0011", "Imaging", "Approved", DateTimeOffset.UtcNow.AddDays(-5),
            DateTimeOffset.UtcNow.AddDays(-4), new DateOnly(2026, 8, 30), RationaleMarker, 3200m)]);

    public static ReferralsSection Referrals() => new(
        [new ReferralRow("REF-2026-0002", "Accepted", "Cardiology", DateTimeOffset.UtcNow.AddDays(-6), null)]);

    public static DocumentsSection Documents() => new(
    [
        new DocumentRow(Guid.NewGuid(), "EnrolmentForm", "Administrative", "Enrolment form",
            new DateOnly(2026, 1, 5), DateTimeOffset.UtcNow.AddMonths(-6), "Active", true),
        new DocumentRow(Guid.NewGuid(), "MedicalReport", "Clinical", DiagnosisMarker,
            new DateOnly(2026, 2, 5), DateTimeOffset.UtcNow.AddMonths(-5), "Active", false),
    ]);

    public static NotesSection Notes() => new(
    [
        new NoteRow(Guid.NewGuid(), "General", "Administrative", "Prefers morning appointments",
            "R. Adel", DateTimeOffset.UtcNow.AddDays(-9), false, true),
        new NoteRow(Guid.NewGuid(), "Clinical", "Clinical", DiagnosisMarker,
            "Dr Adel", DateTimeOffset.UtcNow.AddDays(-8), false, false),
    ]);

    public static FinancialSection Financial() => new(
        "EGP", 250m, "Settled",
        [new FinancialClaimRow("CLM-2026-0031", new DateOnly(2026, 6, 1), 4000m, 3750m, 250m, "Settled")]);

    public static CaseManagementSection Cases() => new(
        [new CaseRow(Guid.NewGuid(), "CASE-2026-0003", "Open", "Chronic", DateTimeOffset.UtcNow.AddMonths(-2))],
        [], []);

    public static TimelineSection Timeline() => new(
    [
        new TimelineRow(DateTimeOffset.UtcNow.AddDays(-1), "PlanChanged", "Administrative", "R. Adel",
            "Plan changed to Gold", "policy"),
        new TimelineRow(DateTimeOffset.UtcNow.AddDays(-2), "ClaimSettled", "Financial", "F. Officer",
            "Claim settled", "claims"),
        new TimelineRow(DateTimeOffset.UtcNow.AddDays(-3), "ProfileViewed", "Access", "Dr Adel",
            "Profile opened", "profile"),
        new TimelineRow(DateTimeOffset.UtcNow.AddDays(-4), "DiagnosisRecorded", "Clinical", "Dr Adel",
            DiagnosisMarker, "emr"),
    ]);

    public static CallHistorySection CallHistory(string level, bool withSummary = true) => new(
        level,
        [new CallHistoryRow("CALL-2026-004137", "Outbound", DateTimeOffset.UtcNow.AddDays(-3),
            DateTimeOffset.UtcNow.AddDays(-3).AddMinutes(6), 372, "NSR", "R. Adel", "RescheduleAppointment",
            "Resolved", null, withSummary ? CallSummaryMarker : null, false, [], "[Outbound] provenance block")],
        null);

    /// <summary>Every section wired to a fixture — the composer under test sees a fully-answering platform, so
    /// anything missing from the payload was withheld on purpose.</summary>
    public static IReadOnlyList<ISectionProvider> AllProviders(bool restrictedResult = false) =>
    [
        new FakeProvider(ProfileSections.Header, Header()),
        new FakeProvider(ProfileSections.Alerts, Alerts()),
        new FakeProvider(ProfileSections.Coverage, Coverage()),
        new FakeProvider(ProfileSections.PastMedicalHistory, Pmh()),
        new FakeProvider(ProfileSections.Encounters, Encounters()),
        new FakeProvider(ProfileSections.Investigations, Investigations(restrictedResult)),
        new FakeProvider(ProfileSections.Prescriptions, Prescriptions()),
        new FakeProvider(ProfileSections.Authorizations, Authorizations()),
        new FakeProvider(ProfileSections.Referrals, Referrals()),
        new FakeProvider(ProfileSections.Documents, Documents()),
        new FakeProvider(ProfileSections.Notes, Notes()),
        new FakeProvider(ProfileSections.Financial, Financial()),
        new FakeProvider(ProfileSections.CaseManagement, Cases()),
        new FakeProvider(ProfileSections.Timeline, Timeline()),
        new CallHistoryFakeProvider(),
    ];

    public static CoverageSection Coverage() => new(
        "Mersal Foundation", "POL-2026-0001", "Gold", 3, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31),
        "Served",
    [
        new CoverageLimitLine("Pharmacy", 5000m, 1200m, 3800m, 10m, "Tier1"),
        new CoverageLimitLine("Dental", 2000m, 0m, 2000m, 20m, "Tier2"),
    ]);

    public static CallerCredentials Caller() => new("Bearer test-token", null, "corr-1");

    public static ProfileContext Context(
        string role, bool treating = false, bool assigned = false, string? providerId = null) => new()
        {
            Roles = new HashSet<string>(StringComparer.Ordinal) { role },
            TreatingRelationship = treating, CaseAssignment = assigned, ProviderId = providerId,
        };
}
