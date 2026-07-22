using Mersal.Policy.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Mersal.Policy.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddPolicyInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        services.AddDbContext<PolicyDbContext>(o =>
            o.UseNpgsql(config.GetConnectionString("Policy") ?? "Host=postgres;Database=hbmp;Username=hbmp;Password=hbmp"));
        return services;
    }
}
