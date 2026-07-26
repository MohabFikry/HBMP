using System.Globalization;
using Mersal.Data;
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

/// <summary>Formulary / PBM check (phase 6.3, US-052). Today it returns the masterdata approved-alternatives for a
/// prescribed drug; it is a clearly-marked, swappable stand-in for a future external PBM/formulary integration
/// (<c>IPbmService</c>) — the dispensing rule that only a policy-approved alternative may be substituted depends on
/// this interface, not on any concrete provider. HTTP impl in Api; tests inject a fake.</summary>
public interface IFormularyService
{
    Task<IReadOnlyList<Guid>> ApprovedAlternativesAsync(Guid drugId, string? bearerToken, CancellationToken ct = default);
}

/// <summary>Resolves a beneficiary id from a policy / passport / member number (phase 6.1 search) via
/// patient-service. Best-effort and fail-safe (a lookup failure yields no match rather than leaking). HTTP impl in
/// Api; tests inject a fake. Rx-number and beneficiary-id searches do not need it.</summary>
public interface IBeneficiaryResolver
{
    Task<Guid?> ResolveAsync(string? policyNo, string? passport, string? memberNo, string? bearerToken, CancellationToken ct = default);
}

/// <summary>Issues the next monotonic business key for a year from a named counter table (rx_seq / referral_seq).</summary>
public sealed class SequenceIssuer(PharmacyDbContext db)
{
    public Task<int> NextAsync(string table, int year, CancellationToken ct = default) => NextCoreAsync(table, year, ct);

    // Whitelist the two known counter tables so no caller-supplied string is ever interpolated into SQL
    // (the value maps to a compile-time constant; anything else throws before touching the DB).
    private const string RxSeq = "rx_seq";
    private const string ReferralSeq = "referral_seq";

    private static string ResolveTable(string table) => table switch
    {
        RxSeq => RxSeq,
        ReferralSeq => ReferralSeq,
        _ => throw new ArgumentOutOfRangeException(nameof(table), table, "Unknown pharmacy sequence table."),
    };

    private async Task<int> NextCoreAsync(string table, int year, CancellationToken ct)
    {
        var seq = ResolveTable(table);
        var conn = db.Database.GetDbConnection();
        var opened = false;
        if (conn.State != System.Data.ConnectionState.Open) { await conn.OpenAsync(ct); opened = true; }
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"INSERT INTO pharmacy.{seq}(year, last_value) VALUES (@y, 1)
                                 ON CONFLICT (year) DO UPDATE SET last_value = pharmacy.{seq}.last_value + 1
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
        services.AddHbmpRls();
        services.AddDbContext<PharmacyDbContext>((sp, o) =>
            o.UseNpgsql(config.GetConnectionString("Pharmacy")
                        ?? throw new System.InvalidOperationException("Database connection string is not configured — inject it via ConnectionStrings env/OpenBao; never a baked credential."))
             .UseSnakeCaseNamingConvention()
             .AddHbmpRlsInterceptors(sp));
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
