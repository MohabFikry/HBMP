using System.Text.Json;
using FluentAssertions;
using Mersal.Events;
using Mersal.Reporting.Infrastructure;
using Mersal.Time;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Reporting.Tests;

/// <summary>
/// The feed, the mapping, and the guard that stops them drifting apart again.
///
/// <para>The failure these exist to prevent is the one the §11 sweep found three times and the §11.3
/// reconciliation found again: two halves of a pipeline, each correct, wired to different names. Nothing
/// throws when a projector's switch falls through — the event is marked processed and no fact is written —
/// so a read model that has quietly stopped being fed is indistinguishable from a quiet week.</para>
/// </summary>
public class ProjectionFeedTests
{
    /// <summary>
    /// Every event on the feed must reach a projector, and every projected type must be on the feed.
    ///
    /// <para>This is the anti-drift test. Adding an event to <see cref="ProjectionFeed"/> that nothing
    /// projects puts traffic on a queue whose consumer discards it AND writes its id into
    /// <c>processed_event</c>, so "have we seen this?" stops being a useful question. Adding a case to a
    /// projector without adding the name here is worse: the code says the fact is handled and the fact never
    /// arrives.</para>
    /// </summary>
    /// <summary>
    /// Projector cases that are knowingly unfed, each with the reason it cannot be.
    /// </summary>
    /// <remarks>
    /// An explicit list rather than a relaxed assertion. The point of the guard is that a NEW gap fails the
    /// build; a gap that is understood and documented is a different thing from one nobody has noticed, and
    /// the difference has to be written down or the guard decays into "some of these are fine".
    /// </remarks>
    private static readonly Dictionary<string, string> KnownUnfed = new(StringComparer.Ordinal)
    {
        ["DiagnosisRecorded"] = "emr-service records diagnoses but publishes no event for them. Feeding the "
            + "top-diagnoses report needs an emr publisher carrying tenant + ICD code and nothing else — the "
            + "fact is a code COUNT, so it must not carry the beneficiary it came from.",

        // `ServiceValued` WAS here, and its reason still reads true: finance publishes `SettlementApproved`,
        // a provider's settlement total, which is a different grain from "this service line was worth this
        // much". The entry is gone because claims-service now publishes `ClaimLineSettled.v1` — a claim line
        // at the moment it settles IS that grain — and `financial_fact` is fed from there instead. Recorded
        // rather than silently deleted: a register entry that disappears with no trace looks the same as one
        // that was never taken seriously.
    };

    [Fact]
    public void The_feed_and_the_projectors_cover_the_same_events_except_where_it_is_written_down()
    {
        var projected = ProjectorEventTypes();

        var reachable = ProjectionFeed.EventTypes
            .Select(ProjectionMapping.ProjectorEventType)
            .ToHashSet(StringComparer.Ordinal);

        reachable.Except(projected).Should().BeEmpty(
            "an event mirrored to the projection queue that no projector claims is traffic and a processed-event "
            + "row, and no fact");

        projected.Except(reachable).Should().BeEquivalentTo(KnownUnfed.Keys,
            "a projector case no publisher feeds is a fact table that stays empty while the code says it is "
            + "handled — every one of them must be listed in KnownUnfed with the reason");

        // And the reasons must be reasons, not placeholders.
        KnownUnfed.Values.Should().OnlyContain(r => r.Length > 40);
    }

    /// <summary>
    /// The names that differ are stated, so a rename on either side fails here rather than in silence.
    /// </summary>
    [Theory]
    [InlineData("EncounterStarted", "EncounterCreated")]
    [InlineData("ApptBooked", "AppointmentBooked")]
    [InlineData("ApptCheckedIn", "AppointmentAttended")]
    [InlineData("ApptNoShow", "AppointmentNoShow")]
    [InlineData("OrderLinesConsumed", "OrderLineConsumed")]
    [InlineData("ClaimApproved.v1", "ClaimSettled")]
    [InlineData("ClaimDenied.v1", "ClaimSettled")]
    // Already agreed — these must pass through untouched.
    [InlineData("AuthApproved", "AuthApproved")]
    [InlineData("MemberEnrolled", "MemberEnrolled")]
    [InlineData("CoverageLimitChanged", "CoverageLimitChanged")]
    public void Published_names_translate_to_the_projector_vocabulary(string published, string projected) =>
        ProjectionMapping.ProjectorEventType(published).Should().Be(projected);

