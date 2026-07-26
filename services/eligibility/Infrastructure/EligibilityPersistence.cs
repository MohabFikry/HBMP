using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;

namespace Mersal.Eligibility.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// Wires the eligibility read models, the cache (Valkey when <c>Cache:Valkey</c> is configured,
    /// else an in-memory fallback for tests/single-node dev), and the checker + projection updater.
    /// </summary>
    public static IServiceCollection AddEligibilityInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        services.AddDbContext<EligibilityDbContext>(o =>
            o.UseNpgsql(config.GetConnectionString("Eligibility")
                        ?? throw new System.InvalidOperationException("Database connection string is not configured — inject it via ConnectionStrings env/OpenBao; never a baked credential."))
             .UseSnakeCaseNamingConvention());

        var valkey = config["Cache:Valkey"];
        if (!string.IsNullOrWhiteSpace(valkey))
        {
            services.TryAddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(valkey));
            services.AddSingleton<IEligibilityCache, ValkeyEligibilityCache>();
        }
        else
        {
            services.AddSingleton<IEligibilityCache, InMemoryEligibilityCache>();
        }

        services.AddScoped<EligibilityChecker>();
        services.AddScoped<ProjectionUpdater>();

        // Reception search backend. Postgres-over-projections is the default (always in sync, reads the
        // min-necessary projections directly). A search cluster can be swapped in behind IReceptionIndex
        // without touching the endpoint or the min-necessary boundary.
        services.AddScoped<IReceptionIndex, PostgresReceptionIndex>();
        return services;
    }
}
