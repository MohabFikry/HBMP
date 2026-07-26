using Mersal.Claims.Domain;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Claims.Infrastructure;

/// <summary>Outcome of an auto-derive intake attempt. <c>Created</c> made a new payable line; <c>Replayed</c> is an
/// idempotent no-op (the event was already consumed); <c>Duplicate</c> means a live payable line already references
/// this fulfillment record → DUPLICATE_CLAIM (no second payable line ever exists).</summary>
public enum IntakeOutcome { Created, Replayed, Duplicate }

public sealed record IntakeResult(IntakeOutcome Outcome, Claim? Claim, ClaimLine? Line, bool NewClaim = false)
{
    public static IntakeResult Duplicate() => new(IntakeOutcome.Duplicate, null, null);
}

/// <summary>The auto-derived origination channel (10b.1). Consumes a min-necessary <see cref="ClaimIntakeEvent"/>
/// (built from <c>OrderLinesConsumed</c> / <c>RxLinesDispensed</c>) and creates exactly one priced <c>claim_line</c>
/// anchored to the fulfillment reference, attached to an open Draft claim for that (provider, beneficiary, day).
/// Three guarantees combine, all required:
/// <list type="number">
/// <item>IDEMPOTENT — dedupe on event id (processed_event); a redelivered event is a no-op returning the prior line;</item>
/// <item>NO DOUBLE-BILLING — the partial unique index <c>ux_claim_line_fulfillment</c> is the final guarantee; a
/// second live line for the same fulfillment_ref fails at the DB and maps to <c>Duplicate</c>;</item>
/// <item>NEVER A GUESSED PRICE — the tariff is resolved from the provider contract; no tariff ⇒ NO_TARIFF + manual
/// pricing, contract_price stays null.</item>
/// </list>
/// The endpoint and the concurrency/idempotency tests exercise this SAME method.</summary>
public sealed class ClaimIntakeExecutor(
    ClaimsDbContext db, ClaimNoIssuer claimNo, IContractTariffProvider tariffs, TimeProvider clock)
{
    public async Task<IntakeResult> IngestAsync(
        ClaimIntakeEvent ev, string? bearerToken,
        Func<Claim, ClaimLine, bool, CancellationToken, Task>? insideTransaction = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ev);

        // (1) Idempotent dedupe on event id — a redelivered event creates nothing new.
        if (await db.ProcessedEvents.AsNoTracking().AnyAsync(p => p.EventId == ev.EventId, ct))
            return await ReplayAsync(ev, ct);

        // (2) Price from the provider's contract tariff for the code on the service date (HTTP, outside the tx).
        var tariff = await tariffs.ResolveAsync(ev.ProviderId, ev.CodeSystem, ev.Code, ev.ServiceDate, bearerToken, ct);
        var (price, recommendation, reasons) = AutoDerivePricing.Price(tariff, ev.Quantity); // 18.A2 (X8): extended, not unit
        var now = clock.GetUtcNow();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            // (3) Attach to the open Draft AutoDerived claim for this (tenant, provider, beneficiary, day), else create.
            var claim = await db.Claims.FirstOrDefaultAsync(c =>
                c.TenantId == ev.TenantId && c.Origin == ClaimOrigin.AutoDerived &&
                c.ProviderId == ev.ProviderId && c.BeneficiaryId == ev.BeneficiaryId &&
                c.ServiceDateFrom == ev.ServiceDate && c.Status == ClaimStatus.Draft, ct);

            var newClaim = claim is null;
            if (claim is null)
            {
                claim = new Claim
                {
                    ClaimId = Guid.NewGuid(),
                    ClaimNo = await claimNo.NextAsync(ev.ServiceDate.Year, ct),
                    Origin = ClaimOrigin.AutoDerived,
                    BeneficiaryId = ev.BeneficiaryId,
                    ProviderId = ev.ProviderId,
                    ProviderLocationId = ev.ProviderLocationId,
                    AuthorizationId = ev.AuthorizationId,
                    TenantId = ev.TenantId,
                    ServiceDateFrom = ev.ServiceDate,
                    ServiceDateTo = ev.ServiceDate,
                    CurrencyCode = ev.CurrencyCode,
                    Status = ClaimStatus.Draft,
                    CreatedAt = now,
                    CreatedBy = "claims-service",
                };
                db.Claims.Add(claim);
            }

            var line = new ClaimLine
            {
                ClaimLineId = Guid.NewGuid(),
                ClaimId = claim.ClaimId,
                FulfillmentRef = ev.FulfillmentRef,
                FulfillmentType = ev.FulfillmentType,
                CodeSystem = ev.CodeSystem,
                Code = ev.Code,
                Description = ev.Description,
                Quantity = ev.Quantity,
                BilledAmount = ev.BilledAmount,
                ContractPrice = price,
                Status = ClaimLineStatus.Pending,
                SystemRecommendation = recommendation,
                ReasonCodes = [.. reasons],
                RuleVersion = AutoDerivePricing.RuleVersion,
                AuthorizationId = ev.AuthorizationId,
            };
            db.ClaimLines.Add(line);

            claim.ClaimedAmount += ev.BilledAmount;
            claim.PricedAmount = (claim.PricedAmount ?? 0) + (price ?? 0);

            db.ProcessedEvents.Add(new ProcessedEvent { EventId = ev.EventId, EventType = ev.EventType, ConsumedAt = now });

            await db.SaveChangesAsync(ct);
            if (insideTransaction is not null) await insideTransaction(claim, line, newClaim, ct);
            await tx.CommitAsync(ct);
            return new IntakeResult(IntakeOutcome.Created, claim, line, newClaim);
        }
        catch (DbUpdateException ex) when (ConstraintOf(ex) == "ux_claim_line_fulfillment")
        {
            // A concurrent / later attempt for the SAME fulfillment_ref lost the race → no second payable line.
            await tx.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            return IntakeResult.Duplicate();
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // processed_event PK race (same event delivered twice concurrently) → idempotent replay.
            await tx.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            return await ReplayAsync(ev, ct);
        }
    }

    private async Task<IntakeResult> ReplayAsync(ClaimIntakeEvent ev, CancellationToken ct)
    {
        var line = await db.ClaimLines.AsNoTracking()
            .FirstOrDefaultAsync(l => l.FulfillmentRef == ev.FulfillmentRef && l.Status != ClaimLineStatus.Void, ct);
        var claim = line is null ? null
            : await db.Claims.AsNoTracking().FirstOrDefaultAsync(c => c.ClaimId == line.ClaimId, ct);
        return new IntakeResult(IntakeOutcome.Replayed, claim, line);
    }

    /// <summary>The Postgres constraint name behind a unique/PK violation (SQLSTATE 23505), read via reflection to
    /// avoid a hard Npgsql compile dependency in this layer. Null when the exception is not a unique violation.</summary>
    private static string? ConstraintOf(DbUpdateException ex)
    {
        for (Exception? e = ex.InnerException; e is not null; e = e.InnerException)
            if (e.GetType().GetProperty("SqlState")?.GetValue(e) as string == "23505")
                return e.GetType().GetProperty("ConstraintName")?.GetValue(e) as string ?? "";
        return null;
    }

    private static bool IsUniqueViolation(DbUpdateException ex) => ConstraintOf(ex) is not null;
}
