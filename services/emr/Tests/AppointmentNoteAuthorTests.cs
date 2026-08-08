using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Mersal.Emr.Api;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Emr.Tests;

/// <summary>
/// 0022 — the booking note's author, in words.
///
/// <para>0014 captured <c>note_by</c>, the author's SUBJECT ID, and that is what reached the screen: the note
/// dialog rendered <i>"Written by c18b985c-cc5f-42eb-8b79-e41b7b84f975"</i>. 0014's own rationale was that an
/// unattributed instruction crossing a team boundary is one nobody can follow up — a uuid is unattributed in
/// every sense that matters to the receptionist reading it.</para>
///
/// <para>The name is snapshotted at write time (19.3's rule for signatures, 0020's for allergen names) rather
/// than joined on read, so this suite's job is to prove the snapshot is TAKEN, that it is taken from the
/// caller rather than invented, and that it is dropped when the note is. The id stays alongside it: a display
/// name is not an identity, and the audit trail correlates on the id.</para>
/// </summary>
[Collection("emr-db")]
public class AppointmentNoteAuthorTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private const string Author = "Nada Fahmy";

    [SkippableFact]
    public async Task A_note_written_at_booking_captures_the_author_by_name_and_by_id()
    {
        Skip.If(EmrApiFactory.Db is null, "EMR_TEST_DB not set — DB integration test skipped.");
        await using var app = new EmrApiFactory();
        try
        {
            using var reception = app.ReceptionClient(displayName: Author);
            var id = await Book(reception, "Interpreter needed — Tigrinya");

            await using var db = EmrApiFactory.Ctx();
            var appt = await db.Appointments.AsNoTracking().SingleAsync(a => a.AppointmentId == id);

            appt.NoteByName.Should().Be(Author, "the desk needs somebody to ask, not a subject id");
            appt.NoteBy.Should().Be(EmrTestAuth.ReceptionSub,
                "the id stays as the authoritative link — a display name is not an identity, and the audit " +
                "trail correlates on the id");
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task The_name_reaches_the_reader_over_HTTP()
    {
        Skip.If(EmrApiFactory.Db is null, "EMR_TEST_DB not set — DB integration test skipped.");
        await using var app = new EmrApiFactory();
        try
        {
            using var reception = app.ReceptionClient(displayName: Author);
            var id = await Book(reception, "Wheelchair access; ground-floor room.");

            // Written to the column is not the same as reaching the screen: the field was on the row and off
            // the response for as long as this bug existed.
            var row = await reception.GetFromJsonAsync<JsonElement>(
                new Uri($"/api/v1/appointments/{id}", UriKind.Relative), Web);

            row.GetProperty("noteByName").GetString().Should().Be(Author);
            row.GetProperty("noteBy").GetString().Should().Be(EmrTestAuth.ReceptionSub);
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task Editing_the_note_re_attributes_it_to_whoever_edited_it()
    {
        Skip.If(EmrApiFactory.Db is null, "EMR_TEST_DB not set — DB integration test skipped.");
        await using var app = new EmrApiFactory();
        try
        {
            using var booker = app.ReceptionClient(displayName: Author);
            var id = await Book(booker, "Original arrangement.");

            using var editor = app.As(EmrTestAuth.DoctorSub, "reception",
                "appointment:write appointment:reserve appointment:read", displayName: "Hana Mansour");
            var edited = await editor.PostAsJsonAsync(
                new Uri($"/api/v1/appointments/{id}/note", UriKind.Relative), new { note = "Revised: son will interpret." }, Web);
            edited.IsSuccessStatusCode.Should().BeTrue("{0}", await edited.Content.ReadAsStringAsync());

            await using var db = EmrApiFactory.Ctx();
            var appt = await db.Appointments.AsNoTracking().SingleAsync(a => a.AppointmentId == id);

            // The attribution follows the TEXT, not the appointment. Leaving the original author on rewritten
            // text is the false attribution 0014 exists to prevent, pointed at the wrong person.
            appt.NoteByName.Should().Be("Hana Mansour");
            appt.NoteBy.Should().Be(EmrTestAuth.DoctorSub);
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task Clearing_the_note_clears_the_author_with_it()
    {
        Skip.If(EmrApiFactory.Db is null, "EMR_TEST_DB not set — DB integration test skipped.");
        await using var app = new EmrApiFactory();
        try
        {
            using var reception = app.ReceptionClient(displayName: Author);
            var id = await Book(reception, "To be removed.");

            var cleared = await reception.PostAsJsonAsync(
                new Uri($"/api/v1/appointments/{id}/note", UriKind.Relative), new { note = (string?)null }, Web);
            cleared.IsSuccessStatusCode.Should().BeTrue("{0}", await cleared.Content.ReadAsStringAsync());

            await using var db = EmrApiFactory.Ctx();
            var appt = await db.Appointments.AsNoTracking().SingleAsync(a => a.AppointmentId == id);

            // Attribution for text nobody can read is a claim about nothing.
            appt.NoteByName.Should().BeNull();
            appt.NoteBy.Should().BeNull();
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task A_nameless_caller_leaves_the_name_null_rather_than_falling_back_to_the_id()
    {
        Skip.If(EmrApiFactory.Db is null, "EMR_TEST_DB not set — DB integration test skipped.");
        await using var app = new EmrApiFactory();
        try
        {
            // No display name on the token at all — a machine caller, or a token shape that carries neither
            // `name` nor `preferred_username`.
            using var reception = app.ReceptionClient();
            var id = await Book(reception, "Written by a nameless principal.");

            await using var db = EmrApiFactory.Ctx();
            var appt = await db.Appointments.AsNoTracking().SingleAsync(a => a.AppointmentId == id);

            // NULL, and specifically NOT the subject. Copying the id into the name column would put the uuid
            // straight back on screen wearing a different column name — readers say "unknown", which is true.
            appt.NoteByName.Should().BeNull();
            appt.NoteBy.Should().Be(EmrTestAuth.ReceptionSub);
        }
        finally { await app.CleanupAsync(); }
    }

    private static async Task<Guid> Book(HttpClient client, string note)
    {
        var from = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(14));
        while (from.DayOfWeek != DayOfWeek.Tuesday) from = from.AddDays(1);
        var slots = new CreateSlotsRequest(
            ProviderId: Guid.NewGuid(), LocationId: Guid.NewGuid(), DoctorId: Guid.NewGuid(),
            DayOfWeek: DayOfWeek.Tuesday, StartTime: new TimeOnly(9, 0), EndTime: new TimeOnly(11, 0),
            SlotMinutes: 30, FromDate: from, ToDate: from, BranchId: null);

        var made = await client.PostAsJsonAsync(new Uri("/api/v1/appointment-slots", UriKind.Relative), slots, Web);
        made.StatusCode.Should().Be(HttpStatusCode.OK, "{0}", await made.Content.ReadAsStringAsync());
        var slotId = (await made.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("slots")[0].GetProperty("slotId").GetGuid();

        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/v1/appointments", UriKind.Relative))
        {
            Content = JsonContent.Create(new BookAppointmentRequest(
                BeneficiaryId: Guid.NewGuid(), ProviderId: slots.ProviderId, LocationId: slots.LocationId,
                AppointmentType: nameof(Mersal.Emr.Domain.AppointmentType.Scheduled), SlotId: slotId,
                ScheduledStart: null, ScheduledEnd: null, ReferralRef: null, OriginEncounterId: null,
                JoinWaitlistIfFull: false, Note: note), options: Web),
        };
        req.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var booked = await client.SendAsync(req);
        booked.StatusCode.Should().Be(HttpStatusCode.Created, "{0}", await booked.Content.ReadAsStringAsync());
        return (await booked.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("appointmentId").GetGuid();
    }
}
