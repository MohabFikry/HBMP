using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Auth.Authorization;
using Mersal.Authz;
using Mersal.Emr.Domain;
using Mersal.Emr.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Emr.Api;

/// <summary>
/// 25.4 (design 42 §4) — leave, public holidays, clinic closures and ad-hoc clinics.
///
/// <para><b>The gap this fills.</b> <c>provider_availability</c> is a weekly recurring rule and nothing else,
/// so the only way to stop slots appearing was to DELETE the rule — which also erased the normal pattern. A
/// clinic lost its Tuesdays permanently to cover one Tuesday's absence, and somebody had to remember to
/// re-create it.</para>
///
/// <para><b>Impact preview before apply.</b> Changing a roster affects appointments that already exist.
/// <c>?dryRun=true</c> returns the affected booked appointments — count and list — and the real POST refuses
/// unless the caller acknowledges that count. Cancelling a clinic day without seeing whose day it is, is how
/// eight people travel to a closed building.</para>
///
/// <para><b>Affected appointments are FLAGGED, never bulk-cancelled.</b> Same rule as a lapsed licence and
/// for the same reason: a person decides who covers the clinic.</para>
/// </summary>
public static class RosterExceptionEndpoints
{
    public const string AcknowledgementProblemType = "urn:hbmp:impact-acknowledgement-required";

