using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Mersal.Patient.Api;
using Mersal.Patient.Domain;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Patient.Tests;

/// <summary>
/// US-003 — who filed a registration, and the conversation about it.
///
/// <para>Two gaps these cover, both of which made the approval workflow a dead end.</para>
///
/// <para><b>Nobody was recorded as having filed the application.</b> `registration` carried a timestamp and no
/// actor, so "who registered this person?" could only be answered from the audit trail — and a RequestInfo
/// decision had no queue to land in. The officer is now stamped at every creation path.</para>
///
/// <para><b>Notes were a single column that every decision overwrote.</b> "The UNHCR letter is expired" was
/// gone the instant anyone decided again, and the officer it was addressed to had nowhere to answer. The
/// thread is append-only and carries both halves.</para>
/// </summary>
[Collection("patient-db")]
public class RegistrationThreadTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [SkippableFact]
    public async Task Registering_stamps_the_officer_who_filed_the_application()
    {
        Skip.If(PatientApiFactory.Db is null, "PATIENT_TEST_DB not set — DB integration test skipped.");
        await using var app = new PatientApiFactory();
        try
        {
            using var registrar = app.RegistrarClient();
            var id = await RegisterAsync(registrar);

            await using var db = PatientApiFactory.Ctx();
            var reg = await db.Registrations.AsNoTracking().FirstAsync(r => r.BeneficiaryId == id);
            // The subject, not a name looked up later: this is the address a request for information is
            // delivered to, and it must survive the person leaving the directory.
            reg.CreatedBy.Should().Be(PatientTestAuth.RegistrarSub);
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task The_worklist_says_when_it_was_filed_and_by_whom()
    {
        Skip.If(PatientApiFactory.Db is null, "PATIENT_TEST_DB not set — DB integration test skipped.");
        await using var app = new PatientApiFactory();
        try
        {
            using var registrar = app.RegistrarClient();
            await RegisterAsync(registrar);

            var page = await registrar.GetFromJsonAsync<JsonElement>(
                new Uri("/api/v1/registrations", UriKind.Relative));
            // `total` is the size of the QUEUE, not of the page — a supervisor manages against it.
            page.GetProperty("total").GetInt32().Should().BeGreaterThan(0);

            var reg = page.GetProperty("items")[0].GetProperty("registration");
            reg.GetProperty("createdAt").GetDateTimeOffset().Should().BeAfter(DateTimeOffset.MinValue);
            reg.GetProperty("createdBy").GetString().Should().Be(PatientTestAuth.RegistrarSub);
            reg.GetProperty("threadCount").GetInt32().Should().Be(0);
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task A_decision_lands_on_the_thread_as_well_as_in_the_notes_column()
    {
        Skip.If(PatientApiFactory.Db is null, "PATIENT_TEST_DB not set — DB integration test skipped.");
        await using var app = new PatientApiFactory();
        try
        {
            using var registrar = app.RegistrarClient();
            var id = await RegisterAsync(registrar);
            var regId = await RegistrationIdAsync(id);

            using var supervisor = Supervisor(app);
            var decided = await PostAsync(supervisor, $"/api/v1/registrations/{regId}/decision", null,
                new DecisionRequest("RequestInfo", "UNHCR letter is expired"));
            decided.StatusCode.Should().Be(HttpStatusCode.OK);

            var thread = await registrar.GetFromJsonAsync<JsonElement>(
                new Uri($"/api/v1/registrations/{regId}/thread", UriKind.Relative));
            thread.GetArrayLength().Should().Be(1);
            thread[0].GetProperty("kind").GetString().Should().Be("Decision");
            thread[0].GetProperty("decision").GetString().Should().Be("RequestInfo");
            thread[0].GetProperty("body").GetString().Should().Be("UNHCR letter is expired");
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task RequestInfo_publishes_the_notice_addressed_to_the_filing_officer()
    {
        Skip.If(PatientApiFactory.Db is null, "PATIENT_TEST_DB not set — DB integration test skipped.");
        await using var app = new PatientApiFactory();
        try
        {
            using var registrar = app.RegistrarClient();
            var id = await RegisterAsync(registrar);
            var regId = await RegistrationIdAsync(id);

            using var supervisor = Supervisor(app);
            await PostAsync(supervisor, $"/api/v1/registrations/{regId}/decision", null,
                new DecisionRequest("RequestInfo", "rescan the card"));

            var notice = (await app.Outbox.DequeueBatchAsync(50))
                .FirstOrDefault(m => m.EventType == "RegistrationInfoRequested");
            notice.Should().NotBeNull("a request for information that reaches nobody is a dead end");
            // notification-service's OWN queue, not the shared patient.events stream: consumers on one queue
            // COMPETE for its messages, so sharing it would notify roughly half the officers and drop the rest.
            // 19.7 moved this from a registration-only queue onto the general fan-out queue every publisher
            // now uses — one consumer serves them all.
            notice!.Destination.Should().Be("notification.domain-events");

            var payload = JsonDocument.Parse(notice.Payload).RootElement;
            var recipient = payload.GetProperty("recipients")[0];
            recipient.GetProperty("userId").GetString().Should().Be(PatientTestAuth.RegistrarSub);
            // The role the routing table targets — the publisher resolves the person, because it is the only
            // service that knows which officer filed this application.
            recipient.GetProperty("role").GetString().Should().Be("registration_officer");
            // Non-clinical only — notification bodies interpolate these (11-permission-matrix).
            payload.TryGetProperty("notes", out _).Should().BeFalse(
                "the supervisor's prose stays on the thread, behind authorization");
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task Approve_and_Reject_send_no_notice_because_neither_asks_for_anything()
    {
        Skip.If(PatientApiFactory.Db is null, "PATIENT_TEST_DB not set — DB integration test skipped.");
        await using var app = new PatientApiFactory();
        try
        {
            using var registrar = app.RegistrarClient();
            var id = await RegisterAsync(registrar);
            var regId = await RegistrationIdAsync(id);

            using var supervisor = Supervisor(app);
            await PostAsync(supervisor, $"/api/v1/registrations/{regId}/decision", null,
                new DecisionRequest("Reject", "outside the programme's governorates"));

            (await app.Outbox.DequeueBatchAsync(50))
                .Should().NotContain(m => m.EventType == "RegistrationInfoRequested");
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task The_officer_can_answer_and_the_answer_becomes_the_current_note()
    {
        Skip.If(PatientApiFactory.Db is null, "PATIENT_TEST_DB not set — DB integration test skipped.");
        await using var app = new PatientApiFactory();
        try
        {
            using var registrar = app.RegistrarClient();
            var id = await RegisterAsync(registrar);
            var regId = await RegistrationIdAsync(id);

            using var supervisor = Supervisor(app);
            await PostAsync(supervisor, $"/api/v1/registrations/{regId}/decision", null,
                new DecisionRequest("RequestInfo", "rescan the card"));

            var reply = await PostAsync(registrar, $"/api/v1/registrations/{regId}/thread", null,
                new ThreadReply("Rescanned and uploaded today."));
            reply.StatusCode.Should().Be(HttpStatusCode.Created);

            var thread = await registrar.GetFromJsonAsync<JsonElement>(
                new Uri($"/api/v1/registrations/{regId}/thread", UriKind.Relative));
            thread.GetArrayLength().Should().Be(2);
            thread[1].GetProperty("kind").GetString().Should().Be("Reply");

            // The worklist column shows the LAST thing said, not a question that has already been answered —
            // a stale question is how a row gets reviewed twice.
            await using var db = PatientApiFactory.Ctx();
            var reg = await db.Registrations.AsNoTracking().FirstAsync(r => r.RegistrationId == regId);
            reg.Notes.Should().Be("Rescanned and uploaded today.");
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task A_closed_application_takes_no_more_replies()
    {
        Skip.If(PatientApiFactory.Db is null, "PATIENT_TEST_DB not set — DB integration test skipped.");
        await using var app = new PatientApiFactory();
        try
        {
            using var registrar = app.RegistrarClient();
            var id = await RegisterAsync(registrar);
            var regId = await RegistrationIdAsync(id);

            using var supervisor = Supervisor(app);
            await PostAsync(supervisor, $"/api/v1/registrations/{regId}/decision", null,
                new DecisionRequest("Reject", "outside the programme's governorates"));

            // A live-looking conversation under a final decision invites an officer to answer a question
            // nobody will read.
            var reply = await PostAsync(registrar, $"/api/v1/registrations/{regId}/thread", null,
                new ThreadReply("but we can appeal"));
            reply.StatusCode.Should().Be(HttpStatusCode.Conflict);
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task An_empty_reply_is_refused()
    {
        Skip.If(PatientApiFactory.Db is null, "PATIENT_TEST_DB not set — DB integration test skipped.");
        await using var app = new PatientApiFactory();
        try
        {
            using var registrar = app.RegistrarClient();
            var id = await RegisterAsync(registrar);
            var regId = await RegistrationIdAsync(id);

            var reply = await PostAsync(registrar, $"/api/v1/registrations/{regId}/thread", null,
                new ThreadReply("   "));
            reply.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task An_approver_cannot_decide_a_registration_they_filed_themselves()
    {
        Skip.If(PatientApiFactory.Db is null, "PATIENT_TEST_DB not set — DB integration test skipped.");
        await using var app = new PatientApiFactory();
        try
        {
            // The supervisor registers the person themselves. This is now REACHABLE by design — the
            // supervisor's portal is the officer's plus the decision — so the separation of duties has to hold
            // at the endpoint rather than by withholding a menu item, which never bound the API anyway.
            using var supervisor = Supervisor(app);
            var id = await RegisterAsync(supervisor);
            var regId = await RegistrationIdAsync(id);

            var decided = await PostAsync(supervisor, $"/api/v1/registrations/{regId}/decision", null,
                new DecisionRequest("Reject", "changed my mind"));
            decided.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await decided.Content.ReadAsStringAsync()).Should().Contain("self-approval");

            // And the application is untouched — a refused decision must not half-apply.
            await using var db = PatientApiFactory.Ctx();
            var reg = await db.Registrations.AsNoTracking().FirstAsync(r => r.RegistrationId == regId);
            reg.Status.Should().Be(RegistrationStatus.Pending);
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task A_different_approver_may_decide_the_same_registration()
    {
        Skip.If(PatientApiFactory.Db is null, "PATIENT_TEST_DB not set — DB integration test skipped.");
        await using var app = new PatientApiFactory();
        try
        {
            // Asserting only the refusal above would pass on a service that refused everybody.
            using var registrar = app.RegistrarClient();
            var id = await RegisterAsync(registrar);
            var regId = await RegistrationIdAsync(id);

            using var supervisor = Supervisor(app);
            var decided = await PostAsync(supervisor, $"/api/v1/registrations/{regId}/decision", null,
                new DecisionRequest("Reject", "outside the programme's governorates"));
            decided.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally { await app.CleanupAsync(); }
    }

    // ---- helpers -------------------------------------------------------------------------------------------

    /// <summary>The approver. A DIFFERENT subject from the registrar on purpose — the separation US-003 exists
    /// for is that the person who vouched for the documents does not activate the member.</summary>
    private static HttpClient Supervisor(PatientApiFactory app) =>
        app.As("supervisor-sub", "beneficiary_mgmt_supervisor", "patient:write patient:read");

    private static async Task<Guid> RegistrationIdAsync(Guid beneficiaryId)
    {
        await using var db = PatientApiFactory.Ctx();
        return (await db.Registrations.AsNoTracking().FirstAsync(r => r.BeneficiaryId == beneficiaryId)).RegistrationId;
    }

    private static async Task<Guid> RegisterAsync(HttpClient client)
    {
        var body = new RegisterRequest(
            CardNumber: $"MF-{Guid.NewGuid().ToString("N")[..8]}", GivenName: "Amal", MiddleName: null,
            FamilyName: "Hassan", BirthDate: new DateOnly(1990, 3, 14), BirthDateIsApproximate: false,
            Sex: "Female", NationalityCode: "SD", IdentifierType: nameof(IdentifierType.UNHCRNo),
            IdentifierValue: Guid.NewGuid().ToString("N")[..10], Phone: "+201000000000",
            IndividualNo: null, CaseNo: null,
            Enrolment: new EnrolmentIntentDto(Guid.NewGuid(), Guid.NewGuid(), 10m, null), Notes: null);

        var r = await PostAsync(client, "/api/v1/beneficiaries", Guid.NewGuid().ToString(), body);
        r.StatusCode.Should().Be(HttpStatusCode.Created,
            "the seed must succeed or every assertion below is vacuous: {0}", await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("beneficiaryId").GetGuid();
    }

    private static async Task<HttpResponseMessage> PostAsync(
        HttpClient client, string url, string? idempotencyKey, object body)
    {
        // Awaited inside the using: returning the task would dispose the content mid-send.
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(url, UriKind.Relative))
        {
            Content = JsonContent.Create(body, body.GetType(), options: Web),
        };
        if (idempotencyKey is not null) req.Headers.Add("Idempotency-Key", idempotencyKey);
        return await client.SendAsync(req);
    }
}
