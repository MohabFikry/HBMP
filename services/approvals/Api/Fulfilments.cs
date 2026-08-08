using Mersal.Approvals.Domain;
using Mersal.Approvals.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Approvals.Api;

/// <summary>The wire shape pharmacy-service and orders-service send when something is actually handed over.</summary>
/// <remarks>
/// One message per dispense / consume. It carries the SOURCE (the prescription or order it was delivered
/// against) and the ITEMS (what was delivered), and nothing clinical: a code, a label, a quantity, and — only
/// when the delivered thing differs from the written one — the reason.
/// </remarks>
public sealed record FulfilmentMessage(
    string? TenantId,
    Guid BeneficiaryId,
    Guid? ProviderId,
    Guid? EncounterId,
    /// <summary><c>Prescription</c> or <c>OrderLine</c>. Anything else is refused rather than coerced.</summary>
    string? Source,
    string? SourceRef,
    /// <summary>RX-2026-000410 / ORD-2026-000900 — the reference a human can look up.</summary>
    string? SourceNo,
    string? BenefitCategory,
    string? ActorUserId,
    DateTimeOffset FulfilledAt,
    IReadOnlyList<FulfilmentItemMessage>? Items);

/// <param name="FulfilmentRef">
/// The dispense_event / order_fulfillment id. UNIQUE per tenant — the guard that survives a redelivery
/// arriving under a new broker message id, which the processed-event ledger cannot catch.
/// </param>
public sealed record FulfilmentItemMessage(
    string? FulfilmentRef,
    Guid? SourceLineId,
    string? OrderedCode,
    string? OrderedLabel,
    string? FulfilledCode,
    string? FulfilledLabel,
    decimal Quantity,
    string? SubstitutionReason);

public enum FulfilmentOutcome
{
    /// <summary>A new fulfilment authorization was issued for this prescription / order.</summary>
    Issued,
    /// <summary>An item was appended to the authorization already issued for it.</summary>
    Appended,
    /// <summary>Every item on the message was already recorded — a replay, correctly a no-op.</summary>
    Duplicate,
    /// <summary>The message could not be trusted; see the reason. Dead-lettered, never guessed at.</summary>
    Rejected,
}

public sealed record FulfilmentResult(FulfilmentOutcome Outcome, Guid? AuthorizationId, string? AuthNo, string? Reason);

/// <summary>
/// Turns "this was handed over" into an authorization (ADR-0034).
/// </summary>
/// <remarks>
/// <para><b>One authorization per prescription / order, accumulating items.</b> A second dispense against the
/// same prescription appends to the same authorization rather than issuing another: the prescription is one
/// course of treatment, and the authorization is what was delivered against it. A member collecting a
/// fortnight's medication in two visits has one authorization with two items, not two authorizations that
/// have to be added up by whoever reads them.</para>
/// <para><b>Nothing here can write to the prescription.</b> This service does not own it and has no client
/// for it. That is the structural half of "a substitution affects the authorization only" — the other half
/// is that the item stores <c>OrderedCode</c> and <c>FulfilledCode</c> as separate fields, so recording a
/// substitution cannot overwrite what the prescriber chose.</para>
/// </remarks>
public sealed class FulfilmentIssuer(ApprovalsDbContext db, AuthNoIssuer authNos)
{
    public async Task<FulfilmentResult> IssueAsync(FulfilmentMessage msg, CancellationToken ct = default)
    {
        if (Validate(msg) is { } invalid) return new(FulfilmentOutcome.Rejected, null, null, invalid);

        var source = Enum.Parse<AuthSource>(msg.Source!);
        var items = msg.Items!;

        // One retry, and only one. Two dispenses landing together both find no authorization and both try to
        // create one; the unique index on (tenant, source, source_ref) lets exactly one through. The loser
        // re-reads and appends, which is the correct outcome rather than an error — but a second collision
        // would mean something other than a race, and looping on it would hide it.
        for (var attempt = 0; ; attempt++)
        {
            var auth = await db.Authorizations.Include(a => a.Items)
                .FirstOrDefaultAsync(
                    a => a.Kind == AuthKind.Fulfilment && a.Source == source && a.SourceRef == msg.SourceRef, ct);

            var issuing = auth is null;
            auth ??= await NewAuthorizationAsync(msg, source, ct);

            var known = auth.Items.Select(i => i.FulfilmentRef).ToHashSet(StringComparer.Ordinal);
            var fresh = items.Where(i => known.Add(i.FulfilmentRef!)).ToList();

            if (fresh.Count == 0 && !issuing)
                return new(FulfilmentOutcome.Duplicate, auth.AuthorizationId, auth.AuthNo, null);

            var added = fresh.Select(item => new AuthorizationItem
            {
                ItemId = Guid.NewGuid(),
                AuthorizationId = auth.AuthorizationId,
                SourceLineId = item.SourceLineId,
                FulfilmentRef = item.FulfilmentRef!,
                OrderedCode = item.OrderedCode!,
                OrderedLabel = item.OrderedLabel,
                FulfilledCode = string.IsNullOrWhiteSpace(item.FulfilledCode) ? item.OrderedCode! : item.FulfilledCode!,
                FulfilledLabel = item.FulfilledLabel,
                Quantity = item.Quantity,
                SubstitutionReason = item.SubstitutionReason,
                FulfilledAt = msg.FulfilledAt,
            }).ToList();

            // Added through the DbSet, NOT by appending to auth.Items. A Guid key is store-generated by
            // convention, so an entity reached through a tracked parent's navigation with its key already set
            // is taken to be an existing row: the second dispense against a prescription generated an UPDATE
            // of a row that does not exist, which surfaces as a concurrency failure with nothing concurrent
            // about it. Adding to the set states the intent instead of inferring it.
            db.Items.AddRange(added);

            // The worklist row shows what was authorized. Recomputed from every item rather than appended to,
            // so a second dispense of the same medicine does not list it twice.
            auth.ServiceCodes = Codes.Serialize(
                auth.Items.Concat(added).Select(i => i.FulfilledCode)
                    .Distinct(StringComparer.Ordinal).OrderBy(c => c, StringComparer.Ordinal).ToList());
            auth.UpdatedAt = msg.FulfilledAt;

            try
            {
                await db.SaveChangesAsync(ct);
                return new(issuing ? FulfilmentOutcome.Issued : FulfilmentOutcome.Appended,
                    auth.AuthorizationId, auth.AuthNo, null);
            }
            catch (DbUpdateException) when (attempt == 0)
            {
                foreach (var entry in db.ChangeTracker.Entries().ToList()) entry.State = EntityState.Detached;
            }
        }
    }

