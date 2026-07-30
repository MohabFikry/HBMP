using System.Reflection;
using FluentAssertions;
using Mersal.Emr.Api;

namespace Mersal.Emr.Tests;

/// <summary>
/// 14.5 booking feed — the boundary guard for <see cref="DoctorAvailabilityResponse"/>.
///
/// <para><b>The defect this prevents.</b> This endpoint answers "which doctors have open time at this
/// branch", as ids and counts. The booking screen needs names and specialties too, and the obvious
/// convenience is to add them here — one call instead of two. Doing so would make emr disclose
/// provider-service's data on the caller's behalf, which is the aggregation shape the platform forbids
/// (profile-service's <c>NoServiceAccountArchitectureTests</c> explains why at length), and it would turn an
/// <c>appointment:read</c> endpoint into a way to enumerate Mersal's clinicians.</para>
///
/// <para>It would also arrive innocently — as "just the display name, so the UI doesn't need a second call" —
/// and every test would still pass, because a richer response breaks nothing. Only a rule that asserts the
/// shape stays narrow can catch it, which is the same reasoning behind the queue-ticket guard next door.</para>
///
/// <para>The screen instead reads provider-service directly under <c>practitioner:read</c> (identity 0018)
/// and joins the two lists client-side — two authorized reads, each from the service that owns the data.</para>
/// </summary>
public class DoctorAvailabilityBoundaryTests
{
    /// <summary>Anything that would make this a disclosure of WHO a practitioner is, rather than of WHEN they
    /// are free, plus the usual clinical/PII terms.</summary>
    private static readonly string[] Forbidden =
    [
        "name", "specialt", "licen", "email", "phone", "national", "passport",
        "diagnos", "note", "result", "beneficiar", "patient",
    ];

    [Fact]
    public void The_availability_feed_names_no_practitioner_and_no_patient()
    {
        var offenders = typeof(DoctorAvailabilityResponse)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .Where(n => Forbidden.Any(term => n.Contains(term, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        offenders.Should().BeEmpty(
            "the availability feed reports WHEN a doctor is free, never who they are — identity belongs to " +
            "provider-service and the booking screen reads it there under practitioner:read");
    }

    [Fact]
    public void The_availability_feed_is_exactly_the_four_agreed_fields()
    {
        // Pinned positively as well as negatively: the forbidden-term list cannot anticipate every way a
        // field might smuggle identity in ("label", "title", "who"), so the shape itself is the contract.
        typeof(DoctorAvailabilityResponse)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .Should().BeEquivalentTo(["DoctorId", "BranchId", "OpenSlots", "NextSlotStart"]);
    }
}
