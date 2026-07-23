using System.Globalization;
using Mersal.Emr.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Mersal.Emr.Infrastructure;

/// <summary>
/// Reads member status for the visit gate. The HTTP implementation (calling eligibility-service) lives
/// in the Api layer; this interface keeps Domain/Infrastructure free of transport concerns and lets
/// tests substitute a fake.
/// </summary>
public interface IMemberStatusProvider
{
    Task<MemberStatus?> GetStatusAsync(Guid beneficiaryId, string? bearerToken, CancellationToken ct = default);
}

/// <summary>Issues the next monotonic Encounter No for a year (atomic upsert on encounter_seq).</summary>
public sealed class EncounterNoIssuer(EmrDbContext db)
{
    public async Task<string> NextAsync(int year, CancellationToken ct = default)
    {
        var conn = db.Database.GetDbConnection();
        var opened = false;
        if (conn.State != System.Data.ConnectionState.Open) { await conn.OpenAsync(ct); opened = true; }
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO emr.encounter_seq(year, last_value) VALUES (@y, 1)
                                ON CONFLICT (year) DO UPDATE SET last_value = emr.encounter_seq.last_value + 1
                                RETURNING last_value;";
            var p = cmd.CreateParameter(); p.ParameterName = "y"; p.Value = year; cmd.Parameters.Add(p);
            var seq = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
            return EncounterNo.Format(year, seq);
        }
        finally { if (opened) await conn.CloseAsync(); }
    }
}

public static class DependencyInjection
{
    public static IServiceCollection AddEmrInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        services.AddDbContext<EmrDbContext>(o =>
            o.UseNpgsql(config.GetConnectionString("Emr")
                        ?? "Host=postgres;Database=hbmp;Username=hbmp;Password=hbmp")
             .UseSnakeCaseNamingConvention());
        services.AddScoped<EncounterNoIssuer>();
        services.AddScoped<AppointmentBookingService>();
        return services;
    }
}
