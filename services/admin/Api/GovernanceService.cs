using System.Text.Json;
using Mersal.Admin.Domain;
using Mersal.Admin.Infrastructure;
using Mersal.Audit.Client;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Admin.Api;

/// <summary>Result of a template save: rejected carries the linter errors (PHI-in-outbound / missing AR-EN parity).</summary>
public sealed record TemplateSaveResult(bool Ok, NotificationTemplateVersion? Version, IReadOnlyList<string> Errors);

/// <summary>
/// Master-data / template / system-config governance (phase 8b.2, FR-MDM-007/008/009 + FR-NOT-005). Every edit is
/// EFFECTIVE-DATED — a change appends a new version and closes the prior version's window, so a historical record
/// resolves the version in force at ITS time. Templates are linted (PHI-safe + AR/EN parity) and config values are
/// typed-validated before save. All edits are audited (before/after, effective-from, rationale).
/// </summary>
public sealed class GovernanceService(AdminDbContext db, IAuditClient audit, TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // ---------------------------------------------------------------- master data
    /// <summary>Append a new effective-dated version of a master-data code (closing the prior version). Audited.</summary>
    public async Task<MasterDataVersion> UpsertMasterDataAsync(ActorContext actor, CodeSystem system, string code,
        object attributes, string rationale, bool retired = false, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var current = await db.MasterDataVersions
            .Where(v => v.System == system && v.Code == code && v.EffectiveTo == null)
            .OrderByDescending(v => v.VersionNo).FirstOrDefaultAsync(ct);

        var priorVersion = 0;
        if (current is not null)
        {
            current.EffectiveTo = now;      // close the prior window (never mutate its content)
            priorVersion = current.VersionNo;
        }
        var maxVersion = await db.MasterDataVersions
            .Where(v => v.System == system && v.Code == code).MaxAsync(v => (int?)v.VersionNo, ct) ?? 0;

        var next = new MasterDataVersion
        {
            VersionId = Guid.NewGuid(), System = system, Code = code, VersionNo = maxVersion + 1,
            AttributesJson = JsonSerializer.Serialize(attributes, Json), Retired = retired,
            EffectiveFrom = now, EffectiveTo = null, ChangedBy = actor.UserId, Rationale = rationale, CreatedAt = now,
        };
        db.MasterDataVersions.Add(next);
        await db.SaveChangesAsync(ct);

        await audit.EmitAsync(new AuditEventDraft
        {
            EntityType = "master_data_version", EntityId = $"{system}:{code}", Action = AuditAction.Update,
            ActorUserId = actor.UserId, ActorRole = actor.Role, ActorMfa = actor.Mfa,
            BeforeState = current is null ? null : JsonSerializer.Serialize(new { version = priorVersion }, Json),
            AfterState = JsonSerializer.Serialize(new { version = next.VersionNo, retired, rationale, effectiveFrom = now }, Json),
            Purpose = "master-data-governance", Severity = AuditSeverity.Notice,
        }, ct);
        return next;
    }

    /// <summary>Resolve the master-data version in force at <paramref name="asOf"/> — how a historical order resolves
    /// the code as it was at its time (null if the code did not exist or was retired then).</summary>
    public async Task<MasterDataVersion?> ResolveAsOfAsync(CodeSystem system, string code, DateTimeOffset asOf, CancellationToken ct = default)
    {
        var versions = await db.MasterDataVersions.AsNoTracking()
            .Where(v => v.System == system && v.Code == code).ToListAsync(ct);
        var inForce = versions.FirstOrDefault(v => v.InForceAt(asOf));
        return inForce is { Retired: true } ? null : inForce;
    }

    // ---------------------------------------------------------------- templates
    /// <summary>Save a bilingual template version — rejected (with linter errors) if it fails PHI-safe / AR-EN parity.</summary>
    public async Task<TemplateSaveResult> SaveTemplateAsync(ActorContext actor, string tenant, string key, string channel,
        string subjectEn, string subjectAr, string bodyEn, string bodyAr, CancellationToken ct = default)
    {
        var lint = TemplateLinter.Lint(channel, subjectEn, subjectAr, bodyEn, bodyAr);
        if (!lint.Ok)
        {
            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "notification_template_version", EntityId = $"{key}:{channel}", Action = AuditAction.Update,
                ActorUserId = actor.UserId, ActorRole = actor.Role, TenantId = tenant, ActorMfa = actor.Mfa,
                DecisionOutcome = "rejected", DecisionReasonCode = "template-lint",
                AfterState = JsonSerializer.Serialize(lint.Errors, Json),
                Purpose = "template-governance", Severity = AuditSeverity.Warning,
            }, ct);
            return new TemplateSaveResult(false, null, lint.Errors);
        }

        var now = clock.GetUtcNow();
        var current = await db.TemplateVersions
            .Where(t => t.TenantId == tenant && t.TemplateKey == key && t.Channel == channel && t.EffectiveTo == null)
            .OrderByDescending(t => t.VersionNo).FirstOrDefaultAsync(ct);
        if (current is not null) current.EffectiveTo = now;
        var maxVersion = await db.TemplateVersions
            .Where(t => t.TenantId == tenant && t.TemplateKey == key && t.Channel == channel)
            .MaxAsync(t => (int?)t.VersionNo, ct) ?? 0;

        var version = new NotificationTemplateVersion
        {
            TemplateVersionId = Guid.NewGuid(), TenantId = tenant, TemplateKey = key, Channel = channel,
            SubjectEn = subjectEn, SubjectAr = subjectAr, BodyEn = bodyEn, BodyAr = bodyAr,
            VersionNo = maxVersion + 1, EffectiveFrom = now, ChangedBy = actor.UserId, CreatedAt = now,
        };
        db.TemplateVersions.Add(version);
        await db.SaveChangesAsync(ct);

        await audit.EmitAsync(new AuditEventDraft
        {
            EntityType = "notification_template_version", EntityId = version.TemplateVersionId.ToString(), Action = AuditAction.Update,
            ActorUserId = actor.UserId, ActorRole = actor.Role, TenantId = tenant, ActorMfa = actor.Mfa,
            DecisionOutcome = "saved",
            AfterState = JsonSerializer.Serialize(new { key, channel, version = version.VersionNo }, Json),
            Purpose = "template-governance", Severity = AuditSeverity.Notice,
        }, ct);
        return new TemplateSaveResult(true, version, []);
    }

    // ---------------------------------------------------------------- system config
    /// <summary>Set a typed, effective-dated config value (null Canonical on a type error → rejected).</summary>
    public async Task<(bool Ok, string? Error, SystemConfig? Config)> SetConfigAsync(ActorContext actor, string tenant,
        string key, ConfigValueType type, string rawValue, CancellationToken ct = default)
    {
        var valid = ConfigValidation.Validate(type, rawValue);
        if (!valid.Ok) return (false, valid.Error, null);

        var now = clock.GetUtcNow();
        var current = await db.SystemConfigs
            .Where(c => c.TenantId == tenant && c.Key == key && c.EffectiveTo == null)
            .OrderByDescending(c => c.VersionNo).FirstOrDefaultAsync(ct);
        if (current is not null) current.EffectiveTo = now;
        var maxVersion = await db.SystemConfigs
            .Where(c => c.TenantId == tenant && c.Key == key).MaxAsync(c => (int?)c.VersionNo, ct) ?? 0;

        var config = new SystemConfig
        {
            ConfigId = Guid.NewGuid(), TenantId = tenant, Key = key, ValueType = type, Value = valid.Canonical!,
            VersionNo = maxVersion + 1, EffectiveFrom = now, UpdatedBy = actor.UserId, UpdatedAt = now,
        };
        db.SystemConfigs.Add(config);
        await db.SaveChangesAsync(ct);

        await audit.EmitAsync(new AuditEventDraft
        {
            EntityType = "system_config", EntityId = $"{tenant}:{key}", Action = AuditAction.Update,
            ActorUserId = actor.UserId, ActorRole = actor.Role, TenantId = tenant == "*" ? null : tenant, ActorMfa = actor.Mfa,
            BeforeState = current is null ? null : JsonSerializer.Serialize(new { current.Value, version = current.VersionNo }, Json),
            AfterState = JsonSerializer.Serialize(new { value = config.Value, type = type.ToString(), version = config.VersionNo }, Json),
            Purpose = "system-config", Severity = AuditSeverity.Notice,
        }, ct);
        return (true, null, config);
    }
}
