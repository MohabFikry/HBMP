using Mersal.Amendment;
using Mersal.Events;
using Mersal.Pharmacy.Domain;
using Mersal.Prescribing;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace Mersal.Pharmacy.Infrastructure;

/// <summary>Everything a chronic amendment changes. Dose and times-per-day are NOT here — changing those is
/// a different prescription, not a rescheduling of this one.</summary>
public sealed record ChronicAmendRequest(
    int NewDurationDays,
    int NewFrequencyMonths,
    /// <summary>The prescriber's EXPLICIT confirmation that shortening below the chronic definition should
    /// convert the script to acute. Absent, that case is reported rather than decided (design 46 §4).</summary>
    bool ConvertToAcute = false);

/// <param name="Reallocation">The arithmetic, so the caller can render the preview the doctor confirms
/// against. Present even on a refusal — "75 units over 25 days" is the fact the decision turns on.</param>
public sealed record ChronicAmendResult(
    AmendOutcome Outcome,
    ChronicReallocation? Reallocation = null,
    Guid? AmendmentId = null,
    Guid? NewLineId = null,
    AmendConflict? Conflict = null);

/// <summary>
/// 30.3 — amend a chronic script's duration and frequency (design 46 §4).
///
/// <para><b>What was dispensed is a fact and is never recalculated.</b> The arithmetic lives in
/// <see cref="ChronicAmendment"/>, pure and shared with the composer's preview; this class is the part that
/// touches the database, and the discipline it adds is that <b>nothing is moved and nothing is copied</b>:</para>
///
/// <list type="bullet">
/// <item>the original line keeps its WHOLE schedule, collected windows exactly as they were;</item>
/// <item>its uncollected windows take the terminal <c>Superseded</c> status, so the sweeper stops seeing them
/// and never records a forfeiture for a collection that was not owed;</item>
/// <item>the successor line gets a FRESH schedule, numbered from 1, anchored at the day after the last
/// collected window closes — not at today, which would let a patient who collected on Monday collect again
/// on Wednesday, and not at the original start, which would re-issue windows already served.</item>
/// </list>
///
/// <para>The full picture is the <c>root_line_id</c> chain read in version order. See
/// docs/superpowers/specs/2026-08-07-chronic-amendment-design.md for the two options rejected — reparenting
/// the windows (a silent rewrite, leaving v1 with a hole) and copying the collected ones (a second row
/// claiming the same collection, so "how much did we hand over" gets two answers).</para>
/// </summary>
public sealed class ChronicAmendExecutor(PharmacyDbContext db)
{
    private const string AmendableStates = "'Active','PartiallyDispensed'";

