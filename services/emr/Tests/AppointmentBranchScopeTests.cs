using FluentAssertions;
using Mersal.Auth;
using Mersal.Authz;
using Mersal.Emr.Api;
using Mersal.Emr.Domain;
using Mersal.Emr.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Emr.Tests;

/// <summary>
/// The branch seam for appointments (design 37 §3, phase 14.4). Two defects lived here:
///
/// 1. <c>POST /appointments</c> never copied a branch onto the row it created, so every appointment was written
///    with <c>branch_id NULL</c> — and the reception board filters on precisely that column. The feature was
///    fully built at both ends and could not show a single booking anyone made.
/// 2. The READ endpoints refused cross-branch rows; the WRITE transitions did not. Knowing an id was enough to
///    check in, no-show or cancel another branch's appointment.
/// </summary>
public class AppointmentBranchScopeTests
{
    private static BranchScopeState Scoped(Guid active) => new()
    {
        Context = new BranchContext(active, new HashSet<Guid> { active }, IsBranchUnrestricted: false),
    };

    private static BranchScopeState Unrestricted() => new() { Context = BranchContext.Unrestricted };

    // ── ResolveBookingBranch: who decides the branch a booking lands in ──────────────────────────────────

    [Fact]
    public void A_branch_scoped_desk_books_into_its_OWN_branch_even_when_the_body_names_none()
    {
        var maadi = Guid.NewGuid();
        var (branch, denied) = AppointmentEndpointsShared.ResolveBookingBranch(Scoped(maadi), requested: null);

        denied.Should().BeNull();
        // This is the bug that made the board empty: the branch must be stamped even when nobody asked for one.
        branch.Should().Be(maadi);
    }

    [Fact]
    public void A_branch_scoped_desk_naming_ANOTHER_branch_is_refused_not_silently_rewritten()
    {
        var maadi = Guid.NewGuid();
        var dokki = Guid.NewGuid();
        var (branch, denied) = AppointmentEndpointsShared.ResolveBookingBranch(Scoped(maadi), requested: dokki);

        branch.Should().BeNull();
        denied.Should().NotBeNull("silently moving the booking to Maadi would strand a patient who was told Dokki");
        StatusOf(denied!).Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public void Naming_your_own_active_branch_explicitly_is_allowed()
    {
        var maadi = Guid.NewGuid();
        var (branch, denied) = AppointmentEndpointsShared.ResolveBookingBranch(Scoped(maadi), requested: maadi);
        denied.Should().BeNull();
        branch.Should().Be(maadi);
    }

    [Fact]
    public void A_branch_unrestricted_caller_books_into_the_branch_it_names()
    {
        // The call centre's whole purpose is a wider reach than one desk (US-015.3).
        var dokki = Guid.NewGuid();
        var (branch, denied) = AppointmentEndpointsShared.ResolveBookingBranch(Unrestricted(), requested: dokki);
        denied.Should().BeNull();
        branch.Should().Be(dokki);
    }

    [Fact]
    public void A_branch_unrestricted_caller_naming_no_branch_leaves_it_unset()
    {
        // An external-provider location has no Mersal branch; that must stay expressible.
        var (branch, denied) = AppointmentEndpointsShared.ResolveBookingBranch(Unrestricted(), requested: null);
        denied.Should().BeNull();
        branch.Should().BeNull();
    }

    // ── DenyIfOutsideBranchAsync: who may WRITE to an existing appointment ───────────────────────────────

    private static readonly string? Db = Environment.GetEnvironmentVariable("EMR_TEST_DB");
    private static EmrDbContext Ctx() =>
        new(new DbContextOptionsBuilder<EmrDbContext>().UseNpgsql(Db).UseSnakeCaseNamingConvention().Options);

    [SkippableFact]
    public async Task Writing_to_another_branchs_appointment_is_denied()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var provider = Guid.NewGuid();
        var maadi = Guid.NewGuid();
        var dokki = Guid.NewGuid();
        try
        {
            var dokkiAppt = await Seed(provider, dokki);
            await using var db = Ctx();

            // A Maadi desk reaching a Dokki appointment by id.
            var denied = await AppointmentEndpointsShared.DenyIfOutsideBranchAsync(dokkiAppt, Scoped(maadi), db, default);
            denied.Should().NotBeNull();
            StatusOf(denied!).Should().Be(StatusCodes.Status403Forbidden);
        }
        finally { await Cleanup(provider); }
    }

