using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using FluentAssertions;
using Mersal.Notification.Api;
using Mersal.Notification.Domain;
using Mersal.Notification.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mersal.Notification.Tests;

/// <summary>
/// Phase 24 Gate 3 — the in-app inbox, over HTTP.
///
/// <para>notification-service's Api layer measured 0.0%, and the rule that layer enforces is the one that
/// matters most here: the inbox is STRICTLY self-service, row-filtered by recipient == caller. Everything
/// else about a notification is cosmetic; who can read it is not. A filter dropped from any of the four
/// inbox routes would have failed no test, and the failure is one user reading another's notices.</para>
/// </summary>
[Collection("notification-db")]
public class NotificationEndpointTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The self-service rule on every route that reads or mutates a notification. One ingest fans out to two
    /// recipients; each sees only their own, cannot fetch the other's delivery status, and cannot mark the
    /// other's as read.
    /// </summary>
    [SkippableFact]
    public async Task The_inbox_shows_only_the_callers_own_notifications_on_every_route()
    {
        Skip.If(NotificationApiFactory.Db is null, "NOTIFICATION_TEST_DB not set — DB integration test skipped.");
        await using var app = new NotificationApiFactory();
        try
        {
            using var system = app.SystemClient();
            (await system.PostAsJsonAsync("/api/v1/notifications/ingest", Ingest(app), Web))
                .StatusCode.Should().Be(HttpStatusCode.OK);

            var (mine, theirs) = await app.NotificationsAsync();
            mine.Should().NotBeNull("the ingest must actually create a notification for each recipient");
            theirs.Should().NotBeNull();

            using var me = app.UserClient(NotificationApiFactory.UserA);
            var inbox = await me.GetFromJsonAsync<List<JsonElement>>(
                new Uri("/api/v1/notifications/", UriKind.Relative), Web);
            var ids = inbox!.Select(e => e.GetProperty("notificationId").GetGuid()).ToList();
            ids.Should().Contain(mine!.NotificationId);
            ids.Should().NotContain(theirs!.NotificationId, "an inbox is one person's, not the tenant's");

            (await me.GetAsync(new Uri($"/api/v1/notifications/{theirs.NotificationId}/delivery", UriKind.Relative)))
                .StatusCode.Should().Be(HttpStatusCode.NotFound,
                    "someone else's notification is reported as absent, not as forbidden — the existence of a " +
                    "notice about another person is itself a disclosure");

            (await me.PostAsync(new Uri($"/api/v1/notifications/{theirs.NotificationId}/read", UriKind.Relative), null))
                .StatusCode.Should().Be(HttpStatusCode.NotFound);

            await using var db = NotificationApiFactory.Ctx();
            (await db.Notifications.AsNoTracking().SingleAsync(n => n.NotificationId == theirs.NotificationId))
                .ReadAt.Should().BeNull("acting on another person's notification changed nothing");
        }
        finally { await app.CleanupAsync(); }
    }

    /// <summary>Marking read stops the escalation timer, so it must actually stamp — and a second call is a
    /// no-op rather than a re-stamp, because the time it was first seen is the fact that matters.</summary>
    [SkippableFact]
    public async Task Marking_read_stamps_once_and_read_all_is_idempotent()
    {
        Skip.If(NotificationApiFactory.Db is null, "NOTIFICATION_TEST_DB not set — DB integration test skipped.");
        await using var app = new NotificationApiFactory();
        try
        {
            using var system = app.SystemClient();
            (await system.PostAsJsonAsync("/api/v1/notifications/ingest", Ingest(app), Web))
                .StatusCode.Should().Be(HttpStatusCode.OK);
            var (mine, _) = await app.NotificationsAsync();

            using var me = app.UserClient(NotificationApiFactory.UserA);
            (await me.PostAsync(new Uri($"/api/v1/notifications/{mine!.NotificationId}/read", UriKind.Relative), null))
                .StatusCode.Should().Be(HttpStatusCode.OK);

            await using (var db = NotificationApiFactory.Ctx())
            {
                var first = await db.Notifications.AsNoTracking()
                    .SingleAsync(n => n.NotificationId == mine.NotificationId);
                first.ReadAt.Should().NotBeNull();

                (await me.PostAsync(new Uri($"/api/v1/notifications/{mine.NotificationId}/read", UriKind.Relative), null))
                    .StatusCode.Should().Be(HttpStatusCode.OK);

                await using var after = NotificationApiFactory.Ctx();
                (await after.Notifications.AsNoTracking().SingleAsync(n => n.NotificationId == mine.NotificationId))
                    .ReadAt.Should().Be(first.ReadAt, "when it was first seen is the fact that matters");
            }

            // read-all reports how many it moved, so a second call reports zero rather than repeating itself.
            var again = await me.PostAsync(new Uri("/api/v1/notifications/read-all", UriKind.Relative), null);
            again.StatusCode.Should().Be(HttpStatusCode.OK);
            (await again.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("marked").GetInt32()
                .Should().Be(0);
        }
        finally { await app.CleanupAsync(); }
    }

    /// <summary>The fan-out seam dedupes on event id: an at-least-once broker redelivers, and a member told
    /// twice about one appointment stops trusting the channel.</summary>
    [SkippableFact]
    public async Task Redelivering_the_same_event_creates_no_second_notification()
    {
        Skip.If(NotificationApiFactory.Db is null, "NOTIFICATION_TEST_DB not set — DB integration test skipped.");
        await using var app = new NotificationApiFactory();
        try
        {
            using var system = app.SystemClient();
            var body = Ingest(app);

            var first = await system.PostAsJsonAsync("/api/v1/notifications/ingest", body, Web);
            first.StatusCode.Should().Be(HttpStatusCode.OK);
            var created = (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("created").GetInt32();
            created.Should().BeGreaterThan(0);

            var replay = await system.PostAsJsonAsync("/api/v1/notifications/ingest", body, Web);
            replay.StatusCode.Should().Be(HttpStatusCode.OK);
            var replayed = await replay.Content.ReadFromJsonAsync<JsonElement>();
            replayed.GetProperty("deduplicated").GetBoolean().Should().BeTrue();
            replayed.GetProperty("created").GetInt32().Should().Be(0);

            await using var db = NotificationApiFactory.Ctx();
            (await db.Notifications.CountAsync(n => n.TenantId == app.Tenant)).Should().Be(created);
        }
        finally { await app.CleanupAsync(); }
    }

    /// <summary>A recipient may not fan out to themselves. The ingest seam is a SYSTEM route: a user holding
    /// only notification:read cannot manufacture a notice, for themselves or anybody else.</summary>
    [SkippableFact]
    public async Task A_recipient_cannot_ingest_and_an_anonymous_caller_reaches_nothing()
    {
        Skip.If(NotificationApiFactory.Db is null, "NOTIFICATION_TEST_DB not set — DB integration test skipped.");
        await using var app = new NotificationApiFactory();
        try
        {
            using var me = app.UserClient(NotificationApiFactory.UserA);
            (await me.PostAsJsonAsync("/api/v1/notifications/ingest", Ingest(app), Web))
                .StatusCode.Should().Be(HttpStatusCode.Forbidden);

            using var anonymous = app.CreateClient();
            (await anonymous.GetAsync(new Uri("/api/v1/notifications/", UriKind.Relative)))
                .StatusCode.Should().Be(HttpStatusCode.Unauthorized);

            await using var db = NotificationApiFactory.Ctx();
            (await db.Notifications.CountAsync(n => n.TenantId == app.Tenant)).Should().Be(0);
        }
        finally { await app.CleanupAsync(); }
    }

    private static IngestRequest Ingest(NotificationApiFactory app) => new(
        // The event type and the role key both come from RoutingTable — an unrouted event fans out to nobody
        // and the ingest reports created: 0, which is a correct answer to a question the test did not mean
        // to ask. `ref` is the placeholder the seeded appointment.reminder template renders.
        EventId: app.EventId, EventType: "AppointmentReminderIssued", TenantId: app.Tenant, EntityRef: "APPT-1",
        Fields: new Dictionary<string, string> { ["ref"] = "APPT-1" },
        RoleRecipients: new Dictionary<string, List<RecipientDto>>
        {
            ["beneficiary"] =
            [
                new RecipientDto(NotificationApiFactory.UserA, "en"),
                new RecipientDto(NotificationApiFactory.UserB, "ar"),
            ],
        });
}

/// <summary>Hosts the real notification endpoints. The service reaches no sibling over HTTP, so nothing is
/// faked but the token.</summary>
public sealed class NotificationApiFactory : WebApplicationFactory<Program>
{
    public static readonly string? Db = Environment.GetEnvironmentVariable("NOTIFICATION_TEST_DB");

    public const string UserA = "11111111-1111-1111-1111-111111111111";
    public const string UserB = "22222222-2222-2222-2222-222222222222";
    public const string SystemSub = "33333333-3333-3333-3333-333333333333";

    public string Tenant { get; } = "t-api-" + Guid.NewGuid().ToString("N")[..10];
    public Guid EventId { get; } = Guid.NewGuid();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Notification"] = Db,
            ["Events:UseInMemoryOutbox"] = "true",
        }));
        builder.ConfigureLogging(l => l.SetMinimumLevel(LogLevel.Warning));
        builder.ConfigureTestServices(s =>
        {
            s.AddAuthentication(NotificationTestAuth.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, NotificationTestAuth>(NotificationTestAuth.SchemeName, _ => { });
            // The escalation sweep wakes mid-test and writes to the rows the assertions read.
            s.RemoveAll<IHostedService>();
        });
    }

    /// <summary>A recipient: reads their own inbox and nothing else.</summary>
    public HttpClient UserClient(string sub) => As(sub, "reception", "notification:read");

    /// <summary>The fan-out caller — a service, not a person.</summary>
    public HttpClient SystemClient() => As(SystemSub, "system", "notification:ingest");

    public HttpClient As(string sub, string role, string scopes)
    {
        var c = CreateClient();
        c.DefaultRequestHeaders.Add("X-Test-Sub", sub);
        c.DefaultRequestHeaders.Add("X-Test-Role", role);
        c.DefaultRequestHeaders.Add("X-Test-Scope", scopes);
        c.DefaultRequestHeaders.Add("X-Test-Tenant", Tenant);
        c.DefaultRequestHeaders.Add("X-Test-Mfa", "1");
        return c;
    }

    /// <summary>
    /// The two recipients' IN-APP notifications, or nulls when the ingest produced none.
    ///
    /// <para>The channel filter is load-bearing: AppointmentReminderIssued targets the beneficiary on InApp AND
    /// Email, so each recipient gets two rows, and the inbox route returns only the InApp one. Picking either
    /// row would make this suite assert against a notification the endpoint under test never returns.</para>
    /// </summary>
    public async Task<(Domain.Notification? Mine, Domain.Notification? Theirs)> NotificationsAsync()
    {
        await using var db = Ctx();
        var rows = await db.Notifications.AsNoTracking()
            .Where(n => n.TenantId == Tenant && n.Channel == NotificationChannel.InApp).ToListAsync();
        return (rows.FirstOrDefault(n => n.RecipientUserId == UserA),
                rows.FirstOrDefault(n => n.RecipientUserId == UserB));
    }

    public async Task CleanupAsync()
    {
        if (Db is null) return;
        await using var db = Ctx();
        await db.Database.ExecuteSqlRawAsync(
            "DELETE FROM notification.notification WHERE tenant_id = {0}; " +
            "DELETE FROM notification.processed_event WHERE event_id = {1};", Tenant, EventId);
    }

    public static NotificationDbContext Ctx() =>
        new(new DbContextOptionsBuilder<NotificationDbContext>().UseNpgsql(Db).UseSnakeCaseNamingConvention().Options);
}

/// <summary>Builds a principal from X-Test-* headers; the house shape.</summary>
public sealed class NotificationTestAuth(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-Test-Sub", out var sub))
            return Task.FromResult(AuthenticateResult.NoResult());

        var claims = new List<Claim> { new("sub", sub.ToString()) };
        if (Request.Headers.TryGetValue("X-Test-Role", out var role))
            foreach (var r in role.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries))
                claims.Add(new Claim("role", r));
        if (Request.Headers.TryGetValue("X-Test-Scope", out var scope)) claims.Add(new Claim("scope", scope.ToString()));
        if (Request.Headers.TryGetValue("X-Test-Tenant", out var tenant)) claims.Add(new Claim("tenant_id", tenant.ToString()));
        if (Request.Headers.ContainsKey("X-Test-Mfa")) claims.Add(new Claim("amr", "otp"));

        var identity = new ClaimsIdentity(claims, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}
