using System.Globalization;
using Mersal.Case.Domain;
using Mersal.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Mersal.Case.Infrastructure;

/// <summary>Resolves the set of case ids a Case Manager holds an ACTIVE assignment to — the input to the
/// case-assignment ABAC condition. Unassignment (active=false) drops the id → immediate revocation.</summary>
public sealed class AssignmentResolver(CaseDbContext db)
{
    public async Task<IReadOnlySet<string>> ActiveCaseIdsForAsync(Guid caseManagerId, CancellationToken ct = default)
    {
        var ids = await db.Assignments.AsNoTracking()
            .Where(a => a.CaseManagerId == caseManagerId && a.Active)
            .Select(a => a.CaseId)
            .ToListAsync(ct);
        return ids.Select(i => i.ToString()).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>True iff this manager currently holds an active assignment to the case (cheap single-case probe).</summary>
    public Task<bool> HasActiveAssignmentAsync(Guid caseId, Guid caseManagerId, CancellationToken ct = default) =>
        db.Assignments.AsNoTracking().AnyAsync(a => a.CaseId == caseId && a.CaseManagerId == caseManagerId && a.Active, ct);
}

/// <summary>Issues the next monotonic Case No for a year (atomic upsert on case_seq).</summary>
public sealed class CaseNoIssuer(CaseDbContext db)
{
    public async Task<string> NextAsync(int year, CancellationToken ct = default)
    {
        var conn = db.Database.GetDbConnection();
        var opened = false;
        if (conn.State != System.Data.ConnectionState.Open) { await conn.OpenAsync(ct); opened = true; }
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO ""case"".case_seq(year, last_value) VALUES (@y, 1)
                                ON CONFLICT (year) DO UPDATE SET last_value = ""case"".case_seq.last_value + 1
                                RETURNING last_value;";
            var p = cmd.CreateParameter(); p.ParameterName = "y"; p.Value = year; cmd.Parameters.Add(p);
            var seq = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
            return CaseNo.Format(year, seq);
        }
        finally { if (opened) await conn.CloseAsync(); }
    }
}

public static class DependencyInjection
{
    public static IServiceCollection AddCaseInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        services.AddHbmpRls();
        services.AddDbContext<CaseDbContext>((sp, o) =>
            o.UseNpgsql(config.GetConnectionString("Case")
                        ?? throw new System.InvalidOperationException("Database connection string is not configured — inject it via ConnectionStrings env/OpenBao; never a baked credential."))
             .UseSnakeCaseNamingConvention()
             .AddHbmpRlsInterceptors(sp));
        services.AddScoped<AssignmentResolver>();
        services.AddScoped<CaseNoIssuer>();
        return services;
    }
}
