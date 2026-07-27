using Mersal.Authz;

namespace Mersal.Profile.Domain;

/// <summary>
/// Applies a section's VARIANT projection (design 39 §4). One dispatcher rather than a projection step inside
/// each provider, because "did every provider remember to project?" is a question with fifteen answers, and
/// "does the dispatcher handle every section?" is a question with one — which the exhaustiveness test below
/// actually checks.
/// </summary>
public static class SectionProjection
{
    /// <summary>Narrow a fetched section payload to the caller's variant. Returns the payload unchanged for a
    /// section whose variants are row filters the owning service applied.</summary>
    public static object? Apply(string key, object? data, string? variant, bool mayViewPhoto)
    {
        if (data is null) return null;

        return (key, data) switch
        {
            (ProfileSections.Header, HeaderSection h) =>
                mayViewPhoto ? h.Project(variant) : h.Project(variant).WithoutPhoto(),
            (ProfileSections.Alerts, AlertsSection a) => a.Project(variant),
            (ProfileSections.Coverage, CoverageSection c) => c.Project(variant),
            (ProfileSections.PastMedicalHistory, PastMedicalHistorySection p) => p.Project(variant),
            (ProfileSections.Encounters, EncountersSection e) => e.Project(variant),
            // Sensitive values are stripped in the same breath as the variant: a result that arrived marked
            // restricted must not survive into the payload even if an upstream regression sends one.
            (ProfileSections.Investigations, InvestigationsSection i) =>
                i.Project(variant).WithSensitiveValuesRemoved(),
            (ProfileSections.Prescriptions, PrescriptionsSection r) => r.Project(variant),
            (ProfileSections.Authorizations, AuthorizationsSection u) => u.Project(variant),
            (ProfileSections.Referrals, ReferralsSection f) => f.Project(variant),
            (ProfileSections.Documents, DocumentsSection d) => d.Project(variant),
            (ProfileSections.Notes, NotesSection n) => n.Project(variant),
            (ProfileSections.Financial, FinancialSection m) => m.Project(variant),
            (ProfileSections.CaseManagement, CaseManagementSection s) => s.Project(variant),
            (ProfileSections.Timeline, TimelineSection t) => t.Project(variant),
            (ProfileSections.CallHistory, CallHistorySection l) => l.Project(variant),

            // Default-deny reaches the projector too. A section whose payload type the dispatcher does not
            // recognise is DROPPED, not passed through: an unprojected payload is precisely the fat-payload
            // failure design 39 §1 names, and "I forgot to add the case" must not be the way it ships.
            _ => null,
        };
    }

    /// <summary>The payload type each section key must produce — the exhaustiveness contract, asserted by test.</summary>
    public static IReadOnlyDictionary<string, Type> ExpectedPayloadTypes { get; } =
        new Dictionary<string, Type>(StringComparer.Ordinal)
        {
            [ProfileSections.Header] = typeof(HeaderSection),
            [ProfileSections.Alerts] = typeof(AlertsSection),
            [ProfileSections.Coverage] = typeof(CoverageSection),
            [ProfileSections.PastMedicalHistory] = typeof(PastMedicalHistorySection),
            [ProfileSections.Encounters] = typeof(EncountersSection),
            [ProfileSections.Investigations] = typeof(InvestigationsSection),
            [ProfileSections.Prescriptions] = typeof(PrescriptionsSection),
            [ProfileSections.Authorizations] = typeof(AuthorizationsSection),
            [ProfileSections.Referrals] = typeof(ReferralsSection),
            [ProfileSections.Documents] = typeof(DocumentsSection),
            [ProfileSections.Notes] = typeof(NotesSection),
            [ProfileSections.Financial] = typeof(FinancialSection),
            [ProfileSections.CaseManagement] = typeof(CaseManagementSection),
            [ProfileSections.Timeline] = typeof(TimelineSection),
            [ProfileSections.CallHistory] = typeof(CallHistorySection),
        };
}