    [Fact]
    public void A_payload_with_no_tenant_is_refused_rather_than_defaulted()
    {
        // Every fact table is tenant-scoped and under RLS. A guessed tenant is one organisation's numbers in
        // another organisation's dashboard, which is worse than a missing fact.
        ProjectionMapping.TryMap(Guid.NewGuid(), "MemberEnrolled", """{"enrollmentId":"x"}""", DateTimeOffset.UtcNow)
            .Should().BeNull();
        ProjectionMapping.TryMap(Guid.NewGuid(), "MemberEnrolled", "not json", DateTimeOffset.UtcNow)
            .Should().BeNull();
        ProjectionMapping.TryMap(Guid.NewGuid(), "MemberEnrolled", "[1,2]", DateTimeOffset.UtcNow)
            .Should().BeNull();
    }

    [Fact]
    public void Scalars_flatten_and_nested_structures_are_left_out()
    {
        var ev = ProjectionMapping.TryMap(Guid.NewGuid(), "MemberEnrolled", """
            {
              "tenantId": "t0", "enrollmentId": "e1", "status": "Active",
              "tatSeconds": 42, "slaBreached": true, "effectiveTo": null,
              "lines": [{"a":1}], "scope": {"b":2}
            }
            """, DateTimeOffset.UtcNow)!;

        ev.Fields["status"].Should().Be("Active");
        ev.Fields["tatSeconds"].Should().Be("42", "numbers become their raw text so long/decimal parsing still works");
        ev.Fields["slaBreached"].Should().Be("true");
        // A per-EVENT fact has no room for per-line detail, and flattening it would invite a fact at the
        // wrong grain.
        ev.Fields.Should().NotContainKey("lines").And.NotContainKey("scope").And.NotContainKey("effectiveTo");
    }

    [Theory]
    // emr books against a location; the encounter fact calls it the clinic.
    [InlineData("ApptBooked", """{"tenantId":"t0","locationId":"L1"}""", "clinicId", "L1")]
    [InlineData("ApptNoShow", """{"tenantId":"t0","locationId":"L2"}""", "clinicId", "L2")]
    // Orders splits Lab from Radiology by the order type, and calls imaging "Imaging".
    [InlineData("OrderLinesConsumed", """{"tenantId":"t0","orderType":"Imaging"}""", "modality", "Radiology")]
    [InlineData("OrderLinesConsumed", """{"tenantId":"t0","orderType":"Lab"}""", "modality", "Lab")]
    [InlineData("OrderLinesConsumed", """{"tenantId":"t0","benefitCategory":"LAB"}""", "code", "LAB")]
    // Pharmacy sends the drug id; the ATC class lives in masterdata and is not resolved on the dispense path.
    [InlineData("RxDispensed", """{"tenantId":"t0","drugId":"D9"}""", "atc", "D9")]
    [InlineData("CoverageLimitChanged", """{"tenantId":"t0","benefitCategory":"DENTAL"}""", "benefitCategoryCode", "DENTAL")]
    public void Fields_the_projectors_read_under_another_name_are_derived(
        string publishedType, string payload, string key, string expected)
    {
        var ev = ProjectionMapping.TryMap(Guid.NewGuid(), publishedType, payload, DateTimeOffset.UtcNow)!;
        ev.Fields[key].Should().Be(expected);
    }

