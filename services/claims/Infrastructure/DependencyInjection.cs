using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Mersal.Claims.Infrastructure;

/// <summary>Tunable claims policy — the dual-control value threshold above which a decision/override needs a second
/// distinct approver (36 §6 / §7). Configurable per deployment; a sensible default keeps it enforced out of the box.</summary>
public sealed record ClaimsOptions
{
    public decimal DualControlThreshold { get; init; } = 10_000m;
}

/// <summary>Wires the claims read/write store: DbContext, the claim-number issuer, and the auto-derive intake
/// executor. The contract-tariff provider is registered in the Api layer (HTTP to provider-service); a NoTariff
/// fallback keeps the service booting without inventing prices.</summary>
public static class DependencyInjection
{
    public static IServiceCollection AddClaimsInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        services.AddDbContext<ClaimsDbContext>(o =>
            o.UseNpgsql(config.GetConnectionString("Claims")
                        ?? "Host=postgres;Database=hbmp;Username=hbmp;Password=hbmp")
             .UseSnakeCaseNamingConvention());

        services.AddScoped<ClaimNoIssuer>();
        services.AddScoped<ClaimIntakeExecutor>();
        services.AddScoped<ClaimsQueries>();
        services.AddScoped<BatchNoIssuer>();
        services.AddScoped<BatchService>();
        services.AddScoped<AdjudicationService>();
        services.AddScoped<DecisionService>();
        // Permissive fact source by default; the HTTP-backed eligibility/policy/approvals/provider wiring lands later.
        services.AddScoped<IExternalAdjudicationFacts, PermissiveAdjudicationFacts>();

        var threshold = decimal.TryParse(config["Claims:DualControlThreshold"], NumberStyles.Any,
            CultureInfo.InvariantCulture, out var t) ? t : 10_000m;
        services.AddSingleton(new ClaimsOptions { DualControlThreshold = threshold });
        return services;
    }
}
