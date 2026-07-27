using System.Diagnostics;
using System.Text.Json;
using Mersal.Audit.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Mersal.Events;

/// <summary>
/// An <see cref="IAuditOutbox"/> for a service that has <b>no database</b> — it publishes each audit event
/// straight to the broker instead of staging it in a transactional outbox.
///
/// <para><b>Why this is not a weakening of the outbox rule.</b> The transactional outbox exists to make an
/// audit write atomic with a state change: the event row and the business row commit or roll back together, so
/// a state change can never exist without its audit record. A read-only composition service
/// (<c>profile-service</c>, phase 20) has no state change and no transaction — there is nothing for the audit
/// emit to be atomic WITH. Staging it in a database would mean giving that service a database purely to hold
/// audit rows, which is precisely the "owns no data" invariant (design 39 §7.4) inverted.</para>
///
/// <para><b>What it costs, stated plainly.</b> A publish that fails is a PHI-read audit event that was not
/// recorded, where the outbox would have retried it. That is why this sink <b>never swallows a failure
/// silently</b>: it logs at Critical and rethrows, so the request fails rather than the read proceeding
/// unaudited. An unaudited PHI read is worse than a failed one — design 39 §7.5 makes "every open is audited"
/// an invariant, and a best-effort audit is not an audit.</para>
/// </summary>
public sealed class DirectAuditSink(IEventPublisher publisher, ILogger<DirectAuditSink> logger) : IAuditOutbox
{
    public const string Destination = OutboxAuditSink.Destination;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async ValueTask EnqueueAsync(AuditEvent auditEvent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        var message = new OutboxMessage
        {
            EventType = "AuditEventRecorded",
            Destination = Destination,
            Payload = JsonSerializer.Serialize(auditEvent, Json),
            CorrelationId = Activity.Current?.TraceId.ToString(),
        };

        try
        {
            await publisher.PublishAsync(message, ct);
        }
        catch (Exception ex)
        {
            // Loud, and then fatal to the request. Silently continuing would serve a patient profile that no
            // access review will ever be able to attribute — the one outcome this whole phase exists to prevent.
            logger.LogCritical(ex,
                "Audit event {EventType} for {EntityType}/{EntityId} could not be published. The request is " +
                "being failed rather than completing unaudited.",
                message.EventType, auditEvent.EntityType, auditEvent.EntityId);
            throw;
        }
    }
}

public static class DirectAuditSinkExtensions
{
    /// <summary>
    /// Register the broker-direct audit sink for a service with no DbContext. Pair with
    /// <c>AddHbmpEvents</c> + the RabbitMQ publisher; do NOT combine with <c>AddHbmpDurableOutbox</c> — a
    /// service that has a database should stage its audit in the same transaction as its writes.
    /// </summary>
    public static IServiceCollection AddHbmpDirectAuditSink(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.Replace(ServiceDescriptor.Scoped<IAuditOutbox, DirectAuditSink>());
        return services;
    }
}