    [SkippableFact]
    public async Task Writing_to_your_OWN_branchs_appointment_is_allowed()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var provider = Guid.NewGuid();
        var maadi = Guid.NewGuid();
        try
        {
            var maadiAppt = await Seed(provider, maadi);
            await using var db = Ctx();
            (await AppointmentEndpointsShared.DenyIfOutsideBranchAsync(maadiAppt, Scoped(maadi), db, default))
                .Should().BeNull();
        }
        finally { await Cleanup(provider); }
    }

    [SkippableFact]
    public async Task A_branch_unrestricted_caller_is_never_blocked()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var provider = Guid.NewGuid();
        try
        {
            var appt = await Seed(provider, Guid.NewGuid());
            await using var db = Ctx();
            (await AppointmentEndpointsShared.DenyIfOutsideBranchAsync(appt, Unrestricted(), db, default))
                .Should().BeNull();
        }
        finally { await Cleanup(provider); }
    }

    [SkippableFact]
    public async Task A_pre_branch_row_is_left_to_the_transitions_own_rules()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var provider = Guid.NewGuid();
        try
        {
            // branch_id is additive (migration 0006): rows written before it exists must not become unreachable.
            var legacy = await Seed(provider, branch: null);
            await using var db = Ctx();
            (await AppointmentEndpointsShared.DenyIfOutsideBranchAsync(legacy, Scoped(Guid.NewGuid()), db, default))
                .Should().BeNull();
        }
        finally { await Cleanup(provider); }
    }

    // ── The queue ticket inherits the appointment's branch ───────────────────────────────────────────────

    [SkippableFact]
    public async Task Check_in_stamps_the_queue_ticket_with_the_appointments_branch()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var provider = Guid.NewGuid();
        var maadi = Guid.NewGuid();
        try
        {
            var apptId = await Seed(provider, maadi);
            await using var db = Ctx();
            var transitions = new AppointmentTransitionService(db);
            var result = await transitions.CheckInAsync(apptId, "MRS-M-1", "A. Patient", 0, null, DateTimeOffset.UtcNow);
            result.Outcome.Should().Be(TransitionOutcome.Ok);

            var ticket = await db.Set<QueueTicket>().AsNoTracking().SingleAsync(t => t.AppointmentId == apptId);
            // GET /queues filters on the ticket's branch — a NULL here makes the arrival invisible to the very
            // desk that just checked them in.
            ticket.BranchId.Should().Be(maadi);
        }
        finally { await Cleanup(provider); }
    }

    private static async Task<Guid> Seed(Guid provider, Guid? branch)
    {
        await using var db = Ctx();
        var now = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid();
        db.Appointments.Add(new Appointment
        {
            AppointmentId = id, TenantId = "11111111-1111-1111-1111-111111111111",
            BeneficiaryId = Guid.NewGuid(), ProviderId = provider, LocationId = Guid.NewGuid(),
            BranchId = branch, AppointmentType = AppointmentType.Scheduled, Status = AppointmentStatus.Booked,
            ScheduledStart = now.AddHours(1), ScheduledEnd = now.AddHours(2), CreatedAt = now, UpdatedAt = now,
        });
        await db.SaveChangesAsync();
        return id;
    }

    private static async Task Cleanup(Guid provider)
    {
        await using var db = Ctx();
        // QueueTicket maps to emr.appointment_queue (EmrDbContext ToTable), not emr.queue_ticket.
        await db.Database.ExecuteSqlRawAsync(
            "DELETE FROM emr.appointment_queue WHERE appointment_id IN (SELECT appointment_id FROM emr.appointment WHERE provider_id = {0})", provider);
        await db.Database.ExecuteSqlRawAsync("DELETE FROM emr.appointment WHERE provider_id = {0}", provider);
    }

    private static int StatusOf(IResult result) => result switch
    {
        ProblemHttpResult p => p.StatusCode,
        IStatusCodeHttpResult s => s.StatusCode ?? 0,
        _ => 0,
    };
}
