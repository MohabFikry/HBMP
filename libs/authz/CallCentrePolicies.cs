namespace Mersal.Authz;

/// <summary>
/// The call-centre policy overlay (phase 15). The Call Centre is a central hotline: <b>MemberScoped / all branches</b>
/// (design 37 §3) — it is NEVER branch-filtered; branch + specialty are selectors, not restrictions. Its defining
/// controls are enforced in callcentre-service itself, not here:
/// <list type="bullet">
///   <item><b>Verify before disclose</b> — no member detail is returned until a Passed verification is recorded for
///   THIS interaction + THIS beneficiary (the <c>VerificationService</c> gate).</item>
///   <item><b>No clinical data, ever</b> — the 360 projection omits diagnoses/results/prescriptions/notes; proven by
///   an authorization test over the serialized payload.</item>
/// </list>
/// This overlay simply grants the <c>call_center</c> role the coarse scopes it needs (tenant-matched), plus a
/// supervisory team-view read for <c>call_center_supervisor</c>/<c>manager</c>. See 10-role-matrix, 11-permission-matrix.
/// </summary>
public static class CallCentrePolicies
{
    public const string Version = "15.0";

    public const string Resource = "call-interaction";

    /// <summary>Open/patch/close an interaction + call log (the agent's own calls).</summary>
    public const string Interaction = "callcentre:interaction";
    /// <summary>Record a caller-verification attempt (pass or fail).</summary>
    public const string Verify = "callcentre:verify";
    /// <summary>Member search + the post-verification 360 read (PHI-read, audited, gated by verification).</summary>
    public const string Read = "callcentre:read";
    /// <summary>Appointment book/reschedule/cancel + contact edits from the call (delegated to emr/patient).</summary>
    public const string Act = "callcentre:act";
    /// <summary>Supervisory read of the whole team's interactions (KPIs / QA).</summary>
    public const string ReadTeam = "callcentre:read-team";

    /// <summary>The call-centre rules on their own (spliceable). All tenant-matched; the role is member-scoped so no
    /// branch condition is attached (branch is a selector inside the service, never a restriction).</summary>
    public static IReadOnlyList<PolicyRule> Rules() =>
    [
        new PolicyRule
        {
            Action = Interaction, ResourceType = Resource,
            Roles = Set("call_center", "call_center_supervisor"), Scopes = Set("callcentre:interaction"),
            RequiredConditions = [AbacConditions.TenantMatch],
        },
        new PolicyRule
        {
            Action = Verify, ResourceType = Resource,
            Roles = Set("call_center", "call_center_supervisor"), Scopes = Set("callcentre:verify"),
            RequiredConditions = [AbacConditions.TenantMatch],
        },
        // Member search + 360 read — sensitive (a disclosure to a caller is a data-protection event; PHI-read audited).
        new PolicyRule
        {
            Action = Read, ResourceType = Resource,
            Roles = Set("call_center", "call_center_supervisor"), Scopes = Set("callcentre:read"),
            RequiredConditions = [AbacConditions.TenantMatch],
            Sensitive = true,
        },
        new PolicyRule
        {
            Action = Act, ResourceType = Resource,
            Roles = Set("call_center", "call_center_supervisor"), Scopes = Set("callcentre:act"),
            RequiredConditions = [AbacConditions.TenantMatch],
            Sensitive = true,
        },
        // Supervisory team view (KPIs / QA) — tenant only.
        new PolicyRule
        {
            Action = ReadTeam, ResourceType = Resource,
            Roles = Set("call_center_supervisor", "manager"), Scopes = Set("callcentre:read"),
            RequiredConditions = [AbacConditions.TenantMatch],
        },
    ];

    /// <summary>Full bundle = platform defaults + the call-centre rules. callcentre-service authorizes with this.</summary>
    public static PolicyBundle Bundle()
    {
        var baseBundle = DefaultPolicies.Bundle();
        return new PolicyBundle(Version, [.. baseBundle.Rules, .. Rules()]);
    }

    private static HashSet<string> Set(params string[] values) => new(values, StringComparer.Ordinal);
}
