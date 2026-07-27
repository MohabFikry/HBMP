using System.Text.Json;
using FluentAssertions;
using Mersal.Authz;
using Mersal.Profile.Domain;

namespace Mersal.Profile.Tests;

/// <summary>
/// The composition engine's own behaviour: degradation, the three distinct states, the caller-token invariant,
/// and the served/withheld report the ProfileViewed audit event is built from.
/// </summary>
public class CompositionTests
{
    private static ProfileComposer Composer(IReadOnlyList<ISectionProvider> providers, ProfileCompositionOptions? options = null) =>
        new(providers, options ?? new ProfileCompositionOptions(), TimeProvider.System);

    private static ProfileSection SectionOf(PatientProfile profile, string key) =>
        profile.Sections.Single(s => s.Key == key);

    // ---------------------------------------------------------------- degradation

    [Fact]
    public async Task One_failing_service_degrades_its_section_and_nothing_else()
    {
        var providers = Fixtures.AllProviders().ToList();
        providers[providers.FindIndex(p => p.Key == ProfileSections.Encounters)] =
            new BrokenProvider(ProfileSections.Encounters);

        var result = await Composer(providers).ComposeAsync(
            Fixtures.Beneficiary, Fixtures.Context("doctor", treating: true), null, Fixtures.Caller());

        SectionOf(result.Profile, ProfileSections.Encounters).State.Should().Be("Unavailable");
        SectionOf(result.Profile, ProfileSections.Investigations).State.Should().Be("Visible");
        SectionOf(result.Profile, ProfileSections.Prescriptions).State.Should().Be("Visible");
        result.Report.Unavailable.Should().ContainSingle().Which.Should().Be(ProfileSections.Encounters);
    }

    [Fact]
    public async Task A_timing_out_service_is_Unavailable_not_empty()
    {
        // The distinction this test defends: "emr did not answer" and "this patient has no encounters" must not
        // render the same way. A clinician who reads the second when the first is true has been misinformed.
        var providers = Fixtures.AllProviders().ToList();
        providers[providers.FindIndex(p => p.Key == ProfileSections.Encounters)] =
            new HangingProvider(ProfileSections.Encounters);

        var result = await Composer(providers, new ProfileCompositionOptions
        {
            SectionTimeout = TimeSpan.FromMilliseconds(50),
            OverallBudget = TimeSpan.FromSeconds(5),
        }).ComposeAsync(Fixtures.Beneficiary, Fixtures.Context("doctor", treating: true), null, Fixtures.Caller());

        var section = SectionOf(result.Profile, ProfileSections.Encounters);
        section.State.Should().Be("Unavailable");
        section.ReasonCode.Should().Be("timeout");
        section.Data.Should().BeNull();
    }

    [Fact]
    public async Task A_section_with_nothing_to_show_is_NotApplicable_not_Unavailable()
    {
        var providers = Fixtures.AllProviders().ToList();
        providers[providers.FindIndex(p => p.Key == ProfileSections.Referrals)] =
            new FakeProvider(ProfileSections.Referrals, null);

        var result = await Composer(providers).ComposeAsync(
            Fixtures.Beneficiary, Fixtures.Context("doctor", treating: true), null, Fixtures.Caller());

        SectionOf(result.Profile, ProfileSections.Referrals).State.Should().Be("NotApplicable");
    }

    [Fact]
    public async Task Restricted_Unavailable_and_NotApplicable_are_three_distinct_states()
    {
        var providers = Fixtures.AllProviders().ToList();
        providers[providers.FindIndex(p => p.Key == ProfileSections.Referrals)] =
            new FakeProvider(ProfileSections.Referrals, null);
        providers[providers.FindIndex(p => p.Key == ProfileSections.Encounters)] =
            new BrokenProvider(ProfileSections.Encounters);

        // A case manager WITHOUT an assignment: caseManagement is Restricted, encounters would be too — so use a
        // treating doctor for the Unavailable/NotApplicable pair and read the Restricted one from investigations.
        var result = await Composer(providers).ComposeAsync(
            Fixtures.Beneficiary, Fixtures.Context("case_manager", assigned: true), null, Fixtures.Caller());

        SectionOf(result.Profile, ProfileSections.Investigations).State.Should().Be("Restricted");
        SectionOf(result.Profile, ProfileSections.Encounters).State.Should().Be("Unavailable");
        SectionOf(result.Profile, ProfileSections.Referrals).State.Should().Be("NotApplicable");
    }

