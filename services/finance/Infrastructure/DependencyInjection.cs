using Mersal.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Mersal.Finance.Infrastructure;

/// <summary>Wires the finance read-model: DbContext, the event projector, the settlement generator + number issuer,
/// and the read-side query service. The contract price provider is registered in the Api layer (HTTP to provider).</summary>
public static class DependencyInjection
{
    public static IServiceCollection AddFinanceInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        services.AddHbmpRls();
        services.AddDbContext<FinanceDbContext>((sp, o) =>
            o.UseNpgsql(config.GetConnectionString("Finance")
                        ?? throw new System.InvalidOperationException("Database connection string is not configured — inject it via ConnectionStrings env/OpenBao; never a baked credential."))
             .UseSnakeCaseNamingConvention()
             .AddHbmpRlsInterceptors(sp));

        services.AddScoped<FinanceEventProjector>();
        services.AddScoped<FinanceQueries>();
        services.AddScoped<SettlementNoIssuer>();
        services.AddScoped<SettlementGenerator>();
        return services;
    }
}
