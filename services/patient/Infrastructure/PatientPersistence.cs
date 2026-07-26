using Mersal.Patient.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Mersal.Patient.Infrastructure;

/// <summary>EF-backed duplicate lookup (active identifiers only).</summary>
public sealed class IdentifierLookup(PatientDbContext db) : IIdentifierLookup
{
    public async Task<Guid?> FindActiveOwnerAsync(IdentifierType type, string normalizedValue, CancellationToken ct = default)
    {
        var row = await db.Identifiers.AsNoTracking()
            .Where(x => x.IdentifierType == type && x.IdentifierValue == normalizedValue && !x.IsDeleted)
            .Select(x => (Guid?)x.BeneficiaryId)
            .FirstOrDefaultAsync(ct);
        return row;
    }
}

/// <summary>Issues the next monotonic Member No for a year (atomic upsert on member_no_seq).</summary>
public sealed class MemberNoIssuer(PatientDbContext db)
{
    public async Task<string> NextAsync(int year, CancellationToken ct = default)
    {
        // Atomic upsert + increment via raw ADO (INSERT…RETURNING is non-composable, so no EF SqlQuery).
        var conn = db.Database.GetDbConnection();
        var opened = false;
        if (conn.State != System.Data.ConnectionState.Open) { await conn.OpenAsync(ct); opened = true; }
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO patient.member_no_seq(year, last_value) VALUES (@y, 1)
                                ON CONFLICT (year) DO UPDATE SET last_value = patient.member_no_seq.last_value + 1
                                RETURNING last_value;";
            var p = cmd.CreateParameter(); p.ParameterName = "y"; p.Value = year; cmd.Parameters.Add(p);
            var seq = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
            return MemberNo.Format(year, seq);
        }
        finally { if (opened) await conn.CloseAsync(); }
    }
}

public static class DependencyInjection
{
    public static IServiceCollection AddPatientInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        services.AddDbContext<PatientDbContext>(o =>
            o.UseNpgsql(config.GetConnectionString("Patient")
                        ?? throw new System.InvalidOperationException("Database connection string is not configured — inject it via ConnectionStrings env/OpenBao; never a baked credential."))
             .UseSnakeCaseNamingConvention());
        services.AddScoped<IIdentifierLookup, IdentifierLookup>();
        services.AddScoped<MemberNoIssuer>();
        return services;
    }
}
