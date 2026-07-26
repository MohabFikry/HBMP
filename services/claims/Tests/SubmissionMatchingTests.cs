using FluentAssertions;
using Mersal.Claims.Domain;

namespace Mersal.Claims.Tests;

/// <summary>Pure-domain unit tests for provider-submission matching (10b.5): the match key + service-date tolerance
/// (match, near-miss, no-match) and document type/size validation. No infrastructure — the rules stand alone.</summary>
public class SubmissionMatchingTests
{
    private static readonly Guid Prov = Guid.NewGuid();
    private static readonly Guid Bene = Guid.NewGuid();
    private static readonly Guid Auth = Guid.NewGuid();

    private static MatchKey Key(string code = "80053", Guid? auth = null) =>
        new(Prov, Bene, ClaimCodeSystem.CPT, code, auth);

    [Fact]
    public void Same_key_and_date_within_tolerance_is_a_match() =>
        SubmissionMatcher.Decide(Key(), new DateOnly(2026, 7, 10), Key(), new DateOnly(2026, 7, 11),
            SubmissionMatcher.DefaultToleranceDays).Should().Be(MatchDecision.Match);

    [Fact]
    public void Same_key_but_date_outside_tolerance_is_a_near_miss() =>
        SubmissionMatcher.Decide(Key(), new DateOnly(2026, 7, 10), Key(), new DateOnly(2026, 7, 20),
            SubmissionMatcher.DefaultToleranceDays).Should().Be(MatchDecision.NearMissDate);

    [Fact]
    public void A_different_service_code_is_no_match() =>
        SubmissionMatcher.Decide(Key("80053"), new DateOnly(2026, 7, 10), Key("99213"), new DateOnly(2026, 7, 10),
            SubmissionMatcher.DefaultToleranceDays).Should().Be(MatchDecision.NoMatch);

    [Fact]
    public void A_different_authorization_is_no_match() =>
        SubmissionMatcher.Decide(Key(auth: Auth), new DateOnly(2026, 7, 10), Key(auth: Guid.NewGuid()),
            new DateOnly(2026, 7, 10), SubmissionMatcher.DefaultToleranceDays).Should().Be(MatchDecision.NoMatch);

    [Theory]
    [InlineData("2026-07-10", "2026-07-12", 2, true)]
    [InlineData("2026-07-10", "2026-07-13", 2, false)]
    [InlineData("2026-07-10", "2026-07-08", 2, true)]
    [InlineData("2026-07-10", "2026-07-10", 0, true)]
    public void Date_tolerance_is_symmetric_and_inclusive(string a, string b, int tol, bool within) =>
        SubmissionMatcher.WithinTolerance(DateOnly.Parse(a), DateOnly.Parse(b), tol).Should().Be(within);

    // ---- document validation ------------------------------------------------------------------------------
    [Fact]
    public void A_pdf_of_reasonable_size_validates() =>
        DocumentValidation.Validate("application/pdf", 1024).Should().BeNull();

    [Fact]
    public void An_unsupported_content_type_is_rejected() =>
        DocumentValidation.Validate("application/x-msdownload", 1024).Should().Be("unsupported-content-type");

    [Fact]
    public void An_empty_document_is_rejected() =>
        DocumentValidation.Validate("image/png", 0).Should().Be("empty-document");

    [Fact]
    public void An_oversized_document_is_rejected() =>
        DocumentValidation.Validate("application/pdf", DocumentValidation.MaxSizeBytes + 1).Should().Be("document-too-large");
}
