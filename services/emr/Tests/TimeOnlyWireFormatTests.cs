using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Mersal.Emr.Api;
using Mersal.Emr.Domain;

namespace Mersal.Emr.Tests;

/// <summary>
/// A time this service PRINTS is a time this service can READ.
/// </summary>
/// <remarks>
/// <para>Every read endpoint here formats a <c>TimeOnly</c> as <c>HH:mm</c> — opening hours are stated in
/// minutes, and a coordinator's screen shows <c>09:00–13:00</c>. .NET's built-in converter requires seconds
/// and throws on <c>"09:00"</c>. So an edit form that loaded a weekly pattern and posted it back was refused
/// by the service that had just handed it those strings, and the <c>JsonException</c> surfaced as an
/// unhandled <b>500</b> — the client showing "the server could not complete this" over a body that was, as
/// far as anyone reading the screen could tell, exactly what it had been given.</para>
///
/// <para>The same two lines cover the roster exception, whose <c>StartTime</c>/<c>EndTime</c> are the same
/// type: recording a part-day absence failed identically, and stayed hidden because a whole-day exception
/// sends nulls and whole-day is the default.</para>
/// </remarks>
public class TimeOnlyWireFormatTests
{
    private static readonly JsonSerializerOptions Options = Wire();

    private static JsonSerializerOptions Wire()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        o.Converters.Add(new HourMinuteTimeOnlyConverter());
        o.Converters.Add(new NullableHourMinuteTimeOnlyConverter());
        return o;
    }

    private sealed record Slice(TimeOnly StartTime, TimeOnly? EndTime);

    [Theory]
    [InlineData("09:00")]      // what every read endpoint prints
    [InlineData("09:00:00")]   // what the built-in converter emits — still accepted
    [InlineData("09:00:00.0000000")]
    public void A_time_of_day_is_read_with_or_without_seconds(string wire)
    {
        var slice = JsonSerializer.Deserialize<Slice>($$"""{"startTime":"{{wire}}","endTime":null}""", Options);

        slice!.StartTime.Should().Be(new TimeOnly(9, 0));
    }

    [Fact]
    public void It_is_written_back_in_the_shape_the_read_endpoints_print()
    {
        // The round trip is the whole point: what comes out has to go back in.
        var json = JsonSerializer.Serialize(new Slice(new TimeOnly(14, 30), null), Options);

        json.Should().Contain("\"startTime\":\"14:30\"");
        JsonSerializer.Deserialize<Slice>(json, Options)!.StartTime.Should().Be(new TimeOnly(14, 30));
    }

    /// <summary>
    /// Null stays null. Midnight is a real time of day, and a whole-day exception that deserialized to 00:00
    /// would read as "away from midnight" to <c>RosterException.IsWholeDay</c> — which decides whether the
    /// exception removes the day or a window inside it.
    /// </summary>
    [Fact]
    public void An_absent_time_does_not_become_midnight()
    {
        var slice = JsonSerializer.Deserialize<Slice>("""{"startTime":"09:00","endTime":null}""", Options);

        slice!.EndTime.Should().BeNull();
    }

    [Fact]
    public void A_value_that_is_not_a_time_is_refused_by_name()
    {
        var act = () => JsonSerializer.Deserialize<Slice>("""{"startTime":"half nine","endTime":null}""", Options);

        // Named, so the failure says which value was wrong rather than "the JSON could not be converted".
        act.Should().Throw<JsonException>().WithMessage("*half nine*");
    }

    // ── over HTTP, which is where it actually bit ────────────────────────────────────────────────────────

    /// <summary>
    /// Editing a weekly pattern with the times the GET returned. This is the exact request the roster screen
    /// makes, and it was a 500.
    /// </summary>
    [SkippableFact]
    public async Task A_weekly_pattern_is_saved_with_the_times_its_own_read_returned()
    {
        Skip.If(EmrApiFactory.Db is null, "EMR_TEST_DB not set — DB integration test skipped.");
        var branch = Guid.NewGuid();
        await using var app = new EmrApiFactory { HomeBranch = branch };
        try
        {
            var id = Guid.NewGuid();
            await using (var db = EmrApiFactory.Ctx())
            {
                db.ProviderAvailabilities.Add(new ProviderAvailability
                {
                    AvailabilityId = id, TenantId = app.Tenant,
                    ProviderId = Guid.NewGuid(), LocationId = Guid.NewGuid(),
                    BranchId = branch, DoctorId = Guid.NewGuid(), DayOfWeek = DayOfWeek.Monday,
                    StartTime = new TimeOnly(9, 0), EndTime = new TimeOnly(13, 0), SlotMinutes = 20,
                });
                await db.SaveChangesAsync();
            }

            using var manager = app.As(EmrTestAuth.ReceptionSub, "clinics_manager",
                "appointment:read branch:roster:write");

            var read = await manager.GetAsync(new Uri("/api/v1/provider-availability", UriKind.Relative));
            var rule = (await read.Content.ReadFromJsonAsync<JsonElement>())[0];
            // The service prints HH:mm. Everything below is that string, unmodified.
            rule.GetProperty("startTime").GetString().Should().Be("09:00");

            var body = new
            {
                providerId = rule.GetProperty("providerId").GetGuid(),
                locationId = rule.GetProperty("locationId").GetGuid(),
                doctorId = rule.GetProperty("doctorId").GetGuid(),
                branchId = rule.GetProperty("branchId").GetGuid(),
                dayOfWeek = rule.GetProperty("dayOfWeek").GetInt32(),
                startTime = rule.GetProperty("startTime").GetString(),
                endTime = rule.GetProperty("endTime").GetString(),
                slotMinutes = 20,
                maxPerDay = 12,
            };

            var saved = await manager.PutAsJsonAsync(
                new Uri($"/api/v1/provider-availability/{id}", UriKind.Relative), body);

            saved.StatusCode.Should().Be(HttpStatusCode.OK, "{0}", await saved.Content.ReadAsStringAsync());
            (await saved.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("maxPerDay").GetInt32().Should().Be(12);
        }
        finally { await app.CleanupAsync(); }
    }

    /// <summary>The other half of the same defect: an absence for part of a day.</summary>
    [SkippableFact]
    public async Task A_part_day_absence_is_recorded_from_the_times_a_clinic_states()
    {
        Skip.If(EmrApiFactory.Db is null, "EMR_TEST_DB not set — DB integration test skipped.");
        var branch = Guid.NewGuid();
        await using var app = new EmrApiFactory { HomeBranch = branch };
        try
        {
            using var manager = app.As(EmrTestAuth.ReceptionSub, "clinics_manager",
                "appointment:read branch:roster:write");

            var r = await manager.PostAsJsonAsync(new Uri("/api/v1/roster-exceptions?dryRun=true", UriKind.Relative), new
            {
                kind = "Leave",
                dateFrom = "2026-09-08",
                dateTo = "2026-09-08",
                reason = "Hospital round",
                branchId = branch,
                practitionerId = Guid.NewGuid(),
                startTime = "11:00",
                endTime = "13:00",
            });

            r.StatusCode.Should().Be(HttpStatusCode.OK, "{0}", await r.Content.ReadAsStringAsync());
        }
        finally { await app.CleanupAsync(); }
    }
}
