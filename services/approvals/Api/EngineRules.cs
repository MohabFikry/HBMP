using System.Text.Json;
using Mersal.Approvals.Domain;
using Mersal.Approvals.Infrastructure;
using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Auth.Authorization;
using Mersal.Authz;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Approvals.Api;

/// <summary>
/// Authoring the approvals engine's routing and SLA rules (ADR-0035 §5.1/§5.4).
/// </summary>
/// <remarks>
/// <para>
/// <b>Routing and SLA first, deliberately.</b> They change WHO decides and BY WHEN, never WHAT is decided.
/// Nothing this surface can express approves or refuses anything, which makes it the right family to prove
/// the rule infrastructure on before pre-auth triggers and auto-approval are built on top of it.
/// </para>
/// <para>
/// <b>Every edit appends.</b> Publishing a change closes the current version's window and opens a new one, so
/// a request routed last Tuesday stays explainable against the rules in force last Tuesday. Nothing is
/// updated in place and nothing is deleted — disabling a rule is a state, not a removal.
/// </para>
/// </remarks>
public static class EngineRules
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        // String enums, BOTH ways. `JsonSerializerDefaults.Web` gives camelCase and case-insensitive
        // properties but still expects enums as NUMBERS — so a predicate arriving as {"priority":"Emergency"}
        // failed to parse and the rule was refused as malformed. The unit tests could not catch it: they
        // round-tripped through these same options, so a number went in and a number came out.
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    /// <summary>
    /// The queues a rule may target.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A closed list, because <b>routing must never strand work</b>. A rule pointing at a queue nobody watches
    /// sends requests somewhere they are invisible, and the symptom is not an error — it is a queue that has
    /// gone quiet, which reads like a good week. A typo would do it.
    /// </para>
    /// <para>
    /// ADR-0035 asked for a watcher count here. There is no watcher model in the platform yet — assignment is
    /// to a reviewer, not to a queue — so this is the achievable half of that intent: a name that was never
    /// declared cannot be saved. When queues gain watchers, the check tightens rather than changes shape.
    /// </para>
    /// </remarks>
    public static readonly IReadOnlySet<string> Queues = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        RuleEvaluator.DefaultQueue, "clinical", "high-cost", "pharmacy", "imaging", "escalation",
    };

    public static void MapEngineRules(this IEndpointRouteBuilder app)
    {
        var v1 = app.MapGroup("/api/v1/approval-rules").WithTags("approvals-engine");

        // ---- read -------------------------------------------------------------------------------------
        v1.MapGet("/", async (
            string? family, ApprovalsDbContext db, ApprovalsGate gate, CancellationToken ct) =>
        {
            var denied = await gate.CheckAsync(ApprovalsPolicies.Configure, "rules", "list", ct);
            if (denied is not null) return denied;

            var q = db.Rules.AsNoTracking();
            if (Enum.TryParse<RuleFamily>(family, ignoreCase: true, out var f)) q = q.Where(r => r.Family == f);

            // Superseded versions are returned too. A supervisor asking "why did this go there last week"
            // needs the rule that was in force then, and hiding closed windows would answer only about today.
            var rows = await q
                .OrderBy(r => r.Family).ThenBy(r => r.Priority).ThenBy(r => r.RuleId)
                .Take(500)
                .Select(r => new RuleView(
                    r.RuleId, r.Family.ToString(), r.Priority, r.PredicateJson, r.ActionJson,
                    r.EffectiveFrom, r.EffectiveTo, r.VersionNo, r.Enabled, r.AuthoredBy, r.Rationale))
                .ToListAsync(ct);

            return Results.Ok(new RuleListView(rows, Queues.OrderBy(x => x).ToList(),
                RuleEvaluator.DefaultQueue));
        }).RequireAuthorization(HbmpPolicies.Scope("auth:configure"));

        // ---- the kill switch ---------------------------------------------------------------------------
        //
        // Its own endpoint, not a field on a rule. The control somebody reaches for at 02:00 because a rule is
        // misbehaving must be one action away and must not require authoring anything — a switch you can only
        // reach by editing the thing that is misbehaving is not a kill switch.
        v1.MapGet("/auto-decision", async (
            ApprovalsDbContext db, ApprovalsGate gate, IHbmpPrincipalAccessor me, CancellationToken ct) =>
        {
            var denied = await gate.CheckAsync(ApprovalsPolicies.Configure, "auto-decision", "read", ct);
            if (denied is not null) return denied;

            var tenant = me.Principal?.TenantId ?? "";
            var row = await db.AutoDecisionSwitches.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenant, ct);

            // No row is answered as OFF rather than as 404. A caller must never have to decide what a missing
            // switch means, and the only safe reading is that nobody is being paid without a human.
            return Results.Ok(new AutoDecisionSwitchView(
                row?.Enabled ?? false,
                row?.Reason ?? "Auto-decision has never been turned on for this tenant.",
                row?.UpdatedBy, row?.UpdatedAt, AutoApproval.HardMaximumEgp));
        }).RequireAuthorization(HbmpPolicies.Scope("auth:configure"));

        v1.MapPut("/auto-decision", async (
            SetAutoDecisionRequest req, ApprovalsDbContext db, ApprovalsGate gate, IAuditClient audit,
            IHbmpPrincipalAccessor me, TimeProvider clock, CancellationToken ct) =>
        {
            var denied = await gate.CheckAsync(ApprovalsPolicies.Configure, "auto-decision", "set", ct);
            if (denied is not null) return denied;

            // A reason is required in BOTH directions. Turning it on is a decision somebody owns; turning it
            // off in a hurry is one somebody should be able to explain the following morning.
            if (string.IsNullOrWhiteSpace(req.Reason))
            {
                return ProblemResults.Invalid("reason-required",
                    "State why. Turning auto-decision on is a decision somebody owns, and turning it off in a "
                    + "hurry is one somebody has to explain afterwards.");
            }

            var tenant = me.Principal?.TenantId;
            if (string.IsNullOrWhiteSpace(tenant))
                return Results.Problem(statusCode: 401, title: "no-tenant", type: "urn:hbmp:no-tenant");

            var now = clock.GetUtcNow();
            var actor = me.Principal?.Subject ?? "unknown";
            var row = await db.AutoDecisionSwitches.FirstOrDefaultAsync(x => x.TenantId == tenant, ct);
            var before = row?.Enabled ?? false;

            if (row is null)
            {
                row = new AutoDecisionSwitch { TenantId = tenant };
                db.AutoDecisionSwitches.Add(row);
            }
            row.Enabled = req.Enabled;
            row.Reason = req.Reason.Trim();
            row.UpdatedBy = actor;
            row.UpdatedAt = now;
            await db.SaveChangesAsync(ct);

            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "auto_decision_switch",
                EntityId = tenant,
                Action = AuditAction.Update,
                ActorUserId = actor,
                ActorRole = me.Principal?.Roles.FirstOrDefault(),
                BeforeState = JsonSerializer.Serialize(new { enabled = before }, Json),
                AfterState = JsonSerializer.Serialize(new { enabled = row.Enabled, row.Reason }, Json),
            }, ct);

            return Results.Ok(new AutoDecisionSwitchView(
                row.Enabled, row.Reason, row.UpdatedBy, row.UpdatedAt, AutoApproval.HardMaximumEgp));
        }).RequireAuthorization(HbmpPolicies.Scope("auth:configure"));

        // ---- author -----------------------------------------------------------------------------------
        v1.MapPost("/", async (
            SaveRuleRequest req, ApprovalsDbContext db, ApprovalsGate gate, IAuditClient audit,
            IHbmpPrincipalAccessor me, TimeProvider clock, CancellationToken ct) =>
        {
            var denied = await gate.CheckAsync(ApprovalsPolicies.Configure, "rules", "author", ct);
            if (denied is not null) return denied;

            if (!Enum.TryParse<RuleFamily>(req.Family, ignoreCase: true, out var family))
                return ProblemResults.Unprocessable("unknown-family", $"'{req.Family}' is not a rule family.");

            if (string.IsNullOrWhiteSpace(req.Rationale))
            {
                return ProblemResults.Invalid("rationale-required",
                    "State why this rule exists. It is what somebody reads when asking why work went where it went.");
            }

            // The predicate must PARSE here, not at evaluation time. A rule that will not parse is skipped by
            // the evaluator — correctly, since a malformed catch-all would swallow the queue — which means a
            // supervisor would see it listed, believe it live, and never learn it does nothing.
            RulePredicate? predicate;
            try
            {
                predicate = JsonSerializer.Deserialize<RulePredicate>(req.Predicate.GetRawText(), Json);
            }
            catch (JsonException)
            {
                predicate = null;
            }
            if (predicate is null)
                return ProblemResults.Unprocessable("predicate-invalid", "This rule's conditions could not be read.");

            // The action must be valid FOR ITS FAMILY. A routing action on an SLA rule would save cleanly and
            // then never do anything, which is the same silent failure in a different place.
            string actionJson;
            switch (family)
            {
                case RuleFamily.Routing:
                {
                    var action = TryRead<RoutingAction>(req.Action);
                    if (action is null || string.IsNullOrWhiteSpace(action.Queue))
                        return ProblemResults.Unprocessable("action-invalid", "A routing rule must name a queue.");
                    if (!Queues.Contains(action.Queue))
                    {
                        return ProblemResults.Unprocessable("unknown-queue",
                            $"'{action.Queue}' is not a queue anybody watches. Routing must never send a request "
                            + $"somewhere it becomes invisible. Known queues: {string.Join(", ", Queues.Order())}.");
                    }
                    actionJson = JsonSerializer.Serialize(action, Json);
                    break;
                }
                case RuleFamily.Sla:
                {
                    var action = TryRead<SlaAction>(req.Action);
                    if (action is null || action.Hours < 1 || action.Hours > 720)
                    {
                        return ProblemResults.Unprocessable("action-invalid",
                            "An SLA rule must set between 1 and 720 hours. Zero would breach on arrival, and "
                            + "beyond a month the deadline has stopped being one.");
                    }
                    actionJson = JsonSerializer.Serialize(action, Json);
                    break;
                }
                case RuleFamily.Preauth:
                {
                    var action = TryRead<PreauthAction>(req.Action);
                    if (action is null || string.IsNullOrWhiteSpace(action.Reason))
                    {
                        return ProblemResults.Unprocessable("action-invalid",
                            "A pre-authorization rule must say WHY this care needs a decision. The reason is "
                            + "shown to the person it stops, and \"authorization is required\" with no account "
                            + "of why is how a gate becomes something people work around.");
                    }
                    // No boolean to validate: a Preauth rule can only ADD a requirement. The plan version's
                    // RequiresPreauth is a contractual term, and nothing here can switch it off.
                    actionJson = JsonSerializer.Serialize(action, Json);
                    break;
                }
                case RuleFamily.AutoApprove:
                {
                    var action = TryRead<AutoApproveAction>(req.Action);
                    if (action is null || string.IsNullOrWhiteSpace(action.Reason))
                    {
                        return ProblemResults.Unprocessable("action-invalid",
                            "An auto-approval rule must say why it exists. An approval with no account of why "
                            + "is indistinguishable, in the ledger, from one nobody meant to make.");
                    }
                    if (action.MaxAmountEgp <= 0m || action.MaxAmountEgp > AutoApproval.HardMaximumEgp)
                    {
                        return ProblemResults.Unprocessable("ceiling-out-of-range",
                            $"An auto-approval ceiling must be between 0 and {AutoApproval.HardMaximumEgp} EGP. "
                            + "The platform maximum binds whatever a rule claims for itself — otherwise "
                            + "\"bounded\" would mean bounded by whatever the last person to edit it typed.");
                    }
                    actionJson = JsonSerializer.Serialize(action, Json);
                    break;
                }
                default:
                    return ProblemResults.Unprocessable("unknown-family", "Unhandled rule family.");
            }

            // A catch-all is fine for routing (it gives unmatched work a home) and fine for SLA (it gives
            // everything a deadline). For PREAUTH it would put every act of care on the platform behind a
            // decision, which is not a configuration change — it is a service outage with a benefit rationale.
            if (family == RuleFamily.Preauth && predicate.IsCatchAll)
            {
                return ProblemResults.Unprocessable("preauth-catch-all",
                    "A pre-authorization rule with no conditions would require a decision for every act of "
                    + "care on the platform. Narrow it — by category, service code, amount or provider.");
            }

            // The worst rule anybody could write: approve anything, up to the ceiling, without a human. Bounded
            // by the ceiling it may be, but "which care" should still be a decision somebody made deliberately.
            if (family == RuleFamily.AutoApprove && predicate.IsCatchAll)
            {
                return ProblemResults.Unprocessable("auto-approve-catch-all",
                    "An auto-approval rule with no conditions would approve ANY request under the ceiling "
                    + "without a human. Narrow it — by category, service code or provider.");
            }

            var tenant = me.Principal?.TenantId;
            if (string.IsNullOrWhiteSpace(tenant))
                return Results.Problem(statusCode: 401, title: "no-tenant", type: "urn:hbmp:no-tenant");

            var now = clock.GetUtcNow();
            var actor = me.Principal?.Subject ?? "unknown";

            // Supersede rather than update. The prior version keeps its content and gains an end to its
            // window; the new one starts now.
            var prior = req.SupersedesRuleId is { } supersedes
                ? await db.Rules.FirstOrDefaultAsync(r => r.RuleId == supersedes && r.EffectiveTo == null, ct)
                : null;
            if (prior is not null) prior.EffectiveTo = now;

            var rule = new ApprovalRule
            {
                RuleId = Guid.NewGuid(),
                TenantId = tenant,
                Family = family,
                Priority = req.Priority,
                PredicateJson = JsonSerializer.Serialize(predicate, Json),
                ActionJson = actionJson,
                EffectiveFrom = now,
                EffectiveTo = null,
                VersionNo = (prior?.VersionNo ?? 0) + 1,
                Enabled = req.Enabled,
                AuthoredBy = actor,
                Rationale = req.Rationale.Trim(),
                CreatedAt = now,
            };
            db.Rules.Add(rule);
            await db.SaveChangesAsync(ct);

            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "approval_rule",
                EntityId = rule.RuleId.ToString(),
                Action = AuditAction.Create,
                ActorUserId = actor,
                ActorRole = me.Principal?.Roles.FirstOrDefault(),
                AfterState = JsonSerializer.Serialize(new
                {
                    family = rule.Family.ToString(), rule.Priority, rule.Enabled,
                    predicate = predicate, action = actionJson, rule.Rationale, rule.VersionNo,
                }, Json),
            }, ct);

            return Results.Created($"/api/v1/approval-rules/{rule.RuleId}", new
            {
                rule.RuleId, family = rule.Family.ToString(), rule.VersionNo, rule.EffectiveFrom,
                supersededVersion = prior?.VersionNo,
            });
        }).RequireAuthorization(HbmpPolicies.Scope("auth:configure"));
    }

    private static T? TryRead<T>(JsonElement e)
    {
        try { return JsonSerializer.Deserialize<T>(e.GetRawText(), Json); }
        catch (JsonException) { return default; }
    }
}

/// <summary>One rule as the supervisor's screen reads it. Superseded versions are included — see the note
/// on the list endpoint.</summary>
public sealed record RuleView(
    Guid RuleId, string Family, int Priority, string Predicate, string Action,
    DateTimeOffset EffectiveFrom, DateTimeOffset? EffectiveTo, int VersionNo, bool Enabled,
    string AuthoredBy, string Rationale);

/// <summary>The rules, plus the queues a routing rule may target and the one nothing-matched falls back to.</summary>
public sealed record RuleListView(
    IReadOnlyList<RuleView> Rules, IReadOnlyList<string> Queues, string DefaultQueue);

/// <summary>The kill switch's state. `Enabled: false` is what a tenant with no row is told.</summary>
public sealed record AutoDecisionSwitchView(
    bool Enabled, string Reason, string? UpdatedBy, DateTimeOffset? UpdatedAt, decimal HardMaximumEgp);

public sealed record SetAutoDecisionRequest(bool Enabled, string Reason);

public sealed record SaveRuleRequest(
    string Family, int Priority, JsonElement Predicate, JsonElement Action,
    string Rationale, bool Enabled = true, Guid? SupersedesRuleId = null);
