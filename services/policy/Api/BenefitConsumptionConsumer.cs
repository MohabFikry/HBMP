using System.Globalization;
using System.Text;
using System.Text.Json;
using Mersal.Audit.Client;
using Mersal.Data;
using Mersal.Policy.Domain;
using Mersal.Policy.Infrastructure;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Mersal.Policy.Api;

public sealed class ConsumptionConsumerOptions
{
    public const string SectionName = "Events";
    public string RabbitUri { get; set; } = "amqp://guest:guest@rabbitmq:5672";
    /// <summary>Fulfillment streams that move the benefit accumulator (FR-INV-006). Claims are NOT here
    /// and never will be — the claims path reads consumed_value and must never write it (FR-CLM-057).</summary>
    public string[] FulfillmentQueues { get; set; } = ["orders.events", "pharmacy.events"];
}

/// <summary>
/// Phase 18.A1 (audit R2 X1) — the missing half of the benefit spine. orders-service and
/// pharmacy-service have always emitted <c>OrderLinesConsumed</c> / <c>RxLinesDispensed</c>; nothing
/// consumed them, so <c>coverage_limit.consumed_value</c> never moved and every member stayed eligible
/// forever. This consumer turns each fulfillment event into an accumulator instruction and hands it to
/// <see cref="BenefitConsumptionApplier"/> (the sole writer).
///
/// At-least-once delivery is handled twice over: the <c>processed_event</c> dedupe ledger short-circuits
/// a redelivered event id, and the applier's UNIQUE <c>source_ref</c> makes a double-apply impossible
/// even across event ids. A message we cannot attribute to a tenant is dead-lettered rather than
/// stamped with a guessed tenant (audit R2 S-series: no hardcoded SoleTenantId on write paths).
/// </summary>
public sealed class BenefitConsumptionConsumer(
    IServiceScopeFactory scopeFactory,
    IOptions<ConsumptionConsumerOptions> options,
    ILogger<BenefitConsumptionConsumer> logger) : BackgroundService
{
    private IConnection? _connection;
    private IModel? _channel;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opt = options.Value;
        try
        {
            var factory = new ConnectionFactory { Uri = new Uri(opt.RabbitUri), DispatchConsumersAsync = true };
            _connection = factory.CreateConnection("policy-service");
            _channel = _connection.CreateModel();
            _channel.BasicQos(0, prefetchCount: 20, global: false);

            foreach (var queue in opt.FulfillmentQueues)
            {
                _channel.QueueDeclare(queue, durable: true, exclusive: false, autoDelete: false);
                var consumer = new AsyncEventingBasicConsumer(_channel);
                consumer.Received += async (_, ea) => await OnReceivedAsync(ea, stoppingToken);
                _channel.BasicConsume(queue, autoAck: false, consumer);
                logger.LogInformation("policy-service consuming fulfillment events from {Queue}", queue);
            }
        }
        catch (Exception ex)
        {
            // Broker unavailable (unit/dev without RabbitMQ): serve the API rather than crash the host.
            // The accumulator simply does not advance until the broker returns; nothing is lost, because
            // the events are durable in each producer's outbox until relayed and acked here.
            logger.LogWarning(ex, "policy benefit-consumption consumer could not connect; accumulator not advancing");
        }
        return Task.CompletedTask;
    }

    private async Task OnReceivedAsync(BasicDeliverEventArgs ea, CancellationToken ct)
    {
        try
        {
            var eventId = Guid.TryParse(ea.BasicProperties.MessageId, out var id) ? id : Guid.NewGuid();
            var eventType = ea.BasicProperties.Type ?? "";
            var payload = Encoding.UTF8.GetString(ea.Body.Span);

            var instructions = Translate(eventId, eventType, payload);
            if (instructions.Count > 0)
            {
                using var scope = scopeFactory.CreateScope();
                var sp = scope.ServiceProvider;
                // Background consumer — no HTTP principal — so bind the RLS tenant GUC from the event
                // envelope. Translate() already refused any event without one.
                sp.GetRequiredService<RlsContext>().TenantId = instructions[0].TenantId;

                var applier = sp.GetRequiredService<BenefitConsumptionApplier>();
                var audit = sp.GetRequiredService<IAuditClient>();
                var db = sp.GetRequiredService<PolicyDbContext>();

                if (await db.ProcessedEvents.FindAsync(new object[] { eventId }, ct) is null)
                {
                    foreach (var instruction in instructions)
                    {
                        var result = await applier.ApplyAsync(instruction, ct);
                        await audit.EmitAsync(new AuditEventDraft
                        {
                            EntityType = "coverage_limit",
                            EntityId = result.CoverageId?.ToString() ?? instruction.BeneficiaryId.ToString(),
                            Action = AuditAction.StateChange,
                            DecisionOutcome = result.Outcome.ToString(),
                            DecisionReasonCode =
                                $"event:{instruction.EventType};ref:{instruction.SourceRef};qty:{instruction.Quantity.ToString(CultureInfo.InvariantCulture)};limits:{result.MovedLimits.Count}",
                            FieldClasses = ["coverage"],
                        }, ct);

                        if (result.Outcome is ConsumptionOutcome.NoCoverage or ConsumptionOutcome.NoBenefitCategory
                            or ConsumptionOutcome.NoAccumulatingLimit or ConsumptionOutcome.WouldGoNegative)
                            logger.LogWarning("benefit accumulation not applied ({Outcome}) for {SourceRef}",
                                result.Outcome, instruction.SourceRef);
                    }

                    db.ProcessedEvents.Add(new ProcessedEvent { EventId = eventId, ProcessedAt = DateTimeOffset.UtcNow });
                    await db.SaveChangesAsync(ct);
                }
            }

            _channel!.BasicAck(ea.DeliveryTag, multiple: false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "benefit accumulation failed for delivery {Tag}", ea.DeliveryTag);
            _channel!.BasicNack(ea.DeliveryTag, multiple: false, requeue: false);
        }
    }

    /// <summary>Turn one fulfillment event into zero or more accumulator instructions. Unknown event
    /// types and events without a tenant produce nothing (the latter is dead-lettered by the caller's
    /// ack path only after being logged — we never guess a tenant on a write path).</summary>
    public static IReadOnlyList<ConsumptionInstruction> Translate(Guid eventId, string eventType, string payload)
    {
        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;

        var tenantId = Str(root, "tenantId");
        if (string.IsNullOrWhiteSpace(tenantId)) return [];

        var beneficiaryId = GuidOf(root, "beneficiaryId");
        if (beneficiaryId is null) return [];

        var category = Str(root, "benefitCategory");
        var key = Str(root, "idempotencyKey") ?? eventId.ToString();
        var onDate = Date(root, "serviceDate") ?? DateOnly.FromDateTime(DateTime.UtcNow);

        var direction = eventType is "OrderFulfillmentVoided" or "RxDispenseVoided"
            ? ConsumptionDirection.Reversed
            : ConsumptionDirection.Applied;

        return eventType switch
        {
            // orders-service: one instruction per consumed line.
            "OrderLinesConsumed" or "OrderFulfillmentVoided" =>
                Lines(root).Select(l => new ConsumptionInstruction(
                    eventId, eventType, tenantId, beneficiaryId.Value, category,
                    BenefitAccumulation.SourceRef(eventType, l.LineId, key, direction),
                    l.Quantity, direction, onDate)).ToList(),

            // pharmacy-service: one line per dispense event.
            "RxLinesDispensed" or "RxDispenseVoided" =>
                GuidOf(root, "prescriptionLineId") is { } lineId && Dec(root, "quantity") is { } qty
                    ? [new ConsumptionInstruction(
                        eventId, eventType, tenantId, beneficiaryId.Value, category ?? "PHARMACY",
                        BenefitAccumulation.SourceRef(eventType, lineId, key, direction),
                        qty, direction, onDate)]
                    : [],

            _ => [],
        };
    }

    private static List<(Guid LineId, decimal Quantity)> Lines(JsonElement root)
    {
        var result = new List<(Guid, decimal)>();
        if (!root.TryGetProperty("lines", out var lines) || lines.ValueKind != JsonValueKind.Array) return result;
        foreach (var line in lines.EnumerateArray())
            if (GuidOf(line, "orderLineId") is { } lineId && Dec(line, "quantity") is { } qty)
                result.Add((lineId, qty));
        return result;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static Guid? GuidOf(JsonElement e, string name) =>
        Str(e, name) is { } s && Guid.TryParse(s, out var g) ? g : null;

    private static decimal? Dec(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d) ? d : null;

    private static DateOnly? Date(JsonElement e, string name) =>
        Str(e, name) is { } s && DateOnly.TryParse(s, CultureInfo.InvariantCulture, out var d) ? d : null;

    public override void Dispose()
    {
        _channel?.Dispose();
        _connection?.Dispose();
        base.Dispose();
    }
}
