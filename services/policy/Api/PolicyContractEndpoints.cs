using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Authz;
using Mersal.Events;
using Mersal.Policy.Domain;
using Mersal.Policy.Infrastructure;
using Mersal.Time;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Policy.Api;

/// <summary>
/// Phase 19.8 — the CONTRACT as an administrable record (design 57).
///
/// <para>A policy could be created and renewed and never edited. Its effective window and member cap were
/// fixed at creation, so a renegotiated ceiling meant a renewal — a different contract — or a hand-written
/// row. <c>status</c> has accepted <c>Suspended</c> since 0001 and nothing has ever set it, so the state a
/// contract enters when a payer stops paying could not be recorded at all. And the row could not name who
/// last touched it, because until 0021 it had no subject columns.</para>
///
/// <para><b>Suspending is not deactivating, and this is the file where that matters.</b> Deactivating a payer
/// is REFUSED while it still funds live policies (19.7): it is a catalogue action, and cascading it would end
/// cover nobody reviewed. Suspending a contract is the opposite — it IS the operation, the thing that happens
/// when a payer stops paying, and it necessarily reaches live members. Refusing it would be refusing the
/// operation. So the reason is mandatory, the affected member count comes back in the response, and the
/// screen states the impact in the confirmation instead of discovering it afterwards.</para>
///
/// <para><b>The write scope is <c>policy:write</c>, not <c>policy:admin</c>.</b> Payers and plans are the
/// benefit PRODUCT and belong to the Policy Administrator; a policy is a MEMBERSHIP artefact — Beneficiary
/// Management issues, renews and suspends contracts, and already holds <c>policy:write</c> for
/// <c>POST /policies</c>. Putting an edit of the same row behind a different scope from its create would mean
/// the team that issues a contract cannot correct its dates.</para>
/// </summary>
public static class PolicyContractEndpoints
{
    public static void MapPolicyAdmin(RouteGroupBuilder v1)
    {
        MapDetail(v1);
        MapUpdate(v1);
        MapStatus(v1);
        MapHistory(v1);
    }

    // ---- read --------------------------------------------------------------------------------------------

    private static void MapDetail(RouteGroupBuilder v1)
    {
        v1.MapGet("/policies/{id:guid}", async (
            Guid id, PolicyDbContext db, PolicyGate gate, IPayerDirectory payers, IAuditClient audit,
            IBusinessCalendar calendar, CancellationToken ct) =>
        {
            if (await gate.CheckAsync(PolicyPolicies.Read, ct) is { } denied) return denied;
            var principal = gate.Principal!;

            var policy = await db.Policies.AsNoTracking()
                .FirstOrDefaultAsync(p => p.PolicyId == id && !p.IsDeleted, ct);
            if (policy is null) return NotFound();

            if (await RefusedByPayerScope(policy, principal, payers, audit, ct) is { } refused) return refused;

            var mayContract = AdministrativeProjection.MayReadContract(principal.Roles);
            var view = PolicyAdminView.From(policy, calendar.DateOf(DateTimeOffset.UtcNow), mayContract);
            var book = await BookAsync(db, policy,
                AdministrativeProjection.MayReadAmounts(principal.Roles), ct);
            return Results.Ok(new PolicyDetailView(view, book));
        })
        .Produces<PolicyDetailView>();
    }

    private static async Task<PolicyBookView> BookAsync(
        PolicyDbContext db, Domain.Policy policy, bool mayReadAmounts, CancellationToken ct)
    {
        var members = db.Enrollments.AsNoTracking().Where(e => e.PolicyId == policy.PolicyId && !e.IsDeleted);
        var memberCount = await members.CountAsync(ct);
        var activeMemberCount = await members.CountAsync(e => e.Status == EnrollmentStatus.Active, ct);

        var planCount = await db.PolicyPlans.AsNoTracking()
            .CountAsync(pp => pp.PolicyId == policy.PolicyId && !pp.IsDeleted, ct);

        var limits = db.CoverageLimits.AsNoTracking()
            .Where(l => db.Coverages.Any(c => c.CoverageId == l.CoverageId && !c.IsDeleted
                                              && c.PolicyId == policy.PolicyId));
        // Summed unconditionally: the percentage below needs the totals even when the caller may not read
        // them. `SumAsync` over an empty set is 0, which is the honest answer for a contract nobody is
        // enrolled under; null is reserved for "you may not see this".
        var committed = await limits.SumAsync(l => (decimal?)l.LimitValue, ct) ?? 0m;
        var consumed = await limits.SumAsync(l => (decimal?)l.ConsumedValue, ct) ?? 0m;

        decimal? percentOfCap = policy.MaxMembers is > 0
            ? Math.Round((decimal)activeMemberCount / policy.MaxMembers.Value * 100m, 1, MidpointRounding.AwayFromZero)
            : null;

        return new PolicyBookView(memberCount, activeMemberCount, planCount,
            mayReadAmounts ? committed : null, mayReadAmounts ? consumed : null, percentOfCap);
    }

