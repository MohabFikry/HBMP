using Mersal.Audit.Client;
using Mersal.Auth.Authorization;
using Mersal.Authz;
using Mersal.CallCentre.Domain;
using Microsoft.EntityFrameworkCore;

namespace Mersal.CallCentre.Api;

/// <summary>
/// Phase 20.3b — the call-history read that the patient profile's <c>callHistory</c> section consumes, plus the
/// server-generated clipboard block.
///
/// <para><b>The rows already existed.</b> This is not a second call log: it is a SECOND, NARROWER PROJECTION of
/// the same <c>call_interaction</c> rows the call-centre workspace has always shown. The workspace keeps its
/// agent/supervisor scoping; this endpoint answers a different question ("what calls has this member had") for a
/// different audience, at the level design 39 §5b says that audience may have.</para>
/// </summary>
public static class CallHistory
{
    public static void MapCallHistory(this IEndpointRouteBuilder app)
    {
        var v1 = app.MapGroup("/api/v1/beneficiaries");

        // ---- The member's call history, projected to the caller's level ------------------------------------
        v1.MapGet("/{beneficiaryId:guid}/call-interactions", async (
            Guid beneficiaryId, string? level, int? page, int? pageSize, string? lang,
            CallDeps deps, CancellationToken ct) =>
        {
            var denied = await deps.Gate.CheckAsync(ProfilePolicies.CallHistoryRead, "call-history", ct);
            if (denied is not null) return denied;

            var effective = ResolveLevel(deps, level);
            if (effective == CallHistoryLevel.None)
                return Results.Ok(new CallHistoryResponse(effective.ToString(), [], null));

            var take = Math.Clamp(pageSize ?? 50, 1, 200);
            var skip = Math.Max(0, (page ?? 1) - 1) * take;

            var interactions = await deps.Db.Interactions.AsNoTracking()
                .Where(x => x.TenantId == deps.Tenant && x.BeneficiaryId == beneficiaryId)
                .OrderByDescending(x => x.StartedAt)
                .Skip(skip).Take(take + 1)
                .ToListAsync(ct);

            var hasMore = interactions.Count > take;
            if (hasMore) interactions.RemoveAt(interactions.Count - 1);

            var rows = await ProjectAsync(deps, interactions, beneficiaryId, effective, lang, ct);

            // A PHI read of the member's contact history — audited whatever the level, because "who read this
            // member's call log" is a question an access review asks regardless of how much of it they saw.
            await deps.AuditAsync("call_history", beneficiaryId.ToString(), AuditAction.Read,
                $"CallHistoryRead:{effective}", callRef: null, severity: AuditSeverity.Notice,
                after: $"rows:{rows.Count}", fieldClasses: ["contact", "operational"]);

            return Results.Ok(new CallHistoryResponse(
                effective.ToString(), rows, hasMore ? (skip + take + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) : null));
        }).RequireAuthorization(HbmpPolicies.Scope("callcentre:history:read"));

        // ---- "Copy all visible" ----------------------------------------------------------------------------
        // A copy is logged like an EXPORT, not like a read: moving PHI to the clipboard is the moment it leaves
        // the platform's control, and that is exactly the moment worth recording (design 39 §5b rule 2).
        v1.MapPost("/{beneficiaryId:guid}/call-interactions/copy", async (
            Guid beneficiaryId, CopyCallSummariesRequest req, string? lang,
            CallDeps deps, CancellationToken ct) =>
        {
            var denied = await deps.Gate.CheckAsync(ProfilePolicies.CallHistoryRead, "call-history-copy", ct);
            if (denied is not null) return denied;
            if (req.CallRefs is not { Count: > 0 })
                return Results.Problem(statusCode: 422, title: "call-refs-required",
                    detail: "At least one call reference is required.", type: "urn:hbmp:call-refs-required");

            var effective = ResolveLevel(deps, req.Level);
            if (effective == CallHistoryLevel.None)
                return Results.Problem(statusCode: 403, title: "no-call-history",
                    detail: "Your role does not receive this member's call history.",
                    type: "urn:hbmp:callcentre-history-denied");

            var refs = req.CallRefs.Distinct(StringComparer.Ordinal).Take(200).ToList();
            var interactions = await deps.Db.Interactions.AsNoTracking()
                .Where(x => x.TenantId == deps.Tenant && x.BeneficiaryId == beneficiaryId && refs.Contains(x.CallRef))
                .OrderByDescending(x => x.StartedAt)
                .ToListAsync(ct);

            // The SAME projection the read endpoint serves. Not a parallel formatter — one code path, so a level
            // that drops the summary drops it here too, by construction rather than by remembering to.
            var rows = await ProjectAsync(deps, interactions, beneficiaryId, effective, lang, ct);

            await deps.AuditAsync("call_summary_copy", beneficiaryId.ToString(), AuditAction.Export,
                "CallSummaryCopied", callRef: string.Join(',', rows.Select(r => r.CallRef)),
                severity: AuditSeverity.High, after: $"level:{effective};count:{rows.Count}",
                fieldClasses: ["contact", "operational"]);

            return Results.Ok(new CopyCallSummariesResponse(
                effective.ToString(), [.. rows.Select(r => r.CallRef)], CallHistoryProjection.CopyAll(rows)));
        }).RequireAuthorization(HbmpPolicies.Scope("callcentre:history:read"));

        // ---- The phase-15 verification fact, for profile-service's gate -------------------------------------
        // profile-service CONSUMES this gate rather than re-implementing it (design 39 §4). Exposing the fact —
        // not the verification detail — is what lets it do that without duplicating the rule.
        app.MapGet("/api/v1/call-interactions/{interactionId:guid}/verification", async (
            Guid interactionId, Guid beneficiaryId, CallDeps deps, CancellationToken ct) =>
        {
            var denied = await deps.Gate.CheckAsync(CallCentrePolicies.Read, "verification-state", ct);
            if (denied is not null) return denied;
            var verified = await deps.Verification.IsVerifiedAsync(interactionId, beneficiaryId, ct);
            return Results.Ok(new { interactionId, beneficiaryId, verified });
        }).RequireAuthorization(HbmpPolicies.Scope("callcentre:read"));
    }

