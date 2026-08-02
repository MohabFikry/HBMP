using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Mersal.Data;
using Mersal.Emr.Domain;
using Mersal.Emr.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Mersal.Emr.Tests;

/// <summary>
/// Steps that ARRIVE BY EVENT, against a real database (ADR-0031).
///
/// <para><see cref="CareEpisodeMappingTests"/> proves the translation; this proves what happens to the result.
/// The three things that can go wrong here all fail quietly: a redelivery writing the step twice, a step
/// attached using a sibling service's idea of who the patient is, and an event for an encounter this tenant
/// does not have. None of them throws, and all three produce a plausible-looking timeline that is wrong.</para>
/// </summary>
[Collection("emr-db")]
public class CareEpisodeAppenderTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly DateTimeOffset Occurred = new(2026, 8, 2, 9, 22, 0, TimeSpan.Zero);

    [SkippableFact]
    public async Task An_order_placed_in_a_visit_lands_on_the_appointment_timeline()
    {
        Skip.If(EmrApiFactory.Db is null, "EMR_TEST_DB not set — DB integration test skipped.");
        await using var app = new EmrApiFactory();
        var (encId, apptId, benId) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        try
        {
            await SeedAsync(app, encId, apptId, benId);

            var outcome = await AppendAsync(app, "OrderCreated", Json(new
            {
                tenantId = app.Tenant, orderId = Guid.NewGuid(), orderNo = "ORD-2026-000014",
                beneficiaryId = Guid.NewGuid(), encounterId = encId, orderType = "Lab",
                orderedByUserId = EmrTestAuth.DoctorSub,
            }), Guid.NewGuid());

            outcome.Should().Be(CareStepOutcome.Appended);

            // The whole point: this reaches the DESK, on the appointment it descended from — the endpoint the
            // reception board opens, which before this slice stopped at check-in.
            using var reception = app.ReceptionClient();
            var timeline = await reception.GetFromJsonAsync<List<JsonElement>>($"/api/v1/appointments/{apptId}/timeline")
                ?? throw new InvalidOperationException("the timeline endpoint answered with no body");

            var step = timeline.Single(s => s.GetProperty("status").GetString() == CareSteps.OrderPlaced);
            step.GetProperty("reference").GetString().Should().Be("ORD-2026-000014");
            step.GetProperty("source").GetString().Should().Be(CareStepSources.Orders);
            step.GetProperty("by").GetString().Should().Be(EmrTestAuth.DoctorSub);
            // The publisher's time, not the relay's. A backlog must not render as an hour of care delivered
            // in one second.
            step.GetProperty("at").GetDateTimeOffset().Should().Be(Occurred);
        }
        finally { await CleanupAsync(app, encId); }
    }

    [SkippableFact]
    public async Task The_same_event_delivered_twice_is_one_step()
    {
        Skip.If(EmrApiFactory.Db is null, "EMR_TEST_DB not set — DB integration test skipped.");
        await using var app = new EmrApiFactory();
        var (encId, apptId, benId) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        try
        {
            await SeedAsync(app, encId, apptId, benId);
            var eventId = Guid.NewGuid();
            var payload = Json(new
            {
                tenantId = app.Tenant, prescriptionId = Guid.NewGuid(), rxNo = "RX-2026-000031",
                beneficiaryId = Guid.NewGuid(), encounterId = encId, orderedByUserId = EmrTestAuth.DoctorSub,
            });

            // Delivery is at-least-once. A broker retry, or a relay that republished after a failed mirror,
            // must not tell the reader the doctor wrote two prescriptions.
            (await AppendAsync(app, "RxCreated", payload, eventId)).Should().Be(CareStepOutcome.Appended);
            (await AppendAsync(app, "RxCreated", payload, eventId)).Should().Be(CareStepOutcome.Duplicate);

            await using var db = EmrApiFactory.Ctx();
            (await db.CareTimeline.CountAsync(s => s.EncounterId == encId && s.Step == CareSteps.PrescriptionWritten))
                .Should().Be(1);
        }
        finally { await CleanupAsync(app, encId); }
    }

    [SkippableFact]
    public async Task The_member_comes_from_our_own_encounter_and_never_from_the_payload()
    {
        Skip.If(EmrApiFactory.Db is null, "EMR_TEST_DB not set — DB integration test skipped.");
        await using var app = new EmrApiFactory();
        var (encId, apptId, benId) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        try
        {
            await SeedAsync(app, encId, apptId, benId);
            // orders-service publishes a beneficiaryId and is truthful about it — but emr OWNS encounters, so
            // emr is the only service that can be wrong about which member a visit is for. Trusting the copy
            // would render a sibling's staleness as this patient's history.
            var someoneElse = Guid.NewGuid();

            await AppendAsync(app, "OrderCreated", Json(new
            {
                tenantId = app.Tenant, orderId = Guid.NewGuid(), orderNo = "ORD-2026-000014",
                beneficiaryId = someoneElse, encounterId = encId, orderType = "Lab",
            }), Guid.NewGuid());

            await using var db = EmrApiFactory.Ctx();
            var step = await db.CareTimeline.SingleAsync(s => s.EncounterId == encId && s.Step == CareSteps.OrderPlaced);
            step.BeneficiaryId.Should().Be(benId);
            step.BeneficiaryId.Should().NotBe(someoneElse);
            // And the appointment, which no sibling carries at all — this is what puts the step on the desk's
            // board rather than only inside the visit.
            step.AppointmentId.Should().Be(apptId);
        }
        finally { await CleanupAsync(app, encId); }
    }

    [SkippableFact]
    public async Task An_event_for_an_encounter_we_do_not_have_is_dropped_not_retried()
    {
        Skip.If(EmrApiFactory.Db is null, "EMR_TEST_DB not set — DB integration test skipped.");
        await using var app = new EmrApiFactory();
        var encId = Guid.NewGuid();
        try
        {
            // Under RLS another tenant's encounter is indistinguishable from one that never existed, and
            // neither gets better by being redelivered. The consumer acks on this outcome; requeuing would
            // spin a permanently unusable message forever.
            var outcome = await AppendAsync(app, "OrderCreated", Json(new
            {
                tenantId = app.Tenant, orderId = Guid.NewGuid(), orderNo = "ORD-2026-000014",
                encounterId = Guid.NewGuid(), orderType = "Lab",
            }), Guid.NewGuid());

            outcome.Should().Be(CareStepOutcome.UnknownEncounter);
        }
        finally { await CleanupAsync(app, encId); }
    }

    /// <summary>Map an event and append it the way the consumer does — same tenant binding off the envelope,
    /// same scoped services — without a broker in the room.</summary>
    private static async Task<CareStepOutcome> AppendAsync(EmrApiFactory app, string eventType, string payload, Guid eventId)
    {
        var draft = CareEpisodeMapping.For(eventType, payload)
            ?? throw new InvalidOperationException($"{eventType} produced no step; the mapping test covers why");

        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<RlsContext>().TenantId = app.Tenant;
        return await scope.ServiceProvider.GetRequiredService<CareEpisodeAppender>()
            .AppendAsync(draft, eventId, Occurred);
    }

    private static async Task SeedAsync(EmrApiFactory app, Guid encId, Guid apptId, Guid benId)
    {
        var now = DateTimeOffset.UtcNow;
        await using var db = EmrApiFactory.Ctx();
        db.Appointments.Add(new Appointment
        {
            AppointmentId = apptId, TenantId = app.Tenant, BeneficiaryId = benId,
            ProviderId = Guid.NewGuid(), LocationId = Guid.NewGuid(),
            AppointmentType = AppointmentType.Scheduled, Status = AppointmentStatus.CheckedIn,
            ScheduledStart = now.AddHours(-1), ScheduledEnd = now.AddMinutes(-30),
        });
        db.Encounters.Add(new Encounter
        {
            EncounterId = encId, EncounterNo = $"ENC-CEA-{encId.ToString()[..8]}",
            BeneficiaryId = benId, AppointmentId = apptId, TenantId = app.Tenant,
            Status = EncounterStatus.InProgress, StartedAt = now.AddMinutes(-20),
            CreatedBy = EmrTestAuth.DoctorSub,
        });
        await db.SaveChangesAsync();
    }

    private static async Task CleanupAsync(EmrApiFactory app, Guid encId)
    {
        if (EmrApiFactory.Db is null) return;
        await using (var db = EmrApiFactory.Ctx())
            await db.Database.ExecuteSqlRawAsync("DELETE FROM emr.care_timeline WHERE encounter_id = {0};", encId);
        await app.CleanupAsync();
    }

    private static string Json(object o) => JsonSerializer.Serialize(o, Web);
}
