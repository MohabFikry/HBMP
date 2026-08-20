using Mersal.Amendment;
using Mersal.Events;
using Mersal.Pharmacy.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace Mersal.Pharmacy.Infrastructure;

/// <summary>The outcome of a cancel or amend attempt on a prescription line.</summary>
public enum AmendOutcome
{
    Applied, Replayed, NotFound, LineNotFound, Conflict,
    AlreadyTerminal, RxNotAmendable, Expired, BelowDispensed, InvalidQuantity, NoChange,
    InvalidReason, InvalidIdempotencyKey, IdempotencyKeyReuse,
}

public sealed record AmendReason(string Code, string? Text);

/// <summary>What happened to the line instead — design 46 §2's "line 2 was dispensed at 14:32 by Maadi
/// Pharmacy", not a generic conflict.</summary>
public sealed record AmendConflict(
    string What, DateTimeOffset? When, Guid? DispensingPharmacyId, string? ReasonCode, string? ReasonText);

public sealed record AmendResult(
    AmendOutcome Outcome, Guid? AmendmentId = null, Guid? NewLineId = null, AmendConflict? Conflict = null,
    /// <summary>30.4 — what the amendment did to the authorisation the prescription carried (design 46 §5).</summary>
    AuthorizationImpact Impact = AuthorizationImpact.NotAuthorized)
{
    public static AmendResult Fail(AmendOutcome outcome, AmendConflict? conflict = null) =>
        new(outcome, Conflict: conflict);
}

/// <summary>
/// 30.2 — the guarded cancel/amend transition for a prescription line (design 46 §2). The medication twin of
/// orders' <c>AmendExecutor</c>; read that file's header for the reasoning about the guarded statement and
/// the three reused mechanisms.
///
/// <para><b>Why a twin rather than a shared generic.</b> The same choice, for the same reason, that
/// <see cref="DispenseExecutor"/> and <c>ConsumeExecutor</c> already embody: the two sides differ in table
/// names, column names, status vocabularies, aggregate roll-up and — from Gate 3 — in whether the line owns
/// a refill schedule. A base class parameterised over all of that is harder to read than two explicit files,
/// and the part that MUST NOT diverge (the amendable-scope rule) is shared for real, in libs/amendment.</para>
/// </summary>
public sealed class AmendExecutor(PharmacyDbContext db)
{
    private const string AmendableStates = "'Active','PartiallyDispensed'";

    private const string GuardedTransitionSql =
        """
        UPDATE pharmacy.prescription_line
           SET status                = @status,
               amendment_reason_code = @code,
               amendment_reason_text = @text,
               amended_by            = @actor,
               amended_at            = @at,
               superseded_by_id      = @successor
         WHERE prescription_line_id = @line
           AND status IN (
        """ + AmendableStates + """
        )
           AND xmin::text = @expected
        """;

    public Task<AmendResult> CancelLineAsync(
        Guid rxId, Guid lineId, string idempotencyKey, AmendReason reason, Guid actor, string? actorDisplay,
        DateTimeOffset now,
        Func<Prescription, PrescriptionLine, LineAmendmentRecord, CancellationToken, Task>? insideTransaction = null,
        CancellationToken ct = default) =>
        ApplyAsync(rxId, lineId, idempotencyKey, reason, actor, actorDisplay, now, null, insideTransaction, ct);

    public Task<AmendResult> AmendLineQuantityAsync(
        Guid rxId, Guid lineId, string idempotencyKey, decimal newQuantity, AmendReason reason, Guid actor,
        string? actorDisplay, DateTimeOffset now,
        Func<Prescription, PrescriptionLine, LineAmendmentRecord, CancellationToken, Task>? insideTransaction = null,
        CancellationToken ct = default) =>
        ApplyAsync(rxId, lineId, idempotencyKey, reason, actor, actorDisplay, now, newQuantity, insideTransaction, ct);

