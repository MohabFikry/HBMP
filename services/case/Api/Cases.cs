using Mersal.Audit.Client;
using Mersal.Auth.Authorization;
using Mersal.Authz;
using Mersal.Case.Domain;
using Mersal.Case.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Case.Api;

/// <summary>Phase 10.1 — case CRUD, My-Cases worklist, assignment (the ABAC anchor), coordination tasks, and
/// escalations. Every case action is assignment-scoped through <see cref="CaseGate"/> (case-assignment ABAC) and
/// audited; assign/unassign is supervisory (Manager / Medical Director). Publishes CaseOpened / CaseAssigned /
/// CaseEscalated / TaskCompleted via the outbox.</summary>
public static class Cases
{
    public static void MapCases(this IEndpointRouteBuilder app)
    {
        var v1 = app.MapGroup("/api/v1/cases");

        // --- Open a new case (intake / supervisory) ---------------------------------------------------------
        v1.MapPost("", async (OpenCaseRequest req, CaseDeps deps, CancellationToken ct) =>
        {
            var denied = await deps.Gate.CheckAsync(CasePolicies.Open, null, "open-case", ct);
            if (denied is not null) return denied;
            if (req.BeneficiaryId == Guid.Empty) return Unprocessable("beneficiary-required", "A beneficiary id is required.");

            var now = deps.Clock.GetUtcNow();
            var c = new CaseFile
            {
                CaseId = Guid.NewGuid(),
                CaseNo = await deps.CaseNo.NextAsync(now.Year, ct),
                TenantId = deps.Tenant ?? "unknown",
                BeneficiaryId = req.BeneficiaryId,
                Category = req.Category,
                Priority = req.Priority ?? CasePriority.Normal,
                Status = CaseStatus.Open,
                Summary = req.Summary,
                OpenedBy = deps.Subject,
                OpenedAt = now,
                CreatedBy = deps.Subject,
                CreatedAt = now,
                UpdatedAt = now,
            };
            await using var tx = await deps.Db.Database.BeginTransactionAsync(ct);
            deps.Db.Cases.Add(c);
            await deps.Db.SaveChangesAsync(ct);
            await deps.Outbox.EnqueueAsync("CaseOpened", "case.events",
                new { caseId = c.CaseId, c.CaseNo, tenantId = c.TenantId, beneficiaryId = c.BeneficiaryId, category = c.Category.ToString() }, ct);
            await tx.CommitAsync(ct);

            await Audit(deps, AuditAction.Create, c, "CaseOpened", null, c.Status.ToString());
            return Results.Created($"/api/v1/cases/{c.CaseId}", CaseView.From(c));
        }).RequireAuthorization(HbmpPolicies.Scope("case:write"))
        .Produces<CaseView>();

        // --- My Cases (caller's ACTIVE assignments) — cursor paged ------------------------------------------
        v1.MapGet("", async (CaseDeps deps, CancellationToken ct, string? cursor, int? pageSize, string? status) =>
        {
            var denied = await deps.Gate.CheckAsync(CasePolicies.ReadList, null, "list-my-cases", ct);
            if (denied is not null) return denied;
            if (!Guid.TryParse(deps.Subject, out var mgr)) return Results.Ok(new CaseListResponse([], null));

            var assigned = await deps.Assignments.ActiveCaseIdsForAsync(mgr, ct);
            var ids = assigned.Select(Guid.Parse).ToList();
            var take = Math.Clamp(pageSize ?? 25, 1, 100);

            var q = deps.Db.Cases.AsNoTracking().Where(c => ids.Contains(c.CaseId));
            if (TryStatus(status, out var st)) q = q.Where(c => c.Status == st);
            if (Guid.TryParse(cursor, out var after))
            {
                var afterCase = await deps.Db.Cases.AsNoTracking().FirstOrDefaultAsync(c => c.CaseId == after, ct);
                if (afterCase is not null) q = q.Where(c => c.OpenedAt < afterCase.OpenedAt);
            }

            var rows = await q.OrderByDescending(c => c.OpenedAt).Take(take + 1).ToListAsync(ct);
            var page = rows.Take(take).ToList();
            var next = rows.Count > take ? page[^1].CaseId.ToString() : null;
            return Results.Ok(new CaseListResponse(page.Select(CaseListItem.From).ToList(), next));
        })
        .RequireAuthorization(HbmpPolicies.Scope("case:read"))
        .Produces<CaseListResponse>();

        // --- Read one case ----------------------------------------------------------------------------------
        v1.MapGet("/{id:guid}", async (Guid id, CaseDeps deps, CancellationToken ct) =>
        {
            var denied = await deps.Gate.CheckAsync(CasePolicies.Read, id, "read-case", ct);
            if (denied is not null) return denied;
            var c = await deps.Db.Cases.AsNoTracking().Include(x => x.Assignments)
                .FirstOrDefaultAsync(x => x.CaseId == id, ct);
            return c is null ? Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found") : Results.Ok(CaseView.From(c));
        }).RequireAuthorization(HbmpPolicies.Scope("case:read"))
        .Produces<CaseView>();

        // --- Update case status -----------------------------------------------------------------------------
        v1.MapPatch("/{id:guid}/status", async (Guid id, UpdateStatusRequest req, CaseDeps deps, CancellationToken ct) =>
        {
            var denied = await deps.Gate.CheckAsync(CasePolicies.Write, id, "update-case", ct);
            if (denied is not null) return denied;
            var c = await deps.Db.Cases.FirstOrDefaultAsync(x => x.CaseId == id, ct);
            if (c is null) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");
            if (!CaseWorkflow.CanTransition(c.Status, req.Status))
                return Conflict($"No legal case transition {c.Status} → {req.Status}.");

            var before = c.Status;
            c.Status = req.Status;
            c.UpdatedAt = deps.Clock.GetUtcNow();
            try { await deps.Db.SaveChangesAsync(ct); }
            catch (DbUpdateConcurrencyException) { return Conflict("This case was updated by someone else."); }
            await Audit(deps, AuditAction.StateChange, c, "CaseStatusChanged", before.ToString(), c.Status.ToString());
            return Results.Ok(CaseView.From(c));
        }).RequireAuthorization(HbmpPolicies.Scope("case:write"))
        .Produces<CaseView>();

        // --- Assign / unassign (supervisory — the ABAC access anchor) ---------------------------------------
        v1.MapPost("/{id:guid}/assign", async (Guid id, AssignRequest req, CaseDeps deps, CancellationToken ct) =>
        {
            var denied = await deps.Gate.CheckAsync(CasePolicies.Manage, id, "assign", ct);
            if (denied is not null) return denied;
            var c = await deps.Db.Cases.FirstOrDefaultAsync(x => x.CaseId == id, ct);
            if (c is null) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");

            var already = await deps.Db.Assignments
                .FirstOrDefaultAsync(a => a.CaseId == id && a.CaseManagerId == req.CaseManagerId && a.Active, ct);
            if (already is not null) return Results.Ok(AssignmentView.From(already));

            var now = deps.Clock.GetUtcNow();
            var a = new CaseAssignment
            {
                AssignmentId = Guid.NewGuid(), CaseId = id, CaseManagerId = req.CaseManagerId,
                AssignedAt = now, Active = true, AssignedBy = deps.Subject,
            };
            // An assignment IS the ABAC anchor — it is what grants this manager access to the case. The grant
            // and the event announcing it commit together, so no watcher is told about access that was rolled
            // back, and no grant lands silently.
            await using var tx = await deps.Db.Database.BeginTransactionAsync(ct);
            deps.Db.Assignments.Add(a);
            await deps.Db.SaveChangesAsync(ct);
            await deps.Outbox.EnqueueAsync("CaseAssigned", "case.events",
                new { caseId = id, c.CaseNo, caseManagerId = req.CaseManagerId }, ct);
            await tx.CommitAsync(ct);
            await Audit(deps, AuditAction.Grant, c, "CaseAssigned", null, req.CaseManagerId.ToString(), AuditSeverity.Notice);
            return Results.Ok(AssignmentView.From(a));
        }).RequireAuthorization(HbmpPolicies.Scope("case:manage"))
        .Produces<AssignmentView>();

        // Unassignment REVOKES access (10 §3.11): set active=false + unassigned_at. The next authz check finds no
        // active assignment → 403 for that manager.
        v1.MapPost("/{id:guid}/unassign", async (Guid id, UnassignRequest req, CaseDeps deps, CancellationToken ct) =>
        {
            var denied = await deps.Gate.CheckAsync(CasePolicies.Manage, id, "unassign", ct);
            if (denied is not null) return denied;
            var a = await deps.Db.Assignments
                .FirstOrDefaultAsync(x => x.CaseId == id && x.CaseManagerId == req.CaseManagerId && x.Active, ct);
            if (a is null) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");

            var now = deps.Clock.GetUtcNow();
            a.Active = false;
            a.UnassignedAt = now;
            a.UnassignedBy = deps.Subject;
            // Unassignment REVOKES access. A revocation that commits without its event leaves every downstream
            // read model still showing this manager on the case.
            await using var tx = await deps.Db.Database.BeginTransactionAsync(ct);
            await deps.Db.SaveChangesAsync(ct);
            await deps.Outbox.EnqueueAsync("CaseUnassigned", "case.events",
                new { caseId = id, caseManagerId = req.CaseManagerId }, ct);
            await tx.CommitAsync(ct);
            await deps.Audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "case", EntityId = id.ToString(), Action = AuditAction.Update,
                ActorUserId = deps.Subject, ActorRole = deps.Roles, TenantId = deps.Tenant,
                DecisionOutcome = "CaseUnassigned", AfterState = $"revoked:{req.CaseManagerId}", Severity = AuditSeverity.Notice,
            }, ct);
            return Results.NoContent();
        }).RequireAuthorization(HbmpPolicies.Scope("case:manage"));

        MapTasks(v1);
        MapEscalations(v1);
    }

    // --- Coordination tasks (kanban) -----------------------------------------------------------------------
    private static void MapTasks(RouteGroupBuilder v1)
    {
        v1.MapGet("/{id:guid}/tasks", async (Guid id, CaseDeps deps, CancellationToken ct) =>
        {
            var denied = await deps.Gate.CheckAsync(CasePolicies.Read, id, "list-tasks", ct);
            if (denied is not null) return denied;
            var rows = await deps.Db.Tasks.AsNoTracking().Where(t => t.CaseId == id)
                .OrderBy(t => t.DueAt ?? DateTimeOffset.MaxValue).ToListAsync(ct);
            return Results.Ok(rows.Select(TaskView.From).ToList());
        }).RequireAuthorization(HbmpPolicies.Scope("case:read"));

        v1.MapPost("/{id:guid}/tasks", async (Guid id, CreateTaskRequest req, CaseDeps deps, CancellationToken ct) =>
        {
            var denied = await deps.Gate.CheckAsync(CasePolicies.Write, id, "create-task", ct);
            if (denied is not null) return denied;
            if (string.IsNullOrWhiteSpace(req.Title)) return Unprocessable("title-required", "A task title is required.");
            if (!await deps.Db.Cases.AnyAsync(c => c.CaseId == id, ct)) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");

            var now = deps.Clock.GetUtcNow();
            var t = new CoordinationTask
            {
                TaskId = Guid.NewGuid(), CaseId = id, Title = req.Title.Trim(), Description = req.Description,
                AssigneeId = req.AssigneeId, DueAt = req.DueAt, Status = TaskState.Todo,
                CreatedBy = deps.Subject, CreatedAt = now, UpdatedAt = now,
            };
            deps.Db.Tasks.Add(t);
            await deps.Db.SaveChangesAsync(ct);
            return Results.Created($"/api/v1/cases/{id}/tasks/{t.TaskId}", TaskView.From(t));
        }).RequireAuthorization(HbmpPolicies.Scope("case:write"))
        .Produces<TaskView>();

        v1.MapPatch("/{id:guid}/tasks/{taskId:guid}", async (Guid id, Guid taskId, UpdateTaskRequest req, CaseDeps deps, CancellationToken ct) =>
        {
            var denied = await deps.Gate.CheckAsync(CasePolicies.Write, id, "update-task", ct);
            if (denied is not null) return denied;
            var t = await deps.Db.Tasks.FirstOrDefaultAsync(x => x.TaskId == taskId && x.CaseId == id, ct);
            if (t is null) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");

            if (req.Status is { } to)
            {
                if (!CaseWorkflow.CanTransition(t.Status, to))
                    return Conflict($"No legal task transition {t.Status} → {to}.");
                t.Status = to;
            }
            if (req.OutcomeNote is not null) t.OutcomeNote = req.OutcomeNote;
            if (req.DueAt is not null) t.DueAt = req.DueAt;
            if (req.AssigneeId is not null) t.AssigneeId = req.AssigneeId;
            t.UpdatedAt = deps.Clock.GetUtcNow();
            await using var tx = await deps.Db.Database.BeginTransactionAsync(ct);
            await deps.Db.SaveChangesAsync(ct);

            if (req.Status == TaskState.Done)
                await deps.Outbox.EnqueueAsync("TaskCompleted", "case.events",
                    new { caseId = id, taskId = t.TaskId, title = t.Title }, ct);
            await tx.CommitAsync(ct);
            return Results.Ok(TaskView.From(t));
        }).RequireAuthorization(HbmpPolicies.Scope("case:write"))
        .Produces<TaskView>();
    }

    // --- Escalations ---------------------------------------------------------------------------------------
    private static void MapEscalations(RouteGroupBuilder v1)
    {
        // Cross-case escalations worklist — every open escalation on the caller's ACTIVE assignments (the same
        // ABAC anchor as My Cases: a manager sees escalations only for cases assigned to them). Literal segment,
        // so it never collides with the `/{id:guid}/escalations` per-case route.
        v1.MapGet("/escalations", async (CaseDeps deps, CancellationToken ct, string? status) =>
        {
            var denied = await deps.Gate.CheckAsync(CasePolicies.ReadList, null, "list-escalations", ct);
            if (denied is not null) return denied;
            if (!Guid.TryParse(deps.Subject, out var mgr)) return Results.Ok(Array.Empty<EscalationListItem>());

            var assigned = await deps.Assignments.ActiveCaseIdsForAsync(mgr, ct);
            var ids = assigned.Select(Guid.Parse).ToList();
            if (ids.Count == 0) return Results.Ok(Array.Empty<EscalationListItem>());

            var q = from e in deps.Db.Escalations.AsNoTracking()
                    join c in deps.Db.Cases.AsNoTracking() on e.CaseId equals c.CaseId
                    where ids.Contains(e.CaseId)
                    orderby e.RaisedAt descending
                    select new { e, c.CaseNo, c.BeneficiaryId };
            var rows = await q.Take(200).ToListAsync(ct);
            var items = rows
                .Where(r => !TryEscStatus(status, out var want) || r.e.Status == want)
                .Select(r => EscalationListItem.From(r.e, r.CaseNo, r.BeneficiaryId)).ToList();
            return Results.Ok(items);
        }).RequireAuthorization(HbmpPolicies.Scope("case:read"))
        .Produces<IEnumerable<EscalationListItem>>();

        v1.MapGet("/{id:guid}/escalations", async (Guid id, CaseDeps deps, CancellationToken ct) =>
        {
            var denied = await deps.Gate.CheckAsync(CasePolicies.Read, id, "list-escalations", ct);
            if (denied is not null) return denied;
            var rows = await deps.Db.Escalations.AsNoTracking().Where(e => e.CaseId == id)
                .OrderByDescending(e => e.RaisedAt).ToListAsync(ct);
            return Results.Ok(rows.Select(EscalationView.From).ToList());
        }).RequireAuthorization(HbmpPolicies.Scope("case:read"));

        v1.MapPost("/{id:guid}/escalate", async (Guid id, EscalateRequest req, CaseDeps deps, CancellationToken ct) =>
        {
            var denied = await deps.Gate.CheckAsync(CasePolicies.Write, id, "escalate", ct);
            if (denied is not null) return denied;
            if (string.IsNullOrWhiteSpace(req.RaisedToRole)) return Unprocessable("role-required", "A target role is required.");
            if (string.IsNullOrWhiteSpace(req.Reason)) return Unprocessable("reason-required", "An escalation reason is required.");
            var c = await deps.Db.Cases.FirstOrDefaultAsync(x => x.CaseId == id, ct);
            if (c is null) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");

            var now = deps.Clock.GetUtcNow();
            var e = new Escalation
            {
                EscalationId = Guid.NewGuid(), CaseId = id, RaisedBy = deps.Subject,
                RaisedToRole = req.RaisedToRole.Trim(), Reason = req.Reason.Trim(),
                Status = EscalationStatus.Raised, RaisedAt = now,
            };
            // The escalation row and the case's move out of OnHold are one change, and CaseEscalated is how the
            // target role finds out it is owed a look. All three commit together.
            await using var tx = await deps.Db.Database.BeginTransactionAsync(ct);
            deps.Db.Escalations.Add(e);
            // An escalation moves the case to the Escalation lane if it isn't terminal.
            if (CaseWorkflow.CanTransition(c.Status, CaseStatus.Active) && c.Status == CaseStatus.OnHold)
                c.Status = CaseStatus.Active;
            await deps.Db.SaveChangesAsync(ct);
            await deps.Outbox.EnqueueAsync("CaseEscalated", "case.events",
                new { caseId = id, escalationId = e.EscalationId, raisedToRole = e.RaisedToRole, reason = e.Reason }, ct);
            await tx.CommitAsync(ct);
            await deps.Audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "case", EntityId = id.ToString(), Action = AuditAction.Create,
                ActorUserId = deps.Subject, ActorRole = deps.Roles, TenantId = deps.Tenant,
                DecisionOutcome = "CaseEscalated", AfterState = e.RaisedToRole, Severity = AuditSeverity.Notice,
            }, ct);
            return Results.Created($"/api/v1/cases/{id}/escalations/{e.EscalationId}", EscalationView.From(e));
        }).RequireAuthorization(HbmpPolicies.Scope("case:write"))
        .Produces<EscalationView>();

        v1.MapPatch("/{id:guid}/escalations/{escId:guid}", async (Guid id, Guid escId, EscalationUpdateRequest req, CaseDeps deps, CancellationToken ct) =>
        {
            var denied = await deps.Gate.CheckAsync(CasePolicies.Write, id, "update-escalation", ct);
            if (denied is not null) return denied;
            var e = await deps.Db.Escalations.FirstOrDefaultAsync(x => x.EscalationId == escId && x.CaseId == id, ct);
            if (e is null) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");
            if (!CaseWorkflow.CanTransition(e.Status, req.Status))
                return Conflict($"No legal escalation transition {e.Status} → {req.Status}.");

            var now = deps.Clock.GetUtcNow();
            e.Status = req.Status;
            if (req.Status == EscalationStatus.Acknowledged) e.AcknowledgedAt = now;
            if (req.Status == EscalationStatus.Resolved) { e.ResolvedAt = now; e.ResolutionNote = req.ResolutionNote; }
            await deps.Db.SaveChangesAsync(ct);
            return Results.Ok(EscalationView.From(e));
        }).RequireAuthorization(HbmpPolicies.Scope("case:write"))
        .Produces<EscalationView>();
    }

    private static async Task Audit(CaseDeps deps, AuditAction action, CaseFile c, string outcome,
        string? before, string? after, AuditSeverity severity = AuditSeverity.Info) =>
        await deps.Audit.EmitAsync(new AuditEventDraft
        {
            EntityType = "case", EntityId = c.CaseId.ToString(), Action = action,
            ActorUserId = deps.Subject, ActorRole = deps.Roles, TenantId = deps.Tenant,
            BeforeState = before, AfterState = after, DecisionOutcome = outcome, Severity = severity,
        });

    private static bool TryStatus(string? status, out CaseStatus parsed)
    {
        parsed = default;
        return !string.IsNullOrWhiteSpace(status) && Enum.TryParse(status, true, out parsed);
    }

    private static bool TryEscStatus(string? status, out EscalationStatus parsed)
    {
        parsed = default;
        return !string.IsNullOrWhiteSpace(status) && Enum.TryParse(status, true, out parsed);
    }

    private static IResult Unprocessable(string title, string detail) =>
        Results.Problem(statusCode: 422, title: title, detail: detail, type: "urn:hbmp:validation");

    private static IResult Conflict(string detail) =>
        Results.Problem(statusCode: 409, title: "conflict", detail: detail, type: "urn:hbmp:conflict");
}
