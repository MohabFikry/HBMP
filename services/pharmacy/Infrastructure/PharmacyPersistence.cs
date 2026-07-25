using System.Globalization;
using Mersal.Pharmacy.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Mersal.Pharmacy.Infrastructure;

/// <summary>Validates a prescription-line drug id against masterdata-service. HTTP impl in Api; tests inject a fake.</summary>
public interface IDrugValidator
{
    Task<bool> DrugExistsAsync(Guid drugId, string? bearerToken, CancellationToken ct = default);
}

public sealed class AllowAllDrugValidator : IDrugValidator
{
    public Task<bool> DrugExistsAsync(Guid drugId, string? bearerToken, CancellationToken ct = default) => Task.FromResult(true);
}

/// <summary>Advisory prescribe-time screening (US-033): interaction across the Rx's drugs + allergy conflicts vs
/// the beneficiary's allergies. Best-effort/non-blocking; HTTP impl in Api, tests inject a fake.</summary>
public interface IPrescribingScreener
{
    Task<AlertScreening> ScreenAsync(Guid beneficiaryId, IReadOnlyList<Guid> drugIds, string? bearerToken, CancellationToken ct = default);
}

/// <summary>Treating-relationship check via emr-service (US-033/US-034). HTTP impl in Api; tests inject a fake.</summary>
public interface ITreatingRelationshipClient
{
    Task<bool> TreatsAsync(Guid beneficiaryId, string? bearerToken, CancellationToken ct = default);
}

/// <summary>Issues the next monotonic business key for a year from a named counter table (rx_seq / referral_seq).</summary>
public sealed class SequenceIssuer(PharmacyDbContext db)
{
    public Task<int> NextAsync(string table, int year, CancellationToken ct = default) => NextCoreAsync(table, year, ct);

    private async Task<int> NextCoreAsync(string table, int year, CancellationToken ct)
    {
        var conn = db.Database.GetDbConnection();
        var opened = false;
        if (conn.State != System.Data.ConnectionState.Open) { await conn.OpenAsync(ct); opened = true; }
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"INSERT INTO pharmacy.{table}(year, last_value) VALUES (@y, 1)
                                 ON CONFLICT (year) DO UPDATE SET last_value = pharmacy.{table}.last_value + 1
                                 RETURNING last_value;";
            var p = cmd.CreateParameter(); p.ParameterName = "y"; p.Value = year; cmd.Parameters.Add(p);
            return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
        }
        finally { if (opened) await conn.CloseAsync(); }
    }
}

public static class DependencyInjection
{
    public static IServiceCollection AddPharmacyInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        services.AddDbContext<PharmacyDbContext>(o =>
            o.UseNpgsql(config.GetConnectionString("Pharmacy")
                        ?? "Host=postgres;Database=hbmp;Username=hbmp;Password=hbmp")
             .UseSnakeCaseNamingConvention());
        services.AddScoped<SequenceIssuer>();

        // Routing policy from configuration (Pharmacy:Routing) — gated drug ids + high-cost threshold.
        var routing = new RxRoutingOptions();
        foreach (var g in config.GetSection("Pharmacy:Routing:GatedDrugIds").GetChildren())
            if (Guid.TryParse(g.Value, out var id)) routing.GatedDrugIds.Add(id);
        foreach (var uc in config.GetSection("Pharmacy:Routing:UnitCosts").GetChildren())
            if (Guid.TryParse(uc.Key, out var id) && decimal.TryParse(uc.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var cost))
                routing.UnitCosts[id] = cost;
        if (decimal.TryParse(config["Pharmacy:Routing:HighCostThreshold"], NumberStyles.Any, CultureInfo.InvariantCulture, out var thr))
            routing.HighCostThreshold = thr;
        services.AddSingleton(routing);
        return services;
    }
}
