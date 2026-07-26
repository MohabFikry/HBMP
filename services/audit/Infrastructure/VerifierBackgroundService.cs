using Mersal.Audit.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Mersal.Audit.Infrastructure;

/// <summary>
/// Scheduled integrity job: periodically re-computes the hash chain for the current (and previous)
/// monthly partition and raises a critical alert on any break (19-audit-strategy.md §4).
/// </summary>
public sealed class VerifierBackgroundService(
    IServiceScopeFactory scopeFactory,
    TimeProvider clock,
    ILogger<VerifierBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                var now = clock.GetUtcNow();
                foreach (var key in new[] { AuditPartition.KeyFor(now), AuditPartition.KeyFor(now.AddMonths(-1)) })
                {
                    using var scope = scopeFactory.CreateScope();
                    var verifier = scope.ServiceProvider.GetRequiredService<AuditVerifier>();
                    var result = await verifier.VerifyPartitionAsync(key, stoppingToken);
                    if (result.IsIntact)
                    {
                        logger.LogInformation("audit chain intact for partition {Partition}", key);
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "audit chain verification pass failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
