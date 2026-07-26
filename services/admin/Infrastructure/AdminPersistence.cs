using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Mersal.Admin.Infrastructure;

/// <summary>DI wiring for the admin-service persistence.</summary>
public static class AdminPersistence
{
    public static IServiceCollection AddAdminInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        services.AddDbContext<AdminDbContext>(o =>
            o.UseNpgsql(config.GetConnectionString("Admin")
                        ?? throw new System.InvalidOperationException("Database connection string is not configured — inject it via ConnectionStrings env/OpenBao; never a baked credential."))
             .UseSnakeCaseNamingConvention());
        return services;
    }
}
