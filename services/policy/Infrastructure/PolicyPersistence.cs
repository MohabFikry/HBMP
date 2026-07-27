using Mersal.Data;
using Mersal.Policy.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Mersal.Policy.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddPolicyInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        services.AddHbmpRls();
        services.AddDbContext<PolicyDbContext>((sp, o) =>
            o.UseNpgsql(config.GetConnectionString("Policy") ?? throw new System.InvalidOperationException("Database connection string is not configured — inject it via ConnectionStrings env/OpenBao; never a baked credential.")).UseSnakeCaseNamingConvention().AddHbmpRlsInterceptors(sp));
        services.AddScoped<BenefitConsumptionApplier>();   // 18.A1 — the sole writer of consumed_value
        services.AddScoped<IPlanVersionResolver, PlanVersionResolver>();  // 19.1 — version in force on a service date
        return services;
    }
}