    // ---- update ------------------------------------------------------------------------------------------

    private static void MapUpdate(RouteGroupBuilder v1)
    {
        v1.MapPut("/policies/{id:guid}", async (
            Guid id, UpdatePolicy req, PolicyDbContext db, PolicyGate gate, IPayerDirectory payers,
            IAuditClient audit, IOutbox outbox, IBusinessCalendar calendar, TimeProvider clock,
            CancellationToken ct) =>
        {
            ArgumentNullException.ThrowIfNull(req);
            if (await gate.CheckAsync(PolicyPolicies.Write, ct) is { } denied) return denied;
            var principal = gate.Principal!;

            var policy = await db.Policies.FirstOrDefaultAsync(p => p.PolicyId == id && !p.IsDeleted, ct);
            if (policy is null) return NotFound();
            if (await RefusedByPayerScope(policy, principal, payers, audit, ct) is { } refused) return refused;

            if (req.EffectiveTo is { } to && to < req.EffectiveFrom)
                return ProblemResults.Invalid("BAD_WINDOW", "effectiveTo must not precede effectiveFrom.");
            if (req.MaxMembers is <= 0)
                return ProblemResults.Invalid("BAD_CAP",
                    "A cap of zero is not 'uncapped', it is 'closed to enrolment'. Leave it empty for no cap.");
            if (req.PayerId is { } payerId && !await db.Payers.AnyAsync(p => p.PayerId == payerId && !p.IsDeleted, ct))
                return ProblemResults.Invalid("UNKNOWN_PAYER", $"Payer {payerId} does not exist.");

            // A cap BELOW the people already enrolled is refused rather than stored. Stored, it would put the
            // contract permanently over its own ceiling, and every enrolment check that reads the cap would
            // refuse for a reason nobody can act on — the members are already there.
            var enrolled = await db.Enrollments.CountAsync(
                e => e.PolicyId == id && !e.IsDeleted && e.Status == EnrollmentStatus.Active, ct);
            if (req.MaxMembers is { } cap && cap < enrolled)
                return ProblemResults.Conflict("CAP_BELOW_ENROLMENT",
                    $"{enrolled} members are already active on this policy, so the cap cannot be set to {cap}. " +
                    "Terminate members first, or set the cap at or above what is already enrolled.");

            var before = Signature(policy);
            policy.EffectiveFrom = req.EffectiveFrom;
            policy.EffectiveTo = req.EffectiveTo;
            policy.MaxMembers = req.MaxMembers;
            if (req.PayerId is { } newPayer) policy.PayerId = newPayer;
            policy.Notes = Blank(req.Notes);
            Stamp(policy, gate, clock);

            await using var tx = await db.Database.BeginTransactionAsync(ct);
            if (await SaveOrConflict(db, ct) is { } conflict) return conflict;

            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "policy", EntityId = policy.PolicyId.ToString(), Action = AuditAction.Update,
                ActorUserId = gate.Subject, DecisionOutcome = "policy-updated", DecisionReasonCode = before,
            }, ct);
            await outbox.EnqueueAsync("PolicyAmended", "policy.events", new
            {
                tenantId = policy.TenantId, policyId = policy.PolicyId, policy.PolicyNo,
                payerId = policy.PayerId, effectiveFrom = policy.EffectiveFrom,
                effectiveTo = policy.EffectiveTo, maxMembers = policy.MaxMembers,
            }, ct);
            await tx.CommitAsync(ct);

            var today = calendar.DateOf(DateTimeOffset.UtcNow);
            return Results.Ok(PolicyAdminView.From(policy, today,
                AdministrativeProjection.MayReadContract(principal.Roles)));
        })
        .Produces<PolicyAdminView>();
    }

    // ---- suspend / resume / expire -----------------------------------------------------------------------

    private static void MapStatus(RouteGroupBuilder v1)
    {
        Map(v1, "suspend", PolicyStatus.Suspended);
        Map(v1, "resume", PolicyStatus.Active);
        Map(v1, "expire", PolicyStatus.Expired);

        static void Map(RouteGroupBuilder v1, string verb, PolicyStatus target) =>
            v1.MapPost($"/policies/{{id:guid}}/{verb}", async (
                Guid id, ChangePolicyStatus req, PolicyDbContext db, PolicyGate gate, IPayerDirectory payers,
                IAuditClient audit, IOutbox outbox, IBusinessCalendar calendar, TimeProvider clock,
                CancellationToken ct) =>
                await SetStatusAsync(id, req, target, db, gate, payers, audit, outbox, calendar, clock, ct))
            .Produces<PolicyStatusResult>();
    }

    private static async Task<IResult> SetStatusAsync(
        Guid id, ChangePolicyStatus req, PolicyStatus target, PolicyDbContext db, PolicyGate gate,
        IPayerDirectory payers, IAuditClient audit, IOutbox outbox, IBusinessCalendar calendar,
        TimeProvider clock, CancellationToken ct)
    {
        if (await gate.CheckAsync(PolicyPolicies.Write, ct) is { } denied) return denied;
        var principal = gate.Principal!;

        var reason = req?.Reason?.Trim();
        if (string.IsNullOrWhiteSpace(reason) || reason.Length < 10)
            return ProblemResults.Invalid("POLICY_STATUS_REASON_REQUIRED",
                "Say why, in a sentence somebody reading this record next year would understand.");

        var policy = await db.Policies.FirstOrDefaultAsync(p => p.PolicyId == id && !p.IsDeleted, ct);
        if (policy is null) return NotFound();
        if (await RefusedByPayerScope(policy, principal, payers, audit, ct) is { } refused) return refused;

        if (policy.Status == target)
            return ProblemResults.Conflict("POLICY_ALREADY_IN_STATUS", $"This policy is already {target}.");

        // Expired is where a contract ends, not a state it passes through. Resuming one would silently
        // re-open cover for everyone it ended, so the way back is a RENEWAL — a new contract, linked to this
        // one, that somebody deliberately issues.
        if (policy.Status == PolicyStatus.Expired)
            return ProblemResults.Conflict("POLICY_EXPIRED",
                "An expired policy is not resumed. Renew it — that issues a successor contract linked to " +
                "this one, which is what re-opening cover actually is.");

        var affected = await db.Enrollments.CountAsync(
            e => e.PolicyId == id && !e.IsDeleted && e.Status == EnrollmentStatus.Active, ct);

        var before = Signature(policy);
        policy.Status = target;
        policy.StatusReason = reason;
        policy.StatusChangedAt = clock.GetUtcNow();
        policy.StatusChangedBy = gate.SubjectId;
        Stamp(policy, gate, clock);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (await SaveOrConflict(db, ct) is { } conflict) return conflict;

        await audit.EmitAsync(new AuditEventDraft
        {
            EntityType = "policy", EntityId = policy.PolicyId.ToString(), Action = AuditAction.StateChange,
            ActorUserId = gate.Subject, DecisionOutcome = target.ToString().ToLowerInvariant(),
            // The blast radius on the audit event, not only in the response: "who suspended this, and how
            // many people did it reach" is one question, and splitting it across two stores makes it two.
            DecisionReasonCode = $"{before};active-members:{affected}",
        }, ct);
        await outbox.EnqueueAsync($"Policy{target}", "policy.events", new
        {
            tenantId = policy.TenantId, policyId = policy.PolicyId, policy.PolicyNo,
            status = target.ToString(), reason, activeMembers = affected,
        }, ct);
        await tx.CommitAsync(ct);

        var today = calendar.DateOf(DateTimeOffset.UtcNow);
        return Results.Ok(new PolicyStatusResult(
            PolicyAdminView.From(policy, today, AdministrativeProjection.MayReadContract(principal.Roles)),
            affected));
    }

    // ---- history -----------------------------------------------------------------------------------------

    private static void MapHistory(RouteGroupBuilder v1)
    {
        v1.MapGet("/policies/{id:guid}/history", async (
            Guid id, PolicyDbContext db, PolicyGate gate, IPayerDirectory payers, IAuditClient audit,
            CancellationToken ct) =>
        {
            if (await gate.CheckAsync(PolicyPolicies.Write, ct) is { } denied) return denied;
            var principal = gate.Principal!;

            var policy = await db.Policies.AsNoTracking().FirstOrDefaultAsync(p => p.PolicyId == id, ct);
            if (policy is null) return NotFound();
            if (await RefusedByPayerScope(policy, principal, payers, audit, ct) is { } refused) return refused;

            var mayContract = AdministrativeProjection.MayReadContract(principal.Roles);
            var rows = await db.PolicyHistory.AsNoTracking()
                .Where(h => h.PolicyId == id)
                .OrderByDescending(h => h.HistoryId)
                .Take(200)
                .ToListAsync(ct);

            return Results.Ok(new PolicyHistoryPage(id,
                rows.ConvertAll(r => PolicyHistoryEntryView.From(r, mayContract))));
        })
        .Produces<PolicyHistoryPage>();
    }

    // ---- shared ------------------------------------------------------------------------------------------

    /// <summary>
    /// The 19.5 payer restriction, applied to the contract itself.
    ///
    /// <para><c>policy-query</c> narrows the LIST by payer and refuses a named one outside the set. Every
    /// route here addresses a single policy by id, so the same rule has to be applied per row or the
    /// restriction protects the register and not the records in it. 403 rather than 404, for the reason
    /// <see cref="QueryEndpoints"/> gives: an empty answer reads as "no such policy".</para>
    ///
    /// <para>A policy with no payer is readable only by an unrestricted caller — a row that might belong to
    /// any payer is not one payer's book of business (<c>PermittedPayers.AllowsUnattributed</c>).</para>
    /// </summary>
    private static async Task<IResult?> RefusedByPayerScope(
        Domain.Policy policy, HbmpPrincipal principal, IPayerDirectory payers, IAuditClient audit,
        CancellationToken ct)
    {
        var permitted = await payers.GetAsync(principal, ct);
        var allowed = policy.PayerId is { } id ? permitted.Allows(id) : permitted.AllowsUnattributed;
        if (allowed) return null;

        await audit.EmitAsync(new AuditEventDraft
        {
            EntityType = "policy", EntityId = policy.PolicyId.ToString(), Action = AuditAction.Grant,
            ActorUserId = principal.Subject, ActorRole = string.Join(',', principal.Roles),
            TenantId = principal.TenantId,
            DecisionOutcome = "PayerScopeDenied", DecisionReasonCode = "payer-not-permitted",
        }, ct);
        return GateResults.Forbidden("urn:hbmp:payer-scope-denied",
            detail: "You are not permitted to read this policy.", reason: "payer-not-permitted");
    }

    private static void Stamp(Domain.Policy p, PolicyGate gate, TimeProvider clock)
    {
        p.UpdatedAt = clock.GetUtcNow();
        p.UpdatedBy = gate.SubjectId;
        p.UpdatedByName = gate.Principal?.DisplayName;
    }

    private static string Signature(Domain.Policy p) =>
        $"{p.Status}|{p.EffectiveFrom:yyyy-MM-dd}..{p.EffectiveTo:yyyy-MM-dd}|cap:{p.MaxMembers?.ToString() ?? "-"}";

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static IResult NotFound() =>
        Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");

    private static async Task<IResult?> SaveOrConflict(PolicyDbContext db, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return null;
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            return ProblemResults.Conflict("DUPLICATE_KEY", "A policy with this number already exists.");
        }
        catch (DbUpdateConcurrencyException)
        {
            return ProblemResults.Conflict("POLICY_CHANGED",
                "Somebody else changed this policy while you were editing it. Reload and reapply your change.");
        }
    }
}
