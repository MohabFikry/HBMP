using FluentAssertions;
using Mersal.Authz;
using Mersal.Orders.Api;

namespace Mersal.Orders.Tests;

/// <summary>
/// 33.8 — the result read's SHAPE, and the report route's gate.
/// </summary>
/// <remarks>
/// <para>The endpoint had no shape test at all, which is how it came to return two different kinds of thing
/// on one route: an ARRAY of fulfillment rows when the caller could read the result, and a single object when
/// they could not. The SPA read both as an object, so <c>resultValue</c> was <c>undefined</c> and every
/// standard result rendered as an em-dash against a real gateway — and <c>category</c>, <c>code</c> and
/// <c>status</c> were never sent at all, because a fulfillment row does not know what was ordered.</para>
///
/// <para><see cref="ReportAccessTests"/> covers the gate matrix as a pure function. These cover the contract
/// the two endpoints hand back, which is the part nothing was watching.</para>
/// </remarks>
public class LineResultViewTests
{
    [Fact]
    public void The_readable_projection_carries_the_line_context_the_dialog_renders()
    {
        var view = new LineResultView(
            Restricted: false, Guid.NewGuid(), Guid.NewGuid(),
            Code: "71260", CodeSystem: "CPT", Category: "Imaging", Status: "Used",
            ResultValue: "No acute intracranial abnormality.", HasReport: true,
            ResultUploadedAt: DateTimeOffset.UtcNow);

        // The four fields the clinician actually reads. Each of them used to be a client-side default —
        // "Result", "—", "—", "Completed" — on every read against a real gateway.
        view.Category.Should().Be("Imaging");
        view.Code.Should().Be("71260");
        view.ResultValue.Should().Be("No acute intracranial abnormality.");
        view.Status.Should().Be("Used");
    }

    [Fact]
    public void Both_projections_carry_the_discriminator_so_a_client_reads_one_field_to_tell_them_apart()
    {
        var readable = new LineResultView(
            false, Guid.NewGuid(), Guid.NewGuid(), "80053", "CPT", "Laboratory", "Used", "Normal", false, null);
        var restricted = new RestrictedResultView(
            true, Guid.NewGuid(), Guid.NewGuid(), "Sensitive", "CPT", "Used", null);

        readable.Restricted.Should().BeFalse();
        restricted.Restricted.Should().BeTrue();

        // Both are objects. A route that answers with an array on one branch and an object on the other
        // cannot be read without knowing which branch the server took, which is the defect this replaced.
        readable.Should().BeOfType<LineResultView>();
        restricted.Should().BeOfType<RestrictedResultView>();
    }

    [Fact]
    public void The_readable_projection_carries_no_document_identifier()
    {
        // `hasReport` is a boolean by design: the bytes come from the gated report route, so the browser needs
        // the answer to "is there one", never the means to fetch it out from under the gate. A document id in
        // the client is a capability — and the restricted projection withholds it for exactly that reason.
        var names = typeof(LineResultView).GetProperties().Select(p => p.Name).ToList();

        names.Should().Contain("HasReport");
        names.Should().NotContain(n => n.Contains("DocumentId", StringComparison.OrdinalIgnoreCase));
        typeof(RestrictedResultView).GetProperties().Select(p => p.Name)
            .Should().NotContain(n => n.Contains("DocumentId", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_result_with_a_report_and_no_summary_is_representable()
    {
        // The radiology case, and the reason the whole fix exists: for imaging the report IS the finding, so
        // a null summary beside `hasReport: true` is a normal result rather than a missing one.
        var view = new LineResultView(
            false, Guid.NewGuid(), Guid.NewGuid(), "71260", "CPT", "Imaging", "Used",
            ResultValue: null, HasReport: true, ResultUploadedAt: DateTimeOffset.UtcNow);

        view.ResultValue.Should().BeNull();
        view.HasReport.Should().BeTrue();
    }

    /// <summary>
    /// The document-service rule the report route leans on is NARROWER than the metadata rule beside it.
    /// </summary>
    /// <remarks>
    /// `DocumentPolicies.Read` is metadata and says so ("never blob bytes"); reception and beneficiary
    /// management legitimately see that a beneficiary has documents on file without being people who read
    /// radiology reports. Pinned here because the two lists sitting next to each other in one file is exactly
    /// how they come to be copied from one another.
    /// </remarks>
    [Fact]
    public void Reading_a_clinical_documents_bytes_is_a_narrower_grant_than_listing_its_metadata()
    {
        var rules = DocumentPolicies.Rules();
        var read = rules.Single(r => r.Action == DocumentPolicies.Read);
        var content = rules.Single(r => r.Action == DocumentPolicies.ContentRead);

        content.Roles.Should().BeSubsetOf(read.Roles);
        content.Roles.Should().NotBeEquivalentTo(read.Roles, "listing a file and reading it are different disclosures");
        content.Roles.Should().Contain("doctor").And.Contain("medical_approval").And.Contain("medical_director");
        content.Roles.Should().NotContain("reception").And.NotContain("beneficiary_mgmt");
        content.Sensitive.Should().BeTrue("every read of a clinical file is an audited PHI access");
    }
}
