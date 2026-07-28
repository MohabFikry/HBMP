using Mersal.Audit.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Mersal.Events;

/// <summary>
/// Wires the transactional outbox + relay. The outbox is <b>durable by default</b> (EF-backed
/// <see cref="EfOutbox"/>, C1): a service registers it with <see cref="AddHbmpDurableOutbox{TContext}"/>
/// alongside its DbContext. The process-local <see cref="InMemoryOutbox"/> is used ONLY when
/// <c>Events:UseInMemoryOutbox=true</c> (appsettings.Development.json / tests) or the explicit
/// <paramref name="useInMemory"/> override. Also binds the audit client's durable outbox
/// (OutboxAuditSink), closing the 0.3 placeholder.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddHbmpEvents(
        this IServiceCollection services, IConfiguration configuration, bool? useInMemory = null)
    {
        services.Configure<EventsOptions>(configuration.GetSection(EventsOptions.SectionName));
        services.TryAddSingleton<IProcessedEventStore, InMemoryProcessedEventStore>();
        services.TryAddScoped<IdempotentConsumer>();

        // Default = durable. In-memory only when explicitly opted in (dev/test), never in production.
        var inMemory = useInMemory ?? configuration.GetValue<bool>($"{EventsOptions.SectionName}:UseInMemoryOutbox");
        if (inMemory)
        {
            services.TryAddSingleton<InMemoryOutbox>();
            services.TryAddSingleton<IOutbox>(sp => sp.GetRequiredService<InMemoryOutbox>());
            services.TryAddSingleton<IOutboxReader>(sp => sp.GetRequiredService<InMemoryOutbox>());
        }
        // else: durable — the service must call AddHbmpDurableOutbox<TContext>() to bind EfOutbox to its context.

        // Route audit emits through the transactional outbox (replaces the in-memory audit placeholder).
        services.Replace(ServiceDescriptor.Scoped<IAuditOutbox, OutboxAuditSink>());

        return services;
    }

    /// <summary>
    /// Registers the durable EF outbox bound to the service's <typeparamref name="TContext"/> — the event
    /// row is written through the same context the handler uses. No-op registration is skipped when an
    /// in-memory outbox is already registered (dev/test), so both paths coexist. Pair with
    /// <c>modelBuilder.AddOutbox(schema)</c> in the context's OnModelCreating + the outbox migration.
    /// </summary>
    public static IServiceCollection AddHbmpDurableOutbox<TContext>(this IServiceCollection services)
        where TContext : DbContext
    {
        services.TryAddScoped<IOutbox>(sp => new EfOutbox(sp.GetRequiredService<TContext>()));
        services.TryAddScoped<IOutboxReader>(sp => new EfOutboxReader(
            sp.GetRequiredService<TContext>(),
            sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<EventsOptions>>()));
        return services;
    }

    /// <summary>Adds the RabbitMQ publisher + relay background service (needs a broker; Tier 2/3 or Docker).</summary>
    /// <summary>
    /// The broker publisher on its own, WITHOUT the relay background service.
    ///
    /// For services that publish directly and own no outbox to drain — profile-service composes other
    /// services' data and holds none of its own, so its PHI-read audit goes straight to the broker
    /// (<see cref="DirectAuditSink"/>). Registering the relay there instead was the only way to get a
    /// publisher, and it dragged in a hosted service that threw "No service for type IOutboxReader" on
    /// every pass forever; removing the relay then took the publisher with it and the service would not
    /// start at all. The two concerns are now separable, which is what they always were.
    /// </summary>
    public static IServiceCollection AddHbmpEventPublisher(this IServiceCollection services)
    {
        services.TryAddSingleton<IEventPublisher, RabbitMqEventPublisher>();
        return services;
    }

    public static IServiceCollection AddHbmpOutboxRelay(this IServiceCollection services)
    {
        services.AddHbmpEventPublisher();
        services.AddHostedService<OutboxRelayService>();
        return services;
    }
}
