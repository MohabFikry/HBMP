using Mersal.Audit.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Mersal.Events;

/// <summary>
/// Wires the transactional outbox + relay. With <paramref name="useInMemory"/> (Tier 1 dev / tests)
/// an in-memory outbox is used; otherwise a service registers its EF/DB-backed IOutbox + IOutboxReader
/// and this adds the RabbitMQ publisher + relay. Also binds the audit client's durable outbox
/// (OutboxAuditSink), closing the 0.3 placeholder.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddHbmpEvents(
        this IServiceCollection services, IConfiguration configuration, bool useInMemory = false)
    {
        services.Configure<EventsOptions>(configuration.GetSection(EventsOptions.SectionName));
        services.TryAddSingleton<IProcessedEventStore, InMemoryProcessedEventStore>();
        services.TryAddScoped<IdempotentConsumer>();

        if (useInMemory)
        {
            services.TryAddSingleton<InMemoryOutbox>();
            services.TryAddSingleton<IOutbox>(sp => sp.GetRequiredService<InMemoryOutbox>());
            services.TryAddSingleton<IOutboxReader>(sp => sp.GetRequiredService<InMemoryOutbox>());
        }

        // Route audit emits through the durable outbox (replaces the in-memory audit placeholder).
        services.Replace(ServiceDescriptor.Scoped<IAuditOutbox, OutboxAuditSink>());

        return services;
    }

    /// <summary>Adds the RabbitMQ publisher + relay background service (needs a broker; Tier 2/3 or Docker).</summary>
    public static IServiceCollection AddHbmpOutboxRelay(this IServiceCollection services)
    {
        services.TryAddSingleton<IEventPublisher, RabbitMqEventPublisher>();
        services.AddHostedService<OutboxRelayService>();
        return services;
    }
}
