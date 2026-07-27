using Mersal.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Mersal.Admin.Infrastructure;

/// <summary>DI wiring for the admin-service persistence.</summary>
public static class AdminPersistence
{
    public static IServiceCollection AddAdminInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        // 18.B2 — admin holds the platform's access-control state (role bindings, break-glass grants, session
        // policy). It had RLS DDL and no binder, and connected as superuser. Binder first, connection second.
        services.AddHbmpRls();
        services.AddDbContext<AdminDbContext>((sp, o) =>
            o.UseNpgsql(config.GetConnectionString("Admin")
                        ?? throw new System.InvalidOperationException("Database connection string is not configured — inject it via ConnectionStrings env/OpenBao; never a baked credential."))
             .UseSnakeCaseNamingConvention()
             .AddHbmpRlsInterceptors(sp));
        return services;
    }
}
