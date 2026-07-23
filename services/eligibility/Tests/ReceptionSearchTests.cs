using FluentAssertions;
using Mersal.Eligibility.Api;
using Mersal.Eligibility.Infrastructure;

namespace Mersal.Eligibility.Tests;

public class ReceptionSearchTests
{
    private static async Task<InMemoryReceptionIndex> Seed()
    {
        var idx = new InMemoryReceptionIndex();
        await idx.UpsertAsync(new ReceptionDocument
        {
            BeneficiaryId = Guid.NewGuid(), MemberNo = "MRS-M-2026-000001",
            GivenName = "Layla", FamilyName = "Haddad", Status = "Active",
            NationalId = "29001011234567", Passport = "A1234567", RefugeeId = "REF-99",
            UnhcrNo = "UNHCR-42", PolicyNo = "POL-1", PrimaryPhone = "+201000000001",
            ActiveCategories = ["CONSULT", "PHARMACY"],
            RemainingLimits = [new RemainingLimit("CONSULT", "Count", 9)],
        });
        return idx;
    }

    [Theory]
    [InlineData("29001011234567")]  // NationalID
    [InlineData("A1234567")]        // Passport
    [InlineData("MRS-M-2026-000001")] // Card / member no
    [InlineData("POL-1")]           // Policy
    [InlineData("+201000000001")]   // Phone
    [InlineData("haddad")]          // name (case-insensitive)
    public async Task Finds_by_each_identifier_type(string q)
    {
        var idx = await Seed();
        var hits = await idx.SearchAsync(q, 25);
        hits.Should().ContainSingle().Which.MemberNo.Should().Be("MRS-M-2026-000001");
    }

    [Fact]
    public async Task Unknown_identifier_returns_empty()
    {
        var idx = await Seed();
        (await idx.SearchAsync("does-not-exist", 25)).Should().BeEmpty();
    }

    [Fact]
    public async Task Result_card_exposes_only_min_necessary_fields()
    {
        var idx = await Seed();
        var hit = (await idx.SearchAsync("A1234567", 25)).Single();
        var card = ReceptionResultCard.From(hit);

        card.Identity.DisplayName.Should().Be("Layla Haddad");
        card.Identity.StatusSemantics.Icon.Should().Be("check-circle"); // non-color status semantics
        card.Coverage.Should().Contain("CONSULT");
        card.RemainingLimits.Should().ContainSingle(l => l.Remaining == 9);
        card.VisitHistory.Count.Should().Be(0); // summary only — no diagnoses/notes
    }

    [Fact]
    public void Compose_builds_a_min_necessary_document_from_projections()
    {
        var member = new MemberProjection
        {
            BeneficiaryId = Guid.NewGuid(), MemberNo = "MRS-M-2026-000009",
            GivenName = "Omar", FamilyName = "Nasser", Status = "Active", NationalId = "111",
        };
        var covs = new[]
        {
            new CoverageProjection { CoverageId = Guid.NewGuid(), BeneficiaryId = member.BeneficiaryId,
                BenefitCategory = "PHARMACY", PolicyNo = "POL-7", Status = "Active",
                LimitsJson = "[{\"limitType\":\"Annual\",\"limitValue\":1000,\"consumedValue\":250}]" },
        };
        var doc = PostgresReceptionIndex.Compose(member, covs);
        doc.ActiveCategories.Should().Contain("PHARMACY");
        doc.PolicyNo.Should().Be("POL-7");
        doc.RemainingLimits.Should().ContainSingle(l => l.Remaining == 750);
    }
}