    private const string SupersedeLineSql =
        """
        UPDATE pharmacy.prescription_line
           SET status                = 'Superseded',
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

    public async Task<ChronicAmendResult> AmendScheduleAsync(
        Guid rxId, Guid lineId, string idempotencyKey, ChronicAmendRequest req, AmendReason reason,
        Guid actor, string? actorDisplay, DateTimeOffset now, DateOnly today, int toleranceDays,
        Func<Prescription, PrescriptionLine, LineAmendmentRecord, CancellationToken, Task>? insideTransaction = null,
        CancellationToken ct = default)
    {
        if (IdempotencyKeyRules.Validate(idempotencyKey) is not null)
            return new ChronicAmendResult(AmendOutcome.InvalidIdempotencyKey);
        if (!AmendmentReasons.IsValid(reason.Code, ReasonScope.Prescription))
            return new ChronicAmendResult(AmendOutcome.InvalidReason);

        var rx = await db.Prescriptions.AsNoTracking().Include(p => p.Lines)
            .FirstOrDefaultAsync(p => p.PrescriptionId == rxId, ct);
        if (rx is null) return new ChronicAmendResult(AmendOutcome.NotFound);

        var line = rx.Lines.FirstOrDefault(l => l.PrescriptionLineId == lineId);
        if (line is null) return new ChronicAmendResult(AmendOutcome.LineNotFound);

        var prior = await db.LineAmendments.AsNoTracking()
            .FirstOrDefaultAsync(a => a.IdempotencyKey == idempotencyKey, ct);
        if (prior is not null)
            return new ChronicAmendResult(AmendOutcome.Replayed, null, prior.AmendmentId, prior.NewLineId);

        var ctx = new AmendContext(
            HeadAmendable: PrescriptionWorkflow.CanAmendLines(rx.Status),
            Expired: rx.Status == RxStatus.Expired || (rx.ExpiresAt is { } e && e <= now));
        if (ctx.Expired) return new ChronicAmendResult(AmendOutcome.Expired);
        if (line.IsTerminal) return new ChronicAmendResult(AmendOutcome.AlreadyTerminal);
        if (!ctx.HeadAmendable) return new ChronicAmendResult(AmendOutcome.RxNotAmendable);

        var windows = await db.DispenseWindows.AsNoTracking()
            .Where(w => w.PrescriptionLineId == lineId).OrderBy(w => w.WindowNo).ToListAsync(ct);

        // READ, never recomputed. The collected quantity is what the counter actually handed over, and
        // deriving it from the allocation would quietly substitute what SHOULD have been collected.
        var collected = windows.Where(w => w.DispensedQuantity > 0).ToList();
        var alreadyDispensed = collected.Sum(w => w.DispensedQuantity);

        var plan = ChronicAmendment.Reallocate(
            Request(line, req), alreadyDispensed, collected.Count, req.ConvertToAcute);

        // Three outcomes the caller must handle rather than the executor deciding.
        if (plan.Outcome is AmendmentOutcome.BelowDispensed)
            return new ChronicAmendResult(AmendOutcome.BelowDispensed, plan);
        if (plan.Outcome is AmendmentOutcome.NoLongerChronic or AmendmentOutcome.NotChecked)
            return new ChronicAmendResult(AmendOutcome.NoChange, plan);

        var amendmentId = Guid.NewGuid();
        var newLineId = Guid.NewGuid();
        var acute = plan.Outcome == AmendmentOutcome.ConvertedToAcute;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var successor = new PrescriptionLine
            {
                PrescriptionLineId = newLineId, TenantId = line.TenantId, PrescriptionId = rxId,
                DrugId = line.DrugId, DrugName = line.DrugName,
                Dose = line.Dose, Route = line.Route, Frequency = line.Frequency,
                RefillsAllowed = line.RefillsAllowed,
                DurationDays = req.NewDurationDays,
                QuantityPrescribed = plan.NewTotal,
                // INVARIANT 2 in one line: what was handed over carries forward, so the remaining quantity is
                // the remainder and not the whole new total.
                QuantityDispensed = alreadyDispensed,
                Status = alreadyDispensed >= plan.NewTotal ? RxLineStatus.Dispensed
                    : alreadyDispensed > 0 ? RxLineStatus.PartiallyDispensed : RxLineStatus.Active,
                VersionNo = line.VersionNo + 1,
                SupersedesId = lineId,
                RootLineId = line.RootLineId,
            };
            db.PrescriptionLines.Add(successor);
            await db.SaveChangesAsync(ct);
            db.Entry(successor).State = EntityState.Detached;

            // ---- THE GUARDED TRANSITION, unchanged from the quantity path ----
            var affected = await db.Database.ExecuteSqlRawAsync(SupersedeLineSql,
                [
                    new NpgsqlParameter("code", NpgsqlDbType.Varchar) { Value = reason.Code },
                    new NpgsqlParameter("text", NpgsqlDbType.Varchar) { Value = (object?)reason.Text ?? DBNull.Value },
                    new NpgsqlParameter("actor", NpgsqlDbType.Uuid) { Value = actor },
                    new NpgsqlParameter("at", NpgsqlDbType.TimestampTz) { Value = now },
                    new NpgsqlParameter("successor", NpgsqlDbType.Uuid) { Value = newLineId },
                    new NpgsqlParameter("line", NpgsqlDbType.Uuid) { Value = lineId },
                    new NpgsqlParameter("expected", NpgsqlDbType.Text) { Value = line.RowVersion.ToString() },
                ], ct);

            if (affected == 0)
            {
                await tx.RollbackAsync(ct);
                db.ChangeTracker.Clear();
                var fresh = await db.PrescriptionLines.AsNoTracking()
                    .FirstAsync(l => l.PrescriptionLineId == lineId, ct);
                return new ChronicAmendResult(
                    fresh.IsTerminal ? AmendOutcome.AlreadyTerminal : AmendOutcome.Conflict, plan);
            }

            var record = new LineAmendmentRecord
            {
                AmendmentId = amendmentId, TenantId = rx.TenantId, PrescriptionId = rxId,
                PrescriptionLineId = lineId, NewLineId = newLineId,
                Action = "Amend", FromStatus = line.Status.ToString(), ToStatus = "Superseded",
                ReasonCode = reason.Code,
                // The conversion is RECORDED, because it changes the dispensing pattern the patient was told
                // to expect: they were told to come back monthly, and nothing else would tell them not to.
                ReasonText = acute
                    ? $"{reason.Text} [converted to acute: {req.NewDurationDays} days]".TrimStart()
                    : reason.Text,
                AmendedBy = actor, AmendedByDisplay = actorDisplay, AmendedAt = now,
                IdempotencyKey = idempotencyKey,
            };
            db.LineAmendments.Add(record);
            await db.SaveChangesAsync(ct);

            // ---- The original's UNCOLLECTED windows step aside. Collected ones are untouched. ----
            //
            // Guarded on dispensed_quantity = 0 in the STATEMENT, not only in the C# above: a collection
            // landing between the read and this write must not have its window superseded out from under it.
            await db.DispenseWindows
                .Where(w => w.PrescriptionLineId == lineId && w.DispensedQuantity == 0
                            && (w.Status == "Pending" || w.Status == "Open"))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(w => w.Status, "Superseded")
                    .SetProperty(w => w.SupersededByAmendmentId, amendmentId), ct);

            // ---- The successor's fresh schedule ----
            if (!acute && plan.RemainingWindows.Count > 0)
                await WriteScheduleAsync(rx, successor, plan, collected, req, today, toleranceDays, ct);

            // The HEAD follows the line it belongs to. Not signed content — no trigger covers it — and a
            // prescription still declaring itself Chronic with a 25-day duration would violate
            // ck_prescription_chronic_requires_schedule the moment anything touched it.
            await db.Prescriptions.Where(p => p.PrescriptionId == rxId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(p => p.Kind, acute ? "Acute" : rx.Kind)
                    .SetProperty(p => p.DurationDays, acute ? (int?)null : req.NewDurationDays)
                    .SetProperty(p => p.RefillFrequencyCode, acute ? null : rx.RefillFrequencyCode), ct);

            if (insideTransaction is not null)
            {
                var updated = await db.PrescriptionLines.AsNoTracking()
                    .FirstAsync(l => l.PrescriptionLineId == lineId, ct);
                await insideTransaction(rx, updated, record, ct);
            }
            await tx.CommitAsync(ct);
            return new ChronicAmendResult(AmendOutcome.Applied, plan, amendmentId, newLineId);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            await tx.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            var winner = await db.LineAmendments.AsNoTracking()
                .FirstAsync(a => a.IdempotencyKey == idempotencyKey, ct);
            return new ChronicAmendResult(AmendOutcome.Replayed, plan, winner.AmendmentId, winner.NewLineId);
        }
    }

    /// <summary>
    /// The successor's dated windows.
    ///
    /// <para>THE ANCHOR is the day after the last COLLECTED window closes — not today, which would let a
    /// patient who collected on Monday collect again on Wednesday and defeat the fixed-window rhythm; and not
    /// the original start, which would re-issue windows already served. With nothing collected the anchor is
    /// the original start, because nothing has happened yet to constrain it.</para>
    /// </summary>
    private async Task WriteScheduleAsync(
        Prescription rx, PrescriptionLine successor, ChronicReallocation plan,
        List<PrescriptionDispenseWindow> collected, ChronicAmendRequest req, DateOnly today, int toleranceDays,
        CancellationToken ct)
    {
        var anchor = collected.Count > 0
            ? collected.Max(w => w.ClosesAt).AddDays(1)
            : await OriginalStartAsync(successor.SupersedesId!.Value, rx, today, ct);

        // The remaining duration, so the last window closes with the script rather than a period after its
        // own opening — WindowSchedule's rule, applied to the remainder.
        var remainingDays = req.NewDurationDays - collected.Count * req.NewFrequencyMonths * ChronicAllocation.DaysPerMonth;
        if (remainingDays < 1) remainingDays = plan.RemainingWindows.Count * req.NewFrequencyMonths * ChronicAllocation.DaysPerMonth;

        var schedule = WindowSchedule.Build(
            plan.RemainingWindows, anchor, req.NewFrequencyMonths, remainingDays, toleranceDays);

        foreach (var w in schedule)
            db.DispenseWindows.Add(new PrescriptionDispenseWindow
            {
                WindowId = Guid.NewGuid(), TenantId = rx.TenantId, PrescriptionId = rx.PrescriptionId,
                PrescriptionLineId = successor.PrescriptionLineId,
                WindowNo = w.WindowNo, ScheduledOpenDate = w.ScheduledOpen,
                OpensAt = w.OpensAt, ClosesAt = w.ClosesAt,
                AllocatedQuantity = w.AllocatedQuantity, DispensedQuantity = 0m, Status = "Pending",
            });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Where the original schedule began, for a script with nothing yet collected.
    ///
    /// <para><paramref name="today"/> is the CAIRO business date, passed in from
    /// <c>IBusinessCalendar.Today()</c>. This fell back to a bare wall-clock reading until
    /// <c>NoBareClockArchitectureTests</c> caught it: a UTC-derived date is the wrong DATE every Cairo
    /// evening, so a script amended after 22:00 local would have opened its first window a day early — and
    /// the early tolerance would have hidden it. (The offending expression is deliberately not spelled out
    /// here: that scanner reads comments as code, and naming the pattern would re-flag this file.)</para>
    /// </summary>
    private async Task<DateOnly> OriginalStartAsync(
        Guid originalLineId, Prescription rx, DateOnly today, CancellationToken ct)
    {
        var first = await db.DispenseWindows.AsNoTracking()
            .Where(w => w.PrescriptionLineId == originalLineId)
            .OrderBy(w => w.WindowNo).Select(w => (DateOnly?)w.ScheduledOpenDate).FirstOrDefaultAsync(ct);
        return first ?? rx.ValidFrom ?? today;
    }

    /// <summary>The amended line as an allocation request. Dose and times per day come from the ORIGINAL —
    /// an amendment that changed them would be a different prescription, not a rescheduling of this one.</summary>
    private static AllocationRequest Request(PrescriptionLine line, ChronicAmendRequest req)
    {
        // The original total divided by its own duration recovers the daily rate, which is what the
        // allocation needs and what the line stores indirectly. Expressed as dose-per-administration with one
        // administration a day, because the split is over days either way and inventing a times-per-day the
        // line does not record would be a guess.
        var perDay = line.DurationDays is > 0
            ? line.QuantityPrescribed / line.DurationDays.Value
            : line.QuantityPrescribed;
        return new AllocationRequest(
            DosePerAdministration: perDay, TimesPerDay: 1,
            DurationDays: req.NewDurationDays, FrequencyMonths: req.NewFrequencyMonths,
            IsPackSplittable: true, PackSize: null);
    }

    private static bool IsUniqueViolation(DbUpdateException ex)
    {
        for (Exception? e = ex.InnerException; e is not null; e = e.InnerException)
            if (e.GetType().GetProperty("SqlState")?.GetValue(e) as string == "23505")
                return true;
        return false;
    }
}
