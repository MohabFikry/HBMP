namespace Mersal.Approvals.Domain;

/// <summary>Which question a rule answers. One table, because they share everything except the action.</summary>
public enum RuleFamily
{
    /// <summary>Which queue a request lands on. Changes WHO decides, never what is decided.</summary>
    Routing,

    /// <summary>How long the reviewer has. Changes BY WHEN, never what is decided.</summary>
    Sla,

    /// <summary>
    /// Also require pre-authorization for care the plan does not already gate.
    /// </summary>
    /// <remarks>
    /// <b>Additive only, and structurally so.</b> A matching rule can only turn the requirement ON — see
    /// <see cref="PreauthAction"/>, which carries a reason and nothing else. There is no field that could
    /// express "stop requiring", so the invariant cannot be broken by a bad rule, a bad migration or a future
    /// author who thought a boolean would be convenient.
    /// </remarks>
    Preauth,

    /// <summary>
    /// Approve without a human, within a ceiling.
    /// </summary>
    /// <remarks>
    /// <b>There is deliberately no AutoReject.</b> The two failure modes are not symmetric. A wrong
    /// auto-approval costs the payer money and a human reviews the claim later; a wrong auto-rejection denies
    /// care to a refugee with nobody having looked, and — per <c>libs/benefit-pricing</c>'s own header — they
    /// have "no reviewer in the loop and no recovery path". The throughput a reject rule would buy is
    /// available without the harm: route to a priority queue with the engine's stated reason attached, which
    /// the <see cref="Routing"/> family already does, and the decision still has a person's name on it.
    /// </remarks>
    AutoApprove,
}

/// <summary>
/// What a rule matches on.
/// </summary>
/// <remarks>
/// <para>
/// Every field is optional and an omitted field matches everything, so a rule states only what it cares
/// about. All present fields must match — AND, never OR. An OR would make "urgent pharmacy requests" and
/// "anything urgent, plus all pharmacy" the same rule text, and the author would have no way to say which
/// they meant.
/// </para>
/// <para>
/// The fields are exactly what an authorization carries at the moment it is picked up. A predicate over
/// something the request does not know yet — a decided amount, a reviewer's workload — would be a rule that
/// can never fire, and it would look correct in the editor.
/// </para>
/// </remarks>
public sealed record RulePredicate
{
    public AuthPriority? Priority { get; init; }
    public AuthSource? Source { get; init; }
    public AuthKind? Kind { get; init; }

    /// <summary>
    /// Matches when the request carries ANY of these service codes.
    /// </summary>
    /// <remarks>
    /// Any-of within the list, because a request carries several codes and "a request containing an MRI" is
    /// the question people actually ask. The list itself is still ANDed with the other fields.
    /// </remarks>
    public IReadOnlyList<string>? ServiceCodes { get; init; }

    public Guid? RequestingProviderId { get; init; }

    /// <summary>
    /// The benefit category — CONSULT, LAB, IMAGING, PHARMACY, REFERRAL.
    /// </summary>
    /// <remarks>
    /// Matched case-insensitively against the same closed vocabulary `eligibility.coverage_projection` is
    /// constrained to. A rule naming a category nothing writes would never fire and would look live.
    /// </remarks>
    public string? BenefitCategory { get; init; }

    /// <summary>
    /// Matches when the estimated amount is at or above this.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A floor rather than a range, because the question a supervisor asks is "anything over X needs a look".
    /// </para>
    /// <para>
    /// <b>An UNKNOWN amount does not match.</b> Strict predicate semantics: a figure nobody supplied cannot be
    /// shown to be at or above the floor, so the rule stays out of it rather than guessing in either
    /// direction. That is predictable, which matters more here than clever.
    /// </para>
    /// <para>
    /// <b>The residual risk, stated rather than papered over.</b> A caller that omits the amount is not gated
    /// by an amount rule. That is NOT the same as care nobody could price — a service the plan cannot price
    /// makes <c>RequiresPreauthAsync</c> indeterminate, which already requires authorization before any rule
    /// is consulted, so the dangerous case is covered by the path above this one. What remains is a caller
    /// who could send an amount and does not. Closing that means making the amount mandatory on the
    /// pre-auth question, which is a change to every caller and belongs in its own decision, not in a
    /// predicate that quietly gates everything it was not told about.
    /// </para>
    /// </remarks>
    public decimal? AmountAtLeast { get; init; }

