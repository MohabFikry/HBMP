using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mersal.Events;

/// <summary>
/// Relays staged outbox messages to the broker: drains a batch, publishes each, marks processed
/// (or failed → retried). This decouples the business transaction from broker availability — the
/// event is durably staged first, delivered at-least-once after.
/// </summary>
public sealed class OutboxRelayService(
    IServiceScopeFactory scopeFactory,
    IEventPublisher publisher,
    IOptions<EventsOptions> options,
    ILogger<OutboxRelayService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opt = options.Value;
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(opt.RelayIntervalMs));
        do
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var reader = scope.ServiceProvider.GetRequiredService<IOutboxReader>();
                var batch = await reader.DequeueBatchAsync(opt.RelayBatchSize, stoppingToken);

                foreach (var msg in batch)
                {
                    try
                    {
                        await publisher.PublishAsync(msg, stoppingToken);
                        await reader.MarkProcessedAsync(msg.EventId, stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        await reader.MarkFailedAsync(msg.EventId, ex.Message, stoppingToken);
                        logger.LogWarning(ex, "outbox relay failed for {EventId}; will retry", msg.EventId);
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "outbox relay pass failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
