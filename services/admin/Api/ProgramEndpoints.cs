using Mersal.Admin.Domain;
using Mersal.Admin.Infrastructure;
using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Auth.Authorization;
using Mersal.Authz;
using Mersal.Events;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Admin.Api;

/// <summary>
/// 21.6 — administration of the programme-enablement gate (design 40 §4, adaptation A4).
///
/// 21.4 built the gate — the tables, the live-counted caps, the two distinct problem types. This is the
/// surface that lets a human change them, which is the part that makes the gate operable: switches nobody
/// can see or set are configuration by migration.
///
/// PLATFORM ADMINISTRATION ONLY. Enablement says which programmes an organisation has been onboarded onto,
/// so a tenant administrator must not be able to grant it to their own tenant — that would make the gate
/// self-service and therefore not a gate. Per A1 this is administrative authority and nothing more: no
/// endpoint here reaches PHI, and enabling a programme never grants a permission (§4).
///
/// Reads are open to tenant administrators, because "is the claims module on for us" is a question an org
/// admin needs answered to make sense of a refusal — and the answer is not sensitive.
/// </summary>
public static class ProgramEndpoints
{
    public static void MapPrograms(this WebApplication app)
    {
        var read = app.MapGroup("/api/v1/admin/programs").WithTags("admin-programs")
            .RequireAuthorization(HbmpPolicies.Scope("admin:read"));
        var write = read.MapGroup("").RequireAuthorization(HbmpPolicies.Scope("admin:write"));

        // The administration screen's whole payload: every known feature and every known cap, present or
        // not. Absent rows are RETURNED as disabled/unlimited rather than omitted — a screen that lists only
        // configured keys cannot be used to configure the others, and an administrator cannot tell "off"
        // from "never set up".
        read.MapGet("/{tenantId}", async (
            string tenantId, AdminGate gate, ProgramAdminService svc, CancellationToken ct) =>
        {
            var denied = await gate.CheckAsync(AdminPolicies.ReadAccess, ct);
            if (denied is not null) return denied;
            var scope = gate.BindTenant(tenantId);
            if (!scope.IsAllowed) return scope.ToProblem();

            return Results.Ok(await svc.DescribeAsync(scope.Tenant!, ct));
        });

        write.MapPut("/{tenantId}/features/{featureKey}", async (
            string tenantId, string featureKey, ProgramChangeRequest req,
            AdminGate gate, ProgramAdminService svc, CancellationToken ct) =>
        {
            var denied = await RequirePlatformAdminAsync(gate, ct);
            if (denied is not null) return denied;
            var scope = gate.BindTenant(tenantId);
            if (!scope.IsAllowed) return scope.ToProblem();

            if (!ProgramCatalog.Features.Contains(featureKey))
                return ProblemResults.Invalid("unknown-feature", $"'{featureKey}' is not a programme feature");
            // A reason is mandatory for the same purpose it is on an override: six months later, "claims was
            // switched off for this NGO" is only reviewable if someone recorded why.
            if (string.IsNullOrWhiteSpace(req.Reason))
                return ProblemResults.Unprocessable("reason-required",
                    "an enablement change without a reason cannot be reviewed later");

            var enabled = req.Enabled ?? false;
            await svc.SetFeatureAsync(AdminContracts.Actor(gate.Principal!), scope.Tenant!, featureKey, enabled, req.Reason, ct);
            return Results.Ok(new { tenant = scope.Tenant, feature = featureKey, enabled });
        });

        write.MapPut("/{tenantId}/limits/{limitKey}", async (
            string tenantId, string limitKey, ProgramChangeRequest req,
            AdminGate gate, ProgramAdminService svc, CancellationToken ct) =>
        {
            var denied = await RequirePlatformAdminAsync(gate, ct);
            if (denied is not null) return denied;
            var scope = gate.BindTenant(tenantId);
            if (!scope.IsAllowed) return scope.ToProblem();

            if (!ProgramCatalog.Limits.Contains(limitKey))
                return ProblemResults.Invalid("unknown-limit", $"'{limitKey}' is not a programme limit");
            if (string.IsNullOrWhiteSpace(req.Reason))
                return ProblemResults.Unprocessable("reason-required",
                    "a cap change without a reason cannot be reviewed later");
            if (req.MaxValue is null or < 0)
                return ProblemResults.Unprocessable("invalid-cap", "a cap must be zero or greater");

            // A cap set BELOW current usage is allowed and deliberately not rejected: an administrator
            // tightening a limit on an over-provisioned tenant is a legitimate act, and the cap only ever
            // refuses the NEXT creation (WouldBreach counts liveCount + 1). Existing rows are never
            // retroactively invalidated — §4's "a switched-off module hides nothing retroactively".
            var usage = await svc.UsageAsync(scope.Tenant!, limitKey, ct);
            await svc.SetLimitAsync(AdminContracts.Actor(gate.Principal!), scope.Tenant!, limitKey, req.MaxValue.Value, req.Reason, ct);
            return Results.Ok(new
            {
                tenant = scope.Tenant, limit = limitKey, maxValue = req.MaxValue.Value,
                currentUsage = usage, alreadyOverCap = usage is { } u && u > req.MaxValue.Value,
            });
        });
    }

