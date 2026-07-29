using FluentAssertions;
using Mersal.Emr.Domain;

namespace Mersal.Emr.Tests;

/// <summary>
/// Slots must carry the branch of the availability rule that produced them. This was the third place the same
/// mistake appeared: the branch arrived on the request, was used for the practitioner-at-branch check, and was
/// then dropped — so every materialized slot was branchless. A branch-scoped desk asking "which clinics can I
/// book into?" is asking a question about slot.branch_id, and the answer was permanently empty.
/// </summary>
public class SlotBranchTests
{
    private static ProviderAvailability Rule(Guid? branch) => new()
    {
        AvailabilityId = Guid.NewGuid(),
        ProviderId = Guid.NewGuid(),
        LocationId = Guid.NewGuid(),
        BranchId = branch,
        DoctorId = Guid.NewGuid(),
        DayOfWeek = DayOfWeek.Wednesday,
        StartTime = new TimeOnly(9, 0),
        EndTime = new TimeOnly(10, 0),
        SlotMinutes = 15,
    };

    [Fact]
    public void Generated_slots_inherit_the_rules_branch()
    {
        var maadi = Guid.NewGuid();
        var rule = Rule(maadi);
        // 2026-07-22 is a Wednesday.
        var slots = SlotGeneration.Generate(rule, new DateOnly(2026, 7, 22), new DateOnly(2026, 7, 22), TimeSpan.FromHours(2));

        slots.Should().HaveCount(4, "a 60-minute window at 15-minute slots yields four");
        slots.Should().OnlyContain(s => s.BranchId == maadi);
        // The other identifying fields were already copied — this guards against a regression that drops one.
        slots.Should().OnlyContain(s => s.ProviderId == rule.ProviderId && s.LocationId == rule.LocationId);
        slots.Should().OnlyContain(s => s.DoctorId == rule.DoctorId);
    }

    [Fact]
    public void A_rule_with_no_branch_still_generates_branchless_slots()
    {
        // An external provider location has no Mersal branch, and that has to stay expressible: inheriting is
        // the rule, not "always set something".
        var slots = SlotGeneration.Generate(Rule(null), new DateOnly(2026, 7, 22), new DateOnly(2026, 7, 22), TimeSpan.FromHours(2));
        slots.Should().NotBeEmpty();
        slots.Should().OnlyContain(s => s.BranchId == null);
    }
}
