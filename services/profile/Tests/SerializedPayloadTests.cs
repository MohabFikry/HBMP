using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Mersal.Authz;
using Mersal.Profile.Domain;

namespace Mersal.Profile.Tests;

/// <summary>
/// Phase 20's central proof: the role × section matrix asserted over the <b>SERIALIZED PAYLOAD</b>, for every
/// role, with every section wired to a fully-answering fixture.
///
/// <para>Over the serialized JSON and not over the object graph, deliberately. The invariant in design 39 §7.1
/// is about what leaves the process, and every historical version of this bug — a field set to null but still
/// written, a property the client was trusted to ignore, a section rendered with <c>display:none</c> — is
/// invisible to a test that inspects a C# object. The fixtures carry marker strings, so the assertion is a
/// substring search over the whole document: if a diagnosis is anywhere in what reception receives, in any
/// shape, under any key, this fails.</para>
/// </summary>
public class SerializedPayloadTests
{
    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web)
    {
        // The same setting profile-service's Program.cs configures. If it ever diverges, these tests are
        // asserting a payload nobody receives — so the API test asserts the live pipeline too.
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private static async Task<string> PayloadAsync(
        ProfileContext context, IReadOnlyList<ISectionProvider>? providers = null,
        IReadOnlyCollection<string>? sections = null)
    {
        var composer = new ProfileComposer(
            providers ?? Fixtures.AllProviders(), new ProfileCompositionOptions(), TimeProvider.System);
        var result = await composer.ComposeAsync(
            Fixtures.Beneficiary, context, sections, Fixtures.Caller());
        return JsonSerializer.Serialize(result.Profile, Wire);
    }

    private static async Task<JsonDocument> DocumentAsync(ProfileContext context) =>
        JsonDocument.Parse(await PayloadAsync(context));

    private static JsonElement? Section(JsonDocument doc, string key) =>
        doc.RootElement.GetProperty("sections").EnumerateArray()
            .Where(s => s.GetProperty("key").GetString() == key)
            .Select(s => (JsonElement?)s)
            .FirstOrDefault();

    // ---------------------------------------------------------------- reception ≠ EMR

    [Fact]
    public async Task Reception_receives_no_clinical_field_anywhere_in_the_payload()
    {
        var json = await PayloadAsync(Fixtures.Context("reception"));

        json.Should().NotContain(Fixtures.DiagnosisMarker, "reception must never receive a diagnosis");
        json.Should().NotContain(Fixtures.ResultMarker, "reception must never receive an investigation result");
        json.Should().NotContain(Fixtures.DrugMarker, "reception must never receive a prescription");
        json.Should().NotContain(Fixtures.ReasonMarker, "the reason for a visit is clinical content");
        json.Should().NotContain(Fixtures.RationaleMarker, "the clinical rationale for an authorization is clinical");
    }

    [Fact]
    public async Task Reception_gets_no_key_at_all_for_the_sections_it_may_never_see()
    {
        using var doc = await DocumentAsync(Fixtures.Context("reception"));

        // Absent, not Restricted: reception has no access to request, so a locked card would be a dead end.
        Section(doc, ProfileSections.PastMedicalHistory).Should().BeNull();
        Section(doc, ProfileSections.Investigations).Should().BeNull();
        Section(doc, ProfileSections.Prescriptions).Should().BeNull();
        Section(doc, ProfileSections.Financial).Should().BeNull();
    }

    [Fact]
    public async Task The_call_centre_receives_no_clinical_field()
    {
        var json = await PayloadAsync(Fixtures.Context("call_center"));
        json.Should().NotContain(Fixtures.DiagnosisMarker);
        json.Should().NotContain(Fixtures.ResultMarker);
        json.Should().NotContain(Fixtures.DrugMarker);
    }

    // ---------------------------------------------------------------- finance ≠ diagnoses (and ≠ photo)

    [Fact]
    public async Task Finance_receives_no_diagnosis_no_result_and_no_photo()
    {
        var json = await PayloadAsync(Fixtures.Context("finance"));

        json.Should().NotContain(Fixtures.DiagnosisMarker);
        json.Should().NotContain(Fixtures.ResultMarker);
        json.Should().NotContain(Fixtures.DrugMarker);
        json.Should().NotContain(Fixtures.AllergyMarker, "an allergy is a clinical fact, not a billing one");
        // The photo field is ABSENT for finance — not empty, not a placeholder URL.
        json.Should().NotContain("/photo", "finance is outside the design-39 §5 photo allow-list");
    }

    [Fact]
    public async Task Finance_receives_call_history_metadata_but_never_the_summary_text()
    {
        var json = await PayloadAsync(Fixtures.Context("finance"));
        json.Should().Contain(ProfileSections.CallHistory, "finance may see that a call happened");
        json.Should().NotContain(Fixtures.CallSummaryMarker, "Meta level carries no summary text (design 39 §5b)");
    }

    [Fact]
    public async Task Finance_sees_money_but_not_the_clinical_rationale_behind_an_authorization()
    {
        var json = await PayloadAsync(Fixtures.Context("finance"));
        json.Should().Contain("AUTH-2026-0011");
        json.Should().Contain("3200", "finance prices the decision");
        json.Should().NotContain(Fixtures.RationaleMarker, "and never reads the reasoning behind it");
    }

    // ---------------------------------------------------------------- provider isolation

    [Fact]
    public async Task A_lab_receives_only_identity_allergies_and_its_own_orders()
    {
        using var doc = await DocumentAsync(Fixtures.Context("lab_tech", providerId: "prov-1"));
        var keys = doc.RootElement.GetProperty("sections").EnumerateArray()
            .Select(s => s.GetProperty("key").GetString()).ToList();

        keys.Should().BeEquivalentTo([
            ProfileSections.Header, ProfileSections.Alerts, ProfileSections.Investigations]);

        var json = await PayloadAsync(Fixtures.Context("lab_tech", providerId: "prov-1"));
        json.Should().NotContain(Fixtures.DrugMarker, "a lab never receives prescriptions");
        json.Should().NotContain(Fixtures.DiagnosisMarker);
        json.Should().NotContain("/photo", "a lab is outside the photo allow-list");
    }

    [Fact]
    public async Task A_pharmacy_receives_its_own_prescriptions_and_never_a_result()
    {
        using var doc = await DocumentAsync(Fixtures.Context("pharmacist", providerId: "prov-2"));
        Section(doc, ProfileSections.Investigations).Should().BeNull("a pharmacy never receives results");

        var json = await PayloadAsync(Fixtures.Context("pharmacist", providerId: "prov-2"));
        json.Should().Contain(Fixtures.DrugMarker);
        json.Should().NotContain(Fixtures.ResultMarker);
    }

    [Fact]
    public async Task A_pharmacy_sees_the_pharmacy_limit_and_no_other_category()
    {
        var json = await PayloadAsync(Fixtures.Context("pharmacist", providerId: "prov-2"));
        json.Should().Contain("Pharmacy");
        json.Should().NotContain("Dental", "the pharmacy limit is one number, not the whole benefit schedule");
    }

    // ---------------------------------------------------------------- treating relationship

    [Fact]
    public async Task A_non_treating_doctor_gets_existence_only_with_a_request_access_action()
    {
        using var doc = await DocumentAsync(Fixtures.Context("doctor"));
        var pmh = Section(doc, ProfileSections.PastMedicalHistory)!.Value;

        pmh.GetProperty("state").GetString().Should().Be("Restricted");
        pmh.GetProperty("reasonCode").GetString().Should().Be(ProfileReasons.NotTreating);
        pmh.TryGetProperty("data", out _).Should().BeFalse("a withheld section carries NO data property at all");
        pmh.TryGetProperty("requestAccessAction", out _).Should().BeTrue(
            "a doctor who may not see this must be offered the way to ask");

        var json = await PayloadAsync(Fixtures.Context("doctor"));
        json.Should().NotContain(Fixtures.DiagnosisMarker);
        json.Should().NotContain(Fixtures.ResultMarker);
    }

    [Fact]
    public async Task A_treating_doctor_receives_the_clinical_record()
    {
        var json = await PayloadAsync(Fixtures.Context("doctor", treating: true));
        json.Should().Contain(Fixtures.DiagnosisMarker);
        json.Should().Contain(Fixtures.ResultMarker);
        json.Should().Contain(Fixtures.DrugMarker);
        json.Should().Contain("/photo", "a treating clinician needs to know they are with the right patient");
        json.Should().NotContain("CLM-2026-0031", "a clinician still never receives the claim ledger");
    }

    // ---------------------------------------------------------------- the sensitive gate

    [Fact]
    public async Task A_restricted_result_reaches_the_approval_team_as_existence_only()
    {
        var json = await PayloadAsync(
            Fixtures.Context("medical_approval"), Fixtures.AllProviders(restrictedResult: true));

        json.Should().Contain("ORD-2026-0007", "the approval team sees that an investigation exists");
        json.Should().Contain("restricted");
        json.Should().NotContain(Fixtures.ResultMarker,
            "design 39 §4 note *: sensitive results stay existence-only for the approval team without a grant");
    }

    [Fact]
    public async Task A_leaked_sensitive_value_is_stripped_even_if_the_owning_service_sends_one()
    {
        // Defence in depth. If orders-service ever regresses and sends a value on a line it marked restricted,
        // the profile must not be the thing that publishes it. This test exists to fail loudly if that stops
        // being true — the profile is the LAST place a sensitive result can leak from, not the first.
        var providers = Fixtures.AllProviders().ToList();
        providers[providers.FindIndex(p => p.Key == ProfileSections.Investigations)] =
            new FakeProvider(ProfileSections.Investigations, Fixtures.LeakyInvestigations());

        var json = await PayloadAsync(Fixtures.Context("medical_approval"), providers);
        json.Should().NotContain(Fixtures.ResultMarker);
    }

    // ---------------------------------------------------------------- case assignment

    [Fact]
    public async Task An_assigned_case_manager_coordinates_without_reading_results()
    {
        using var doc = await DocumentAsync(Fixtures.Context("case_manager", assigned: true));
        Section(doc, ProfileSections.CaseManagement)!.Value.GetProperty("state").GetString()
            .Should().Be("Visible");
        Section(doc, ProfileSections.Investigations)!.Value.GetProperty("state").GetString()
            .Should().Be("Restricted");

        var json = await PayloadAsync(Fixtures.Context("case_manager", assigned: true));
        json.Should().NotContain(Fixtures.ResultMarker);
        json.Should().NotContain(Fixtures.DrugMarker);
        json.Should().Contain(Fixtures.DiagnosisMarker, "a coded condition is coordination-visible in summary");
        json.Should().NotContain("Long-standing, diet controlled",
            "the summary variant drops the clinician's narrative");
    }

    // ---------------------------------------------------------------- class-filtered lists

    [Fact]
    public async Task An_administrative_caller_sees_administrative_notes_and_documents_only()
    {
        var json = await PayloadAsync(Fixtures.Context("reception"));
        json.Should().Contain("Prefers morning appointments");
        json.Should().NotContain(Fixtures.DiagnosisMarker,
            "a clinical note and a clinical document both carry the diagnosis marker");
    }

    [Fact]
    public async Task A_platform_admin_sees_who_looked_never_what_was_found()
    {
        using var doc = await DocumentAsync(Fixtures.Context("super_admin"));
        var keys = doc.RootElement.GetProperty("sections").EnumerateArray()
            .Select(s => s.GetProperty("key").GetString()).ToList();
        keys.Should().BeEquivalentTo([ProfileSections.Header, ProfileSections.Timeline]);

        var json = await PayloadAsync(Fixtures.Context("super_admin"));
        json.Should().Contain("ProfileViewed", "the ACCESS timeline is an admin's business");
        json.Should().NotContain("PlanChanged", "the administrative timeline is not");
        json.Should().NotContain(Fixtures.DiagnosisMarker);
        json.Should().NotContain("/photo");
    }

    // ---------------------------------------------------------------- the ?sections= subset

    [Fact]
    public async Task The_context_bar_subset_returns_header_and_alerts_only()
    {
        using var doc = JsonDocument.Parse(await PayloadAsync(
            Fixtures.Context("doctor", treating: true), sections: ProfileSections.ContextBar));

        doc.RootElement.GetProperty("sections").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task Asking_for_a_section_you_may_not_see_does_not_get_you_it()
    {
        // The subset narrows; it never widens. A reception client asking for `investigations` gets the same
        // nothing it gets by not asking.
        using var doc = JsonDocument.Parse(await PayloadAsync(
            Fixtures.Context("reception"), sections: [ProfileSections.Investigations, ProfileSections.Header]));

        var keys = doc.RootElement.GetProperty("sections").EnumerateArray()
            .Select(s => s.GetProperty("key").GetString()).ToList();
        keys.Should().BeEquivalentTo([ProfileSections.Header]);
    }

    // ---------------------------------------------------------------- table-driven sweep

    public static TheoryData<string> EveryRole()
    {
        var data = new TheoryData<string>();
        foreach (var role in ProfilePolicies.KnownRoles) data.Add(role);
        return data;
    }

    [Theory]
    [MemberData(nameof(EveryRole))]
    public async Task No_role_receives_a_section_the_matrix_withholds(string role)
    {
        // The sweep: for EVERY role the matrix names, every section present in the serialized payload with data
        // must be one the matrix decided is Visible. This is the assertion that survives a new section being
        // added — the specific tests above would not notice section 16.
        var context = Fixtures.Context(role, treating: true, assigned: true, providerId: "prov-1");
        using var doc = await DocumentAsync(context);

        var allowed = ProfilePolicies.DecideAll(context)
            .Where(d => d.State == ProfileSectionState.Visible)
            .Select(d => d.Key)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var section in doc.RootElement.GetProperty("sections").EnumerateArray())
        {
            var key = section.GetProperty("key").GetString()!;
            if (section.TryGetProperty("data", out _))
                allowed.Should().Contain(key, "'{0}' received data for '{1}' without a Visible cell", role, key);
        }
    }

    [Theory]
    [MemberData(nameof(EveryRole))]
    public async Task Only_the_photo_allow_list_receives_a_photo_reference(string role)
    {
        var json = await PayloadAsync(Fixtures.Context(role, treating: true, assigned: true));
        var mayView = ProfilePhotoAccess.MayView([role]);

        if (mayView) json.Should().Contain("/photo", "'{0}' is on the design-39 §5 allow-list", role);
        else json.Should().NotContain("/photo", "'{0}' is NOT on the design-39 §5 allow-list", role);
    }
}
