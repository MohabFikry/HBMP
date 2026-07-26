using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Mersal.Interop.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInteropInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        services.AddDbContext<InteropDbContext>(o =>
            o.UseNpgsql(config.GetConnectionString("Interop")
                        ?? throw new System.InvalidOperationException(
                            "Database connection string is not configured — inject it via ConnectionStrings env/OpenBao; never a baked credential."))
             .UseSnakeCaseNamingConvention());
        services.AddScoped<IFhirDataSource, HttpFhirDataSource>();
        return services;
    }
}
