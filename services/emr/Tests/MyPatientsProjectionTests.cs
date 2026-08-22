using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Mersal.Emr.Api;
using Mersal.Emr.Domain;

namespace Mersal.Emr.Tests;

/// <summary>
/// <c>GET /encounters/mine</c> — the treating clinician's own worklist ("My Patients").
///
/// <para>The list rendered "Beneficiary •••4821" on every row, because the response carried no name and the
/// client had nothing but the id to mask. That is unusable as a worklist: the doctor cannot tell which of
/// their own patients a row is without opening it, and they are entitled to know — they read the full
/// clinical record behind each one.</para>
///
/// <para>What these tests pin is WHERE the name comes from. It is emr's own
/// <c>appointment.beneficiary_name</c>, captured at booking, and NOT a lookup against patient-service: a
/// service that fetches a sibling's data on the caller's behalf is the aggregation shape this platform
/// forbids (see <c>NoServiceAccountArchitectureTests</c>). The walk-in case is the other half — no
/// appointment means no name, and the row must keep the masked token rather than render blank.</para>
/// </summary>
[Collection("emr-db")]
public class MyPatientsProjectionTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [SkippableFact]
    public async Task Worklist_carries_the_patient_name_from_the_appointment_it_was_started_from()
    {
        Skip.If(EmrApiFactory.Db is null, "EMR_TEST_DB not set — DB integration test skipped.");
        await using var app = new EmrApiFactory();
        try
        {
            var beneficiary = Guid.NewGuid();
            var appointmentId = await SeedCheckedInAppointment(app, beneficiary, "Fatma Ibrahim");

            using var doctor = app.DoctorClient();
            (await StartVisit(doctor, new { beneficiaryId = beneficiary, appointmentId })).EnsureSuccessStatusCode();

            var mine = await doctor.GetFromJsonAsync<List<EncounterResponse>>("/api/v1/encounters/mine", Web);
            mine.Should().ContainSingle(e => e.AppointmentId == appointmentId)
                .Which.BeneficiaryName.Should().Be("Fatma Ibrahim");
        }
        finally { await app.CleanupAsync(); }
    }

    /// <summary>A walk-in was never booked, so no name was ever captured. Null — the client renders the
    /// masked token, which is honest; a blank cell reads as data loss.</summary>
    [SkippableFact]
    public async Task A_walk_in_encounter_carries_no_name_rather_than_an_empty_one()
    {
        Skip.If(EmrApiFactory.Db is null, "EMR_TEST_DB not set — DB integration test skipped.");
        await using var app = new EmrApiFactory();
        try
        {
            var beneficiary = Guid.NewGuid();
            using var doctor = app.DoctorClient();
            (await StartVisit(doctor, new { beneficiaryId = beneficiary })).EnsureSuccessStatusCode();

            var mine = await doctor.GetFromJsonAsync<List<EncounterResponse>>("/api/v1/encounters/mine", Web);
            mine.Should().ContainSingle(e => e.BeneficiaryId == beneficiary)
                .Which.BeneficiaryName.Should().BeNull();
        }
        finally { await app.CleanupAsync(); }
    }

    /// <summary>The BRANCH the visit happened at, from the same appointment the name comes from.
    ///
    /// <para>"My Patients" shows a doctor who works more than one branch every patient they treat, with no
    /// way to tell which building any of them was seen in. An encounter has no branch of its own — it records
    /// care, not a place — so the value belongs to the appointment the visit was started from, which this
    /// query is already reading.</para></summary>
    [SkippableFact]
    public async Task Worklist_carries_the_branch_of_the_appointment_the_visit_was_started_from()
    {
        Skip.If(EmrApiFactory.Db is null, "EMR_TEST_DB not set — DB integration test skipped.");
        // The doctor's reach has to include the clinic the appointment is at. Opening a visit is a WRITE,
        // and a BranchScoped caller with no assignment may write nowhere — which these tests were escaping
        // only because emr dropped the resolved scope mode and every guard read the doctor as unrestricted.
        var branchId = Guid.NewGuid();
        await using var app = new EmrApiFactory { HomeBranch = branchId };
        try
        {
            var beneficiary = Guid.NewGuid();
            var appointmentId = await SeedCheckedInAppointment(app, beneficiary, "Amal Hassan", branchId);

            using var doctor = app.DoctorClient();
            (await StartVisit(doctor, new { beneficiaryId = beneficiary, appointmentId })).EnsureSuccessStatusCode();

            var mine = await doctor.GetFromJsonAsync<List<EncounterResponse>>("/api/v1/encounters/mine", Web);
            mine.Should().ContainSingle(e => e.AppointmentId == appointmentId)
                .Which.BranchId.Should().Be(branchId);
        }
        finally { await app.CleanupAsync(); }
    }

    /// <summary>A walk-in has no appointment and therefore no branch. Null means "there was no booking", not
    /// "we do not know" — the panel says so in words rather than leaving the cell blank.</summary>
    [SkippableFact]
    public async Task A_walk_in_encounter_carries_no_branch()
    {
        Skip.If(EmrApiFactory.Db is null, "EMR_TEST_DB not set — DB integration test skipped.");
        await using var app = new EmrApiFactory();
        try
        {
            var beneficiary = Guid.NewGuid();
            using var doctor = app.DoctorClient();
            (await StartVisit(doctor, new { beneficiaryId = beneficiary })).EnsureSuccessStatusCode();

            var mine = await doctor.GetFromJsonAsync<List<EncounterResponse>>("/api/v1/encounters/mine", Web);
            mine.Should().ContainSingle(e => e.BeneficiaryId == beneficiary)
                .Which.BranchId.Should().BeNull();
        }
        finally { await app.CleanupAsync(); }
    }

    /// <summary>A booked appointment that carries a branch but NO name still yields its branch.
    ///
    /// <para>The projection used to filter the appointment lookup on <c>BeneficiaryName != null</c>, which was
    /// harmless while the name was the only thing it fetched. Reading the branch through the same dictionary
    /// made that filter load-bearing in a way it was never meant to be: it would have dropped the branch of
    /// every unnamed row along with the name it was skipping.</para></summary>
    [SkippableFact]
    public async Task An_appointment_with_a_branch_and_no_name_still_yields_its_branch()
    {
        Skip.If(EmrApiFactory.Db is null, "EMR_TEST_DB not set — DB integration test skipped.");
        var branchId = Guid.NewGuid();
        await using var app = new EmrApiFactory { HomeBranch = branchId };
        try
        {
            var beneficiary = Guid.NewGuid();
            var appointmentId = await SeedCheckedInAppointment(app, beneficiary, name: null, branchId);

            using var doctor = app.DoctorClient();
            (await StartVisit(doctor, new { beneficiaryId = beneficiary, appointmentId })).EnsureSuccessStatusCode();

            var mine = await doctor.GetFromJsonAsync<List<EncounterResponse>>("/api/v1/encounters/mine", Web);
            var e = mine.Should().ContainSingle(x => x.AppointmentId == appointmentId).Which;
            e.BranchId.Should().Be(branchId);
            e.BeneficiaryName.Should().BeNull();
        }
        finally { await app.CleanupAsync(); }
    }

    /// <summary>The worklist is still the CALLER's own. The name is an addition to the projection, not a
    /// relaxation of the narrowing that decides which rows appear in it.</summary>
    [SkippableFact]
    public async Task Another_clinicians_encounter_stays_out_of_the_list()
    {
        Skip.If(EmrApiFactory.Db is null, "EMR_TEST_DB not set — DB integration test skipped.");
        await using var app = new EmrApiFactory();
        try
        {
            var beneficiary = Guid.NewGuid();
            var appointmentId = await SeedCheckedInAppointment(app, beneficiary, "Khaled Mostafa");

            using var doctor = app.DoctorClient();
            (await StartVisit(doctor, new { beneficiaryId = beneficiary, appointmentId })).EnsureSuccessStatusCode();

            // A different subject, same role and scopes.
            using var colleague = app.As(Guid.NewGuid().ToString(), "doctor",
                "emr:read emr:write encounter:write appointment:read");
            var theirs = await colleague.GetFromJsonAsync<List<EncounterResponse>>("/api/v1/encounters/mine", Web);
            theirs.Should().BeEmpty();
        }
        finally { await app.CleanupAsync(); }
    }

    /// <summary>POST /encounters, with the Idempotency-Key the endpoint requires — a retried "start visit"
    /// must not open a second encounter for one arrival, so the header is mandatory and its absence is a
    /// 400 rather than a silently-duplicated visit.</summary>
    private static async Task<HttpResponseMessage> StartVisit(HttpClient client, object body)
    {
        // Awaited inside the using: returning the task would dispose the content mid-send.
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/v1/encounters", UriKind.Relative))
        {
            Content = JsonContent.Create(body, body.GetType(), options: Web),
        };
        req.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        return await client.SendAsync(req);
    }

    /// <summary>A CheckedIn appointment carrying the name reception captured at booking. Written straight to
    /// the datastore: this suite is about the read projection, not about re-proving the booking flow.</summary>
    private static async Task<Guid> SeedCheckedInAppointment(
        EmrApiFactory app, Guid beneficiary, string? name, Guid? branchId = null)
    {
        var appointmentId = Guid.NewGuid();
        await using var db = EmrApiFactory.Ctx();
        db.Appointments.Add(new Appointment
        {
            AppointmentId = appointmentId,
            BeneficiaryId = beneficiary,
            ProviderId = Guid.NewGuid(),
            LocationId = Guid.NewGuid(),
            // Null = an external provider location rather than a Mersal branch, which is the column's own
            // meaning (phase 14) and the second way a row legitimately has no branch.
            BranchId = branchId,
            AppointmentType = AppointmentType.Scheduled,
            Status = AppointmentStatus.CheckedIn,
            ScheduledStart = DateTimeOffset.UtcNow,
            ScheduledEnd = DateTimeOffset.UtcNow.AddMinutes(20),
            // Named, not null: a null DoctorId is a general clinic session open to whoever is on shift, so
            // naming the test doctor keeps the assigned-doctor rule in play rather than bypassing it.
            DoctorId = Guid.Parse(EmrTestAuth.DoctorSub),
            BeneficiaryName = name,
            TenantId = app.Tenant,
        });
        await db.SaveChangesAsync();
        return appointmentId;
    }
}