    // ---------------------------------------------------------------- gate before fetch

    [Fact]
    public async Task A_section_the_caller_may_never_see_is_never_fetched()
    {
        // Cheaper, and it means the owning service is never asked about a patient on behalf of someone with no
        // business asking — which would leave a PHI-read audit trail in emr for an access that never happened.
        var pmh = new FakeProvider(ProfileSections.PastMedicalHistory, Fixtures.Pmh());
        var providers = Fixtures.AllProviders().ToList();
        providers[providers.FindIndex(p => p.Key == ProfileSections.PastMedicalHistory)] = pmh;

        await Composer(providers).ComposeAsync(
            Fixtures.Beneficiary, Fixtures.Context("reception"), null, Fixtures.Caller());

        pmh.Calls.Should().Be(0, "reception has no past-medical-history cell, so emr is never called");
    }

    [Fact]
    public async Task A_restricted_section_is_never_fetched_either()
    {
        var investigations = new FakeProvider(ProfileSections.Investigations, Fixtures.Investigations());
        var providers = Fixtures.AllProviders().ToList();
        providers[providers.FindIndex(p => p.Key == ProfileSections.Investigations)] = investigations;

        await Composer(providers).ComposeAsync(
            Fixtures.Beneficiary, Fixtures.Context("doctor"), null, Fixtures.Caller());

        investigations.Calls.Should().Be(0,
            "a non-treating doctor is told the section exists — the data is never retrieved to tell them");
    }

    // ---------------------------------------------------------------- the caller-token invariant

    [Fact]
    public async Task Composing_without_the_callers_bearer_is_refused_outright()
    {
        // Invariant 2 at runtime, not only in the architecture test. There is no service-account fallback to
        // reach for, so the only correct behaviour when the caller's token is absent is to refuse.
        var act = async () => await Composer(Fixtures.AllProviders()).ComposeAsync(
            Fixtures.Beneficiary, Fixtures.Context("doctor", treating: true), null,
            new CallerCredentials(string.Empty, null, null));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*CALLER'S token*");
    }

