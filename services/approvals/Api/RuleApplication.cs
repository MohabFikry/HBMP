using System.Text.Json;
using Mersal.Approvals.Domain;
using Mersal.Approvals.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Approvals.Api;

/// <summary>What the engine decided for one request, and which rule decided it.</summary>
/// <param name="Queue">Where it goes. Never null — see <see cref="RuleEvaluator.DefaultQueue"/>.</param>
/// <param name="RoutedByRule">The rule that chose the queue, or null when nothing matched.</param>
/// <param name="SlaHours">The reviewer's window, or null to fall back to the priority-based default.</param>
/// <param name="SlaByRule">The rule that set the window, or null when nothing matched.</param>
public sealed record RuleOutcome(string Queue, Guid? RoutedByRule, int? SlaHours, Guid? SlaByRule);

/// <summary>A rule that ALSO requires pre-authorization, and the reason it gives.</summary>
public sealed record PreauthRuleOutcome(Guid RuleId, string Reason);

/// <summary>
/// Applying the engine's routing and SLA rules to one request (ADR-0035 §5.4).
/// </summary>
/// <remarks>
/// <para>
/// <b>Fail-closed, and "closed" here means the default.</b> If the rule store cannot be read, the request goes
/// to the queue that existed before rules did and keeps the priority-based SLA. It does NOT go unrouted and it
/// does NOT lose its deadline: an engine that cannot reach its rules must degrade to the behaviour that was
/// correct yesterday, never to no behaviour at all. A request nobody can see is worse than one routed
/// imperfectly, and a request with no deadline is worse than one with a generic deadline.
/// </para>
/// <para>
/// This is the only place the rules touch a real request, and it can change two things: which queue, and how
/// many hours. It cannot change a status, a decision or an amount.
/// </para>
/// </remarks>
public sealed class RuleApplication(ApprovalsDbContext db, ILogger<RuleApplication> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        // String enums, BOTH ways. `JsonSerializerDefaults.Web` gives camelCase and case-insensitive
        // properties but still expects enums as NUMBERS — so a predicate arriving as {"priority":"Emergency"}
        // failed to parse and the rule was refused as malformed. The unit tests could not catch it: they
        // round-tripped through these same options, so a number went in and a number came out.
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public async Task<RuleOutcome> ForAsync(Authorization auth, DateTimeOffset at, CancellationToken ct)
    {
        var facts = FactsFrom(auth);

        List<ApprovalRule> rules;
        try
        {
            // Both families in one read. Two queries would let a rule set change between them and route a
            // request under one version while timing it under another.
            rules = await db.Rules.AsNoTracking()
                .Where(r => r.Enabled && r.EffectiveFrom <= at && (r.EffectiveTo == null || r.EffectiveTo > at))
                .ToListAsync(ct);
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException or OperationCanceledException)
        {
            // Loud: this IS a degradation nobody chose. The tenant may have rules that are not being applied,
            // and the only symptom otherwise is work quietly arriving in the wrong place.
            logger.LogError(ex,
                "Approval rules could not be read; routing to {Queue} and using the priority SLA. Any configured "
                + "routing is NOT being applied.", RuleEvaluator.DefaultQueue);
            return new RuleOutcome(RuleEvaluator.DefaultQueue, null, null, null);
        }

        var routing = RuleEvaluator.FirstMatch(rules, RuleFamily.Routing, at, facts, ParsePredicate);
        var sla = RuleEvaluator.FirstMatch(rules, RuleFamily.Sla, at, facts, ParsePredicate);

        var queue = routing is null ? RuleEvaluator.DefaultQueue : ReadQueue(routing);
        var hours = sla is null ? (int?)null : ReadHours(sla);

        return new RuleOutcome(queue, routing?.RuleId, hours, hours is null ? null : sla?.RuleId);
    }

    /// <summary>The facts a rule may match on, taken from the request as it stands.</summary>
    private static RuleFacts FactsFrom(Authorization auth)
    {
        string[] codes;
        try
        {
            codes = JsonSerializer.Deserialize<string[]>(auth.ServiceCodes, Json) ?? [];
        }
        catch (JsonException)
        {
            // An unreadable code list means no code matches — never "matches everything". A rule scoped to
            // MRI must not catch a request whose codes could not be parsed.
            codes = [];
        }

        return new RuleFacts(auth.Priority, auth.Source, auth.Kind, codes, auth.RequestingProviderId);
    }

    private static RulePredicate? ParsePredicate(ApprovalRule r)
    {
        try { return JsonSerializer.Deserialize<RulePredicate>(r.PredicateJson, Json); }
        catch (JsonException) { return null; }
    }

    /// <summary>The queue a matched routing rule names, falling back if its action is unreadable.</summary>
    private static string ReadQueue(ApprovalRule rule)
    {
        try
        {
            var action = JsonSerializer.Deserialize<RoutingAction>(rule.ActionJson, Json);
            return string.IsNullOrWhiteSpace(action?.Queue) ? RuleEvaluator.DefaultQueue : action.Queue;
        }
        catch (JsonException) { return RuleEvaluator.DefaultQueue; }
    }

    /// <summary>
    /// Does a rule ALSO require pre-authorization for this care? (ADR-0035 §5.2)
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Additive only.</b> The caller has already asked the plan. This can turn the answer from "no" to
    /// "yes"; it can never turn "yes" into "no" — there is no shape of rule that expresses it. The plan
    /// version's <c>RequiresPreauth</c> is a contractual term between the payer and Mersal, and a local rule
    /// able to switch it off would silently override a contract, surfacing months later as a denied claim
    /// nobody could trace to a configuration change.
    /// </para>
    /// <para>
    /// <b>Fail-safe here is the OTHER direction from routing.</b> If the rules cannot be read, this returns
    /// null and the plan's own answer stands — it does NOT invent a requirement. Gating care nobody chose to
    /// gate, because a database was briefly unreachable, would stop a beneficiary being treated for an
    /// infrastructure reason. The plan's answer is already fail-closed in <c>TierPricingService</c>; adding a
    /// second, invented gate on top of it would be a failure mode with no author.
    /// </para>
    /// </remarks>
    public async Task<PreauthRuleOutcome?> PreauthAsync(
        string benefitCategory, IReadOnlyList<string> serviceCodes, decimal? estimatedAmount,
        Guid? providerId, DateTimeOffset at, CancellationToken ct)
    {
        List<ApprovalRule> rules;
        try
        {
            rules = await db.Rules.AsNoTracking()
                .Where(r => r.Family == RuleFamily.Preauth && r.Enabled
                            && r.EffectiveFrom <= at && (r.EffectiveTo == null || r.EffectiveTo > at))
                .ToListAsync(ct);
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException or OperationCanceledException)
        {
            logger.LogError(ex,
                "Pre-authorization rules could not be read; the plan's own answer stands. Any configured "
                + "additional trigger is NOT being applied.");
            return null;
        }

        // The facts a pre-auth question carries. Priority/source/kind are not known at ordering time — there is
        // no authorization yet — so they take neutral values and a rule predicating on them simply will not
        // match, which is the honest outcome rather than a false one.
        var facts = new RuleFacts(
            AuthPriority.Routine, AuthSource.OrderLine, AuthKind.Review,
            serviceCodes, providerId, benefitCategory, estimatedAmount);

        var match = RuleEvaluator.FirstMatch(rules, RuleFamily.Preauth, at, facts, ParsePredicate);
        if (match is null) return null;

        try
        {
            var action = JsonSerializer.Deserialize<PreauthAction>(match.ActionJson, Json);
            // A rule whose reason cannot be read still REQUIRES — it matched, and the requirement is the
            // conservative half. Only the explanation is lost, and the response says so rather than inventing one.
            return new PreauthRuleOutcome(match.RuleId,
                string.IsNullOrWhiteSpace(action?.Reason)
                    ? "A pre-authorization rule matched this care; its stated reason could not be read."
                    : action.Reason);
        }
        catch (JsonException)
        {
            return new PreauthRuleOutcome(match.RuleId,
                "A pre-authorization rule matched this care; its stated reason could not be read.");
        }
    }

    /// <summary>The hours a matched SLA rule sets, or null to keep the priority-based default.</summary>
    private static int? ReadHours(ApprovalRule rule)
    {
        try
        {
            var action = JsonSerializer.Deserialize<SlaAction>(rule.ActionJson, Json);
            return action is { Hours: > 0 } ? action.Hours : null;
        }
        catch (JsonException) { return null; }
    }
}
