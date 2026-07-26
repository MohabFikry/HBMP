using System.Globalization;
using Mersal.Approvals.Domain;
using Mersal.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Mersal.Approvals.Infrastructure;

/// <summary>The field-scoped clinical context a reviewer sees on the review view (US-060). This is an EXPLICIT
/// projection — EMR summary, clinical notes, and supporting documents/reports — assembled by calling emr /
/// document services with the caller's purpose (PUR), never the raw records. The HTTP implementation lives in the
/// Api layer; tests inject a fake. Fail-closed: a null result means the clinical context could not be assembled.</summary>
public interface IClinicalContextProvider
{
    Task<ClinicalContext?> GetAsync(Guid beneficiaryId, string? sourceRef, string? bearerToken, CancellationToken ct = default);
}

/// <summary>Minimum-necessary clinical projection for the review view. Only what a reviewer needs to decide —
/// no raw EMR record, no fields beyond these classes.</summary>
public sealed record ClinicalContext(
    string EmrSummary,
    IReadOnlyList<ClinicalNote> Notes,
    IReadOnlyList<SupportingDocument> Documents)
{
    /// <summary>The audit "fields returned" list (PHI-read record) — the field classes exposed to the reviewer.</summary>
    public static readonly string[] FieldClasses = ["emr_summary", "clinical_note", "supporting_document"];
}

// SensitivityLevel + CallerHasAccess are supplied by the data owner (emr/orders) that assembled the item for
// THIS caller (author or active report-access grant). The review projection reduces non-Standard items the caller
// cannot access to existence-metadata-only via Mersal.Authz.SensitiveDisclosure (design 37 §6, H4).
public sealed record ClinicalNote(string Type, string Author, DateTimeOffset AuthoredAt, string Summary,
    string SensitivityLevel = "Standard", bool CallerHasAccess = true);
public sealed record SupportingDocument(Guid DocumentId, string Kind, string FileName,
    string SensitivityLevel = "Standard", bool CallerHasAccess = true);

/// <summary>Priority-based SLA policy (hours to due), overridable from config (Approvals:Sla). Defaults mirror
/// <see cref="AuthorizationWorkflow.SlaDue"/>.</summary>
public sealed class SlaOptions
{
    public int EmergencyHours { get; set; } = 1;
    public int UrgentHours { get; set; } = 4;
    public int RoutineHours { get; set; } = 48;

    public DateTimeOffset DueFrom(AuthPriority priority, DateTimeOffset from) => priority switch
    {
        AuthPriority.Emergency => from.AddHours(EmergencyHours),
        AuthPriority.Urgent => from.AddHours(UrgentHours),
        _ => from.AddHours(RoutineHours),
    };
}

/// <summary>Issues the next monotonic Auth No for a year (atomic upsert on auth_seq).</summary>
public sealed class AuthNoIssuer(ApprovalsDbContext db)
{
    public async Task<string> NextAsync(int year, CancellationToken ct = default)
    {
        var conn = db.Database.GetDbConnection();
        var opened = false;
        if (conn.State != System.Data.ConnectionState.Open) { await conn.OpenAsync(ct); opened = true; }
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO approvals.auth_seq(year, last_value) VALUES (@y, 1)
                                ON CONFLICT (year) DO UPDATE SET last_value = approvals.auth_seq.last_value + 1
                                RETURNING last_value;";
            var p = cmd.CreateParameter(); p.ParameterName = "y"; p.Value = year; cmd.Parameters.Add(p);
            var seq = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
            return AuthNo.Format(year, seq);
        }
        finally { if (opened) await conn.CloseAsync(); }
    }
}

public static class DependencyInjection
{
    public static IServiceCollection AddApprovalsInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        services.AddHbmpRls();
        services.AddDbContext<ApprovalsDbContext>((sp, o) =>
            o.UseNpgsql(config.GetConnectionString("Approvals")
                        ?? throw new System.InvalidOperationException("Database connection string is not configured — inject it via ConnectionStrings env/OpenBao; never a baked credential."))
             .UseSnakeCaseNamingConvention()
             .AddHbmpRlsInterceptors(sp));
        services.AddScoped<AuthNoIssuer>();

        var sla = new SlaOptions();
        if (int.TryParse(config["Approvals:Sla:EmergencyHours"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var eh)) sla.EmergencyHours = eh;
        if (int.TryParse(config["Approvals:Sla:UrgentHours"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var uh)) sla.UrgentHours = uh;
        if (int.TryParse(config["Approvals:Sla:RoutineHours"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var rh)) sla.RoutineHours = rh;
        services.AddSingleton(sla);
        return services;
    }
}
