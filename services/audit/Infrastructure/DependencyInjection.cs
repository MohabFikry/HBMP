using Mersal.Audit.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Mersal.Audit.Infrastructure;

/// <summary>Wires the audit-service infrastructure: DB store, WORM, ingest/verify, RabbitMQ consumer.</summary>
public static class DependencyInjection
{
    public static IServiceCollection AddAuditInfrastructure(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<AuditDbContext>(o =>
            o.UseNpgsql(configuration.GetConnectionString("Audit")
                        ?? throw new System.InvalidOperationException("Database connection string is not configured — inject it via ConnectionStrings env/OpenBao; never a baked credential.")));

        services.Configure<WormStoreOptions>(configuration.GetSection(WormStoreOptions.SectionName));
        services.Configure<RabbitMqOptions>(configuration.GetSection(RabbitMqOptions.SectionName));

        services.AddScoped<IAuditEventStore, PostgresAuditEventStore>();
        services.AddSingleton<IWormStore, MinioWormStore>();
        services.AddSingleton<IIntegrityAlerter, LoggingIntegrityAlerter>();

        services.AddScoped<AuditIngestService>();
        services.AddScoped<AuditVerifier>();

        services.AddHostedService<RabbitMqAuditConsumer>();
        services.AddHostedService<VerifierBackgroundService>();

        return services;
    }
}