    public static void MapRosterExceptions(this WebApplication app)
    {
        // Reads sit on appointment:read — the desk needs to know why a day is empty, and a closure is not
        // sensitive. Writes need the branch roster authority (25.1); the Network Team's provider:write is
        // deliberately NOT accepted here, because a roster is a clinic's own business.
        var read = app.MapGroup("/api/v1").RequireAuthorization(HbmpPolicies.Scope("appointment:read"));
        var write = app.MapGroup("/api/v1").RequireAuthorization(HbmpPolicies.Scope("branch:roster:write"));

        // GET /roster-exceptions — the exceptions calendar, branch-scoped like every other emr read.
        read.MapGet("/roster-exceptions", async (
            Guid? branchId, Guid? practitionerId, DateOnly? from, DateOnly? to,
            BranchScopeState branch, IHbmpPrincipalAccessor me, EmrDbContext db, TimeProvider clock,
            CancellationToken ct) =>
        {
            // The CLINIC's today, in Cairo — emr owns its own conversion (AppointmentDay does the same) and
            // does not take a dependency on libs/time. Deriving this from the UTC date would show yesterday's
            // calendar for the two to three hours every evening when Cairo has rolled over and UTC has not.
            var lo = from ?? ClinicToday(clock);
            var hi = to ?? lo.AddDays(90);
            if (hi < lo) return Results.Problem(statusCode: 400, title: "to must not precede from", type: "urn:hbmp:invalid-range");

            var q = db.RosterExceptions.AsNoTracking()
                .Where(e => e.DateFrom <= hi && e.DateTo >= lo);
            if (practitionerId is { } p) q = q.Where(e => e.PractitionerId == p);

            // A branch-scoped caller sees their own clinic's exceptions; a clinics manager sees every branch
            // in reach. Practitioner-only exceptions (branch_id NULL) belong to no single clinic, so they are
            // returned to any caller who can reach the practitioner — filtering them out by branch would hide
            // "Dr Hala is on leave" from the clinic she was due to work at.
            var permitted = BranchQueryScope.PermittedFor(BranchModeOf(me), branch.Context, branchId);
            if (permitted is not null)
            {
                var ids = permitted.ToList();
                q = q.Where(e => e.BranchId == null || ids.Contains(e.BranchId.Value));
            }

            var rows = await q.OrderBy(e => e.DateFrom).Take(1000).ToListAsync(ct);
            return Results.Ok(rows.Select(ToView));
        });

        // POST /roster-exceptions[?dryRun=true] — impact preview, then apply.
        write.MapPost("/roster-exceptions", async (
            CreateRosterException req, bool? dryRun,
            BranchScopeState branch, IHbmpPrincipalAccessor me, EmrDbContext db, IAuditClient audit,
            TimeProvider clock, CancellationToken ct) =>
        {
            var tenant = me.Principal?.TenantId;
            if (string.IsNullOrEmpty(tenant)) return Results.Problem(statusCode: 403, title: "no tenant scope on principal");

            if (RosterExceptionRules.Validate(req) is { } invalid)
                return Results.Problem(statusCode: 400, title: invalid.Title, type: invalid.Type, detail: invalid.Detail);

            var kind = Enum.Parse<RosterExceptionKind>(req.Kind);

            // The branch is resolved server-side exactly as a booking's is: a branch-scoped coordinator closes
            // their OWN clinic and may not name another.
            var (targetBranch, denied) = AppointmentEndpointsShared.ResolveBookingBranch(branch, req.BranchId);
            if (denied is not null) return denied;
            if (targetBranch is null && req.PractitionerId is null)
                return Results.Problem(statusCode: 400, title: "an exception must name a branch, a practitioner, or both",
                    type: "urn:hbmp:roster-exception-target-required");

            // THE IMPACT. Computed identically for the preview and the apply — one query, called twice —
            // because a preview that does not match what apply does is worse than no preview: it is a number
            // somebody signed off.
            var affected = await ImpactedAppointmentsAsync(db, kind, targetBranch, req.PractitionerId, req.DateFrom, req.DateTo, clock.GetUtcNow(), ct);

            if (dryRun == true)
            {
                return Results.Ok(new
                {
                    dryRun = true,
                    affectedCount = affected.Count,
                    // Listed, not just counted. "8 appointments" is a number; the list is what lets a
                    // coordinator recognise the two who cannot easily travel again.
                    affected = affected.Select(a => new
                    {
                        a.AppointmentId, a.BeneficiaryId, a.BeneficiaryName,
                        a.ScheduledStart, a.DoctorId, a.BranchId,
                    }),
                });
            }

            // ACKNOWLEDGEMENT. The apply must state the count it expects, and it must match what the apply
            // itself just computed — so a preview taken an hour ago, before two more people booked, does not
            // silently cover them. Re-preview and re-confirm.
            if (req.AcknowledgedImpactCount != affected.Count)
                return Results.Problem(
                    statusCode: 409, title: "impact-acknowledgement-required", type: AcknowledgementProblemType,
                    detail: $"This change affects {affected.Count} booked appointment(s); the request acknowledged " +
                            $"{req.AcknowledgedImpactCount?.ToString() ?? "none"}. Re-run the preview and confirm the current count.",
                    extensions: new Dictionary<string, object?> { ["affectedCount"] = affected.Count });

            var now = clock.GetUtcNow();
            var row = new RosterException
            {
                ExceptionId = Guid.NewGuid(), TenantId = tenant,
                BranchId = targetBranch, PractitionerId = req.PractitionerId,
                DateFrom = req.DateFrom, DateTo = req.DateTo, Kind = kind,
                StartTime = req.StartTime, EndTime = req.EndTime, Reason = req.Reason.Trim(),
                CreatedAt = now, CreatedBy = me.Principal?.Subject, UpdatedAt = now, UpdatedBy = me.Principal?.Subject,
            };

            await using var tx = await db.Database.BeginTransactionAsync(ct);
            db.RosterExceptions.Add(row);

            // FLAG, never cancel — design 42 §7 rule 6. The system does not cancel a refugee's appointment.
            var flagged = 0;
            foreach (var a in affected.Where(a => a.ReassignmentNeededAt == null))
            {
                a.ReassignmentNeededAt = now;
                a.UpdatedAt = now;
                flagged++;
            }
            await db.SaveChangesAsync(ct);

            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "roster_exception", EntityId = row.ExceptionId.ToString(), Action = AuditAction.Create,
                ActorUserId = me.Principal?.Subject, TenantId = tenant, ActorMfa = me.Principal?.MfaSatisfied ?? false,
                DecisionOutcome = $"{kind}", DecisionReasonCode = row.Reason,
            }, ct);
            await tx.CommitAsync(ct);

            return Results.Created($"/api/v1/roster-exceptions/{row.ExceptionId}", new
            {
                exceptionId = row.ExceptionId,
                affectedCount = affected.Count,
                flagged,
                cancelled = 0,   // stated explicitly, because "none cancelled" is the guarantee being made
            });
        });

        // DELETE /roster-exceptions/{id} — soft delete. Removing a closure RESTORES availability; it does not
        // un-flag the appointments that were flagged, because a coordinator may already have rung those
        // patients and moved them.
        write.MapDelete("/roster-exceptions/{id:guid}", async (
            Guid id, BranchScopeState branch, IHbmpPrincipalAccessor me, EmrDbContext db, IAuditClient audit,
            TimeProvider clock, CancellationToken ct) =>
        {
            var row = await db.RosterExceptions.FirstOrDefaultAsync(e => e.ExceptionId == id, ct);
            if (row is null) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");

            if (row.BranchId is { } b)
            {
                var (_, denied) = AppointmentEndpointsShared.ResolveBookingBranch(branch, b);
                if (denied is not null) return denied;
            }

            row.IsDeleted = true;
            row.UpdatedAt = clock.GetUtcNow();
            row.UpdatedBy = me.Principal?.Subject;
            await db.SaveChangesAsync(ct);

            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "roster_exception", EntityId = id.ToString(), Action = AuditAction.SoftDelete,
                ActorUserId = me.Principal?.Subject, TenantId = me.Principal?.TenantId,
                DecisionOutcome = "withdrawn",
            }, ct);
            return Results.Ok(new { exceptionId = id, withdrawn = true });
        });
    }

    /// <summary>
    /// The booked appointments a subtractive exception would strand. ONE implementation, used by the preview
    /// and by the apply, so the number a coordinator acknowledged is the number that gets flagged.
    ///
    /// An AdHocClinic strands nothing — it adds availability — so it returns empty rather than being special
    /// cased at two call sites.
    /// </summary>
    internal static async Task<List<Appointment>> ImpactedAppointmentsAsync(
        EmrDbContext db, RosterExceptionKind kind, Guid? branchId, Guid? practitionerId,
        DateOnly from, DateOnly to, DateTimeOffset now, CancellationToken ct)
    {
        if (kind == RosterExceptionKind.AdHocClinic) return [];

        // Cairo civil days → UTC instants, the same conversion the appointment board uses. Doing this in UTC
        // would clip the first hours of the first day and the last of the last.
        var lo = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), CairoOffsetOn(from)).ToUniversalTime();
        var hi = new DateTimeOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue), CairoOffsetOn(to.AddDays(1))).ToUniversalTime();

        var q = db.Appointments
            .Where(a => a.ScheduledStart >= lo && a.ScheduledStart < hi
                        // FUTURE only, and still live. A past appointment already happened; a cancelled one
                        // has nothing to reassign.
                        && a.ScheduledStart > now
                        && (a.Status == AppointmentStatus.Booked || a.Status == AppointmentStatus.CheckedIn));

        if (branchId is { } b) q = q.Where(a => a.BranchId == b);
        if (practitionerId is { } p) q = q.Where(a => a.DoctorId == p);

        return await q.OrderBy(a => a.ScheduledStart).Take(1000).ToListAsync(ct);
    }

    /// <summary>Every exception overlapping a date range for this branch/practitioner, in the shape the single
    /// availability computation consumes. Practitioner-only and branch-only exceptions both come back — the
    /// domain's <c>AppliesTo</c> decides which of them bite.</summary>
    internal static async Task<IReadOnlyCollection<RosterException>> OverlappingAsync(
        EmrDbContext db, Guid? branchId, Guid? practitionerId, DateOnly from, DateOnly to, CancellationToken ct) =>
        await db.RosterExceptions.AsNoTracking()
            .Where(e => e.DateFrom <= to && e.DateTo >= from
                        && ((branchId != null && e.BranchId == branchId) || (practitionerId != null && e.PractitionerId == practitionerId)))
            .ToListAsync(ct);

    private static DateOnly ClinicToday(TimeProvider clock)
    {
        var now = clock.GetUtcNow();
        // cairo-date: offset-probe (the inner UTC date only selects the offset; the returned date is Cairo's)
        return DateOnly.FromDateTime(now.ToOffset(CairoOffsetOn(DateOnly.FromDateTime(now.UtcDateTime))).DateTime);
    }

    private static ScopeMode BranchModeOf(IHbmpPrincipalAccessor me) =>
        me.Principal is null ? ScopeMode.MemberScoped : BranchScopeModes.ModeFor(me.Principal);

    private static TimeSpan CairoOffsetOn(DateOnly on)
    {
        try
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById(
                OperatingSystem.IsWindows() ? "Egypt Standard Time" : "Africa/Cairo");
            return tz.GetUtcOffset(on.ToDateTime(TimeOnly.MinValue));
        }
        catch (TimeZoneNotFoundException) { return TimeSpan.FromHours(2); }
        catch (InvalidTimeZoneException) { return TimeSpan.FromHours(2); }
    }

    private static object ToView(RosterException e) => new
    {
        e.ExceptionId, e.BranchId, e.PractitionerId,
        e.DateFrom, e.DateTo, Kind = e.Kind.ToString(),
        e.StartTime, e.EndTime, e.Reason,
        WholeDay = e.IsWholeDay, Subtractive = e.IsSubtractive,
        e.CreatedAt, e.CreatedBy,
    };
}