    private async Task<Authorization> NewAuthorizationAsync(FulfilmentMessage msg, AuthSource source, CancellationToken ct)
    {
        var auth = new Authorization
        {
            AuthorizationId = Guid.NewGuid(),
            AuthNo = await authNos.NextAsync(msg.FulfilledAt.Year, ct),
            BeneficiaryId = msg.BeneficiaryId,
            Kind = AuthKind.Fulfilment,
            Source = source,
            SourceRef = msg.SourceRef,
            EncounterId = msg.EncounterId,
            RequestingProviderId = msg.ProviderId,
            RequestedScope = Scope(msg),
            Priority = AuthPriority.Routine,
            Status = AuthStatus.Issued,
            // Submitted and decided at the same instant: nothing waited on anybody. Leaving DecidedAt null
            // would make the worklist's elapsed-time column count upward forever on settled work.
            SubmittedAt = msg.FulfilledAt,
            DecidedAt = msg.FulfilledAt,
            TatSeconds = 0,
            CreatedAt = msg.FulfilledAt,
            UpdatedAt = msg.FulfilledAt,
            CreatedBy = msg.ActorUserId,
        };
        db.Authorizations.Add(auth);
        return auth;
    }

    /// <summary>The reference and benefit category, in the same <c>itemRef</c> key the worklist already reads
    /// for a validity-extension row — one projection, one place to look.</summary>
    private static string Scope(FulfilmentMessage msg) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            itemRef = msg.SourceNo,
            benefitCategory = msg.BenefitCategory,
        });

    /// <summary>Null when the message can be trusted; otherwise why it cannot. A refused message is
    /// dead-lettered — an authorization stamped with a guessed tenant or an invented code is worse than
    /// none, because it looks like a record.</summary>
    public static string? Validate(FulfilmentMessage m)
    {
        if (string.IsNullOrWhiteSpace(m.TenantId)) return "no tenant on the envelope";
        if (!Enum.TryParse<AuthSource>(m.Source, out var s) || s is not (AuthSource.Prescription or AuthSource.OrderLine))
            return $"source must be Prescription or OrderLine, not \"{m.Source}\"";
        if (string.IsNullOrWhiteSpace(m.SourceRef)) return "no sourceRef";
        if (m.BeneficiaryId == Guid.Empty) return "no beneficiary";
        if (m.Items is null || m.Items.Count == 0) return "no items";

        foreach (var i in m.Items)
        {
            if (string.IsNullOrWhiteSpace(i.FulfilmentRef)) return "an item has no fulfilmentRef";
            if (string.IsNullOrWhiteSpace(i.OrderedCode)) return "an item has no orderedCode";
            if (i.Quantity <= 0) return "an item has a non-positive quantity";
            // The DB enforces this too. Both, because a substitution with no stated reason is a molecule the
            // prescriber did not choose and no account of why — and a message that got this far without one
            // is a producer bug worth naming rather than a row worth writing.
            var fulfilled = string.IsNullOrWhiteSpace(i.FulfilledCode) ? i.OrderedCode : i.FulfilledCode;
            if (!string.Equals(i.OrderedCode, fulfilled, StringComparison.Ordinal)
                && string.IsNullOrWhiteSpace(i.SubstitutionReason))
                return "a substituted item carries no reason";
        }

        return null;
    }
}