    /// <summary>
    /// Enablement is platform-administration authority: only a Super Admin may change it.
    ///
    /// <see cref="AdminGate"/> alone is not enough here. Its check would pass for an Org Admin holding
    /// <c>admin:manage-tenant</c> within their own tenant, and "the tenant may switch on its own programmes"
    /// is precisely the thing this gate exists to prevent. So the role is asserted explicitly, and the
    /// refusal is a distinct code rather than the generic admin denial — the remedy is Mersal, not their
    /// administrator, which is the same separation the three 403 treatments render.
    /// </summary>
    private static async Task<IResult?> RequirePlatformAdminAsync(AdminGate gate, CancellationToken ct)
    {
        var denied = await gate.CheckAsync(AdminPolicies.ManageTenant, ct);
        if (denied is not null) return denied;

        return gate.Principal!.IsInRole("super_admin")
            ? null
            : Results.Problem(statusCode: 403, title: "platform-administration-required",
                type: "https://mersal.foundation/problems/platform-administration-required",
                detail: "programme enablement is set by Mersal programme administration, not by the tenant");
    }
}

/// <summary>The known keys, mirrored from migration 0008's CHECK constraints so the screen can render every
/// switch rather than only the ones a tenant happens to have a row for.</summary>
public static class ProgramCatalog
{
    public static IReadOnlySet<string> Features { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        ProgramFeatures.Claims, ProgramFeatures.CallCentre, ProgramFeatures.Interop,
        ProgramFeatures.ReportingExtracts, ProgramFeatures.Pharmacy, ProgramFeatures.Orders,
        ProgramFeatures.Approvals, ProgramFeatures.Emr, ProgramFeatures.Finance,
        ProgramFeatures.Documents, ProgramFeatures.CaseManagement,
    };

    public static IReadOnlySet<string> Limits { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        ProgramLimits.ActiveUsers, ProgramLimits.ActiveProviderUsers,
        ProgramLimits.MonthlyExtracts, ProgramLimits.StorageMb,
    };
}

public sealed record ProgramChangeRequest(string Reason, bool? Enabled = null, int? MaxValue = null);

/// <summary>One feature switch as the administration screen shows it.</summary>
public sealed record ProgramFeatureView(string Key, bool Enabled, bool Configured, string? ChangedBy, DateTimeOffset? ChangedAt);

/// <summary>
/// One cap, with its live usage.
/// </summary>
/// <param name="MaxValue">Null = unlimited (no row), which is the fail-open direction 0008 chose deliberately.</param>
/// <param name="CurrentUsage">
/// Null = <b>this service cannot count it</b>, which is not the same as zero and must not render as zero.
/// admin-service owns role bindings, so it can count users; monthly extracts and storage are owned by
/// reporting- and document-service, and inventing a 0 here would tell an administrator a tenant was idle.
/// </param>
public sealed record ProgramLimitView(string Key, int? MaxValue, int? CurrentUsage, string? ChangedBy, DateTimeOffset? ChangedAt);