/// <summary>Request validation, in Domain-shaped pure form so the rules are testable without a host.</summary>
public static class RosterExceptionRules
{
    public sealed record Problem(string Title, string Type, string Detail);

    public static Problem? Validate(CreateRosterException req)
    {
        ArgumentNullException.ThrowIfNull(req);

        if (!Enum.TryParse<RosterExceptionKind>(req.Kind, out var kind))
            return new Problem("unknown roster exception kind", "urn:hbmp:invalid-roster-kind",
                $"'{req.Kind}' is not one of Leave, PublicHoliday, ClinicClosed, AdHocClinic.");

        if (req.DateTo < req.DateFrom)
            return new Problem("dateTo must not precede dateFrom", "urn:hbmp:invalid-range", "");

        if (string.IsNullOrWhiteSpace(req.Reason))
            // Mandatory, and not merely for tidiness: a cancelled clinic day is something a patient will ask
            // about, and "no reason recorded" is not an answer anyone can give them.
            return new Problem("a reason is required", "urn:hbmp:reason-required",
                "A closure or absence must record why — it is what a patient asking about their cancelled " +
                "appointment is owed, and what a coordinator reads six weeks later.");

        if (req.Reason.Length > 300)
            return new Problem("reason is too long", "urn:hbmp:reason-too-long", "Maximum 300 characters.");

        // Both times or neither: a half-open window reads as a whole afternoon to one person and a
        // data-entry slip to another.
        if ((req.StartTime is null) != (req.EndTime is null))
            return new Problem("start and end time must be given together", "urn:hbmp:invalid-window",
                "Leave both blank for a whole-day exception.");

        if (req.StartTime is { } s && req.EndTime is { } e && e <= s)
            return new Problem("endTime must be after startTime", "urn:hbmp:invalid-window", "");

        if (kind == RosterExceptionKind.AdHocClinic && req.StartTime is null)
            return new Problem("an ad-hoc clinic needs a time window", "urn:hbmp:invalid-window",
                "An extra clinic has to say when it runs — there is no weekly pattern to inherit it from.");

        return null;
    }
}

/// <summary>25.4 — create a roster exception. <c>AcknowledgedImpactCount</c> is what the caller saw in the
/// dry-run; the apply refuses unless it still matches, so a preview taken before two more people booked does
/// not silently cover them.</summary>
/// <remarks>CA1711 suppressed for the same reason as <see cref="RosterException"/>: "roster exception" is the
/// domain term used by design 42 §4, the table, the route and the UI, and this is a DTO, not an exception
/// type. Renaming only the C# side would leave one concept with two names.</remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Naming", "CA1711:Identifiers should not have incorrect suffix",
    Justification = "Domain term from design 42 §4; matches the table, the route and the UI. Not an exception type.")]
public sealed record CreateRosterException(
    string Kind, DateOnly DateFrom, DateOnly DateTo, string Reason,
    Guid? BranchId = null, Guid? PractitionerId = null,
    TimeOnly? StartTime = null, TimeOnly? EndTime = null,
    int? AcknowledgedImpactCount = null);