    [Theory]
    [InlineData("Imaging")]    // enqueued BEFORE the switch, relayed after it
    [InlineData("Radiology")]  // enqueued after
    public void Order_type_maps_to_modality_under_both_spellings(string orderType)
    {
        // 29.1 / design 45 §1 (b) — the in-flight-outbox half of the rename. The outbox is durable, so events
        // enqueued while orders-service still said "Imaging" are relayed after the deploy that made it say
        // "Radiology". Both must land on ONE modality: if the legacy spelling stopped being translated, a
        // month of radiology volume would silently split across two dimension values and every utilisation
        // report over the switch window would be wrong without being empty — the failure mode that is hardest
        // to notice, because the report still renders.
        var ev = ProjectionMapping.TryMap(
            Guid.NewGuid(), "OrderLinesConsumed", $$"""{"tenantId":"t0","orderType":"{{orderType}}"}""",
            DateTimeOffset.UtcNow)!;

        ev.Fields["modality"].Should().Be("Radiology");
    }

    [Fact]
    public void Deriving_never_overwrites_a_field_the_publisher_already_set()
    {
        // The publisher is authoritative about its own event. An alias that clobbered a real value would make
        // this table the truth about a field it does not own.
        var ev = ProjectionMapping.TryMap(Guid.NewGuid(), "OrderLinesConsumed",
            """{"tenantId":"t0","orderType":"Lab","modality":"Radiology"}""", DateTimeOffset.UtcNow)!;
        ev.Fields["modality"].Should().Be("Radiology");
    }

    [Fact]
    public void The_original_event_id_is_what_the_projection_dedupes_on()
    {
        // The mirror carries the publisher's MessageId, and EventProjector dedupes on it in the same
        // transaction that writes the facts — so a relay retry cannot double-count a member.
        var id = Guid.NewGuid();
        var ev = ProjectionMapping.TryMap(id, "MemberEnrolled", """{"tenantId":"t0"}""", DateTimeOffset.UtcNow)!;
        ev.EventId.Should().Be(id);
    }

    [Fact]
    public void The_feed_carries_only_what_reporting_needs()
    {
        // An allow-list, not "mirror everything": the platform publishes over a hundred distinct event types
        // and reporting projects twenty. Mirroring the rest would be five times the traffic onto a queue that
        // discards it.
        ProjectionFeed.Includes("AuthApproved").Should().BeTrue();
        ProjectionFeed.Includes("RxCreated").Should().BeFalse();
        ProjectionFeed.Includes("ApptCancelled").Should().BeFalse();
        ProjectionFeed.Includes("ClaimAdjudicated.v1").Should().BeFalse(
            "adjudication is a pre-decision recommendation; booking it as cost would count money a reviewer "
            + "may still reduce, and again when they do");
        ProjectionFeed.Includes(null).Should().BeFalse();
    }