public sealed record ProgramEnablementView(
    string TenantId, IReadOnlyList<ProgramFeatureView> Features, IReadOnlyList<ProgramLimitView> Limits);

/// <summary>Reads and writes the enablement tables, with history + audit on every change.</summary>
public sealed class ProgramAdminService(AdminDbContext db, IAuditClient audit, TimeProvider clock, IOutbox outbox)
{
    public async Task<ProgramEnablementView> DescribeAsync(string tenantId, CancellationToken ct = default)
    {
        var featureRows = await db.Database
            .SqlQueryRaw<FeatureRow>(
                """
                -- AdminDbContext uses the snake_case naming convention, so a FromSql projection is matched
                -- on snake_case column names, NOT on the record's property names. Aliasing to "Key"/"ChangedAt"
                -- produced 'The required column changed_at was not present' at runtime.
                SELECT feature_key AS key, enabled, changed_by, changed_at
                FROM admin.tenant_feature WHERE tenant_id = {0}
                """, tenantId)
            .ToListAsync(ct);

        var limitRows = await db.Database
            .SqlQueryRaw<LimitRow>(
                """
                SELECT limit_key AS key, max_value, changed_by, changed_at
                FROM admin.tenant_limit WHERE tenant_id = {0}
                """, tenantId)
            .ToListAsync(ct);

        var byFeature = featureRows.ToDictionary(r => r.Key, StringComparer.Ordinal);
        var byLimit = limitRows.ToDictionary(r => r.Key, StringComparer.Ordinal);

        var features = ProgramCatalog.Features.OrderBy(k => k, StringComparer.Ordinal)
            .Select(k => byFeature.TryGetValue(k, out var row)
                ? new ProgramFeatureView(k, row.Enabled, Configured: true, row.ChangedBy, row.ChangedAt)
                // Absent ⇒ disabled AND unconfigured. Both facts are shown, because "nobody has decided" and
                // "someone decided no" call for different conversations.
                : new ProgramFeatureView(k, Enabled: false, Configured: false, null, null))
            .ToList();

        var limits = new List<ProgramLimitView>();
        foreach (var k in ProgramCatalog.Limits.OrderBy(k => k, StringComparer.Ordinal))
        {
            byLimit.TryGetValue(k, out var row);
            limits.Add(new ProgramLimitView(k, row?.MaxValue, await UsageAsync(tenantId, k, ct), row?.ChangedBy, row?.ChangedAt));
        }

        return new ProgramEnablementView(tenantId, features, limits);
    }

    /// <summary>
    /// The live count behind a cap, or null when this service does not own the data.
    ///
    /// Counted, never stored — the same reason 0008 gives: a counter is wrong after the first failed
    /// transaction and the direction of the error is unrecoverable. This is the number the screen shows;
    /// <see cref="TenantProgramStore.CheckLimitAsync"/> recounts under a lock at mutation time, so what is
    /// displayed is advisory and what is enforced is transactional.
    /// </summary>
    public async Task<int?> UsageAsync(string tenantId, string limitKey, CancellationToken ct = default)
    {
        var active = db.RoleBindings.AsNoTracking()
            .Where(b => b.TenantId == tenantId && b.Status == BindingStatus.Active);

        return limitKey switch
        {
            ProgramLimits.ActiveUsers =>
                await active.Select(b => b.SubjectUserId).Distinct().CountAsync(ct),
            ProgramLimits.ActiveProviderUsers =>
                await active.Where(b => b.ScopeType == ScopeType.Provider)
                    .Select(b => b.SubjectUserId).Distinct().CountAsync(ct),
            // Owned by reporting- and document-service. Null means "not known here" and the UI says so;
            // returning 0 would be a measurement this service never took.
            _ => null,
        };
    }

