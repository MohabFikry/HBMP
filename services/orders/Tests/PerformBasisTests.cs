using FluentAssertions;
using Mersal.Orders.Api;

namespace Mersal.Orders.Tests;

/// <summary>
/// The basis a cost share is quoted on at the bench: <c>?perform=&lt;lineId&gt;:&lt;qty&gt;</c>.
/// </summary>
/// <remarks>
/// The exact counterpart of pharmacy's <c>DispenseBasisTests</c>, and deliberately so: a lab bench and a
/// dispensing counter are the same situation — someone in front of a patient about to be told what they owe —
/// and the two must not answer differently. Each rejection exists because the alternative is a smaller,
/// entirely plausible-looking share with nothing on screen to say an examination fell out of it.
/// </remarks>
public class PerformBasisTests
{
    private static readonly Guid LineA = Guid.Parse("aaaaaaaa-1111-1111-1111-111111111111");
    private static readonly Guid LineB = Guid.Parse("bbbbbbbb-2222-2222-2222-222222222222");

    private static Guid[] Known => [LineA, LineB];

    [Fact]
    public void No_basis_at_all_asks_the_whole_order_question()
    {
        OrderPricing.PerformBasis.TryParse(null, Known, out var basis, out var error).Should().BeTrue();
        basis.Should().BeNull();
        error.Should().BeNull();
    }

    [Fact]
    public void A_basis_is_read_per_line()
    {
        OrderPricing.PerformBasis.TryParse([$"{LineA}:1", $"{LineB}:2"], Known, out var basis, out _)
            .Should().BeTrue();

        basis![LineA].Should().Be(1m);
        basis[LineB].Should().Be(2m);
    }

    [Fact]
    public void A_line_that_is_not_on_this_order_is_refused_rather_than_skipped()
    {
        var stale = Guid.NewGuid();

        OrderPricing.PerformBasis.TryParse([$"{stale}:1"], Known, out var basis, out var error)
            .Should().BeFalse();

        basis.Should().BeNull();
        error.Should().Contain(stale.ToString());
    }

    [Theory]
    [InlineData("nope:1")]
    [InlineData("aaaaaaaa-1111-1111-1111-111111111111:x")]
    [InlineData(":1")]
    public void A_malformed_entry_is_refused(string entry)
    {
        OrderPricing.PerformBasis.TryParse([entry], Known, out _, out var error).Should().BeFalse();
        error.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void A_negative_quantity_is_refused()
    {
        OrderPricing.PerformBasis.TryParse([$"{LineA}:-2"], Known, out _, out var error).Should().BeFalse();
        error.Should().NotBeNull();
    }

    [Fact]
    public void Repeated_entries_for_one_line_add_up()
    {
        OrderPricing.PerformBasis.TryParse([$"{LineA}:1", $"{LineA}:2"], Known, out var basis, out _)
            .Should().BeTrue();

        basis![LineA].Should().Be(3m);
    }
}