    /// <summary>
    /// End to end, over the datastore: a real publisher payload becomes a real fact.
    /// </summary>
    /// <remarks>
    /// The payloads below are copied from the publishers, not invented — an approvals decision as
    /// `Decisions.cs` enqueues it, an enrolment as `MembershipCommands.cs` does, a consume as `Consume.cs`
    /// does. That is the whole point: the unit tests above prove the mapping in isolation, and this proves the
    /// mapping against what is actually on the wire. A fixture written to match the mapping would agree with
    /// itself and tell us nothing.
    /// </remarks>
    [SkippableFact]
    public async Task Real_publisher_payloads_become_facts()
    {
        var db = Environment.GetEnvironmentVariable("REPORTING_TEST_DB");
        Skip.If(db is null, "REPORTING_TEST_DB not set — DB integration test skipped.");

        var tenant = "t-" + Guid.NewGuid().ToString("N")[..10];
        var at = new DateTimeOffset(2026, 7, 15, 9, 0, 0, TimeSpan.Zero);
        await using var ctx = new ReportingDbContext(
            new DbContextOptionsBuilder<ReportingDbContext>().UseNpgsql(db).UseSnakeCaseNamingConvention().Options);
        var projector = new EventProjector(ctx, TimeProvider.System,
            new BusinessCalendar(TimeProvider.System), new AnalyticsProjector(ctx, TimeProvider.System));

        try
        {
            var enrolment = $$"""
                {"tenantId":"{{tenant}}","enrollmentId":"{{Guid.NewGuid()}}","beneficiaryId":"{{Guid.NewGuid()}}",
                 "policyId":"{{Guid.NewGuid()}}","policyPlanId":"{{Guid.NewGuid()}}","memberNo":"MEM-1",
                 "effectiveFrom":"2026-07-15","payerId":"{{Guid.NewGuid()}}","relationship":"Principal","status":"Active"}
                """;
            var decision = $$"""
                {"authorizationId":"{{Guid.NewGuid()}}","authNo":"AUTH-2026-1","beneficiaryId":"{{Guid.NewGuid()}}",
                 "tenantId":"{{tenant}}","source":"Order","priority":"Urgent","reviewerId":"r-1",
                 "tatSeconds":420,"slaBreached":true,"breakGlass":false}
                """;
            var consume = $$"""
                {"orderId":"{{Guid.NewGuid()}}","orderType":"Imaging","tenantId":"{{tenant}}",
                 "beneficiaryId":"{{Guid.NewGuid()}}","benefitCategory":"RAD","serviceDate":"2026-07-15",
                 "providerId":"{{Guid.NewGuid()}}","lines":[{"orderLineId":"x","quantity":1}],"idempotencyKey":"k"}
                """;

            foreach (var (type, payload) in new[]
                     { ("MemberEnrolled", enrolment), ("AuthApproved", decision), ("OrderLinesConsumed", consume) })
            {
                var ev = ProjectionMapping.TryMap(Guid.NewGuid(), type, payload, at);
                ev.Should().NotBeNull($"{type} is on the feed and must map");
                (await projector.ProjectAsync(ev!)).Should().BeTrue($"{type} must produce a fact");
            }

            (await ctx.EnrolmentFacts.AsNoTracking().CountAsync(f => f.TenantId == tenant)).Should().Be(1);

            var auth = await ctx.AuthorizationFacts.AsNoTracking().SingleAsync(f => f.TenantId == tenant);
            // The two the decision computes and used to keep to itself — without them the approval-TAT
            // report, which is the reason an authorization read model exists, has nothing to report.
            auth.TatSeconds.Should().Be(420);
            auth.SlaBreached.Should().BeTrue();
            auth.Priority.Should().Be("Urgent");

            // Imaging is Radiology to the read model, and the benefit category is the code. Both were absent
            // from the wire, so every consumed line would have landed in Lab under "unknown".
            var util = await ctx.UtilizationFacts.AsNoTracking()
                .Where(f => f.TenantId == tenant).ToListAsync();
            util.Should().Contain(f => f.Dimension == "Radiology" && f.Code == "RAD");
            util.Should().Contain(f => f.Dimension == "Provider");
        }
        finally
        {
            // Written out rather than looped: EF's analyzer refuses an interpolated table name, and the two
            // fact families genuinely have different table names (`authorization_fact` from phase 8.2,
            // `fact_enrolment` from 19.6b) rather than one convention.
            await ctx.Database.ExecuteSqlRawAsync("DELETE FROM reporting.fact_enrolment WHERE tenant_id = {0}", tenant);
            await ctx.Database.ExecuteSqlRawAsync("DELETE FROM reporting.authorization_fact WHERE tenant_id = {0}", tenant);
            await ctx.Database.ExecuteSqlRawAsync("DELETE FROM reporting.utilization_fact WHERE tenant_id = {0}", tenant);
        }
    }

    /// <summary>
    /// The event types the two projectors actually handle, read off their source.
    /// </summary>
    /// <remarks>
    /// Read from the file rather than duplicated as a list here, deliberately: a hand-copied expectation is a
    /// third place to keep in step, and it would be updated in the same edit that broke the pipeline.
    /// </remarks>
    private static HashSet<string> ProjectorEventTypes()
    {
        var root = FindRepoRoot();
        var types = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in new[] { "EventProjector.cs", "AnalyticsProjector.cs" })
        {
            var path = Path.Combine(root, "services", "reporting", "Infrastructure", file);
            foreach (var line in File.ReadAllLines(path))
            {
                var m = System.Text.RegularExpressions.Regex.Match(line.Trim(), @"^case ""([^""]+)"":$");
                if (m.Success) types.Add(m.Groups[1].Value);
            }
        }
        types.Should().NotBeEmpty("the projector sources must be readable for this guard to mean anything");
        return types;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CLAUDE.md"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}
