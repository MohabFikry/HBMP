using System.Text.Json;

namespace Mersal.Events;

/// <summary>
/// Phase 18.B2 (audit R2 S-series) — read the owning tenant out of a domain-event envelope.
///
/// Background consumers have no HTTP principal, so they must bind the RLS tenant GUC themselves before the
/// projection write. Until now eligibility-service did that by stamping a hardcoded <c>SoleTenantId</c>
/// constant. That is a write path choosing its own authorization context: correct only for as long as the
/// platform has exactly one tenant, and silently wrong — not failing, WRONG — on the day it has two, because
/// every tenant's events would land in tenant one's projections and every eligibility check would answer
/// from the wrong member's coverage. The compile still succeeds and the tests still pass; only the answers
/// change. A guessed tenant is worse than a refused message.
///
/// So: the tenant comes from the envelope, and a message without one is refused. Publishers on the RLS side
/// of the platform already carry <c>tenantId</c> (18.A1 extended orders/pharmacy to match).
/// </summary>
public static class EventTenant
{
    /// <summary>Property names accepted on the envelope root, in precedence order. camelCase is the
    /// platform's wire convention; the others exist because a hand-rolled publisher is a matter of time.</summary>
    private static readonly string[] Names = ["tenantId", "tenant_id", "TenantId"];

    /// <summary>The tenant on the envelope, or null when the payload is not an object, carries no tenant
    /// property, or carries an empty one. Never throws on malformed JSON — an unparseable body is simply
    /// untenanted, and the caller dead-letters it like any other unattributable message.</summary>
    public static string? Of(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return null;
        try
        {
            using var doc = JsonDocument.Parse(payload);
            return Of(doc.RootElement);
        }
        catch (JsonException) { return null; }
    }

    /// <inheritdoc cref="Of(string)"/>
    public static string? Of(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in Names)
        {
            if (!root.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind != JsonValueKind.String) continue;
            var tenant = value.GetString();
            if (!string.IsNullOrWhiteSpace(tenant)) return tenant;
        }
        return null;
    }
}
