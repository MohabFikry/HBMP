using FluentAssertions;
using Mersal.Orders.Domain;

namespace Mersal.Orders.Tests;

/// <summary>
/// 29.2b / design 45 §2b — <b>an external provider sees only its own rows</b>.
///
/// <para>Written BEFORE the queue endpoint, as the build prompt requires, because the defect this exists to
/// prevent is not a subtle one and was still missed: <c>DispensingGate</c> asks the ownership question of the
/// CALLER's own provider id, so the rule compares the caller against themselves and any authenticated
/// pharmacist browses the whole network queue. Nothing failed — no error, no empty screen; the queue simply
/// contained other pharmacies' work, which looks exactly like a busy queue.</para>
///
/// <para>The reason no test caught it is that answering "can provider A see provider B's work?" requires TWO
/// providers, and every test had one. These tests have two.</para>
/// </summary>
public class ProviderOwnershipTests
{
    private static readonly Guid ProviderA = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001");
    private static readonly Guid ProviderB = Guid.Parse("bbbbbbbb-0000-4000-8000-000000000002");

    [Fact]
    public void Provider_A_cannot_see_provider_Bs_order()
    {
        // THE test. Everything else in this file is a way of getting this wrong that also has to be closed.
        ProviderOwnership.MayAccess(callerProviderId: ProviderA, assignedProviderId: ProviderB)
            .Should().BeFalse();
    }

    [Fact]
    public void Provider_A_can_see_its_own_order()
    {
        // Stated so the test above cannot be satisfied by a rule that simply denies everything — a
        // fail-closed check that never allows is not secure, it is broken, and it fails in a direction
        // somebody will "fix" by loosening it.
        ProviderOwnership.MayAccess(ProviderA, ProviderA).Should().BeTrue();
    }

    [Fact]
    public void An_unassigned_order_belongs_to_no_external_provider()
    {
        // The same defect reached by a different route: a null owner reading as "unowned, therefore visible"
        // produces a queue showing everything nobody has claimed. Lab and Radiology orders are fulfilled
        // inside Mersal's clinics and carry no assignment — every one of them would be in every external
        // centre's queue.
        ProviderOwnership.MayAccess(ProviderA, assignedProviderId: null).Should().BeFalse();
        ProviderOwnership.MayAccess(ProviderA, Guid.Empty).Should().BeFalse();
    }

    [Fact]
    public void A_caller_with_no_provider_identity_sees_nothing()
    {
        // Holding a token is not being a provider. This is the "checks only that the caller holds *a*
        // ProviderId" half of the R3 finding, inverted: holding none must not pass either.
        ProviderOwnership.MayAccess(callerProviderId: null, assignedProviderId: ProviderA).Should().BeFalse();
        ProviderOwnership.MayAccess(Guid.Empty, ProviderA).Should().BeFalse();
    }

    [Fact]
    public void Two_empty_ids_do_not_match_each_other()
    {
        // Guid.Empty == Guid.Empty is TRUE, so a naive equality check hands every unassigned order to every
        // unaffiliated caller. This is the single most likely way to implement the rule wrongly and still see
        // the two tests above pass.
        ProviderOwnership.MayAccess(Guid.Empty, Guid.Empty).Should().BeFalse();
        ProviderOwnership.MayAccess(null, null).Should().BeFalse();
    }

    [Fact]
    public void A_queue_built_from_the_filter_contains_only_the_callers_work()
    {
        var orders = new[]
        {
            Order(ProviderA), Order(ProviderB), Order(null), Order(ProviderA), Order(ProviderB),
        };

        var forA = ProviderOwnership.OwnedBy(orders, ProviderA).ToList();

        forA.Should().HaveCount(2);
        forA.Should().OnlyContain(o => o.AssignedProviderId == ProviderA);
    }

    [Fact]
    public void The_two_providers_queues_are_disjoint_and_neither_is_everything()
    {
        // Belt and braces on the filter: A's queue and B's queue must not overlap, and neither may be the
        // whole list. A filter that returned everything would pass "contains only mine" if all the fixtures
        // happened to be mine.
        var orders = new[] { Order(ProviderA), Order(ProviderB), Order(null) };

        var forA = ProviderOwnership.OwnedBy(orders, ProviderA).ToList();
        var forB = ProviderOwnership.OwnedBy(orders, ProviderB).ToList();

        forA.Should().NotBeEmpty();
        forB.Should().NotBeEmpty();
        forA.Should().NotIntersectWith(forB);
        (forA.Count + forB.Count).Should().BeLessThan(orders.Length, "the unassigned order is in neither queue");
    }

    private static InvestigationOrder Order(Guid? assigned) => new()
    {
        OrderId = Guid.NewGuid(),
        OrderNo = $"ORD-2026-{Random.Shared.Next(100000, 999999)}",
        OrderType = OrderType.Procedure,
        Status = OrderStatus.Active,
        AssignedProviderId = assigned,
    };
}
