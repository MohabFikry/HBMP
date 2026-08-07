using FluentAssertions;
using Mersal.Pharmacy.Api;

namespace Mersal.Pharmacy.Tests;

/// <summary>
/// The basis a cost share is quoted on: <c>?dispense=&lt;lineId&gt;:&lt;qty&gt;</c> on the pricing endpoint.
/// </summary>
/// <remarks>
/// <para>
/// The counter's share tiles used to answer one question — "what does the whole prescription cost the
/// patient" — while a pharmacist was handing over part of it. Stock is short, or the member can only pay for
/// half a course today; a partial dispense is the ordinary case rather than the exception, and quoting the
/// whole-prescription share against it overstates what is owed at that moment by exactly the part not being
/// collected.
/// </para>
/// <para>
/// <b>Every rejection here exists because the alternative is a wrong number that looks right.</b> A skipped
/// line, a clamped quantity or a silently-ignored malformed entry all produce a smaller, entirely
/// plausible-looking share, with nothing on screen to say a medicine fell out of it. A 400 is visible; a
/// quietly reduced figure is not.
/// </para>
/// </remarks>
public class DispenseBasisTests
{
    private static readonly Guid LineA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid LineB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static Guid[] Known => [LineA, LineB];

    [Fact]
    public void No_basis_at_all_asks_the_whole_prescription_question()
    {
        RxPricing.DispenseBasis.TryParse(null, Known, out var basis, out var error).Should().BeTrue();
        basis.Should().BeNull("a null basis is 'price the whole prescription', not 'price nothing'");
        error.Should().BeNull();

        RxPricing.DispenseBasis.TryParse([], Known, out basis, out _).Should().BeTrue();
        basis.Should().BeNull();
    }

    [Fact]
    public void A_basis_is_read_per_line()
    {
        RxPricing.DispenseBasis.TryParse([$"{LineA}:7", $"{LineB}:2"], Known, out var basis, out var error)
            .Should().BeTrue();

        error.Should().BeNull();
        basis.Should().NotBeNull();
        basis![LineA].Should().Be(7m);
        basis[LineB].Should().Be(2m);
    }

    [Fact]
    public void A_line_that_is_not_on_this_prescription_is_refused_rather_than_skipped()
    {
        var stale = Guid.NewGuid();

        RxPricing.DispenseBasis.TryParse([$"{LineA}:7", $"{stale}:3"], Known, out var basis, out var error)
            .Should().BeFalse("dropping the unknown line would quote the member for less than they collect, "
                              + "and the tile would look exactly as confident as a correct one");

        basis.Should().BeNull();
        error.Should().Contain(stale.ToString());
    }

    [Fact]
    public void A_negative_quantity_is_refused()
    {
        RxPricing.DispenseBasis.TryParse([$"{LineA}:-1"], Known, out _, out var error).Should().BeFalse();
        error.Should().NotBeNull();
    }

    [Theory]
    [InlineData("not-a-line:3")]
    [InlineData("11111111-1111-1111-1111-111111111111")]
    [InlineData("11111111-1111-1111-1111-111111111111:")]
    [InlineData("11111111-1111-1111-1111-111111111111:abc")]
    [InlineData(":3")]
    public void A_malformed_entry_is_refused(string entry)
    {
        RxPricing.DispenseBasis.TryParse([entry], Known, out var basis, out var error).Should().BeFalse();
        basis.Should().BeNull();
        error.Should().NotBeNullOrWhiteSpace("a caller sending nonsense must be told, not answered anyway");
    }

    [Fact]
    public void A_zero_quantity_parses_and_leaves_the_basis_empty_of_value()
    {
        // Zero is accepted rather than refused — "this line is not part of what I am handing over" is a
        // legitimate thing to say. The endpoint then finds a basis worth nothing and falls back to quoting the
        // whole prescription, which is why the tiles never read "Patient pays EGP 0.00" on an untouched screen.
        RxPricing.DispenseBasis.TryParse([$"{LineA}:0"], Known, out var basis, out var error).Should().BeTrue();
        error.Should().BeNull();
        basis!.Should().ContainKey(LineA).WhoseValue.Should().Be(0m);
    }

    [Fact]
    public void Repeated_entries_for_one_line_add_up()
    {
        // Not last-wins. A caller that splits a line across two entries means both, and taking only the second
        // would quote less than is being handed over — the same failure as dropping a line.
        RxPricing.DispenseBasis.TryParse([$"{LineA}:4", $"{LineA}:3"], Known, out var basis, out _)
            .Should().BeTrue();

        basis![LineA].Should().Be(7m);
    }

    [Fact]
    public void A_decimal_quantity_is_read_invariantly()
    {
        // The wire is invariant culture. Parsing "2.5" under a comma-decimal server locale would read 25.
        RxPricing.DispenseBasis.TryParse([$"{LineA}:2.5"], Known, out var basis, out _).Should().BeTrue();
        basis![LineA].Should().Be(2.5m);
    }
}