    [Fact]
    public async Task A_non_bearer_authorization_header_is_refused_too()
    {
        var act = async () => await Composer(Fixtures.AllProviders()).ComposeAsync(
            Fixtures.Beneficiary, Fixtures.Context("doctor", treating: true), null,
            new CallerCredentials("Basic dXNlcjpwYXNz", null, null));

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // ---------------------------------------------------------------- the audit report

    [Fact]
    public async Task The_report_names_served_and_withheld_sections_separately()
    {
        var result = await Composer(Fixtures.AllProviders()).ComposeAsync(
            Fixtures.Beneficiary, Fixtures.Context("doctor"), null, Fixtures.Caller());

        result.Report.Served.Should().Contain(ProfileSections.Header);
        result.Report.Withheld.Should().Contain(ProfileSections.PastMedicalHistory);
        // "Opened the profile" without "and these sections were withheld" cannot answer an access review.
        CompositionReport.Describe(result.Profile.Sections)
            .Should().Contain($"{ProfileSections.PastMedicalHistory}:Restricted");
    }

    [Fact]
    public async Task Sections_arrive_in_design_39_render_order_with_alerts_pinned_under_the_header()
    {
        var result = await Composer(Fixtures.AllProviders()).ComposeAsync(
            Fixtures.Beneficiary, Fixtures.Context("doctor", treating: true), null, Fixtures.Caller());

        var keys = result.Profile.Sections.Select(s => s.Key).ToList();
        keys[0].Should().Be(ProfileSections.Header);
        keys[1].Should().Be(ProfileSections.Alerts);
        keys.Should().BeInAscendingOrder(Comparer<string>.Create(
            (a, b) => ProfileSections.All.ToList().IndexOf(a).CompareTo(ProfileSections.All.ToList().IndexOf(b))));
    }

    // ---------------------------------------------------------------- the projector's own exhaustiveness

    [Fact]
    public void The_projector_handles_every_section_key()
    {
        // An unhandled key falls through to null and the section reports Unavailable. That is safe, but silently
        // safe — this test makes adding a section without teaching the projector a build failure instead.
        foreach (var key in ProfileSections.All)
        {
            SectionProjection.ExpectedPayloadTypes.Should().ContainKey(key);
            var payload = SamplePayload(key);
            SectionProjection.Apply(key, payload, null, mayViewPhoto: true)
                .Should().NotBeNull("section '{0}' has no case in the projector", key);
        }
    }

    [Fact]
    public void An_unrecognised_payload_is_dropped_rather_than_passed_through()
    {
        SectionProjection.Apply(ProfileSections.Header, new { name = "leak" }, null, true).Should().BeNull();
    }

    private static object SamplePayload(string key) => key switch
    {
        ProfileSections.Header => Fixtures.Header(),
        ProfileSections.Alerts => Fixtures.Alerts(),
        ProfileSections.Coverage => Fixtures.Coverage(),
        ProfileSections.PastMedicalHistory => Fixtures.Pmh(),
        ProfileSections.Encounters => Fixtures.Encounters(),
        ProfileSections.Investigations => Fixtures.Investigations(),
        ProfileSections.Prescriptions => Fixtures.Prescriptions(),
        ProfileSections.Authorizations => Fixtures.Authorizations(),
        ProfileSections.Referrals => Fixtures.Referrals(),
        ProfileSections.Documents => Fixtures.Documents(),
        ProfileSections.Notes => Fixtures.Notes(),
        ProfileSections.Financial => Fixtures.Financial(),
        ProfileSections.CaseManagement => Fixtures.Cases(),
        ProfileSections.Timeline => Fixtures.Timeline(),
        ProfileSections.CallHistory => Fixtures.CallHistory("Full"),
        _ => throw new InvalidOperationException($"no sample payload for '{key}'"),
    };

    // ---------------------------------------------------------------- ?sections= parsing

    [Fact]
    public void An_entirely_unrecognised_section_list_means_everything_not_nothing()
    {
        // A typo must not silently return an empty profile — which reads exactly like a patient with no record.
        Api.ProfileEndpoints.ParseSections("hedaer,alerst").Should().BeNull();
        Api.ProfileEndpoints.ParseSections("header,nonsense").Should().BeEquivalentTo([ProfileSections.Header]);
        Api.ProfileEndpoints.ParseSections(null).Should().BeNull();
    }

    // ---------------------------------------------------------------- shared upstream calls

    [Fact]
    public async Task Sections_that_share_an_upstream_do_not_call_it_twice()
    {
        // Not a performance nicety: four calls to policy's administrative-360 would write four PHI-read audit
        // events, and one user's single glance at a patient would read as four accesses in the review.
        var json = JsonDocument.Parse("""{"memberships":[],"documents":[],"notes":[]}""");
        var counting = new CountingHttp(json);
        var source = new Infrastructure.AdministrativeSource(counting.AsCallerScopedHttp());

        var caller = Fixtures.Caller();
        await Task.WhenAll(
            source.GetAsync(Fixtures.Beneficiary, caller, default).AsTask(),
            source.GetAsync(Fixtures.Beneficiary, caller, default).AsTask(),
            source.GetAsync(Fixtures.Beneficiary, caller, default).AsTask());

        counting.Calls.Should().Be(1);
        source.Dispose();
    }
}

internal static class TaskExtensions
{
    public static Task<T> AsTask<T>(this Task<T> task) => task;
}
