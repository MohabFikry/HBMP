using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Mersal.Audit.Client;

/// <summary>
/// Produces a deterministic, canonical byte representation of an <see cref="AuditEvent"/>
/// (excluding <see cref="AuditEvent.RecordHash"/>) so the same logical record always hashes
/// identically regardless of runtime/serializer ordering. This is the input to the hash chain.
///
/// Rules: keys sorted ordinal; timestamps ISO-8601 UTC to milliseconds; enums as invariant names;
/// null fields omitted; string arrays joined with a unit separator; the prev_hash IS included
/// (that is what chains records together). Any change to any field changes the hash.
/// </summary>
public static class AuditCanonicalizer
{
    private const char UnitSeparator = '\u001F';

    public static byte[] Canonicalize(AuditEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);

        // Ordered key/value pairs. RecordHash is deliberately excluded; PrevHash is included.
        var fields = new SortedDictionary<string, string?>(StringComparer.Ordinal)
        {
            ["action"] = e.Action.ToString(),
            ["actorMfa"] = e.ActorMfa ? "1" : "0",
            ["actorRole"] = e.ActorRole,
            ["actorUserId"] = e.ActorUserId,
            ["afterState"] = e.AfterState,
            ["auditEventId"] = e.AuditEventId.ToString("N", CultureInfo.InvariantCulture),
            ["beforeState"] = e.BeforeState,
            ["breakGlass"] = e.BreakGlass ? "1" : "0",
            ["correlationId"] = e.CorrelationId,
            ["decisionOutcome"] = e.DecisionOutcome,
            ["decisionPolicyId"] = e.DecisionPolicyId,
            ["decisionReasonCode"] = e.DecisionReasonCode,
            ["entityId"] = e.EntityId,
            ["entityType"] = e.EntityType,
            ["fieldClasses"] = string.Join(UnitSeparator, e.FieldClasses),
            ["occurredAt"] = e.OccurredAt.ToUniversalTime()
                .ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
            ["prevHash"] = e.PrevHash,
            ["providerId"] = e.ProviderId,
            ["purpose"] = e.Purpose,
            ["serviceName"] = e.ServiceName,
            ["sessionId"] = e.SessionId,
            ["severity"] = e.Severity.ToString(),
            ["sourceService"] = e.SourceService,
            ["tenantId"] = e.TenantId,
        };

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            foreach (var (k, v) in fields)
            {
                if (v is null) continue; // nulls omitted so present-vs-absent is unambiguous
                writer.WriteString(k, v);
            }
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    /// <summary>The canonical form as a UTF-8 string (for diagnostics/tests).</summary>
    public static string CanonicalString(AuditEvent e) => Encoding.UTF8.GetString(Canonicalize(e));
}
