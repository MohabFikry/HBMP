using Mersal.Auth;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Mersal.Audit.Client;

/// <summary>
/// One-call wiring for the audit client. Every service calls
/// <c>services.AddHbmpAuditClient("patient-service")</c>. Registers <see cref="IAuditClient"/> and
/// replaces the auth no-op sink with the durable <see cref="AuditAuthEventSink"/>.
/// The <see cref="IAuditOutbox"/> is provided by libs/events (0.5); until then an in-memory outbox
/// is registered so dev/test builds still emit (never a silent production no-op — see IAuditOutbox).
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddHbmpAuditClient(
        this IServiceCollection services, string serviceName, bool useInMemoryOutbox = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IAuditClientContext>(new AuditClientContext(serviceName));
        services.TryAddScoped<IAuditClient, AuditClient>();

        if (useInMemoryOutbox)
        {
            services.TryAddSingleton<InMemoryAuditOutbox>();
            services.TryAddSingleton<IAuditOutbox>(sp => sp.GetRequiredService<InMemoryAuditOutbox>());
        }
        // else: libs/events registers the durable DB-backed IAuditOutbox (0.5).

        // Replace the libs/auth NullAuthEventSink with the durable bridge (scoped-per-call).
        services.RemoveAll<IAuthEventSink>();
        services.AddSingleton<IAuthEventSink, AuditAuthEventSink>();

        return services;
    }
}
