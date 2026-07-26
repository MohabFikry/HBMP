using FluentAssertions;
using Mersal.Approvals.Api;
using Mersal.Approvals.Domain;
using Mersal.Approvals.Infrastructure;

namespace Mersal.Approvals.Tests;

/// <summary>16.6 (H4, design 37 §6): the clinical review projection must reduce a non-Standard (e.g. mental-health)
/// note/report to EXISTENCE METADATA ONLY for a reviewer without access (author or active report-access grant),
/// so the medical-approval team's standing oversight cannot become a side channel around the sensitive-result
/// gate. Standard content and access-holders are unaffected. (Role gating — finance/reception/case denied at the
/// engine — is proven in ApprovalsAuthzTests.)</summary>
public class ApprovalsSensitivityTests
{
    private static Authorization Auth() => new()
    {
        AuthorizationId = Guid.NewGuid(), AuthNo = "AUTH-2026-000001", BeneficiaryId = Guid.NewGuid(),
        Source = AuthSource.OrderLine, ServiceCodes = "[]", RequestedScope = "{}",
    };

    private static ClinicalContext Context(string sensitivity, bool callerHasAccess) => new(
        EmrSummary: "summary",
        Notes: [new ClinicalNote("Psychiatry", "Dr X", DateTimeOffset.UtcNow, "suicidal ideation; started SSRI", sensitivity, callerHasAccess)],
        Documents: [new SupportingDocument(Guid.NewGuid(), "MentalHealthReport", "psych-eval.pdf", sensitivity, callerHasAccess)]);

    [Fact]
    public void MentalHealth_result_is_metadata_only_for_a_reviewer_without_access()
    {
        var view = ReviewView.From(Auth(), Context("HighlySensitive", callerHasAccess: false));

        var note = view.Notes.Should().ContainSingle().Subject;
        note.Restricted.Should().BeTrue();
        note.Summary.Should().NotContain("suicidal").And.Contain("RESTRICTED");
        note.Type.Should().Be("Psychiatry"); // category (existence) is still shown

        var doc = view.Documents.Should().ContainSingle().Subject;
        doc.Restricted.Should().BeTrue();
        doc.DocumentId.Should().Be(Guid.Empty);   // no fetchable ref
        doc.FileName.Should().BeEmpty();
        doc.Kind.Should().Be("MentalHealthReport"); // category stays
    }

    [Fact]
    public void Access_holder_sees_full_sensitive_content()
    {
        var view = ReviewView.From(Auth(), Context("HighlySensitive", callerHasAccess: true));

        view.Notes.Single().Restricted.Should().BeFalse();
        view.Notes.Single().Summary.Should().Contain("suicidal");
        view.Documents.Single().Restricted.Should().BeFalse();
        view.Documents.Single().DocumentId.Should().NotBe(Guid.Empty);
    }

    [Fact]
    public void Standard_content_is_never_restricted()
    {
        var view = ReviewView.From(Auth(), Context("Standard", callerHasAccess: false));

        view.Notes.Single().Restricted.Should().BeFalse();
        view.Documents.Single().Restricted.Should().BeFalse();
    }
}
