using Mersal.Audit.Client;
using Mersal.Auth.Authorization;
using Mersal.Authz;
using Mersal.Case.Domain;
using Mersal.Case.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Case.Api;

/// <summary>Phase 10.1 — the beneficiary-360 COORDINATION view + the manual eligibility override (FR-ELG-007).
/// The 360 is assignment-scoped (case-assignment ABAC), assembled through <see cref="IBeneficiary360Assembler"/>
/// as a field-scoped, minimum-necessary DTO (diagnosis coord-visible; notes/rx/results masked), and EVERY assembly
/// writes a PHI-read audit event naming the fields returned. Fail-closed: if the coordination view cannot be
/// assembled the endpoint returns 502 rather than a partial leak.</summary>
public static class Beneficiary360Endpoint
{
    public static void MapBeneficiary360(this IEndpointRouteBuilder app)
    {
        var v1 = app.MapGroup("/api/v1/cases");

        v1.MapGet("/{id:guid}/beneficiary-360", async (
            Guid id, CaseDeps deps, IBeneficiary360Assembler assembler, HttpRequest http, CancellationToken ct) =>
        {
            // case-assignment ABAC: only an assigned Case Manager reaches the coordination view.
            var denied = await deps.Gate.CheckAsync(CasePolicies.Read360, id, "coordination", ct);
            if (denied is not null) return denied;

            var c = await deps.Db.Cases.AsNoTracking().FirstOrDefaultAsync(x => x.CaseId == id, ct);
            if (c is null) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");

            var bearer = http.Headers.Authorization.ToString();
            var view = await assembler.AssembleAsync(c, bearer, ct);
            if (view is null)
                return Results.Problem(statusCode: 502, title: "coordination-view-unavailable",
                    detail: "The beneficiary coordination view could not be assembled.", type: "urn:hbmp:upstream-unavailable");

            // PHI-read audit: actor, case, beneficiary, and the field classes returned (never the values).
            await deps.Audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "beneficiary_360", EntityId = c.BeneficiaryId.ToString(), Action = AuditAction.Read,
                ActorUserId = deps.Subject, ActorRole = deps.Roles, TenantId = deps.Tenant,
                Purpose = "coordination", FieldClasses = Beneficiary360.FieldClasses,
                AfterState = $"case:{c.CaseNo}", Severity = AuditSeverity.Notice,
            }, ct);
            return Results.Ok(view);
        }).RequireAuthorization(HbmpPolicies.Scope("case:read"));

        // FR-ELG-007 — manual eligibility override initiated by the Case Manager, mandatory reason + audit,
        // delegated to eligibility-service (the source of truth). We record intent + audit here; the actual
        // override is applied by eligibility (the delegation seam is the eligibility ingest endpoint).
        v1.MapPost("/{id:guid}/eligibility-override", async (
            Guid id, EligibilityOverrideRequest req, CaseDeps deps, CancellationToken ct) =>
        {
            var denied = await deps.Gate.CheckAsync(CasePolicies.Write, id, "eligibility-override", ct);
            if (denied is not null) return denied;
            if (string.IsNullOrWhiteSpace(req.Reason))
                return Results.Problem(statusCode: 422, title: "reason-required",
                    detail: "A reason is mandatory for a manual eligibility override (FR-ELG-007).", type: "urn:hbmp:validation");

            var c = await deps.Db.Cases.AsNoTracking().FirstOrDefaultAsync(x => x.CaseId == id, ct);
            if (c is null) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");

            await deps.Outbox.EnqueueAsync("EligibilityOverrideRequested", "case.events", new
            {
                caseId = id, beneficiaryId = c.BeneficiaryId, eligible = req.Eligible,
                reason = req.Reason.Trim(), validUntil = req.ValidUntil, requestedBy = deps.Subject,
            }, ct);
            await deps.Audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "eligibility_override", EntityId = c.BeneficiaryId.ToString(), Action = AuditAction.Decision,
                ActorUserId = deps.Subject, ActorRole = deps.Roles, TenantId = deps.Tenant,
                DecisionOutcome = req.Eligible ? "OverrideEligible" : "OverrideIneligible",
                DecisionReasonCode = req.Reason.Trim(), Purpose = "coordination", Severity = AuditSeverity.High,
            }, ct);
            return Results.Accepted($"/api/v1/cases/{id}");
        }).RequireAuthorization(HbmpPolicies.Scope("case:write"));
    }
}
