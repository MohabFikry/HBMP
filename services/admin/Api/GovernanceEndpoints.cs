using Mersal.Admin.Domain;
using Mersal.Admin.Infrastructure;
using Mersal.Authz;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Admin.Api;

public sealed record MasterDataEditRequest(string System, string Code, Dictionary<string, object> Attributes,
    string Rationale, bool Retired = false)
{
    public CodeSystem SystemEnum => Enum.Parse<CodeSystem>(System, ignoreCase: true);
    public bool SystemValid => Enum.TryParse<CodeSystem>(System, ignoreCase: true, out _);
}

public sealed record TemplateEditRequest(string TemplateKey, string Channel, string BodyEn, string BodyAr,
    string SubjectEn = "", string SubjectAr = "", string? Tenant = null);

public sealed record ConfigEditRequest(string Key, string ValueType, string Value, string? Tenant = null)
{
    public ConfigValueType TypeEnum => Enum.Parse<ConfigValueType>(ValueType, ignoreCase: true);
    public bool TypeValid => Enum.TryParse<ConfigValueType>(ValueType, ignoreCase: true, out _);
}

/// <summary>Master-data / template / system-config governance endpoints (phase 8b.2). Governance-role gated
/// (FR-MDM-008) + audited; all edits are effective-dated.</summary>
public static class GovernanceEndpoints
{
    public static void MapGovernance(this WebApplication app)
    {
        var g = app.MapGroup("/api/v1/admin").WithTags("admin-governance");

        // Master-data edit — appends a new effective-dated version (clinical governance / Super Admin only).
        g.MapPost("/master-data", async (MasterDataEditRequest req, AdminGate gate, GovernanceService svc, CancellationToken ct) =>
        {
            var denied = await gate.CheckAsync(AdminPolicies.EditMasterData, ct);
            if (denied is not null) return denied;
            if (!req.SystemValid) return ProblemResults.Invalid("unknown-code-system");
            if (string.IsNullOrWhiteSpace(req.Rationale)) return ProblemResults.Invalid("rationale-required");

            var v = await svc.UpsertMasterDataAsync(AdminContracts.Actor(gate.Principal!), req.SystemEnum, req.Code,
                req.Attributes, req.Rationale, req.Retired, ct);
            return Results.Created($"/api/v1/admin/master-data/{req.System}/{req.Code}",
                new { v.VersionId, system = v.System.ToString(), v.Code, v.VersionNo, v.EffectiveFrom });
        });

        // Resolve the version in force at a given date (how a historical record resolves the code).
        g.MapGet("/master-data/{system}/{code}/as-of", async (string system, string code, DateTimeOffset at,
            AdminGate gate, GovernanceService svc, CancellationToken ct) =>
        {
            var denied = await gate.CheckAsync(AdminPolicies.ReadAccess, ct);
            if (denied is not null) return denied;
            if (!Enum.TryParse<CodeSystem>(system, ignoreCase: true, out var sys)) return ProblemResults.Invalid("unknown-code-system");

            var v = await svc.ResolveAsOfAsync(sys, code, at, ct);
            return v is null ? Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found") : Results.Ok(new { v.VersionId, v.VersionNo, v.AttributesJson, v.EffectiveFrom, v.EffectiveTo });
        });

        // List the master-data versions currently in force (effective_to IS NULL) — the governance read surface.
        g.MapGet("/master-data", async (AdminGate gate, AdminDbContext db, CancellationToken ct) =>
        {
            var denied = await gate.CheckAsync(AdminPolicies.ReadAccess, ct);
            if (denied is not null) return denied;
            var rows = await db.MasterDataVersions.AsNoTracking()
                .Where(v => v.EffectiveTo == null)
                .OrderBy(v => v.System).ThenBy(v => v.Code).Take(500)
                .Select(v => new { v.VersionId, system = v.System.ToString(), v.Code, v.VersionNo, v.Retired, v.EffectiveFrom, v.Rationale })
                .ToListAsync(ct);
            return Results.Ok(rows);
        });

        // List the system-config entries currently in force for the caller's scope — the config read surface.
        g.MapGet("/system-config", async (AdminGate gate, AdminDbContext db, CancellationToken ct) =>
        {
            var denied = await gate.CheckAsync(AdminPolicies.ReadAccess, ct);
            if (denied is not null) return denied;
            var rows = await db.SystemConfigs.AsNoTracking()
                .Where(c => c.EffectiveTo == null)
                .OrderBy(c => c.TenantId).ThenBy(c => c.Key).Take(500)
                .Select(c => new { c.ConfigId, c.TenantId, c.Key, type = c.ValueType.ToString(), c.Value, c.VersionNo, c.EffectiveFrom })
                .ToListAsync(ct);
            return Results.Ok(rows);
        });

        // Notification template — linted (PHI-safe + AR/EN parity) before save.
        g.MapPost("/templates", async (TemplateEditRequest req, AdminGate gate, GovernanceService svc, CancellationToken ct) =>
        {
            var denied = await gate.CheckAsync(AdminPolicies.EditTemplate, ct);
            if (denied is not null) return denied;
            var p = gate.Principal!;
            var scope = gate.BindTenant(req.Tenant);
            if (!scope.IsAllowed) return scope.ToProblem();
            var tenant = scope.Tenant!;

            var result = await svc.SaveTemplateAsync(AdminContracts.Actor(p), tenant, req.TemplateKey, req.Channel,
                req.SubjectEn, req.SubjectAr, req.BodyEn, req.BodyAr, ct);
            if (result.Ok)
                return Results.Created($"/api/v1/admin/templates/{result.Version!.TemplateVersionId}",
                    new { result.Version.TemplateVersionId, result.Version.TemplateKey, result.Version.Channel, result.Version.VersionNo });
            return ProblemResults.Unprocessable("template-lint", extra: new Dictionary<string, object?> { ["errors"] = result.Errors });
        });

        // System configuration — typed + validated + effective-dated.
        g.MapPut("/system-config", async (ConfigEditRequest req, AdminGate gate, GovernanceService svc, CancellationToken ct) =>
        {
            var denied = await gate.CheckAsync(AdminPolicies.EditConfig, ct);
            if (denied is not null) return denied;
            var p = gate.Principal!;
            var scope = gate.BindTenant(req.Tenant);
            if (!scope.IsAllowed) return scope.ToProblem();
            var tenant = scope.Tenant!;
            if (!req.TypeValid) return ProblemResults.Invalid("unknown-value-type");

            var (ok, error, config) = await svc.SetConfigAsync(AdminContracts.Actor(p), tenant, req.Key, req.TypeEnum, req.Value, ct);
            return ok
                ? Results.Ok(new { config!.ConfigId, config.Key, value = config.Value, type = config.ValueType.ToString(), config.VersionNo })
                : ProblemResults.Invalid(error ?? "error");
        });
    }
}
