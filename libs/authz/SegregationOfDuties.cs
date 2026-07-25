namespace Mersal.Authz;

/// <summary>
/// The Segregation-of-Duties (SoD) conflict matrix from <c>10-role-matrix.md §7</c>, evaluated at
/// ASSIGNMENT time: no single human may hold two roles/capabilities that together let them both originate
/// and approve the same sensitive transaction, or self-elevate their own access. The policy engine also
/// evaluates record-scoped SoD at DECISION time (e.g., a doctor may not adjudicate a case they authored) —
/// that lives in the deciding service (approvals). This type is the reusable assignment-time gate the
/// admin surface consults before granting a role (phase 8b.1).
/// </summary>
public static class SegregationOfDuties
{
    /// <summary>
    /// SoD "tokens" — a role, or a finer-grained capability the matrix splits a role into (payment
    /// initiate vs release, claims submit vs adjudicate). A user holds a SET of tokens; conflicts are over
    /// the set. Roles map to their coarse token of the same name; fine tokens are namespaced.
    /// </summary>
    public static class Tokens
    {
        public const string Doctor = "doctor";
        public const string MedicalApproval = "medical_approval";
        public const string MedicalDirector = "medical_director";
        public const string OrgAdmin = "org_admin";
        public const string SuperAdmin = "super_admin";
        public const string ProviderAdmin = "provider_admin";
        public const string NetworkTeam = "network_team";
        public const string BeneficiaryMgmt = "beneficiary_mgmt";

        // Finance is split by the matrix into the two halves that must never meet.
        public const string FinancePaymentInitiate = "finance:payment_initiate";
        public const string FinancePaymentRelease = "finance:payment_release";

        // Beneficiary-management create/merge vs the supervisor who approves a merge.
        public const string BeneficiaryCreateMerge = "beneficiary_mgmt:create_merge";
        public const string BeneficiaryMergeApprove = "beneficiary_mgmt:merge_approve";

        // Claims (R6 roles; the matrix already names them so SoD holds the day they arrive).
        public const string ClaimsSubmitter = "claims:submitter";
        public const string ClaimsOfficer = "claims_officer";
        public const string ClaimsReviewer = "claims_reviewer";
        public const string ClaimsSettlementIssuer = "claims:settlement_issuer";
    }

    /// <summary>Clinical roles a provider-admin must never self-grant (PHI self-elevation).</summary>
    public static readonly IReadOnlySet<string> ClinicalRoles =
        new HashSet<string>(StringComparer.Ordinal) { "doctor", "nurse", "lab_tech", "imaging_tech", "pharmacist" };

    /// <summary>Provider-affiliated roles that must never adjudicate claims (a provider deciding its own money).</summary>
    public static readonly IReadOnlySet<string> ProviderAffiliatedRoles =
        new HashSet<string>(StringComparer.Ordinal)
        { "provider_admin", "doctor", "nurse", "lab_tech", "imaging_tech", "pharmacist" };

    /// <summary>Roles that carry claims-adjudication authority (decide lines / close batch).</summary>
    public static readonly IReadOnlySet<string> ClaimsAdjudicationRoles =
        new HashSet<string>(StringComparer.Ordinal) { "claims_officer", "claims_reviewer" };

    /// <summary>An incompatible pair with the human-readable risk from the matrix.</summary>
    public sealed record ConflictRule(string TokenA, string TokenB, string Reason);

    /// <summary>An SoD violation for a specific proposed grant — the already-held token, the conflicting one,
    /// and why. Surfaced to the admin (rejection) and to reviewers as a high-severity signal.</summary>
    public sealed record Violation(string HeldToken, string ConflictingToken, string Reason);

    // The unordered incompatible pairs. Group memberships (clinical / provider-affiliated / adjudication) are
    // expanded so every concrete pair is explicit and unit-testable.
    private static readonly ConflictRule[] Rules = BuildRules();

