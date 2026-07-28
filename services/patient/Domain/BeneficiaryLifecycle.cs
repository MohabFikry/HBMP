namespace Mersal.Patient.Domain;

/// <summary>
/// The beneficiary/member lifecycle (23-state-machines §1). Only legal transitions are allowed;
/// some require a mandatory reason. Illegal transitions are rejected (audited as TransitionDenied).
/// </summary>
public static class BeneficiaryLifecycle
{
    // from → set of allowed to-states. This table IS 23 §1 — the conformance test in
    // libs/time/../StateMachineConformanceTests parses the spec's diagram and asserts both directions,
    // so a transition added here without amending the doc fails the build, and vice versa.
    //
    // 18.A4: three UNDECLARED transitions were removed — Suspended→Inactive, Expired→Inactive and
    // Blocked→Inactive. Blocked→Inactive was the dangerous one: it let a fraud-blocked member be quietly
    // retired to Inactive, erasing the Blocked signal without a director's review, and Inactive→Active
    // then re-admitted them. If operations genuinely need a deactivation path out of those states it is a
    // spec amendment to 23 §1 with a recorded reason, not a silent widening here.
    private static readonly Dictionary<BeneficiaryStatus, HashSet<BeneficiaryStatus>> Allowed = new()
    {
        [BeneficiaryStatus.Pending] = [BeneficiaryStatus.Active, BeneficiaryStatus.Inactive],   // activate / abandon
        [BeneficiaryStatus.Active] = [BeneficiaryStatus.Suspended, BeneficiaryStatus.Expired, BeneficiaryStatus.Blocked, BeneficiaryStatus.Inactive],
        [BeneficiaryStatus.Suspended] = [BeneficiaryStatus.Active, BeneficiaryStatus.Blocked, BeneficiaryStatus.Expired],
        [BeneficiaryStatus.Expired] = [BeneficiaryStatus.Active],                                // renew
        [BeneficiaryStatus.Blocked] = [BeneficiaryStatus.Active],                                // unblock (director)
        [BeneficiaryStatus.Inactive] = [BeneficiaryStatus.Active],                               // reactivate
    };

    // Transitions that require a mandatory, recorded reason.
    private static readonly HashSet<BeneficiaryStatus> RequireReasonTo =
        [BeneficiaryStatus.Suspended, BeneficiaryStatus.Blocked, BeneficiaryStatus.Expired, BeneficiaryStatus.Inactive];

    public static bool CanTransition(BeneficiaryStatus from, BeneficiaryStatus to) =>
        Allowed.TryGetValue(from, out var set) && set.Contains(to);

    public static bool RequiresReason(BeneficiaryStatus to) => RequireReasonTo.Contains(to);

    /// <summary>Validate a requested transition; returns an error message or null if legal.</summary>
    public static string? Validate(BeneficiaryStatus from, BeneficiaryStatus to, string? reason)
    {
        if (from == to) return $"already in status {to}";
        if (!CanTransition(from, to)) return $"illegal transition {from} → {to}";
        if (RequiresReason(to) && string.IsNullOrWhiteSpace(reason)) return $"a reason is required to move to {to}";
        return null;
    }

    // 23 §1's Actor column, enforced for the two transitions where the actor IS the control. Blocked means
    // "fraud/abuse confirmed": the spec reserves blocking for Super Admin / Director and unblocking for a
    // Director's case review. Until now any patient:write holder could quietly lift a fraud block — the
    // exact erasure 18.A4 removed the Blocked→Inactive edge to prevent, still reachable one edge over.
    //
    // The OTHER actor cells (suspend = case manager/finance, reactivate = registration, …) are deliberately
    // NOT enforced here: they describe who routinely performs a step, several name non-role actors
    // ("System (timer)", "policy-service"), and locking them down would break legitimate desk work the
    // portal already ships. The fraud edges are different in kind — they are the control, not the workflow.
    private static readonly Dictionary<(BeneficiaryStatus From, BeneficiaryStatus To), string[]> RestrictedActors = new()
    {
        [(BeneficiaryStatus.Active, BeneficiaryStatus.Blocked)] = ["super_admin", "medical_director"],
        [(BeneficiaryStatus.Suspended, BeneficiaryStatus.Blocked)] = ["super_admin", "medical_director"],
        [(BeneficiaryStatus.Blocked, BeneficiaryStatus.Active)] = ["medical_director"],
    };

    /// <summary>Whether <paramref name="roles"/> may perform this transition (23 §1 Actor column). Returns
    /// null when permitted, else the roles that could. Unlisted transitions are open to any authorized
    /// caller — scope and engine checks still apply upstream.</summary>
    public static string? DeniedActor(BeneficiaryStatus from, BeneficiaryStatus to, IEnumerable<string> roles) =>
        RestrictedActors.TryGetValue((from, to), out var allowed) && !roles.Any(allowed.Contains)
            ? string.Join(" / ", allowed)
            : null;
}

/// <summary>Issues monotonic Member No business keys <c>MRS-M-YYYY-NNNNNN</c> per year (0A §3).</summary>
public static class MemberNo
{
    public static string Format(int year, int sequence) =>
        $"MRS-M-{year:D4}-{sequence:D6}";
}