    public async Task SetFeatureAsync(
        ActorContext actor, string tenantId, string featureKey, bool enabled, string reason, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO admin.tenant_feature (tenant_id, feature_key, enabled, changed_by, changed_at, row_version)
            VALUES ({0}, {1}, {2}, {3}, {4}, 0)
            ON CONFLICT (tenant_id, feature_key) DO UPDATE
              SET enabled = {2}, changed_by = {3}, changed_at = {4},
                  row_version = admin.tenant_feature.row_version + 1
            """, [tenantId, featureKey, enabled, actor.UserId, now], ct);

        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO admin.tenant_feature_history (tenant_id, feature_key, enabled, changed_by, changed_at, change_reason)
            VALUES ({0}, {1}, {2}, {3}, {4}, {5})
            """, [tenantId, featureKey, enabled, actor.UserId, now, reason], ct);

        await audit.EmitAsync(new AuditEventDraft
        {
            EntityType = "tenant_feature", EntityId = $"{tenantId}/{featureKey}", Action = AuditAction.Update,
            ActorUserId = actor.UserId, TenantId = tenantId,
            DecisionOutcome = enabled ? "FeatureEnabled" : "FeatureDisabled",
            // Severity Notice: switching a programme off removes a whole module from an organisation, which
            // is the kind of change someone should be able to find without knowing to look for it.
            Severity = AuditSeverity.Notice,
            AfterState = $"{{\"feature\":\"{featureKey}\",\"enabled\":{(enabled ? "true" : "false")},\"reason\":{Json(reason)}}}",
        }, ct);

        // 21.4 PROPAGATION — the switch is administered here but ENFORCED wherever the module lives, off the
        // `features` claim (design 40 §5 mode 1: resolved once at token issuance, carried in the token). The
        // issuer cannot read admin.tenant_feature — that is another service's schema — so the change travels
        // as an event and identity-service keeps a projection of it. Staged in the SAME transaction as the
        // row and its history by the outbox, so a switch that was recorded is a switch that will propagate:
        // the alternative is a tenant whose screen says "enabled" while every token still says otherwise.
        //
        // `changedAt` rides along because delivery is at-least-once and NOT ordered. The projection compares
        // it and refuses to move backwards, so a redelivered "off" from five minutes ago cannot undo the "on"
        // that followed it.
        await outbox.EnqueueAsync(
            "TenantFeatureChanged", "admin.events",
            new { tenantId, featureKey, enabled, changedAt = now, changedBy = actor.UserId }, ct);
    }

    public async Task SetLimitAsync(
        ActorContext actor, string tenantId, string limitKey, int maxValue, string reason, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO admin.tenant_limit (tenant_id, limit_key, max_value, changed_by, changed_at, row_version)
            VALUES ({0}, {1}, {2}, {3}, {4}, 0)
            ON CONFLICT (tenant_id, limit_key) DO UPDATE
              SET max_value = {2}, changed_by = {3}, changed_at = {4},
                  row_version = admin.tenant_limit.row_version + 1
            """, [tenantId, limitKey, maxValue, actor.UserId, now], ct);

        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO admin.tenant_limit_history (tenant_id, limit_key, max_value, changed_by, changed_at, change_reason)
            VALUES ({0}, {1}, {2}, {3}, {4}, {5})
            """, [tenantId, limitKey, maxValue, actor.UserId, now, reason], ct);

        await audit.EmitAsync(new AuditEventDraft
        {
            EntityType = "tenant_limit", EntityId = $"{tenantId}/{limitKey}", Action = AuditAction.Update,
            ActorUserId = actor.UserId, TenantId = tenantId, DecisionOutcome = "LimitSet",
            Severity = AuditSeverity.Notice,
            AfterState = $"{{\"limit\":\"{limitKey}\",\"maxValue\":{maxValue},\"reason\":{Json(reason)}}}",
        }, ct);
    }

    /// <summary>Reasons are administrator free text — embedding one unescaped would corrupt the audit event's
    /// JSON, and the audit record is the thing that has to survive.</summary>
    private static string Json(string value) => System.Text.Json.JsonSerializer.Serialize(value);

    private sealed record FeatureRow(string Key, bool Enabled, string? ChangedBy, DateTimeOffset? ChangedAt);
    private sealed record LimitRow(string Key, int MaxValue, string? ChangedBy, DateTimeOffset? ChangedAt);
}
