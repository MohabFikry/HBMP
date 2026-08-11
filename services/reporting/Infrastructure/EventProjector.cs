using System.Globalization;
using Mersal.Reporting.Domain;
using Microsoft.EntityFrameworkCore;
using Mersal.Time;

namespace Mersal.Reporting.Infrastructure;

/// <summary>A canonical domain event handed to the read-model projector: id + type + tenant + a min-necessary,
/// de-identified field bag (coded values, counts, timings — NO PHI). The projection consumer builds it from the raw
/// domain event; the seam endpoint accepts it directly (deferred fanout bus, see README).</summary>
public sealed record ReportingEvent(
    Guid EventId,
    string EventType,
    string TenantId,
    IReadOnlyDictionary<string, string> Fields,
    DateTimeOffset OccurredAt);

/// <summary>Projects domain events into the reporting read-model (phase 8.2). Idempotent: dedupe on event id
/// (redelivery is a no-op). It never writes to source domains and never stores row-level PHI — only coded
/// aggregates, counts, amounts and timings. Financial facts are built from service codes/amounts only (no
/// diagnosis).</summary>
public sealed class EventProjector(
    ReportingDbContext db, TimeProvider clock, IBusinessCalendar calendar, AnalyticsProjector analytics)
{
    /// <summary>Returns true if the event was projected, false if it was a duplicate / unmapped.</summary>
    public async Task<bool> ProjectAsync(ReportingEvent ev, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ev);
        if (await db.ProcessedEvents.AnyAsync(p => p.EventId == ev.EventId, ct))
            return false;

        var period = calendar.DateOf(ev.OccurredAt);   // 18.A3 — the Cairo day the event happened on
        // 19.6b's analytics facts share this dedupe ledger and this transaction deliberately. A second ledger
        // would let one projector accept an event the other rejected, and the two read models would disagree
        // about how many members exist — with no way to tell which was right.
        var handled = Apply(ev, period) | await analytics.ProjectAsync(ev, period, ct);

        db.ProcessedEvents.Add(new ProcessedEvent { EventId = ev.EventId, EventType = ev.EventType, ConsumedAt = clock.GetUtcNow() });
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            if (await db.ProcessedEvents.AsNoTracking().AnyAsync(p => p.EventId == ev.EventId, ct)) return false;
            throw;
        }
        return handled;
    }

    private bool Apply(ReportingEvent ev, DateOnly period)
    {
        switch (ev.EventType)
        {
            case "AuthSubmitted":
                UpsertPending(ev, "Submitted");
                return true;
            case "AuthUnderReview":
                UpsertPending(ev, "UnderReview");
                return true;
            case "AuthInfoRequested":
                // Still in flight (awaiting info) → remains pending, but also a decision fact for rejected/rework KPIs.
                UpsertPending(ev, "InfoRequested");
                AddAuthFact(ev, period, "InfoRequested");
                return true;
            case "AuthApproved":
            case "AuthPartiallyApproved":
            case "AuthEmergencyApproved":
            case "AuthOverridden":
            case "AuthRejected":
                AddAuthFact(ev, period, ev.EventType["Auth".Length..]);
                RemovePending(ev);
                return true;

            case "EncounterCreated":
                AddEncounter(ev, period, EncounterKind.Encounter);
                return true;
            case "AppointmentBooked":
                AddEncounter(ev, period, EncounterKind.Booked);
                return true;
            case "AppointmentAttended":
                AddEncounter(ev, period, EncounterKind.Attended);
                return true;
            case "AppointmentNoShow":
                AddEncounter(ev, period, EncounterKind.NoShow);
                return true;

            case "OrderLineConsumed":
                // modality decides lab vs radiology; the provider dimension is also incremented.
                var dim = string.Equals(Field(ev, "modality"), "Radiology", StringComparison.OrdinalIgnoreCase)
                    ? UtilizationDimension.Radiology : UtilizationDimension.Lab;
                AddUtilization(ev, period, dim, Field(ev, "code"));
                if (Field(ev, "providerId") is { Length: > 0 } prov)
                    AddUtilization(ev, period, UtilizationDimension.Provider, prov, ev.EventId, suffix: "prov");
                return true;
            case "RxDispensed":
                AddUtilization(ev, period, UtilizationDimension.Drug, Field(ev, "atc"));
                AddCode(ev, period, CodeKind.Medication, Field(ev, "atc"), ev.EventId, suffix: "med");
                return true;
            case "DiagnosisRecorded":
                AddCode(ev, period, CodeKind.Diagnosis, Field(ev, "icd"));
                return true;

            /*
             * The cost of one settled service line — what `financial-summary` and the executive dashboard's
             * financial widget are made of.
             *
             * THIS CASE USED TO BE `ServiceValued`, AND NOTHING PUBLISHED IT. It sat in
             * ProjectionFeedTests.KnownUnfed since phase 8.2 with an accurate reason: finance publishes
             * `SettlementApproved`, which is a provider's settlement total, and reporting a settlement as a
             * service valuation would be the wrong grain. That reasoning never stopped being true — what
             * changed is that claims-service began publishing the terminal decision, and a claim LINE at the
             * moment it settles is exactly "this service line was worth this much".
             *
             * So the gap did not close because the objection was wrong. It closed because a different event
             * turned out to be the right grain, which is why the KnownUnfed entry is deleted rather than
             * merely relaxed.
             */
            case "ClaimLineSettled":
                AddFinancial(ev, period);
                return true;

            default:
                return false; // unmapped event — recorded as processed so it isn't reconsidered
        }
    }

    private void AddAuthFact(ReportingEvent ev, DateOnly period, string outcome) =>
        db.AuthorizationFacts.Add(new AuthorizationFact
        {
            EventId = ev.EventId, TenantId = ev.TenantId, AuthNo = Field(ev, "authNo"),
            Priority = Field(ev, "priority", "Routine"), Outcome = outcome, ReviewerId = FieldOrNull(ev, "reviewerId"),
            RejectionReasonCode = FieldOrNull(ev, "rejectionReason"),
            TatSeconds = long.TryParse(Field(ev, "tatSeconds"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var t) ? t : null,
            SlaBreached = Bool(ev, "slaBreached"), Period = period, DecidedAt = ev.OccurredAt,
        });

    private void UpsertPending(ReportingEvent ev, string status)
    {
        if (!Guid.TryParse(Field(ev, "authorizationId"), out var id)) return;
        var row = db.PendingAuthorizations.Local.FirstOrDefault(p => p.AuthorizationId == id)
                  ?? db.PendingAuthorizations.Find(id);
        if (row is null)
        {
            db.PendingAuthorizations.Add(new PendingAuthorization
            {
                AuthorizationId = id, TenantId = ev.TenantId, Priority = Field(ev, "priority", "Routine"),
                Status = status, SubmittedAt = ev.OccurredAt,
                SlaDueAt = DateTimeOffset.TryParse(Field(ev, "slaDueAt"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var s) ? s : null,
            });
        }
        else { row.Status = status; }
    }

    private void RemovePending(ReportingEvent ev)
    {
        if (!Guid.TryParse(Field(ev, "authorizationId"), out var id)) return;
        var row = db.PendingAuthorizations.Find(id);
        if (row is not null) db.PendingAuthorizations.Remove(row);
    }

    private void AddEncounter(ReportingEvent ev, DateOnly period, EncounterKind kind) =>
        db.EncounterFacts.Add(new EncounterFact
        {
            EventId = ev.EventId, TenantId = ev.TenantId, ClinicId = Field(ev, "clinicId", "unknown"),
            Kind = kind.ToString(), Period = period,
        });

    private void AddUtilization(ReportingEvent ev, DateOnly period, UtilizationDimension dim, string code,
        Guid? eventId = null, string? suffix = null) =>
        db.UtilizationFacts.Add(new UtilizationFact
        {
            EventId = Derive(eventId ?? ev.EventId, suffix), TenantId = ev.TenantId, Dimension = dim.ToString(),
            Code = string.IsNullOrEmpty(code) ? "unknown" : code, Period = period,
        });

    private void AddCode(ReportingEvent ev, DateOnly period, CodeKind kind, string code,
        Guid? eventId = null, string? suffix = null) =>
        db.CodeCounts.Add(new CodeCount
        {
            EventId = Derive(eventId ?? ev.EventId, suffix), TenantId = ev.TenantId, Kind = kind.ToString(),
            Code = string.IsNullOrEmpty(code) ? "unknown" : code, Period = period,
        });

    private void AddFinancial(ReportingEvent ev, DateOnly period) =>
        db.FinancialFacts.Add(new FinancialFact
        {
            EventId = ev.EventId, TenantId = ev.TenantId, ServiceLine = Field(ev, "serviceLine", "General"),
            ServiceCode = Field(ev, "serviceCode", "unknown"),
            Amount = decimal.TryParse(Field(ev, "amount"), NumberStyles.Number, CultureInfo.InvariantCulture, out var a) ? a : 0m,
            Period = period,
        });

    // A single event that increments two dimensions (e.g. consume → modality + provider) needs distinct unique
    // EventIds so both survive the per-fact unique index. Derive a deterministic id from the base + a suffix.
    private static Guid Derive(Guid baseId, string? suffix)
    {
        if (string.IsNullOrEmpty(suffix)) return baseId;
        var bytes = baseId.ToByteArray();
        var s = System.Text.Encoding.UTF8.GetBytes(suffix);
        for (var i = 0; i < s.Length && i < bytes.Length; i++) bytes[i] ^= s[i];
        return new Guid(bytes);
    }

    private static string Field(ReportingEvent ev, string key, string fallback = "") =>
        ev.Fields.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : fallback;
    private static string? FieldOrNull(ReportingEvent ev, string key) =>
        ev.Fields.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;
    private static bool Bool(ReportingEvent ev, string key) =>
        ev.Fields.TryGetValue(key, out var v) && bool.TryParse(v, out var b) && b;
}
