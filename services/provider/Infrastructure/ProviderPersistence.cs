using Mersal.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Mersal.Provider.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddProviderInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        services.AddHbmpRls();
        services.AddDbContext<ProviderDbContext>((sp, o) =>
            o.UseNpgsql(config.GetConnectionString("Provider") ?? throw new System.InvalidOperationException("Database connection string is not configured — inject it via ConnectionStrings env/OpenBao; never a baked credential."))
                .UseSnakeCaseNamingConvention()
                .AddInterceptors(sp.GetRequiredService<RlsConnectionInterceptor>()));
        // Default to accept-all; the Api layer replaces this with the masterdata-backed HTTP validator.
        services.TryAddScoped<ICodeValidator, AllowAllCodeValidator>();
        return services;
    }
}