    private async Task<AmendResult> ApplyAsync(
        Guid rxId, Guid lineId, string idempotencyKey, AmendReason reason, Guid actor, string? actorDisplay,
        DateTimeOffset now, decimal? newQuantity,
        Func<Prescription, PrescriptionLine, LineAmendmentRecord, CancellationToken, Task>? insideTransaction,
        CancellationToken ct)
    {
        // Validate the CALLER's key — the part before the reserved "::" separator. The whole-prescription
        // cancel composes a per-line key from it, as ConsumeExecutor does; validating the composed key would
        // reject the platform's own convention. See orders' AmendExecutor for the full note.
        if (IdempotencyKeyRules.Validate(CallerPortOf(idempotencyKey)) is not null)
            return AmendResult.Fail(AmendOutcome.InvalidIdempotencyKey);
        if (!AmendmentReasons.IsValid(reason.Code, ReasonScope.Prescription))
            return AmendResult.Fail(AmendOutcome.InvalidReason);

        var rx = await db.Prescriptions.AsNoTracking().Include(p => p.Lines)
            .FirstOrDefaultAsync(p => p.PrescriptionId == rxId, ct);
        if (rx is null) return AmendResult.Fail(AmendOutcome.NotFound);

        var line = rx.Lines.FirstOrDefault(l => l.PrescriptionLineId == lineId);
        if (line is null) return AmendResult.Fail(AmendOutcome.LineNotFound);

        var requestHash = HashRequest(rxId, lineId, newQuantity, reason);

        var prior = await db.LineAmendments.AsNoTracking()
            .FirstOrDefaultAsync(a => a.IdempotencyKey == idempotencyKey, ct);
        if (prior is not null)
            return IdempotencyKeyRules.Matches(prior.RequestHash, requestHash)
                ? new AmendResult(AmendOutcome.Replayed, prior.AmendmentId, prior.NewLineId)
                : AmendResult.Fail(AmendOutcome.IdempotencyKeyReuse);

        var ctx = new AmendContext(
            HeadAmendable: PrescriptionWorkflow.CanAmendLines(rx.Status),
            Expired: rx.Status == RxStatus.Expired || (rx.ExpiresAt is { } e && e <= now));
        var subject = new AmendableLine(lineId, line.IsTerminal, line.QuantityPrescribed, line.QuantityDispensed);

        var error = newQuantity is { } q
            ? LineAmendability.ForAmend(subject, q, ctx)
            : LineAmendability.ForCancel(subject, ctx);
        if (error != AmendabilityError.None)
            return AmendResult.Fail(Map(error), await DescribeAsync(line, ct));

        var amendmentId = Guid.NewGuid();
        var newLineId = newQuantity is null ? (Guid?)null : Guid.NewGuid();
        var toStatus = newQuantity is null ? nameof(RxLineStatus.Cancelled) : nameof(RxLineStatus.Superseded);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            if (newLineId is { } nid)
                await InsertSuccessorAsync(line, nid, newQuantity!.Value, ct);

            // ---- THE GUARDED TRANSITION. The check and the write are one statement. ----
            var affected = await db.Database.ExecuteSqlRawAsync(GuardedTransitionSql,
                [
                    new NpgsqlParameter("status", NpgsqlDbType.Text) { Value = toStatus },
                    new NpgsqlParameter("code", NpgsqlDbType.Varchar) { Value = reason.Code },
                    new NpgsqlParameter("text", NpgsqlDbType.Varchar) { Value = (object?)reason.Text ?? DBNull.Value },
                    new NpgsqlParameter("actor", NpgsqlDbType.Uuid) { Value = actor },
                    new NpgsqlParameter("at", NpgsqlDbType.TimestampTz) { Value = now },
                    new NpgsqlParameter("successor", NpgsqlDbType.Uuid) { Value = (object?)newLineId ?? DBNull.Value },
                    new NpgsqlParameter("line", NpgsqlDbType.Uuid) { Value = lineId },
                    new NpgsqlParameter("expected", NpgsqlDbType.Text) { Value = line.RowVersion.ToString() },
                ], ct);

            if (affected == 0)
            {
                await tx.RollbackAsync(ct);
                db.ChangeTracker.Clear();
                var fresh = await db.PrescriptionLines.AsNoTracking()
                    .FirstAsync(l => l.PrescriptionLineId == lineId, ct);
                return AmendResult.Fail(
                    fresh.IsTerminal ? AmendOutcome.AlreadyTerminal : AmendOutcome.Conflict,
                    await DescribeAsync(fresh, ct));
            }

            // ---- 30.4 THE AUTHORISATION QUESTION (design 46 §5) --------------------------------------
            // Answered locally, from what the prescription already knows — see the note in orders'
            // AmendExecutor for why this is not an HTTP call to approvals. The drug id stands in for the
            // code: it is what a reviewer approved, and a different drug is a different thing however small
            // the quantity.
            var impact = newQuantity is { } amendedQty
                ? AuthorizationScope.Assess(
                    new AmendedScope(line.DrugId.ToString(), amendedQty, line.DurationDays),
                    rx.AuthorizationId is null
                        ? null
                        : new ApprovedScope([line.DrugId.ToString()], line.QuantityPrescribed, line.DurationDays))
                // A cancellation cannot exceed what was approved.
                : AuthorizationImpact.WithinApprovedScope;

            var record = new LineAmendmentRecord
            {
                AmendmentId = amendmentId, TenantId = rx.TenantId, PrescriptionId = rxId,
                PrescriptionLineId = lineId, NewLineId = newLineId,
                Action = newQuantity is null ? "Cancel" : "Amend",
                FromStatus = line.Status.ToString(), ToStatus = toStatus,
                ReasonCode = reason.Code, ReasonText = reason.Text,
                AmendedBy = actor, AmendedByDisplay = actorDisplay, AmendedAt = now,
                IdempotencyKey = idempotencyKey, RequestHash = requestHash,
            };
            db.LineAmendments.Add(record);
            await db.SaveChangesAsync(ct);

            await ApplyAggregateStatusAsync(rxId, ct);

            if (insideTransaction is not null)
            {
                var updated = await db.PrescriptionLines.AsNoTracking()
                    .FirstAsync(l => l.PrescriptionLineId == lineId, ct);
                await insideTransaction(rx, updated, record, ct);
            }
            // BEYOND the approved scope: back to Submitted, which IsDispensable excludes — so the counter
            // refuses the script until a reviewer has looked at what changed. Applied after the roll-up,
            // which recomputes from the lines and would otherwise overwrite it.
            if (impact == AuthorizationImpact.BeyondApprovedScope)
                await db.Prescriptions.Where(p => p.PrescriptionId == rxId
                        && (p.Status == RxStatus.Approved || p.Status == RxStatus.PartiallyDispensed))
                    .ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, RxStatus.Submitted), ct);

            await tx.CommitAsync(ct);
            return new AmendResult(AmendOutcome.Applied, amendmentId, newLineId, Impact: impact);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            await tx.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            var winner = await db.LineAmendments.AsNoTracking()
                .FirstAsync(a => a.IdempotencyKey == idempotencyKey, ct);
            return IdempotencyKeyRules.Matches(winner.RequestHash, requestHash)
                ? new AmendResult(AmendOutcome.Replayed, winner.AmendmentId, winner.NewLineId)
                : AmendResult.Fail(AmendOutcome.IdempotencyKeyReuse);
        }
    }

    /// <summary>
    /// The new version. Every clinical field is COPIED — drug, dose, route, frequency, duration, refills —
    /// because an amendment changes one thing and everything else must survive it byte for byte.
    ///
    /// <para><c>QuantityDispensed</c> carries forward: invariant 2. Without it, a line with 10 of 30 handed
    /// over, amended to 20, would offer 20 MORE rather than 10.</para>
    /// </summary>
    private async Task InsertSuccessorAsync(
        PrescriptionLine original, Guid newLineId, decimal newQuantity, CancellationToken ct)
    {
        var successor = new PrescriptionLine
        {
            PrescriptionLineId = newLineId, TenantId = original.TenantId,
            PrescriptionId = original.PrescriptionId,
            DrugId = original.DrugId, DrugName = original.DrugName, QuantityUnit = original.QuantityUnit,
            // 31.5 — a superseding version is the SAME clinical instruction with one number
            // changed; the dose and frequency it was written from carry across unaltered.
            DoseAmount = original.DoseAmount, TimesPerDay = original.TimesPerDay,
            Dose = original.Dose, Route = original.Route, Frequency = original.Frequency,
            DurationDays = original.DurationDays, RefillsAllowed = original.RefillsAllowed,
            QuantityPrescribed = newQuantity,
            QuantityDispensed = original.QuantityDispensed,
            Status = original.QuantityDispensed >= newQuantity
                ? RxLineStatus.Dispensed
                : original.QuantityDispensed > 0 ? RxLineStatus.PartiallyDispensed : RxLineStatus.Active,
            VersionNo = original.VersionNo + 1,
            SupersedesId = original.PrescriptionLineId,
            RootLineId = original.RootLineId,
        };
        db.PrescriptionLines.Add(successor);
        await db.SaveChangesAsync(ct);
        db.Entry(successor).State = EntityState.Detached;
    }

    private async Task<AmendConflict?> DescribeAsync(PrescriptionLine line, CancellationToken ct)
    {
        if (line.Status is RxLineStatus.Cancelled or RxLineStatus.Superseded)
            return new AmendConflict(line.Status.ToString(), line.AmendedAt, null,
                line.AmendmentReasonCode, line.AmendmentReasonText);

        var last = await db.DispenseEvents.AsNoTracking()
            .Where(d => d.PrescriptionLineId == line.PrescriptionLineId)
            .OrderByDescending(d => d.DispensedAt).FirstOrDefaultAsync(ct);
        return last is null
            ? null
            : new AmendConflict("Dispensed", last.DispensedAt, last.DispensingPharmacyId, null, null);
    }

    /// <summary>The caller-supplied portion of a possibly-composed key: everything before the first <c>::</c>.</summary>
    private static string CallerPortOf(string key)
    {
        var at = key.IndexOf(IdempotencyKeyRules.Separator, StringComparison.Ordinal);
        return at < 0 ? key : key[..at];
    }

    private static string HashRequest(Guid rxId, Guid lineId, decimal? newQuantity, AmendReason reason) =>
        IdempotencyKeyRules.Hash(
            rxId.ToString(), lineId.ToString(),
            newQuantity is { } q ? IdempotencyKeyRules.Amount(q) : "cancel",
            reason.Code, reason.Text ?? "-");

    private async Task ApplyAggregateStatusAsync(Guid rxId, CancellationToken ct)
    {
        const int maxAttempts = 5;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            var fresh = await db.Prescriptions.AsNoTracking().Include(p => p.Lines)
                .FirstAsync(p => p.PrescriptionId == rxId, ct);
            var current = fresh.Status;
            var recomputed = Dispensing.RecomputePrescriptionStatus(fresh);
            if (recomputed == current) return;

            var affected = await db.Prescriptions.Where(p => p.PrescriptionId == rxId && p.Status == current)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, recomputed), ct);
            if (affected == 1) return;
        }
    }

    private static AmendOutcome Map(AmendabilityError error) => error switch
    {
        AmendabilityError.AlreadyTerminal => AmendOutcome.AlreadyTerminal,
        AmendabilityError.OrderNotAmendable => AmendOutcome.RxNotAmendable,
        AmendabilityError.Expired => AmendOutcome.Expired,
        AmendabilityError.BelowConsumed => AmendOutcome.BelowDispensed,
        AmendabilityError.InvalidQuantity => AmendOutcome.InvalidQuantity,
        AmendabilityError.NoChange => AmendOutcome.NoChange,
        _ => AmendOutcome.Conflict,
    };

    private static bool IsUniqueViolation(DbUpdateException ex)
    {
        for (Exception? e = ex.InnerException; e is not null; e = e.InnerException)
            if (e.GetType().GetProperty("SqlState")?.GetValue(e) as string == "23505")
                return true;
        return false;
    }
}