    /// <summary>Does this request match?</summary>
    public bool Matches(RuleFacts facts)
    {
        if (Priority is { } p && facts.Priority != p) return false;
        if (Source is { } s && facts.Source != s) return false;
        if (Kind is { } k && facts.Kind != k) return false;
        if (RequestingProviderId is { } provider && facts.RequestingProviderId != provider) return false;

        if (BenefitCategory is { Length: > 0 } category
            && !string.Equals(facts.BenefitCategory, category, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (AmountAtLeast is { } floor)
        {
            // An UNKNOWN amount does not clear a floor. Treating null as zero would let precisely the requests
            // nobody could price slip under a "anything over X needs a look" rule.
            if (facts.EstimatedAmount is not { } amount || amount < floor) return false;
        }

        if (ServiceCodes is { Count: > 0 } wanted)
        {
            // Case-insensitive: a service code is an identifier, and an editor that treated "MRI-01" and
            // "mri-01" as different rules would produce one that silently never fires.
            if (!wanted.Any(w => facts.ServiceCodes.Any(
                    c => string.Equals(c, w, StringComparison.OrdinalIgnoreCase))))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>True when the predicate constrains nothing — it matches every request.</summary>
    public bool IsCatchAll =>
        Priority is null && Source is null && Kind is null
        && RequestingProviderId is null && BenefitCategory is null && AmountAtLeast is null
        && (ServiceCodes is null || ServiceCodes.Count == 0);
}

/// <summary>What the evaluator is told about one request. A snapshot, so evaluation is pure.</summary>
/// <param name="BenefitCategory">CONSULT / LAB / IMAGING / PHARMACY / REFERRAL, or null when not known.</param>
/// <param name="EstimatedAmount">
/// What the care is expected to cost, or null when nobody could price it. NULL is UNKNOWN and never zero: an
/// amount-floor rule must not be cleared by a request whose cost could not be established.
/// </param>
public sealed record RuleFacts(
    AuthPriority Priority,
    AuthSource Source,
    AuthKind Kind,
    IReadOnlyList<string> ServiceCodes,
    Guid? RequestingProviderId,
    string? BenefitCategory = null,
    decimal? EstimatedAmount = null);

/// <summary>
/// The tenant's auto-decision kill switch.
/// </summary>
/// <remarks>
/// <para>
/// <b>No row means OFF.</b> Auto-approval is opt-in per tenant and stays opt-in: a new tenant, a restored
/// database and a failed migration all produce "no row", and every one of those must mean nobody is being paid
/// without a human having looked.
/// </para>
/// <para>
/// It lives in approvals' own schema rather than as a config row in admin-service, because the switch somebody
/// reaches for at 02:00 must not depend on another service being reachable — and if it could not be read, the
/// safe reading is "off", which a local row makes deterministic rather than a matter of whether an HTTP call
/// timed out.
/// </para>
/// </remarks>
public sealed class AutoDecisionSwitch
{
    public string TenantId { get; set; } = "";
    public bool Enabled { get; set; }
    /// <summary>Why it is in this state. Turning it on is a decision somebody owns; turning it off in a hurry
    /// is one somebody should be able to explain afterwards.</summary>
    public string Reason { get; set; } = "";
    public string UpdatedBy { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>An effective-dated rule.</summary>
public sealed class ApprovalRule
{
    public Guid RuleId { get; set; }
    public string TenantId { get; set; } = "";
    public RuleFamily Family { get; set; }

    /// <summary>
    /// Lower runs first.
    /// </summary>
    /// <remarks>
    /// Ties break on <see cref="RuleId"/>, so two rules with the same priority always resolve the same way.
    /// Without that, which of them wins would depend on the order the database happened to return rows — the
    /// same request routed two ways on two days, with nothing changed and nothing to point at.
    /// </remarks>
    public int Priority { get; set; }

    public string PredicateJson { get; set; } = "{}";
    public string ActionJson { get; set; } = "{}";

    public DateTimeOffset EffectiveFrom { get; set; }
    public DateTimeOffset? EffectiveTo { get; set; }
    public int VersionNo { get; set; }
    public bool Enabled { get; set; } = true;

    public string AuthoredBy { get; set; } = "";
    public string Rationale { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }

    public bool InForceAt(DateTimeOffset at) =>
        Enabled && at >= EffectiveFrom && (EffectiveTo is null || at < EffectiveTo);
}

/// <summary>A rule's routing action: the queue a matching request lands on.</summary>
public sealed record RoutingAction(string Queue);

/// <summary>A rule's SLA action: how many hours the reviewer has.</summary>
public sealed record SlaAction(int Hours);

/// <summary>
/// A rule's pre-auth action: why this care also needs authorization.
/// </summary>
/// <remarks>
/// <para>
/// <b>A reason, and nothing else — that is the design.</b> The plan version's own <c>RequiresPreauth</c> is a
/// contractual term between the payer and Mersal. A rule that could switch it OFF would silently override a
/// contract, and the divergence would surface months later as a denied claim nobody could trace back to a
/// configuration change. So this record has no boolean: a matching rule means "also require", and there is
/// nothing to write that means anything else.
/// </para>
/// <para>
/// The reason is shown to the person it stops. "Authorization is required" with no account of why is how a
/// gate becomes something people work around.
/// </para>
/// </remarks>
public sealed record PreauthAction(string Reason);

/// <summary>
/// A rule's auto-approval action: the ceiling it may approve up to, and why it exists.
/// </summary>
/// <param name="MaxAmountEgp">
/// The most this rule may approve without a human. Per-rule AND bounded by a tenant-wide hard maximum, so a
/// supervisor cannot write one rule that approves everything.
/// </param>
/// <param name="Reason">
/// Recorded as the decision's rationale. An approval with no account of why is indistinguishable, in the
/// ledger, from one nobody meant to make.
/// </param>
public sealed record AutoApproveAction(decimal MaxAmountEgp, string Reason);

/// <summary>
/// Whether a request may be approved without a human, and if not, why not.
/// </summary>
/// <remarks>
/// Every condition is checked and the FIRST failure is reported, because "it did not auto-approve" is not an
/// answer anybody can act on. A supervisor whose rule never fires needs to know whether the switch is off, the
/// amount was over the ceiling, or a clinical warning was outstanding — three different remedies.
/// </remarks>
public enum AutoApproveRefusal
{
    /// <summary>It may. Nothing refused it.</summary>
    None,
    /// <summary>The tenant's kill switch is off. This is the default and it is deliberate.</summary>
    SwitchOff,
    /// <summary>No rule matched — the ordinary case, not a fault.</summary>
    NoRule,
    /// <summary>The amount is unknown. An unpriced request is never auto-approved.</summary>
    AmountUnknown,
    /// <summary>Over the rule's own ceiling.</summary>
    OverRuleCeiling,
    /// <summary>Over the tenant-wide hard maximum, whatever the rule says.</summary>
    OverHardMaximum,
    /// <summary>A clinical warning is outstanding on this request.</summary>
    ClinicalWarning,
    /// <summary>The benefit category is excluded by the plan.</summary>
    CategoryExcluded,
}

/// <summary>
/// The conditions an auto-approval must clear, in one place.
/// </summary>
/// <remarks>
/// <para>
/// Pure and synchronous, like <see cref="RuleEvaluator"/>, so the decision that pays a provider without a
/// human can be tested without a database or a server — and so it always gives the same answer for the same
/// inputs, which is the minimum a machine decision owes anybody reviewing it later.
/// </para>
/// <para>
/// <b>Every gate fails toward the human.</b> Unknown amount, no matching rule, missing switch: all refuse.
/// There is no input that produces an approval by omission.
/// </para>
/// </remarks>
public static class AutoApproval
{
    /// <summary>
    /// The most any rule may approve, whatever it says about itself.
    /// </summary>
    /// <remarks>
    /// A ceiling on the ceiling. Without it, "bounded" would mean "bounded by whatever the last person to edit
    /// a rule typed", and a single mistyped figure would be the entire control.
    /// </remarks>
    public const decimal HardMaximumEgp = 5_000m;

    /// <summary>May this request be approved without a human?</summary>
    public static AutoApproveRefusal Check(
        bool switchEnabled, AutoApproveAction? matched, decimal? amount,
        bool hasOutstandingClinicalWarning, bool categoryExcluded)
    {
        // The switch first, and unconditionally. It is what somebody reaches for when a rule is misbehaving,
        // and it must not depend on anything about the request being well-formed.
        if (!switchEnabled) return AutoApproveRefusal.SwitchOff;
        if (matched is null) return AutoApproveRefusal.NoRule;

        // An unpriced request is never auto-approved. "We could not work out what this costs" is not a small
        // amount, and approving it would be paying an unknown figure without a human.
        if (amount is not { } value) return AutoApproveRefusal.AmountUnknown;

        if (categoryExcluded) return AutoApproveRefusal.CategoryExcluded;
        if (hasOutstandingClinicalWarning) return AutoApproveRefusal.ClinicalWarning;
        if (value > matched.MaxAmountEgp) return AutoApproveRefusal.OverRuleCeiling;
        if (value > HardMaximumEgp) return AutoApproveRefusal.OverHardMaximum;

        return AutoApproveRefusal.None;
    }

    /// <summary>
    /// How a rule-made decision is attributed: <c>rule:&lt;id&gt;@v&lt;n&gt;</c>.
    /// </summary>
    /// <remarks>
    /// Never a person's subject. The ledger is hash-chained so a decision cannot be quietly reattributed
    /// later; writing a human's id on a machine's decision would falsify it at the moment of writing instead.
    /// </remarks>
    public static string Attribution(Guid ruleId, int versionNo) => $"rule:{ruleId}@v{versionNo}";
}

/// <summary>
/// Picking the rule that applies.
/// </summary>
/// <remarks>
/// <para>
/// Pure and synchronous on purpose. Everything it needs is passed in, so the decision that routes a
/// beneficiary's request can be tested without a database, a clock or a server — and so the same inputs
/// always produce the same answer, which is what makes a routing decision explainable after the fact.
/// </para>
/// <para>
/// <b>First match wins, and the order is total.</b> By <see cref="ApprovalRule.Priority"/>, then by
/// <c>RuleId</c>. A partial order would leave two same-priority rules resolving by whatever the query
/// returned first.
/// </para>
/// </remarks>
public static class RuleEvaluator
{
    /// <summary>
    /// The queue a request routes to when no rule matches, or when the rules could not be read.
    /// </summary>
    /// <remarks>
    /// <b>Routing must never strand work.</b> A request that matched nothing still has to land somewhere a
    /// human is looking, and the honest place is the queue that existed before rules did. Returning "no
    /// queue" would leave the request invisible, which is worse than routing it imperfectly.
    /// </remarks>
    public const string DefaultQueue = "default";

    /// <summary>Rules in force at an instant, in the order they are applied.</summary>
    public static IEnumerable<ApprovalRule> InForce(
        IEnumerable<ApprovalRule> rules, RuleFamily family, DateTimeOffset at) =>
        rules.Where(r => r.Family == family && r.InForceAt(at))
             .OrderBy(r => r.Priority)
             .ThenBy(r => r.RuleId);

    /// <summary>
    /// The first rule in force that matches, or null.
    /// </summary>
    /// <param name="parse">
    /// How to read a rule's predicate. Passed in so a malformed one is the CALLER's problem to report —
    /// swallowing a parse failure here would silently skip a rule a supervisor believes is live.
    /// </param>
    public static ApprovalRule? FirstMatch(
        IEnumerable<ApprovalRule> rules, RuleFamily family, DateTimeOffset at, RuleFacts facts,
        Func<ApprovalRule, RulePredicate?> parse)
    {
        foreach (var rule in InForce(rules, family, at))
        {
            var predicate = parse(rule);
            // A predicate that will not parse is treated as NOT matching, never as matching everything. A
            // malformed catch-all would swallow the whole queue.
            if (predicate is null) continue;
            if (predicate.Matches(facts)) return rule;
        }
        return null;
    }
}