    /// <summary>
    /// Decide the level SERVER-SIDE, then clamp anything the client asked for down to it.
    ///
    /// <para>A clamp rather than a rejection: a supervisor legitimately asks for <c>meta</c> to skim, and
    /// refusing that would be obstructive, while honouring a request to widen would be the bug. So the client's
    /// value may narrow and can never widen.</para>
    ///
    /// <para>The ceiling here is what the ROLE could maximally have — a case manager's cell is Full, and this
    /// service has no way to know whether they hold an active assignment. profile-service does know, and asks
    /// for the level ITS matrix resolved. Two clamps of the same value, and the narrower wins: the one that
    /// enforces nothing is the one that gets called by something else next year.</para>
    /// </summary>
    private static CallHistoryLevel ResolveLevel(CallDeps deps, string? requested)
    {
        var principal = deps.Me.Principal;
        if (principal is null) return CallHistoryLevel.None;

        var ceiling = ProfilePolicies.CallHistoryLevelFor(new ProfileContext
        {
            Roles = principal.Roles,
            // The MAXIMUM this role could reach. The caller-specific narrowing (does this case manager hold an
            // assignment? does this doctor treat this patient?) is applied by the caller that knows.
            TreatingRelationship = true,
            CaseAssignment = true,
        });

        if (string.IsNullOrWhiteSpace(requested)) return ceiling;
        return Enum.TryParse<CallHistoryLevel>(requested, ignoreCase: true, out var asked)
            ? ProfilePolicies.Clamp(asked, ceiling)
            : ceiling;
    }

    private static async Task<List<ProjectedCallRow>> ProjectAsync(
        CallDeps deps, List<CallInteraction> interactions, Guid beneficiaryId,
        CallHistoryLevel level, string? lang, CancellationToken ct)
    {
        if (interactions.Count == 0) return [];
        var ids = interactions.ConvertAll(i => i.InteractionId);

        // Verification detail is fetched ONLY when the level can show it. Reading rows the projection would
        // discard is how a "we filter it out later" implementation ends up logging what it never showed.
        var verifications = level >= CallHistoryLevel.Full
            ? await deps.Db.Verifications.AsNoTracking()
                .Where(v => ids.Contains(v.InteractionId) && v.BeneficiaryId == beneficiaryId)
                .OrderByDescending(v => v.VerifiedAt)
                .ToListAsync(ct)
            : [];

        var links = level >= CallHistoryLevel.Operational
            ? await deps.Db.AppointmentLinks.AsNoTracking()
                .Where(l => ids.Contains(l.InteractionId))
                .ToListAsync(ct)
            : [];

        var memberRef = interactions[0].BeneficiaryId?.ToString();

        return interactions.ConvertAll(i => CallHistoryProjection.Project(
            new CallRowSource(
                i,
                memberRef,
                // The agent's display name is only assembled at Full — the identity of who handled a call is
                // operational detail an approver does not need to coordinate care.
                level >= CallHistoryLevel.Full ? i.AgentUserId.ToString() : null,
                links.FirstOrDefault(l => l.InteractionId == i.InteractionId)?.BranchId,
                verifications.FirstOrDefault(v => v.InteractionId == i.InteractionId),
                [.. links.Where(l => l.InteractionId == i.InteractionId)
                    .Select(l => new LinkedArtifactView("Appointment", l.AppointmentId.ToString(), l.Action.ToString()))]),
            level,
            lang ?? CallHistoryProjection.English));
    }
}

/// <summary>A page of projected call-history rows. <c>Level</c> is echoed so a client can render the honest
/// caption ("summary not available at your access level") rather than an empty column.</summary>
public sealed record CallHistoryResponse(string Level, IReadOnlyList<ProjectedCallRow> Items, string? NextCursor);

/// <summary>"Copy all visible". <c>Level</c> may only narrow what the server already decided.</summary>
public sealed record CopyCallSummariesRequest(IReadOnlyList<string> CallRefs, string? Level);

/// <summary>The joined clipboard block, generated from the served projection.</summary>
public sealed record CopyCallSummariesResponse(string Level, IReadOnlyList<string> CallRefs, string CopyText);
