using FluentAssertions;
using Mersal.Patient.Domain;

namespace Mersal.Patient.Tests;

public class RegistrationTests
{
    private sealed class FakeLookup(params (IdentifierType, string, Guid)[] existing) : IIdentifierLookup
    {
        private readonly Dictionary<(IdentifierType, string), Guid> _map =
            existing.ToDictionary(e => (e.Item1, IdentifierValidation.Normalize(e.Item2)), e => e.Item3);
        public Task<Guid?> FindActiveOwnerAsync(IdentifierType type, string norm, CancellationToken ct = default) =>
            Task.FromResult(_map.TryGetValue((type, norm), out var id) ? id : (Guid?)null);
    }

    private static RegisterBeneficiaryRequest Req(params NewIdentifier[] ids) =>
        new("Amina", "Yusuf", new DateOnly(1990, 1, 1), "F", "SY", ids, []);

    [Fact]
    public async Task Valid_single_identifier_creates_pending_beneficiary()
    {
        var reg = new BeneficiaryRegistrar(new FakeLookup(), TimeProvider.System);

        var result = await reg.RegisterAsync(Req(new NewIdentifier(IdentifierType.UNHCRNo, "123-45C67890", "SY", true)), "officer-1");

        var created = result.Should().BeOfType<RegistrationResult.Created>().Subject;
        created.Beneficiary.Status.Should().Be(BeneficiaryStatus.Pending);   // US-001: created Pending
        created.Beneficiary.Identifiers.Should().ContainSingle();
        created.Beneficiary.MemberNo.Should().BeNull();                      // issued only at activation
    }

    [Fact]
    public async Task Duplicate_active_identifier_returns_existing_id_not_a_new_row()
    {
        var existingId = Guid.NewGuid();
        var reg = new BeneficiaryRegistrar(new FakeLookup((IdentifierType.NationalID, "29001011234567", existingId)), TimeProvider.System);

        var result = await reg.RegisterAsync(Req(new NewIdentifier(IdentifierType.NationalID, "29001011234567")), "officer-1");

        var dup = result.Should().BeOfType<RegistrationResult.DuplicateIdentifier>().Subject;
        dup.ExistingBeneficiaryId.Should().Be(existingId);   // US-001: name the existing record, no duplicate
    }

    [Fact]
    public async Task Duplicate_detection_is_normalization_insensitive()
    {
        var existingId = Guid.NewGuid();
        var reg = new BeneficiaryRegistrar(new FakeLookup((IdentifierType.Passport, "AB123456", existingId)), TimeProvider.System);

        // same value with spaces / lower-case → still a duplicate
        var result = await reg.RegisterAsync(Req(new NewIdentifier(IdentifierType.Passport, " ab 123456 ")), "o");
        result.Should().BeOfType<RegistrationResult.DuplicateIdentifier>();
    }

    [Fact]
    public async Task Missing_name_or_identifier_is_invalid_with_field_list()
    {
        var reg = new BeneficiaryRegistrar(new FakeLookup(), TimeProvider.System);

        var result = await reg.RegisterAsync(new RegisterBeneficiaryRequest("", "", null, null, null, [], []), "o");

        var invalid = result.Should().BeOfType<RegistrationResult.Invalid>().Subject;
        invalid.Errors.Should().Contain(e => e.Contains("givenName"))
            .And.Contain(e => e.Contains("familyName"))
            .And.Contain(e => e.Contains("identifier"));
    }

    [Theory]
    [InlineData(IdentifierType.NationalID, "12345")]       // not 14 digits
    [InlineData(IdentifierType.Passport, "AB")]            // too short
    [InlineData(IdentifierType.UNHCRNo, "!!")]             // bad chars
    public async Task Bad_identifier_format_is_rejected(IdentifierType type, string value)
    {
        var reg = new BeneficiaryRegistrar(new FakeLookup(), TimeProvider.System);
        var result = await reg.RegisterAsync(Req(new NewIdentifier(type, value)), "o");
        result.Should().BeOfType<RegistrationResult.Invalid>();
    }
}

public class LifecycleTests
{
    [Theory]
    [InlineData(BeneficiaryStatus.Pending, BeneficiaryStatus.Active, true)]
    [InlineData(BeneficiaryStatus.Active, BeneficiaryStatus.Suspended, true)]
    [InlineData(BeneficiaryStatus.Suspended, BeneficiaryStatus.Active, true)]
    [InlineData(BeneficiaryStatus.Expired, BeneficiaryStatus.Active, true)]
    [InlineData(BeneficiaryStatus.Pending, BeneficiaryStatus.Suspended, false)] // illegal
    [InlineData(BeneficiaryStatus.Blocked, BeneficiaryStatus.Suspended, false)] // illegal
    public void Transition_legality(BeneficiaryStatus from, BeneficiaryStatus to, bool legal)
        => BeneficiaryLifecycle.CanTransition(from, to).Should().Be(legal);

    [Fact]
    public void Suspend_and_block_require_a_reason()
    {
        BeneficiaryLifecycle.Validate(BeneficiaryStatus.Active, BeneficiaryStatus.Suspended, reason: null)
            .Should().Contain("reason is required");
        BeneficiaryLifecycle.Validate(BeneficiaryStatus.Active, BeneficiaryStatus.Suspended, reason: "fraud review")
            .Should().BeNull();
    }

    [Fact]
    public void Illegal_transition_is_reported()
        => BeneficiaryLifecycle.Validate(BeneficiaryStatus.Pending, BeneficiaryStatus.Blocked, "x")
            .Should().Contain("illegal transition");

    [Fact]
    public void MemberNo_formats_per_year_and_sequence()
        => MemberNo.Format(2026, 42).Should().Be("MRS-M-2026-000042");
}
