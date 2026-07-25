using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Mersal.Claims.Infrastructure;

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
        return services;
    }
}