    private static ConflictRule[] BuildRules()
    {
        var rules = new List<ConflictRule>
        {
            new(Tokens.Doctor, Tokens.MedicalApproval, "Self-approval of own clinical request"),
            new(Tokens.Doctor, Tokens.MedicalDirector, "Self-approval of own clinical request"),
            new(Tokens.FinancePaymentInitiate, Tokens.FinancePaymentRelease, "Fraudulent payment through single actor"),
            new(Tokens.BeneficiaryCreateMerge, Tokens.BeneficiaryMergeApprove, "Fabricated/duplicated beneficiary"),
            new(Tokens.OrgAdmin, Tokens.SuperAdmin, "Unilateral privilege escalation"),
            new(Tokens.NetworkTeam, Tokens.FinancePaymentRelease, "Rate manipulation + self-pay"),
            new(Tokens.ClaimsSubmitter, Tokens.ClaimsOfficer, "Self-adjudication of a claim one raised"),
            new(Tokens.ClaimsSubmitter, Tokens.ClaimsReviewer, "Self-adjudication of a claim one raised"),
            new(Tokens.ClaimsOfficer, Tokens.ClaimsReviewer, "Dual control defeated: single actor decides and approves"),
            new(Tokens.ClaimsSettlementIssuer, Tokens.FinancePaymentInitiate, "Settlement issuer must not initiate payment"),
            new(Tokens.ClaimsSettlementIssuer, Tokens.FinancePaymentRelease, "Settlement issuer must not release payment"),
        };

        // Provider Admin must not self-grant any clinical role (self-elevation to PHI).
        foreach (var clinical in ClinicalRoles)
            rules.Add(new(Tokens.ProviderAdmin, clinical, "Self-elevation to PHI access"));

        // A provider-affiliated role must never also adjudicate claims (a provider deciding its own money).
        foreach (var affiliated in ProviderAffiliatedRoles)
            foreach (var adjudicator in ClaimsAdjudicationRoles)
                if (!string.Equals(affiliated, adjudicator, StringComparison.Ordinal))
                    rules.Add(new(affiliated, adjudicator, "A provider deciding its own money"));

        // Claims adjudication must never also release settlement/payment.
        foreach (var adjudicator in ClaimsAdjudicationRoles)
            rules.Add(new(adjudicator, Tokens.FinancePaymentRelease, "Single actor could both approve and pay"));

        return rules.ToArray();
    }

    /// <summary>The full, expanded conflict-rule set (exposed for admin display and tests).</summary>
    public static IReadOnlyList<ConflictRule> ConflictRules => Rules;

    /// <summary>
    /// The tokens a set of roles implies. A role contributes its own name plus, for roles the matrix splits,
    /// the derived capability tokens (so holding <c>finance</c> means holding BOTH payment halves unless the
    /// caller passed the fine token). This is deliberately conservative: a coarse <c>finance</c> grant is
    /// treated as covering initiate+release, so a coarse+coarse assignment can still be blocked.
    /// </summary>
    public static IReadOnlySet<string> Expand(IEnumerable<string> roles)
    {
        ArgumentNullException.ThrowIfNull(roles);
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in roles)
        {
            var role = raw.Trim().ToLowerInvariant();
            if (role.Length == 0) continue;
            set.Add(role);
            switch (role)
            {
                case "finance":
                    set.Add(Tokens.FinancePaymentInitiate);
                    set.Add(Tokens.FinancePaymentRelease);
                    break;
                case Tokens.BeneficiaryMgmt:
                    set.Add(Tokens.BeneficiaryCreateMerge);
                    break;
            }
        }
        return set;
    }

    /// <summary>
    /// Every SoD violation that would result from a user simultaneously holding <paramref name="held"/> and
    /// the newly proposed <paramref name="proposed"/> tokens/roles. Empty ⇒ the grant is SoD-clean. Both
    /// inputs are role names or fine tokens; they are expanded and matched against the conflict matrix.
    /// </summary>
    public static IReadOnlyList<Violation> Evaluate(IEnumerable<string> held, IEnumerable<string> proposed)
    {
        var heldSet = Expand(held);
        var proposedSet = Expand(proposed);
        var union = new HashSet<string>(heldSet, StringComparer.Ordinal);
        union.UnionWith(proposedSet);

        var violations = new List<Violation>();
        foreach (var rule in Rules)
        {
            if (!union.Contains(rule.TokenA) || !union.Contains(rule.TokenB)) continue;

            // Only report a conflict the proposed grant actually introduces (at least one side is new),
            // and attribute it to the already-held side so the message reads naturally.
            var aNew = proposedSet.Contains(rule.TokenA);
            var bNew = proposedSet.Contains(rule.TokenB);
            if (!aNew && !bNew) continue; // pre-existing conflict, not introduced by this grant

            var (held0, conflicting) = aNew && !heldSet.Contains(rule.TokenA)
                ? (rule.TokenB, rule.TokenA)
                : (rule.TokenA, rule.TokenB);
            violations.Add(new Violation(held0, conflicting, rule.Reason));
        }
        return violations;
    }

    /// <summary>True if granting <paramref name="proposed"/> to a user already holding <paramref name="held"/>
    /// would breach SoD.</summary>
    public static bool Conflicts(IEnumerable<string> held, IEnumerable<string> proposed) =>
        Evaluate(held, proposed).Count > 0;
}
