using FluentAssertions;
using Mersal.Finance.Domain;
using Mersal.Finance.Infrastructure;

namespace Mersal.Finance.Tests;

/// <summary>Pure finance building blocks: settlement numbering, the read-not-owned price book, and CSV export
/// rendering (masked-min PII, correct billing columns, row count for the audit).</summary>
public class FinanceDomainTests
{
    [Fact]
    public void Settlement_number_is_year_scoped_and_zero_padded()
    {
        SettlementNo.Format(2026, 7).Should().Be("STL-2026-000007");
        string.CompareOrdinal(SettlementNo.Format(2026, 2), SettlementNo.Format(2026, 1)).Should().BeGreaterThan(0);
    }

    [Fact]
    public void A_price_book_returns_the_agreed_price_or_signals_absence()
    {
        var book = new ContractPriceBook(Guid.NewGuid(), "EGP",
            new Dictionary<string, decimal> { ["70450"] = 350.00m });
        book.TryPrice("70450", out var price).Should().BeTrue();
        price.Should().Be(350.00m);
        book.TryPrice("99999", out _).Should().BeFalse();   // absent → caller falls back to the observed FLOOR
        ContractPriceBook.Empty().Prices.Should().BeEmpty();
    }

    [Fact]
    public void Csv_export_masks_pii_and_carries_only_billing_columns()
    {
        var view = UtilizationView.From(new List<UtilizationRow>
        {
            new("70450", "Radiology", "Imaging", "prov-1", 3, 2, 700.00m),
            new("80053", "Lab", "General", null, 5, 5, 250.00m),
        });
        var (csv, rows) = FinanceQueries.ToCsv(view);

        rows.Should().Be(2);
        csv.Should().StartWith("service_code,service_line,coverage_category,provider_ref,authorized_qty,delivered_qty,spend");
        csv.Should().Contain("70450,Radiology,Imaging,prov-1,3,2,700.00");
        // No beneficiary name / PII column exists; provider is a reference token only.
        csv.ToLowerInvariant().Should().NotContain("diagnosis").And.NotContain("beneficiary");
    }

    [Fact]
    public void The_projection_whitelist_names_only_billing_field_classes()
    {
        FinanceProjection.AllowedClasses.Should().Contain(new[] { "service_code", "amount", "quantity", "pii_masked" });
        FinanceProjection.AllowedClasses.Should().NotContain("diagnosis");
        FinanceProjection.ForbiddenTokens.Should().Contain("diagnosis");
    }
}
