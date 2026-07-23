using FluentAssertions;
using Mersal.Patient.Domain;

namespace Mersal.Patient.Tests;

public class RegistrationWorkflowTests
{
    private static Registration Reg(RegistrationStatus s = RegistrationStatus.Pending, bool docs = true, bool cov = true)
        => new() { RegistrationId = Guid.NewGuid(), BeneficiaryId = Guid.NewGuid(), Status = s, DocumentsVerified = docs, CoverageBound = cov };

    [Fact]
    public void Approve_allowed_when_docs_verified_and_coverage_bound()
        => RegistrationRules.ValidateDecision(Reg(), RegistrationDecision.Approve, null).Should().BeNull();

    [Fact]
    public void Approve_blocked_without_verified_documents()
        => RegistrationRules.ValidateDecision(Reg(docs: false), RegistrationDecision.Approve, null)
            .Should().Contain("documents are not verified");

    [Fact]
    public void Approve_blocked_without_bound_coverage()
        => RegistrationRules.ValidateDecision(Reg(cov: false), RegistrationDecision.Approve, null)
            .Should().Contain("no policy/coverage is bound");

    [Fact]
    public void Reject_requires_a_reason()
    {
        RegistrationRules.ValidateDecision(Reg(), RegistrationDecision.Reject, null).Should().Contain("reason is required");
        RegistrationRules.ValidateDecision(Reg(), RegistrationDecision.Reject, "ineligible").Should().BeNull();
    }

    [Fact]
    public void RequestInfo_requires_notes()
        => RegistrationRules.ValidateDecision(Reg(), RegistrationDecision.RequestInfo, null).Should().Contain("missing information");

    [Fact]
    public void Cannot_decide_an_already_final_registration()
    {
        RegistrationRules.ValidateDecision(Reg(RegistrationStatus.Active), RegistrationDecision.Approve, null).Should().Contain("already Active");
        RegistrationRules.ValidateDecision(Reg(RegistrationStatus.Rejected), RegistrationDecision.Approve, "x").Should().Contain("already Rejected");
    }

    [Theory]
    [InlineData(RegistrationDecision.Approve, RegistrationStatus.Active)]
    [InlineData(RegistrationDecision.RequestInfo, RegistrationStatus.InfoRequested)]
    [InlineData(RegistrationDecision.Reject, RegistrationStatus.Rejected)]
    public void Decision_maps_to_status(RegistrationDecision d, RegistrationStatus s)
        => RegistrationRules.ResultOf(d).Should().Be(s);
}
