using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Auth.Authorization;
using Mersal.Authz;
using Mersal.Case.Domain;
using Mersal.Case.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Mersal.Time;

namespace Mersal.Case.Api;

/// <summary>
/// Phase 20 — the seam the patient profile's <c>caseManagement</c> section reads, and the source of the
/// <c>assigned</c> fact profile-service's fact resolver consults.
///
/// <para><b>Two jobs, deliberately in one endpoint.</b> "Does this case manager hold an active assignment
/// covering this beneficiary" is a case-service fact, and it is also the ABAC input the profile's matrix needs
/// to decide half a dozen OTHER sections. Answering it here — beside the rows it is derived from — is what
/// stops the profile from inventing a second, drifting answer. Unassignment empties the set and the access is
/// revoked in the same breath (design 39 §4 cross-cutting, 10 §3.11).</para>
///
/// <para><c>assigned</c> is returned even when it is <c>false</c>, and the section is then empty rather than
/// 403: an unassigned case manager is entitled to know the beneficiary HAS cases (their profile shows the
/// section Restricted), just not to read them.</para>
/// </summary>
public static class ProfileCasesEndpoint
{
    public static void MapProfileCases(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/cases/for-beneficiary/{beneficiaryId:guid}", async (
            Guid beneficiaryId, CaseDbContext db, AssignmentResolver assignments,
            IAuditClient audit, IHbmpPrincipalAccessor me, CancellationToken ct) =>
        {
            var principal = me.Principal;
            if (principal is null) return GateResults.Unauthenticated();

            var cases = await db.Cases.AsNoTracking()
                .Where(c => c.BeneficiaryId == beneficiaryId && !c.Deleted && c.TenantId == principal.TenantId)
                .OrderByDescending(c => c.OpenedAt)
                .ToListAsync(ct);

            // The ASSIGNMENT fact, resolved from the assignment rows themselves.
            var assigned = false;
            if (Guid.TryParse(principal.Subject, out var manager) && cases.Count > 0)
            {
                var active = await assignments.ActiveCaseIdsForAsync(manager, ct);
                assigned = cases.Any(c => active.Contains(c.CaseId.ToString()));
            }

            var denied = ProfileSeam.Check(
                principal, ProfileSeam.ContextFor(principal, caseAssignment: assigned),
                ProfileSections.CaseManagement);

            // A caller the matrix does not grant the section still gets the ASSIGNMENT FACT and nothing else.
            // profile-service needs it to decide the OTHER sections a case manager's row gates on, and refusing
            // to answer would make an unassigned manager's whole profile collapse to Unavailable rather than
            // Restricted — "it broke" instead of "you may not see this", which is the exact confusion design 39
            // §6 forbids.
            if (denied is not null) return Results.Ok(ProfileCasesView.NotEntitled(assigned));

            var caseIds = cases.ConvertAll(c => c.CaseId);
            var tasks = await db.Tasks.AsNoTracking()
                .Where(t => caseIds.Contains(t.CaseId))
                .OrderBy(t => t.DueAt)
                .Take(200)
                .ToListAsync(ct);
            var escalations = await db.Escalations.AsNoTracking()
                .Where(e => caseIds.Contains(e.CaseId))
                .OrderByDescending(e => e.RaisedAt)
                .Take(200)
                .ToListAsync(ct);

            var view = new ProfileCasesView(
                assigned,
                [.. cases.Select(c => new ProfileCaseView(
                    c.CaseId, c.CaseNo, c.Status.ToString(), c.Category.ToString(), c.OpenedAt))],
                [.. tasks.Select(t => new ProfileTaskView(
                    t.TaskId, t.Title, t.Status.ToString(),
                    t.DueAt is { } due ? BusinessCalendar.DateIn(due) : null))],
                // The escalation REASON is coordination content, not clinical content — an escalation says
                // "this needs a decision from the approval team", which is precisely what a coordinator and an
                // approver both need to see.
                [.. escalations.Select(e => new ProfileEscalationView(
                    e.EscalationId, e.Reason, e.Status.ToString(), e.RaisedAt))]);

            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "profile_cases", EntityId = beneficiaryId.ToString(), Action = AuditAction.Read,
                ActorUserId = principal.Subject, ActorRole = string.Join(',', principal.Roles),
                TenantId = principal.TenantId,
                Purpose = "patient-profile",
                DecisionOutcome = "ProfileCasesRead",
                DecisionReasonCode = $"cases:{view.Cases.Count};assigned:{assigned}",
                FieldClasses = ["care_plan"],
                Severity = AuditSeverity.Notice,
            }, ct);

            return Results.Ok(view);
        }).RequireAuthorization(HbmpPolicies.Scope("profile:read"));
    }
}

public sealed record ProfileCaseView(Guid CaseId, string CaseNo, string Status, string? Category, DateTimeOffset OpenedAt);
public sealed record ProfileTaskView(Guid TaskId, string Title, string Status, DateOnly? DueOn);
public sealed record ProfileEscalationView(Guid EscalationId, string Reason, string Status, DateTimeOffset RaisedAt);

/// <summary>The caseManagement section, plus the assignment fact the profile's other sections depend on.</summary>
public sealed record ProfileCasesView(
    bool Assigned,
    IReadOnlyList<ProfileCaseView> Cases,
    IReadOnlyList<ProfileTaskView> Tasks,
    IReadOnlyList<ProfileEscalationView> Escalations)
{
    /// <summary>The fact, and nothing else.</summary>
    public static ProfileCasesView NotEntitled(bool assigned) => new(assigned, [], [], []);
}
