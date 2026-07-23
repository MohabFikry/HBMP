using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Mersal.Provider.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddProviderInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        services.AddScoped<RlsContext>();
        services.AddScoped<RlsConnectionInterceptor>();
        services.AddDbContext<ProviderDbContext>((sp, o) =>
            o.UseNpgsql(config.GetConnectionString("Provider") ?? "Host=postgres;Database=hbmp;Username=hbmp;Password=hbmp")
                .UseSnakeCaseNamingConvention()
                .AddInterceptors(sp.GetRequiredService<RlsConnectionInterceptor>()));
        // Default to accept-all; the Api layer replaces this with the masterdata-backed HTTP validator.
        services.TryAddScoped<ICodeValidator, AllowAllCodeValidator>();
        return services;
    }
}
