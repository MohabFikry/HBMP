using Mersal.Audit.Client;
using Mersal.Authz;
using Mersal.Interop.Domain.Integration;
using Mersal.Interop.Infrastructure;
using Mersal.Interop.Infrastructure.Integration;

namespace Mersal.Interop.Api;

/// <summary>
/// Integration-governance + inbound-ingest endpoints (13.2), under <c>/interop/integration</c>. Administering the
/// partner registry and enabling an integration are admin actions gated by <see cref="InteropPolicies"/>; the
/// DPIA gate refuses enablement (audited) until a DPIA + data-sharing agreement are recorded. Inbound receipt
/// runs the anti-corruption pipeline — every message is staged + audited; malformed/disabled ones are quarantined.
/// </summary>
public static class IntegrationEndpoints
{
    public static void MapIntegration(this WebApplication app)
    {
        var g = app.MapGroup("/interop/integration");

        // List partners + their DPIA/enablement state (admin read).
        g.MapGet("/partners", async (InteropGate gate, IExternalPartnerRegistry registry, CancellationToken ct) =>
        {
            var denied = await gate.CheckAsync(InteropPolicies.PartnerRead, InteropPolicies.GovernanceResource, "list-partners", ct);
            if (denied is not null) return denied;
            return Results.Ok(await registry.ListAsync(ct));
        });

        // Record a DPIA sign-off + data-sharing agreement reference for a partner (admin write, audited).
        g.MapPost("/partners/{id}/dpia", async (string id, DpiaRecord body, InteropGate gate,
            IExternalPartnerRegistry registry, IAuditClient audit, CancellationToken ct) =>
        {
            var denied = await gate.CheckAsync(InteropPolicies.PartnerManage, InteropPolicies.GovernanceResource, "record-dpia", ct);
            if (denied is not null) return denied;

            var partner = await registry.GetAsync(id, ct);
            if (partner is null) return Results.Problem(statusCode: 404, title: "Unknown partner", detail: $"No partner '{id}'.");
            if (string.IsNullOrWhiteSpace(body?.DataSharingAgreementRef))
                return Results.Problem(statusCode: 422, title: "Missing agreement", detail: "A data-sharing agreement reference is required to record a DPIA sign-off.");

            await registry.UpsertAsync(partner with
            {
                Dpia = DpiaStatus.SignedOff,
                DataSharingAgreementRef = body.DataSharingAgreementRef,
                CrossBorder = body.CrossBorder,
            }, ct);
            await Audit(audit, gate, id, AuditAction.Update, "dpia-recorded", $"dsa={body.DataSharingAgreementRef}", AuditSeverity.Notice, ct);
            return Results.Ok(await registry.GetAsync(id, ct));
        });

        // Attempt to ENABLE a partner — refused (audited) unless the DPIA gate passes.
        g.MapPost("/partners/{id}/enable", async (string id, InteropGate gate,
            IExternalPartnerRegistry registry, IAuditClient audit, CancellationToken ct) =>
        {
            var denied = await gate.CheckAsync(InteropPolicies.PartnerManage, InteropPolicies.GovernanceResource, "enable-partner", ct);
            if (denied is not null) return denied;

            var outcome = await registry.TryEnableAsync(id, ct);
            await Audit(audit, gate, id, AuditAction.StateChange,
                outcome.Allowed ? "enabled" : "enable-refused", outcome.ReasonCode,
                outcome.Allowed ? AuditSeverity.Notice : AuditSeverity.Warning, ct);

            return outcome.Allowed
                ? Results.Ok(await registry.GetAsync(id, ct))
                : Results.Problem(statusCode: 409, title: "Enablement refused", detail: outcome.Message,
                    extensions: new Dictionary<string, object?> { ["code"] = outcome.ReasonCode });
        });

        // Disable a partner (admin write, audited).
        g.MapPost("/partners/{id}/disable", async (string id, InteropGate gate,
            IExternalPartnerRegistry registry, IAuditClient audit, CancellationToken ct) =>
        {
            var denied = await gate.CheckAsync(InteropPolicies.PartnerManage, InteropPolicies.GovernanceResource, "disable-partner", ct);
            if (denied is not null) return denied;
            await registry.DisableAsync(id, ct);
            await Audit(audit, gate, id, AuditAction.StateChange, "disabled", null, AuditSeverity.Notice, ct);
            return Results.Ok(await registry.GetAsync(id, ct));
        });

        // Receive an inbound partner message → anti-corruption pipeline (stage → map/quarantine → internal events).
        g.MapPost("/inbound/{partnerId}", async (string partnerId, InboundBody body, InteropGate gate,
            InboundIngestionService ingestion, IAuditClient audit, CancellationToken ct) =>
        {
            var denied = await gate.CheckAsync(InteropPolicies.InboundIngest, InteropPolicies.GovernanceResource, "inbound-ingest", ct);
            if (denied is not null) return denied;

            var result = await ingestion.IngestAsync(new InboundMessage(partnerId, body?.Format ?? "unknown", body?.Body ?? ""), ct);
            await Audit(audit, gate, partnerId, AuditAction.Create,
                result.IsMapped ? "inbound-mapped" : "inbound-quarantined", result.QuarantineReason,
                result.IsMapped ? AuditSeverity.Info : AuditSeverity.Warning, ct);

            return result.IsMapped
                ? Results.Ok(new { state = "Mapped", events = result.Mapped!.Count })
                : Results.Ok(new { state = "Quarantined", reason = result.QuarantineReason });
        });
    }

    private static ValueTask Audit(IAuditClient audit, InteropGate gate, string partnerId, AuditAction action,
        string outcome, string? reason, AuditSeverity severity, CancellationToken ct)
    {
        var p = gate.Principal;
        return audit.EmitAsync(new AuditEventDraft
        {
            EntityType = "integration-partner",
            EntityId = partnerId,
            Action = action,
            ActorUserId = p?.Subject,
            ActorRole = p is null ? null : string.Join(',', p.Roles),
            TenantId = p?.TenantId,
            SessionId = p?.SessionId,
            ActorMfa = p?.MfaSatisfied ?? false,
            Purpose = "integration-governance",
            DecisionOutcome = outcome,
            DecisionReasonCode = reason,
            Severity = severity,
        }, ct);
    }
}

/// <summary>Body for recording a DPIA sign-off (the data-sharing agreement reference is mandatory).</summary>
public sealed record DpiaRecord(string DataSharingAgreementRef, bool CrossBorder);

/// <summary>Body for an inbound partner message (opaque partner-format payload).</summary>
public sealed record InboundBody(string Format, string Body);
