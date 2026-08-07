using System.Text.Json;
using FluentAssertions;
using Mersal.Approvals.Domain;

namespace Mersal.Approvals.Tests;

/// <summary>
/// Which rule applies to a request (ADR-0035 §5.1/§5.4).
/// </summary>
/// <remarks>
/// <para>
/// This decides which desk a beneficiary's request lands on and how long the reviewer has. It is pure and
/// synchronous precisely so that decision can be tested without a database, a clock or a server — and so the
/// same inputs always give the same answer, which is what makes a routing decision explainable weeks later
/// when somebody asks why a request sat where it sat.
/// </para>
/// <para>
/// Routing and SLA were chosen as the first engine family because they change WHO decides and BY WHEN, never
/// WHAT is decided. Nothing here can approve or refuse anything.
/// </para>
/// </remarks>
public class RuleEvaluatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 5, 10, 0, 0, TimeSpan.Zero);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Exactly what the API uses — string enums both ways. See the wire-format tests below.</summary>
    private static readonly JsonSerializerOptions WireJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private static RulePredicate? Parse(ApprovalRule r)
    {
        try { return JsonSerializer.Deserialize<RulePredicate>(r.PredicateJson, Json); }
        catch (JsonException) { return null; }
    }

    private static ApprovalRule Rule(
        int priority, RulePredicate predicate, string queue = "specialist",
        Guid? id = null, bool enabled = true,
        DateTimeOffset? from = null, DateTimeOffset? to = null, RuleFamily family = RuleFamily.Routing) =>
        new()
        {
            RuleId = id ?? Guid.NewGuid(),
            TenantId = "t1",
            Family = family,
            Priority = priority,
            PredicateJson = JsonSerializer.Serialize(predicate, Json),
            ActionJson = JsonSerializer.Serialize(new RoutingAction(queue), Json),
            EffectiveFrom = from ?? Now.AddDays(-1),
            EffectiveTo = to,
            Enabled = enabled,
            Rationale = "because",
        };

    private static RuleFacts Facts(
        AuthPriority priority = AuthPriority.Routine,
        AuthSource source = AuthSource.OrderLine,
        AuthKind kind = AuthKind.Review,
        string[]? codes = null,
        Guid? provider = null,
        string? category = null,
        decimal? amount = null) =>
        new(priority, source, kind, codes ?? [], provider, category, amount);

    // ---- matching -----------------------------------------------------------------------------

    [Fact]
    public void An_empty_predicate_matches_everything()
    {
        // The catch-all a supervisor writes last, to give anything unmatched a home.
        new RulePredicate().Matches(Facts()).Should().BeTrue();
        new RulePredicate().IsCatchAll.Should().BeTrue();
    }

    [Fact]
    public void Present_fields_are_ANDed_not_ORed()
    {
        // "Urgent pharmacy requests" and "anything urgent, plus all pharmacy" would be the same rule text
        // under OR, and the author would have no way to say which they meant.
        var p = new RulePredicate { Priority = AuthPriority.Urgent, Source = AuthSource.Prescription };

        p.Matches(Facts(AuthPriority.Urgent, AuthSource.Prescription)).Should().BeTrue();
        p.Matches(Facts(AuthPriority.Urgent, AuthSource.OrderLine)).Should().BeFalse();
        p.Matches(Facts(AuthPriority.Routine, AuthSource.Prescription)).Should().BeFalse();
    }

    [Fact]
    public void Service_codes_match_ANY_of_the_listed_ones()
    {
        // A request carries several codes, and "a request containing an MRI" is the question people ask.
        var p = new RulePredicate { ServiceCodes = ["MRI-01", "CT-02"] };

        p.Matches(Facts(codes: ["XR-09", "MRI-01"])).Should().BeTrue();
        p.Matches(Facts(codes: ["XR-09"])).Should().BeFalse();
        p.Matches(Facts(codes: [])).Should().BeFalse();
    }

    [Fact]
    public void A_service_code_matches_regardless_of_case()
    {
        // A code is an identifier. A rule that silently never fires because somebody typed it lower-case is
        // indistinguishable, from the editor, from a rule that is working.
        new RulePredicate { ServiceCodes = ["mri-01"] }
            .Matches(Facts(codes: ["MRI-01"])).Should().BeTrue();
    }

    [Fact]
    public void The_service_code_list_is_still_ANDed_with_the_other_fields()
    {
        var p = new RulePredicate { Priority = AuthPriority.Emergency, ServiceCodes = ["MRI-01"] };
        p.Matches(Facts(AuthPriority.Routine, codes: ["MRI-01"])).Should().BeFalse();
        p.Matches(Facts(AuthPriority.Emergency, codes: ["MRI-01"])).Should().BeTrue();
    }

    // ---- ordering -----------------------------------------------------------------------------

    [Fact]
    public void The_lowest_priority_number_wins()
    {
        var specific = Rule(10, new RulePredicate { Priority = AuthPriority.Urgent }, "urgent-desk");
        var catchAll = Rule(100, new RulePredicate(), "default-desk");

        var match = RuleEvaluator.FirstMatch(
            [catchAll, specific], RuleFamily.Routing, Now, Facts(AuthPriority.Urgent), Parse);

        match.Should().Be(specific);
    }

    [Fact]
    public void Two_rules_at_the_same_priority_always_resolve_the_same_way()
    {
        // Ties break on RuleId. Without a total order, which one wins depends on the order the database
        // happened to return rows — the same request routed two ways on two days, with nothing changed and
        // nothing to point at.
        var a = Rule(10, new RulePredicate(), "a", id: Guid.Parse("11111111-1111-1111-1111-111111111111"));
        var b = Rule(10, new RulePredicate(), "b", id: Guid.Parse("22222222-2222-2222-2222-222222222222"));

        RuleEvaluator.FirstMatch([b, a], RuleFamily.Routing, Now, Facts(), Parse).Should().Be(a);
        RuleEvaluator.FirstMatch([a, b], RuleFamily.Routing, Now, Facts(), Parse).Should().Be(a);
    }

    [Fact]
    public void A_rule_of_another_family_is_never_considered()
    {
        // An SLA rule cannot route and a routing rule cannot set a deadline. Mixing them would let a
        // supervisor's change to one silently alter the other.
        var slaRule = Rule(1, new RulePredicate(), family: RuleFamily.Sla);
        RuleEvaluator.FirstMatch([slaRule], RuleFamily.Routing, Now, Facts(), Parse).Should().BeNull();
    }

    // ---- effective dating ---------------------------------------------------------------------

    [Fact]
    public void A_rule_that_has_not_started_yet_does_not_apply()
    {
        var future = Rule(1, new RulePredicate(), from: Now.AddDays(1));
        RuleEvaluator.FirstMatch([future], RuleFamily.Routing, Now, Facts(), Parse).Should().BeNull();
    }

    [Fact]
    public void A_rule_whose_window_has_closed_does_not_apply()
    {
        var past = Rule(1, new RulePredicate(), from: Now.AddDays(-10), to: Now.AddDays(-1));
        RuleEvaluator.FirstMatch([past], RuleFamily.Routing, Now, Facts(), Parse).Should().BeNull();
    }

    [Fact]
    public void A_decision_can_be_explained_against_the_rules_in_force_at_ITS_time()
    {
        // The whole point of effective dating: a request routed last Tuesday must be explainable against last
        // Tuesday's rules, not today's.
        var old = Rule(1, new RulePredicate(), "old-desk", from: Now.AddDays(-10), to: Now.AddDays(-2));
        var current = Rule(1, new RulePredicate(), "new-desk", from: Now.AddDays(-2));

        RuleEvaluator.FirstMatch([old, current], RuleFamily.Routing, Now.AddDays(-5), Facts(), Parse)
            .Should().Be(old);
        RuleEvaluator.FirstMatch([old, current], RuleFamily.Routing, Now, Facts(), Parse)
            .Should().Be(current);
    }

    [Fact]
    public void A_disabled_rule_does_not_apply_even_inside_its_window()
    {
        var off = Rule(1, new RulePredicate(), enabled: false);
        RuleEvaluator.FirstMatch([off], RuleFamily.Routing, Now, Facts(), Parse).Should().BeNull();
    }

    // ---- failing safely -----------------------------------------------------------------------

    [Fact]
    public void A_malformed_predicate_is_skipped_and_never_treated_as_a_catch_all()
    {
        // The dangerous reading. A predicate that will not parse must NOT be taken to match everything — a
        // malformed catch-all would swallow the entire queue onto one desk, and the rule would look fine in
        // the list. It is skipped, and the rule below it gets its chance.
        var broken = Rule(1, new RulePredicate());
        broken.PredicateJson = "{ this is not json";
        var good = Rule(2, new RulePredicate(), "real-desk");

        var match = RuleEvaluator.FirstMatch([broken, good], RuleFamily.Routing, Now, Facts(), Parse);
        match.Should().Be(good);
    }

    [Fact]
    public void No_rules_at_all_means_no_match_and_the_caller_falls_back()
    {
        // Not an exception, and not a queue invented here. The caller owns the fallback, and it is
        // DefaultQueue — routing must never strand work where nobody is looking.
        RuleEvaluator.FirstMatch([], RuleFamily.Routing, Now, Facts(), Parse).Should().BeNull();
        RuleEvaluator.DefaultQueue.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void The_default_queue_is_a_real_place_and_not_an_empty_string()
    {
        // A request that matched nothing still has to land somewhere a human is looking. "No queue" would
        // leave it invisible, which is worse than routing it imperfectly.
        RuleEvaluator.DefaultQueue.Should().Be("default");
    }

    // ---- the WIRE format, not a round trip ---------------------------------------------------

    [Fact]
    public void A_predicate_written_the_way_the_WIRE_writes_it_parses()
    {
        // The defect this exists for, found by probing the live endpoint and not by any test above.
        // `JsonSerializerDefaults.Web` gives camelCase and case-insensitive properties but still expects
        // enums as NUMBERS, so a predicate arriving as {"priority":"Emergency"} failed to parse and the rule
        // was refused as malformed — with a message saying the conditions could not be read, which was true
        // and useless.
        //
        // None of the tests above could catch it: they SERIALIZE with the same options they deserialize with,
        // so a number goes in and a number comes out and the round trip is always clean. This one pins a
        // literal string, which is what a browser actually sends.
        var wire = """{"priority":"Emergency","source":"Prescription","serviceCodes":["MRI-01"]}""";

        var parsed = JsonSerializer.Deserialize<RulePredicate>(wire, WireJson);

        parsed.Should().NotBeNull();
        parsed!.Priority.Should().Be(AuthPriority.Emergency);
        parsed.Source.Should().Be(AuthSource.Prescription);
        parsed.ServiceCodes.Should().BeEquivalentTo(["MRI-01"]);
    }

    [Fact]
    public void An_enum_written_as_a_NUMBER_is_still_read()
    {
        // Backwards compatibility: any rule stored before the converter was added holds its enums as numbers,
        // and those rules are live. Refusing them would silently disable a supervisor's existing routing.
        var parsed = JsonSerializer.Deserialize<RulePredicate>("""{"priority":2}""", WireJson);
        parsed!.Priority.Should().Be(AuthPriority.Emergency);
    }

    // ---- pre-auth triggers: additive only (ADR-0035 §5.2) ---------------------------------------

    [Fact]
    public void A_preauth_action_has_no_way_to_say_STOP_requiring()
    {
        // The invariant is STRUCTURAL, not checked. `PreauthAction` carries a reason and nothing else, so
        // there is no rule anybody can write — by hand, by migration, or by a future author who thought a
        // boolean would be convenient — that removes a requirement. The plan version's RequiresPreauth is a
        // contractual term between the payer and Mersal; a local rule able to switch it off would silently
        // override a contract and surface months later as a denied claim nobody could trace.
        var fields = typeof(PreauthAction).GetProperties().Select(p => p.Name).ToList();
        fields.Should().BeEquivalentTo(["Reason"]);
        fields.Should().NotContain(f => f.Contains("Require", StringComparison.OrdinalIgnoreCase));
        typeof(PreauthAction).GetProperties()
            .Should().NotContain(p => p.PropertyType == typeof(bool) || p.PropertyType == typeof(bool?));
    }

    [Fact]
    public void An_amount_floor_matches_at_and_above_the_threshold()
    {
        var p = new RulePredicate { AmountAtLeast = 5000m };
        p.Matches(Facts(amount: 5000m)).Should().BeTrue();
        p.Matches(Facts(amount: 7500m)).Should().BeTrue();
        p.Matches(Facts(amount: 4999m)).Should().BeFalse();
    }

    [Fact]
    public void An_UNKNOWN_amount_does_NOT_clear_an_amount_floor()
    {
        // Strict predicate semantics: a figure nobody supplied cannot be shown to be at or above the floor, so
        // the rule stays out of it rather than guessing in either direction.
        //
        // This is NOT the "care nobody could price" case, and it is worth being exact about the difference,
        // because the two look alike. A service the plan cannot price makes RequiresPreauthAsync
        // indeterminate, which requires authorization BEFORE any rule is consulted — so that case is gated by
        // the path above this one, not by the predicate. What is left here is a caller who could send an
        // amount and did not, and the answer to that is to make the amount mandatory on the question rather
        // than to have a predicate quietly gate everything it was not told about.
        new RulePredicate { AmountAtLeast = 5000m }.Matches(Facts(amount: null)).Should().BeFalse();
    }

    [Fact]
    public void A_benefit_category_matches_regardless_of_case()
    {
        new RulePredicate { BenefitCategory = "imaging" }.Matches(Facts(category: "IMAGING")).Should().BeTrue();
        new RulePredicate { BenefitCategory = "IMAGING" }.Matches(Facts(category: "PHARMACY")).Should().BeFalse();
        // A request with no category does not match a category rule — same reasoning as the amount floor.
        new RulePredicate { BenefitCategory = "IMAGING" }.Matches(Facts(category: null)).Should().BeFalse();
    }

    [Fact]
    public void A_category_and_an_amount_are_ANDed()
    {
        var p = new RulePredicate { BenefitCategory = "IMAGING", AmountAtLeast = 5000m };
        p.Matches(Facts(category: "IMAGING", amount: 9000m)).Should().BeTrue();
        p.Matches(Facts(category: "IMAGING", amount: 100m)).Should().BeFalse();
        p.Matches(Facts(category: "LAB", amount: 9000m)).Should().BeFalse();
    }

    [Fact]
    public void A_preauth_rule_is_not_considered_when_asking_about_routing()
    {
        var preauth = Rule(1, new RulePredicate(), family: RuleFamily.Preauth);
        RuleEvaluator.FirstMatch([preauth], RuleFamily.Routing, Now, Facts(), Parse).Should().BeNull();
    }

    [Fact]
    public void A_predicate_with_only_a_category_or_amount_is_NOT_a_catch_all()
    {
        // The catch-all check gates whether a preauth rule may be saved at all, so it has to count the fields
        // preauth actually uses. Missing them would let "anything over 5000" be refused as unconditional.
        new RulePredicate { BenefitCategory = "IMAGING" }.IsCatchAll.Should().BeFalse();
        new RulePredicate { AmountAtLeast = 1m }.IsCatchAll.Should().BeFalse();
        new RulePredicate().IsCatchAll.Should().BeTrue();
    }
}
