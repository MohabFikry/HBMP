using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Mersal.Reporting.Infrastructure;

/// <summary>Wires the reporting read-model: DbContext, the event projector, and the aggregate query service.</summary>
public static class DependencyInjection
{
    public static IServiceCollection AddReportingInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        services.AddDbContext<ReportingDbContext>(o =>
            o.UseNpgsql(config.GetConnectionString("Reporting")
                        ?? "Host=postgres;Database=hbmp;Username=hbmp;Password=hbmp")
             .UseSnakeCaseNamingConvention());

        services.AddScoped<EventProjector>();
        services.AddScoped<ReportQueries>();
        return services;
    }
}
